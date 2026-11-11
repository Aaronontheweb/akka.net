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
    /// Reuses pipe storage for one outstanding bounded batch. The graph supplies the
    /// next batch only after the send loop has consumed the preceding one.
    /// </summary>
    internal sealed class ArteryQuicOutputPipe
    {
        private readonly Pipe _pipe;
        private readonly Stream _stream;
        private readonly Action<int> _written;
        private readonly CancellationToken _stop;
        private readonly CancellationToken _channelCancellation;
        private bool _completed;

        internal ArteryQuicOutputPipe(Stream stream, Action<int> written, CancellationToken stop,
            CancellationToken channelCancellation, MemoryPool<byte>? pool = null)
        {
            _stream = stream;
            _written = written;
            _stop = stop;
            _channelCancellation = channelCancellation;
            // Demand is already bounded to one input by the graph. Disabling the
            // pipe's separate pause threshold preserves that existing byte budget.
            _pipe = new Pipe(new PipeOptions(pool: pool, pauseWriterThreshold: 0,
                resumeWriterThreshold: 0, minimumSegmentSize: 16 * 1024,
                useSynchronizationContext: false));
            Completion = RunAsync();
        }

        internal Task Completion { get; }

        internal void Write(ReadOnlySequence<byte> bytes)
        {
            var length = checked((int)bytes.Length);
            bytes.CopyTo(_pipe.Writer.GetSpan(length));
            _pipe.Writer.Advance(length);
            // With no pause threshold a flush cannot wait for consumption. Observe
            // its result once, including a reader that has already failed.
            var result = _pipe.Writer.FlushAsync(_stop).GetAwaiter().GetResult();
            if (result.IsCanceled || result.IsCompleted)
                throw new IOException("The QUIC output pipe is closed.");
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
                    var length = checked((int)bytes.Length);
                    using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop, _channelCancellation))
                    {
                        foreach (var segment in bytes)
                        {
                            if (!segment.IsEmpty)
                                await _stream.WriteAsync(segment, lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    // Advance only after the native write settles, even if stop
                    // was requested while native code retained the submitted memory.
                    _pipe.Reader.AdvanceTo(bytes.End);
                    if (length != 0)
                        _written(length);
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
