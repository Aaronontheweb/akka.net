//-----------------------------------------------------------------------
// <copyright file="TcpTransportConnection.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.IO
{
    /// <summary>
    /// Plaintext TCP implementation of <see cref="ITransportConnection"/>.
    /// Owns an input <c>Pipe</c> (read pump → actor) and a lock-free output hand-off
    /// (<see cref="LockFreeOutputQueue"/>, actor → write pump), each driven by a pump loop that
    /// bridges to a NetworkStream.
    /// </summary>
    /// <remarks>
    /// The output side previously used a second <c>System.IO.Pipelines.Pipe</c> whose single
    /// <c>_sync</c> Monitor lock was taken by both the producing actor thread and the consuming
    /// write-pump thread; under load that contended lock dominated CPU. It has been replaced with
    /// <see cref="LockFreeOutputQueue"/>, a lock-free single-producer/single-consumer byte hand-off.
    /// The input side is unchanged.
    /// </remarks>
    public sealed class TcpTransportConnection : ITransportConnection
    {
        private readonly Socket _socket;
        private readonly Stream _stream;
        private readonly Pipe _inputPipe;
        private readonly LockFreeOutputQueue _output = new();
        private readonly CancellationTokenSource _cts = new();

        /// <summary>
        /// Creates a transport connection from an already-connected socket.
        /// Starts the read and write pump loops immediately.
        /// </summary>
        public TcpTransportConnection(Socket socket, PipeOptions? inputPipeOptions = null,
            PipeOptions? outputPipeOptions = null)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);

            _inputPipe = new Pipe(inputPipeOptions ?? PipeOptions.Default);
            // Output uses the lock-free SPSC hand-off. outputPipeOptions is retained for API
            // compatibility but no longer used now that the output Pipe has been removed.
            _ = outputPipeOptions;

            ReadCompleted = RunReadPumpAsync(_cts.Token);
            WriteCompleted = RunWritePumpAsync(_cts.Token);
        }

        /// <summary>
        /// Creates a transport connection from an existing stream (for TLS or testing).
        /// </summary>
        public TcpTransportConnection(Socket socket, Stream stream, PipeOptions? inputPipeOptions = null,
            PipeOptions? outputPipeOptions = null)
        {
            _socket = socket;
            _stream = stream;

            _inputPipe = new Pipe(inputPipeOptions ?? PipeOptions.Default);
            // Output uses the lock-free SPSC hand-off. outputPipeOptions is retained for API
            // compatibility but no longer used now that the output Pipe has been removed.
            _ = outputPipeOptions;

            ReadCompleted = RunReadPumpAsync(_cts.Token);
            WriteCompleted = RunWritePumpAsync(_cts.Token);
        }

        public PipeReader Input => _inputPipe.Reader;

        /// <inheritdoc/>
        public Task ReadCompleted { get; }

        /// <inheritdoc/>
        public Task WriteCompleted { get; }

        /// <inheritdoc/>
        public bool HasReadError => Volatile.Read(ref _hasReadError);

        /// <inheritdoc/>
        public Exception? ReadError => Volatile.Read(ref _readError);

        private bool _hasReadError;
        private Exception? _readError;

        internal void WriteUnflushed(ReadOnlyMemory<byte> data)
        {
            _output.Write(data.Span);
        }

        internal void WriteUnflushed(ReadOnlySequence<byte> data)
        {
            foreach (var segment in data)
            {
                _output.Write(segment.Span);
            }
        }

        public ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            WriteUnflushed(data);
            return FlushAsync(ct);
        }

        public ValueTask<FlushResult> WriteAsync(ReadOnlySequence<byte> data, CancellationToken ct = default)
        {
            WriteUnflushed(data);
            return FlushAsync(ct);
        }

        public ValueTask<FlushResult> FlushAsync(CancellationToken ct = default)
        {
            // Publish accumulated writes to the consumer and wake it. Callers (TcpConnection) fire flush
            // fire-and-forget and do not observe the FlushResult, so return an "accepted more data" result.
            _output.Flush();
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
        }

        public async Task ShutdownAsync()
        {
            // Signal completion to the output hand-off — the write pump drains remaining data and exits.
            _output.CompleteWriter();

            // Wait for write pump to finish flushing
            await WriteCompleted.ConfigureAwait(false);

            // Half-close the socket (send FIN).
            // SocketException is expected if the peer already reset the connection.
            try
            {
                _socket.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException) { } // slopwatch-ignore: SW003 socket may already be closed by peer or abort
        }

        public async Task CloseAsync()
        {
            // Signal completion to the output hand-off — the write pump drains remaining data and exits.
            _output.CompleteWriter();

            // Wait for write pump to finish flushing
            await WriteCompleted.ConfigureAwait(false);

            // Cancel to unblock the read pump (which may be blocked on stream.ReadAsync)
            _cts.Cancel();

            // Wait for read pump to exit — it may throw OperationCanceledException (from CTS cancel)
            // or IOException/SocketException (from stream close). Both are expected during shutdown.
            try { await ReadCompleted.ConfigureAwait(false); }
            catch (Exception) when (_cts.IsCancellationRequested) { } // slopwatch-ignore: SW003 expected cancellation or I/O error during shutdown

            // Close the stream and socket
            await _stream.DisposeAsync().ConfigureAwait(false);
            _socket.Close();
        }

        public void Abort()
        {
            // Cancel pumps immediately
            _cts.Cancel();

            // Abort the output hand-off (discard unflushed data, no flush) and complete the input pipe,
            // waking/unblocking any parked pump. InvalidOperationException if already completed — safe to ignore.
            _output.Abort();
            try { _inputPipe.Writer.Complete(); } catch (InvalidOperationException) { } // slopwatch-ignore: SW003 pipe may already be completed

            // RST the socket — SocketException/ObjectDisposedException if already closed.
            try
            {
                _socket.LingerState = new LingerOption(true, 0);
                _socket.Close();
            }
            catch (ObjectDisposedException) { } // slopwatch-ignore: SW003 socket may already be disposed
            catch (SocketException) { } // slopwatch-ignore: SW003 socket may already be closed

            // Dispose the stream — ObjectDisposedException if already disposed.
            try { _stream.Dispose(); } catch (ObjectDisposedException) { } // slopwatch-ignore: SW003 stream may already be disposed
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            // Abort the output hand-off (wakes a parked write pump, no flush) and complete the input pipe.
            _output.Abort();
            await _inputPipe.Writer.CompleteAsync().ConfigureAwait(false);

            // Wait for pump tasks — they may throw OperationCanceledException or I/O errors during shutdown.
            try
            {
                await Task.WhenAll(ReadCompleted, WriteCompleted).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested) { } // slopwatch-ignore: SW003 expected errors during disposal

            await _stream.DisposeAsync().ConfigureAwait(false);
            _socket.Dispose();
            _cts.Dispose();
        }

        /* ================================================================= */
        /*  Read pump: Stream → Input Pipe                                   */
        /* ================================================================= */

        private async Task RunReadPumpAsync(CancellationToken ct)
        {
            var writer = _inputPipe.Writer;
            Exception? error = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var memory = writer.GetMemory();
                    var bytesRead = await _stream.ReadAsync(memory, ct).ConfigureAwait(false);

                    if (bytesRead == 0)
                        break; // EOF — peer closed

                    writer.Advance(bytesRead);

                    var flushResult = await writer.FlushAsync(ct).ConfigureAwait(false);
                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                        break; // Reader (actor) is done
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // slopwatch-ignore: SW003 normal CTS-driven shutdown
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // Set error fields BEFORE completing the pipe writer.
                // This ensures the actor can synchronously check HasReadError
                // when it handles the PipeReadCompleted with IsCompleted,
                // even if the ReadPumpFailed message hasn't been processed yet.
                if (error != null)
                {
                    Volatile.Write(ref _readError, error);
                    Volatile.Write(ref _hasReadError, true);
                }

                // Complete the pipe writer WITHOUT passing the exception.
                // This preserves buffered data so the actor can drain it before
                // checking ReadCompleted.IsFaulted for the error.
                await writer.CompleteAsync().ConfigureAwait(false);
            }

            // If there was an error, throw it so ReadCompleted.IsFaulted is true.
            // This must happen AFTER the pipe writer is completed so buffered data
            // is available for the actor to drain.
            if (error != null)
                throw error;
        }

        /* ================================================================= */
        /*  Write pump: Output queue → Stream                                */
        /* ================================================================= */

        private async Task RunWritePumpAsync(CancellationToken ct)
        {
            Exception? error = null;

            try
            {
                while (true)
                {
                    // Hard abort: exit immediately without flushing queued data.
                    if (_output.IsAborted)
                        break;

                    if (_output.TryDequeue(out var segment))
                    {
                        try
                        {
                            await _stream.WriteAsync(segment.AsMemory(), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            _output.ReturnBuffer(segment);
                        }

                        continue;
                    }

                    // Graceful completion: producer finished and the queue is fully drained.
                    if (_output.IsCompletedAndDrained)
                        break;

                    if (ct.IsCancellationRequested)
                        break;

                    // Nothing queued — park until the producer signals (data / completion / abort).
                    await _output.WaitAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // slopwatch-ignore: SW003 normal CTS-driven shutdown
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // Return any still-queued buffers to the pool (faulted/aborted teardown).
                _output.OnConsumerExit();
            }

            // If there was a write error, surface it so WriteCompleted faults and the actor observes it.
            if (error != null)
                throw error;
        }
    }
}
