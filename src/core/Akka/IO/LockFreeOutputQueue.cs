//-----------------------------------------------------------------------
// <copyright file="LockFreeOutputQueue.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Akka.IO
{
    /// <summary>
    /// Lock-free single-producer/single-consumer byte hand-off used by
    /// <see cref="TcpTransportConnection"/> in place of the output <c>System.IO.Pipelines.Pipe</c>.
    ///
    /// The output Pipe took a single <c>_sync</c> Monitor lock on BOTH ends (the actor-dispatcher
    /// producer that writes/flushes and the write-pump consumer that reads/advances). Under load that
    /// contended lock dominated CPU (~70% of non-idle work on a 24-thread box). This replacement keeps
    /// the producer and consumer off any shared Monitor:
    ///
    /// * Producer (actor thread): accumulates writes into a pooled fill buffer (<see cref="Write"/>),
    ///   and on <see cref="Flush"/> publishes the filled buffer to a lock-free
    ///   <see cref="ConcurrentQueue{T}"/> and wakes the consumer.
    /// * Consumer (write pump): drains segments (<see cref="TryDequeue"/>), writes them to the socket,
    ///   returns the buffers to the pool, and parks on a lock-free
    ///   <see cref="ManualResetValueTaskSourceCore{TResult}"/> when empty (<see cref="WaitAsync"/>).
    ///
    /// SPSC discipline: ALL of <see cref="Write"/>/<see cref="Flush"/>/<see cref="CompleteWriter"/>/
    /// <see cref="Abort"/> are called only on the single producer (actor) thread; the dequeue/return/wait
    /// methods are called only on the single consumer (write-pump) thread. The wake handshake and the
    /// queue itself are the only cross-thread state and are lock-free.
    /// </summary>
    internal sealed class LockFreeOutputQueue : IValueTaskSource<bool>
    {
        private const int MinFillSize = 4096;

        // Cross-thread: lock-free queue of filled buffers (rented from ArrayPool) handed producer -> consumer.
        private readonly ConcurrentQueue<ArraySegment<byte>> _segments = new();

        // Cross-thread wake handshake. SINGLE consumer only.
        private ManualResetValueTaskSourceCore<bool> _wake;
        private int _waiting;                   // 1 = consumer parked awaiting _wake; 0 = not parked (Volatile/Interlocked only)
        private volatile bool _writerCompleted; // producer finished; consumer drains remaining then exits
        private volatile bool _aborted;         // hard abort; consumer exits WITHOUT flushing
        private volatile bool _consumerExited;  // consumer loop has exited; producer ops become no-ops

        // Producer-only state (actor thread). Not shared.
        private byte[]? _fill;
        private int _fillPos;

        public LockFreeOutputQueue()
        {
            // Never run the awaiting continuation (the write pump) inline on the producer's SetResult call —
            // that would hijack the actor dispatcher thread to run socket I/O.
            _wake.RunContinuationsAsynchronously = true;
        }

        /* ============================ producer side (actor thread) ============================ */

        /// <summary>Append bytes to the current (unflushed) fill buffer. Copies, mirroring Pipe.Writer.Write.</summary>
        public void Write(ReadOnlySpan<byte> data)
        {
            if (_consumerExited || data.IsEmpty)
                return;

            EnsureFillCapacity(data.Length);
            data.CopyTo(_fill!.AsSpan(_fillPos));
            _fillPos += data.Length;
        }

        /// <summary>Publish the accumulated fill buffer (if any) to the consumer and wake it.</summary>
        public void Flush()
        {
            PublishFill();
            Signal();
        }

        /// <summary>
        /// Graceful completion: flush any pending bytes, mark completed, wake the consumer so it drains
        /// the remainder and exits. Mirrors <c>PipeWriter.CompleteAsync()</c> (which flushes buffered data).
        /// </summary>
        public void CompleteWriter()
        {
            PublishFill();
            _writerCompleted = true;
            Signal();
        }

        /// <summary>
        /// Hard abort: discard any unflushed producer bytes (no flush) and wake the consumer so it exits
        /// immediately without writing queued data. Mirrors abort tear-down.
        /// </summary>
        public void Abort()
        {
            _aborted = true;
            if (_fill != null)
            {
                ArrayPool<byte>.Shared.Return(_fill);
                _fill = null;
                _fillPos = 0;
            }
            Signal();
        }

        private void EnsureFillCapacity(int needed)
        {
            if (_fill == null)
            {
                _fill = ArrayPool<byte>.Shared.Rent(Math.Max(MinFillSize, needed));
                _fillPos = 0;
            }
            else if (_fillPos + needed > _fill.Length)
            {
                var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_fill.Length * 2, _fillPos + needed));
                Array.Copy(_fill, bigger, _fillPos);
                ArrayPool<byte>.Shared.Return(_fill);
                _fill = bigger;
            }
        }

        private void PublishFill()
        {
            if (_fill == null || _fillPos == 0)
                return;

            if (_consumerExited)
            {
                // Consumer is gone; don't leak the rented buffer.
                ArrayPool<byte>.Shared.Return(_fill);
            }
            else
            {
                _segments.Enqueue(new ArraySegment<byte>(_fill, 0, _fillPos));
            }

            _fill = null;
            _fillPos = 0;
        }

        /// <summary>Wake a parked consumer, if any. Lock-free; no-op if the consumer is actively draining.</summary>
        private void Signal()
        {
            // Only complete the wake source if the consumer actually parked on it (claimed via 1 -> 0).
            // If it is mid-drain (_waiting == 0) it will observe the new queue state / flags on its next loop.
            if (Interlocked.Exchange(ref _waiting, 0) == 1)
                _wake.SetResult(true);
        }

        /* ============================ consumer side (write pump) ============================ */

        public bool IsAborted => _aborted;

        /// <summary>True once the producer completed AND every queued segment has been drained.</summary>
        public bool IsCompletedAndDrained => _writerCompleted && _segments.IsEmpty;

        public bool TryDequeue(out ArraySegment<byte> segment) => _segments.TryDequeue(out segment);

        public void ReturnBuffer(ArraySegment<byte> segment)
        {
            if (segment.Array != null)
                ArrayPool<byte>.Shared.Return(segment.Array);
        }

        /// <summary>
        /// Park until the producer signals (new data, completion, or abort). Returns a synchronously-completed
        /// ValueTask if work is already available, so the pump never sleeps with data pending (no lost wakeup).
        /// </summary>
        public ValueTask<bool> WaitAsync()
        {
            // Reset BEFORE arming so any SetResult races against the version we are about to hand out.
            _wake.Reset();
            Volatile.Write(ref _waiting, 1);

            // Re-check after arming: if work appeared between the caller's last drain and arming, unarm and
            // return synchronously rather than risk parking with work pending.
            if (!_segments.IsEmpty || _writerCompleted || _aborted)
            {
                Volatile.Write(ref _waiting, 0);
                return new ValueTask<bool>(true);
            }

            return new ValueTask<bool>(this, _wake.Version);
        }

        /// <summary>
        /// Called by the consumer when its loop exits (graceful, abort, or error). Drains and returns any
        /// still-queued buffers to the pool so a faulted/aborted connection does not leak rented arrays.
        /// </summary>
        public void OnConsumerExit()
        {
            _consumerExited = true;
            while (_segments.TryDequeue(out var seg))
            {
                if (seg.Array != null)
                    ArrayPool<byte>.Shared.Return(seg.Array);
            }
        }

        /* ============================ IValueTaskSource<bool> ============================ */

        public bool GetResult(short token) => _wake.GetResult(token);
        public ValueTaskSourceStatus GetStatus(short token) => _wake.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _wake.OnCompleted(continuation, state, token, flags);
    }
}
