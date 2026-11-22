//-----------------------------------------------------------------------
// <copyright file="InboundReadAheadStage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using Akka.Streams;
using Akka.Streams.Stage;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// Byte-bounded read-ahead in front of <see cref="ArteryInboundProcessingStage"/> on an
    /// accepted connection (<c>akka.remote.artery.advanced.inbound-read-batching</c>).
    ///
    /// <para>
    /// The streams TCP stage runs the connection actor in pull mode: it only sends
    /// <c>Tcp.ResumeReading</c> to the connection actor when its outlet is pulled, and the
    /// connection actor only then reads its input pipe and replies with one <c>Tcp.Received</c>.
    /// Without this stage that pull is issued by the processing stage only after it has decoded,
    /// deserialized and dispatched every frame of the previous chunk, so the
    /// ResumeReading -> pipe read -> Received round trip through two actor mailboxes is serialized
    /// with that work.
    /// </para>
    ///
    /// <para>
    /// This stage re-pulls upstream as soon as a chunk arrives, as long as fewer than
    /// <see cref="MaxBufferedBytes"/> bytes are buffered, so the next read is already in flight
    /// while the current chunk is processed. When downstream pulls, every buffered chunk is
    /// handed over as ONE <see cref="ReadOnlySequence{T}"/> (chained segments over the same
    /// memory, no copy), so the processing stage frames everything available in one push.
    /// </para>
    ///
    /// <para>
    /// Ordering: chunks are emitted strictly in arrival order. Back-pressure: once the buffer
    /// reaches <see cref="MaxBufferedBytes"/> the stage stops pulling, which stops
    /// ResumeReading; buffered bytes therefore never exceed the bound plus one received chunk.
    /// Completion: upstream completion is deferred until the buffer has been handed downstream;
    /// upstream failure fails the stage immediately (same as without the stage).
    /// </para>
    /// </summary>
    internal sealed class InboundReadAheadStage : GraphStage<FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>>>
    {
        public InboundReadAheadStage(long maxBufferedBytes)
        {
            if (maxBufferedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes), maxBufferedBytes, "Must be > 0.");

            MaxBufferedBytes = maxBufferedBytes;
            Shape = new FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>>(In, Out);
        }

        /// <summary>Upstream is no longer pulled once at least this many bytes are buffered.</summary>
        public long MaxBufferedBytes { get; }

        public Inlet<ReadOnlySequence<byte>> In { get; } = new("InboundReadAhead.in");
        public Outlet<ReadOnlySequence<byte>> Out { get; } = new("InboundReadAhead.out");

        public override FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>> Shape { get; }

        protected override Attributes InitialAttributes { get; } = Attributes.CreateName("inboundReadAhead");

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

        private sealed class Logic : InAndOutGraphStageLogic
        {
            private readonly InboundReadAheadStage _stage;
            private readonly Queue<ReadOnlySequence<byte>> _chunks = new();
            private long _bufferedBytes;

            public Logic(InboundReadAheadStage stage) : base(stage.Shape)
            {
                _stage = stage;
                SetHandler(stage.In, this);
                SetHandler(stage.Out, this);
            }

            public override void PreStart() => Pull(_stage.In);

            public override void OnPush()
            {
                var chunk = Grab(_stage.In);
                if (!chunk.IsEmpty)
                {
                    _chunks.Enqueue(chunk);
                    _bufferedBytes += chunk.Length;
                }

                if (IsAvailable(_stage.Out))
                    Flush();

                PullIfBelowBound();
            }

            public override void OnUpstreamFinish()
            {
                if (_chunks.Count == 0)
                    CompleteStage();
                // else: OnPull hands over the remainder, then completes.
            }

            public override void OnPull()
            {
                if (_chunks.Count > 0)
                    Flush();

                if (IsClosed(_stage.In))
                {
                    if (_chunks.Count == 0)
                        CompleteStage();
                    return;
                }

                PullIfBelowBound();
            }

            private void PullIfBelowBound()
            {
                if (_bufferedBytes < _stage.MaxBufferedBytes && !IsClosed(_stage.In) && !HasBeenPulled(_stage.In))
                    Pull(_stage.In);
            }

            /// <summary>Pushes every buffered chunk downstream as one sequence. Caller ensures Out is available and the buffer is non-empty.</summary>
            private void Flush()
            {
                if (_chunks.Count == 0)
                    return;

                var batch = _chunks.Count == 1 ? _chunks.Dequeue() : Concat();
                _bufferedBytes = 0;
                Push(_stage.Out, batch);
            }

            private ReadOnlySequence<byte> Concat()
            {
                ChunkSegment? head = null;
                ChunkSegment? tail = null;
                while (_chunks.Count > 0)
                {
                    foreach (var memory in _chunks.Dequeue())
                    {
                        if (memory.IsEmpty)
                            continue;

                        var segment = new ChunkSegment(memory);
                        if (tail is null)
                            head = segment;
                        else
                            tail.Chain(segment);
                        tail = segment;
                    }
                }

                return head is null
                    ? ReadOnlySequence<byte>.Empty
                    : new ReadOnlySequence<byte>(head, 0, tail!, tail!.Memory.Length);
            }
        }

        private sealed class ChunkSegment : ReadOnlySequenceSegment<byte>
        {
            public ChunkSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

            public void Chain(ChunkSegment next)
            {
                next.RunningIndex = RunningIndex + Memory.Length;
                Next = next;
            }
        }
    }
}
