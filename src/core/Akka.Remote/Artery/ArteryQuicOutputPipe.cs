// -----------------------------------------------------------------------
// <copyright file="ArteryQuicOutputPipe.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// Accumulates ordinary frames under pipe backpressure while one persistent
    /// send loop submits bounded native writes in their original byte order.
    /// </summary>
    internal sealed class ArteryQuicOutputPipe
    {
        private readonly Pipe _pipe;
        private readonly Stream _stream;
        private readonly Action<int> _written;
        private readonly CancellationToken _stop;
        private readonly CancellationToken _channelCancellation;
        private bool _completed;
        private long _acceptedBytes;
        private long _sentBytes;
        private const int MaximumWriteSize = 16 * 1024;

        internal ArteryQuicOutputPipe(Stream stream, Action<int> written, CancellationToken stop,
            CancellationToken channelCancellation, MemoryPool<byte>? pool = null)
        {
            _stream = stream;
            _written = written;
            _stop = stop;
            _channelCancellation = channelCancellation;
            // Replace the preceding lane batcher, rather than adding another
            // independent accumulator. An atomic copy may overshoot by one chunk.
            _pipe = new Pipe(new PipeOptions(pool: pool, pauseWriterThreshold: 16 * 1024,
                resumeWriterThreshold: 8 * 1024, minimumSegmentSize: MaximumWriteSize,
                useSynchronizationContext: false));
            Completion = RunAsync();
        }

        internal Task Completion { get; }
        // Read only after the producer and send loop have both settled.
        internal long UnsentBytes => _acceptedBytes - _sentBytes;

        internal ValueTask<FlushResult> WriteAsync(ReadOnlySequence<byte> bytes)
        {
            _acceptedBytes += bytes.Length;
            if (bytes.IsEmpty)
                return new ValueTask<FlushResult>(new FlushResult(false, false));
            if (bytes.Length > MaximumWriteSize)
                return WriteLargeAsync(bytes);
            var length = checked((int)bytes.Length);
            bytes.CopyTo(_pipe.Writer.GetSpan(length));
            _pipe.Writer.Advance(length);
            return _pipe.Writer.FlushAsync(_stop);
        }

        private async ValueTask<FlushResult> WriteLargeAsync(ReadOnlySequence<byte> bytes)
        {
            var result = new FlushResult(false, false);
            while (!bytes.IsEmpty)
            {
                var length = (int)Math.Min(bytes.Length, MaximumWriteSize);
                bytes.Slice(0, length).CopyTo(_pipe.Writer.GetSpan(length));
                _pipe.Writer.Advance(length);
                result = await _pipe.Writer.FlushAsync(_stop).ConfigureAwait(false);
                if (result.IsCanceled || result.IsCompleted)
                    throw new IOException("The QUIC output pipe closed while copying a frame.");
                bytes = bytes.Slice(length);
            }
            return result;
        }

        internal void Complete()
        {
            if (_completed)
                return;
            _completed = true;
            _pipe.Writer.Complete();
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    var result = await _pipe.Reader.ReadAsync(_stop).ConfigureAwait(false);
                    var bytes = result.Buffer;
                    using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop, _channelCancellation))
                    {
                        foreach (var segment in bytes)
                        {
                            var remaining = segment;
                            while (!remaining.IsEmpty)
                            {
                                var length = Math.Min(remaining.Length, MaximumWriteSize);
                                await _stream.WriteAsync(remaining.Slice(0, length), lifetime.Token).ConfigureAwait(false);
                                _sentBytes += length;
                                _written(length);
                                remaining = remaining.Slice(length);
                            }
                        }
                    }
                    // Advance only after the native write settles, even if stop
                    // was requested while native code retained the submitted memory.
                    _pipe.Reader.AdvanceTo(bytes.End);
                    if (result.IsCompleted)
                        break;
                }
            }
            finally
            {
                await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            }
        }
    }
}
