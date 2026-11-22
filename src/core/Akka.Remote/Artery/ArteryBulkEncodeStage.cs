//-----------------------------------------------------------------------
// <copyright file="ArteryBulkEncodeStage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Threading.Channels;
using Akka.IO;
using Akka.Streams;
using Akka.Streams.Stage;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// Drop-in alternative to <see cref="ArteryEncodeStage"/> for the ordinary outbound stream, enabled
    /// with <c>akka.remote.artery.advanced.outbound-bulk-drain</c>. Whenever downstream pulls and an
    /// envelope arrives from upstream, this stage encodes it and then keeps taking whatever else is
    /// ALREADY waiting in the lane's channel (<see cref="ChannelReader{T}.TryRead"/>), encoding each
    /// envelope in turn, and pushes all of those frames as ONE multi-segment sequence.
    ///
    /// <para>
    /// <b>Why here.</b> Upstream of this stage every hop emits one element per pull, and the
    /// downstream side only ever sees what the lane produced since its last pull, so a deep lane
    /// channel was turned into a stream of single-frame elements (see <see cref="LaneWriteBatchStage"/>
    /// and the lane/MergeHub wiring in <see cref="ArteryRemoting"/>). Taking the already-queued envelopes
    /// directly from the channel is the only point at which "already queued" is known exactly.
    /// </para>
    ///
    /// <para>
    /// <b>No added latency.</b> Nothing is awaited and no timer exists: the drain loop stops at the first
    /// empty <c>TryRead</c>, at the byte cap, or at the frame cap. A lone envelope is pushed exactly as
    /// <see cref="ArteryEncodeStage"/> would push it.
    /// </para>
    ///
    /// <para>
    /// <b>Ordering and the handshake stage.</b> The envelope that arrives through the stream has already
    /// passed the lane's <see cref="OutboundHandshakeStage"/>, which only releases traffic once the
    /// handshake is complete and never holds more than one envelope (it does not pull while it holds
    /// one), so every envelope still in the channel is younger than the one just received. The extra
    /// envelopes taken here are therefore in channel (FIFO) order, immediately behind it. The stage is
    /// not used when <c>test-mode</c> is on, because the failure-injection stage sits upstream of the
    /// handshake stage and would be bypassed.
    /// </para>
    ///
    /// <para>
    /// <b>Byte cap.</b> The encoded size is only known after encoding, and an envelope cannot be put
    /// back in the channel. A frame that would push a non-empty batch over the cap is therefore kept
    /// as the seed of the NEXT push (<see cref="Logic"/>'s pending frame), which is emitted on the next
    /// downstream pull before anything else is read. A first frame larger than the cap is pushed
    /// alone. The pending frame is the only encoded data held across handler calls; if the stage stops
    /// while holding one, it is disposed and reported through <c>onDropped</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Encode failure.</b> An encode failure on the envelope received from upstream fails the stage
    /// immediately, exactly as <see cref="ArteryEncodeStage"/> does. A failure on a later, drained envelope
    /// first pushes the frames encoded before it and then fails the stage on the next pull, which is the
    /// order of events one-element-at-a-time encoding produces.
    /// </para>
    /// </summary>
    internal sealed class ArteryBulkEncodeStage : GraphStage<FlowShape<IOutboundEnvelope, ReadOnlySequence<byte>>>
    {
        private readonly ChannelReader<IOutboundEnvelope> _reader;
        private readonly long _maxBytes;
        private readonly int _maxFrames;
        private readonly Action<long>? _onDropped;

        /// <param name="serialization">The sending actor system's serialization extension.</param>
        /// <param name="originUid">The sending system's UID, stamped into every encoded envelope's fixed header.</param>
        /// <param name="reader">The lane channel this stage's upstream source reads from.</param>
        /// <param name="maxBytes">Largest total frame bytes of one pushed element, unless a single frame exceeds it.</param>
        /// <param name="maxFrames">Largest number of frames in one pushed element.</param>
        /// <param name="pool">Encode buffer pool; <see langword="null"/> means <see cref="ArrayPool{T}.Shared"/>.</param>
        /// <param name="onDropped">Invoked from PostStop with the byte count of an encoded frame still held when the stage stops.</param>
        public ArteryBulkEncodeStage(
            Akka.Serialization.Serialization serialization,
            long originUid,
            ChannelReader<IOutboundEnvelope> reader,
            long maxBytes,
            int maxFrames,
            ArrayPool<byte>? pool = null,
            Action<long>? onDropped = null)
        {
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (maxFrames <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFrames));

            Serialization = serialization ?? throw new ArgumentNullException(nameof(serialization));
            OriginUid = originUid;
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _maxBytes = maxBytes;
            _maxFrames = maxFrames;
            Pool = pool;
            _onDropped = onDropped;
            Shape = new FlowShape<IOutboundEnvelope, ReadOnlySequence<byte>>(In, Out);
        }

        public Akka.Serialization.Serialization Serialization { get; }
        public long OriginUid { get; }
        public ArrayPool<byte>? Pool { get; }

        public Inlet<IOutboundEnvelope> In { get; } = new("ArteryBulkEncode.in");
        public Outlet<ReadOnlySequence<byte>> Out { get; } = new("ArteryBulkEncode.out");

        public override FlowShape<IOutboundEnvelope, ReadOnlySequence<byte>> Shape { get; }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

        private sealed class Logic : GraphStageLogic, IInHandler, IOutHandler
        {
            private readonly ArteryBulkEncodeStage _stage;

            // The frame that did not fit behind the previous push; it seeds the next one.
            private OwnedSequenceSegment? _pending;
            private Exception? _deferredFailure;

            // The batch under construction. Only ever lives inside one handler call.
            private OwnedSequenceSegment? _head;
            private OwnedSequenceSegment? _tail;
            private long _batchBytes;
            private int _batchFrames;

            public Logic(ArteryBulkEncodeStage stage) : base(stage.Shape)
            {
                _stage = stage;
                SetHandler(stage.In, this);
                SetHandler(stage.Out, this);
            }

            public void OnPush()
            {
                var elem = Grab(_stage.In);

                // Same failure behaviour as ArteryEncodeStage: an exception here fails the stage.
                // Nothing is held at this point, so there is nothing to clean up.
                var first = Encode(elem);
                Add(first);

                DrainAvailable();
                PushBatch();
            }

            public void OnPull()
            {
                if (_pending is { } pending)
                {
                    _pending = null;
                    Add(pending);
                    DrainAvailable();
                    PushBatch();
                    return;
                }

                if (_deferredFailure is { } failure)
                {
                    _deferredFailure = null;
                    FailStage(failure);
                    return;
                }

                if (IsClosed(_stage.In))
                    CompleteStage();
                else if (!HasBeenPulled(_stage.In))
                    Pull(_stage.In);
            }

            public void OnUpstreamFinish()
            {
                // A held pending frame or failure still has to be delivered on the next pull.
                if (_pending is null && _deferredFailure is null)
                    CompleteStage();
            }

            public void OnUpstreamFailure(Exception e) => FailStage(e);

            public void OnDownstreamFinish(Exception cause) => InternalOnDownstreamFinish(cause);

            public override void PostStop()
            {
                var lost = 0L;
                if (_pending is { } pending)
                {
                    lost += pending.Memory.Length;
                    pending.DisposeOwner();
                    _pending = null;
                }

                // A batch is only ever held within a single handler call; this covers an unexpected
                // exception escaping between Add and PushBatch.
                var segment = _head;
                while (segment is not null)
                {
                    lost += segment.Memory.Length;
                    segment.DisposeOwner();
                    segment = (OwnedSequenceSegment?)segment.Next;
                }

                _head = null;
                _tail = null;

                if (lost > 0)
                    _stage._onDropped?.Invoke(lost);
            }

            private OwnedSequenceSegment Encode(IOutboundEnvelope elem)
            {
                var writer = ArteryEnvelopeCodec.Encode(
                    _stage.Serialization, _stage.OriginUid, elem.SenderPath, elem.RecipientPath, elem.Message, _stage.Pool);

                // Detach moves ownership of the encoded, pooled buffer into the segment, as in ArteryEncodeStage.
                var owner = writer.Detach();
                return new OwnedSequenceSegment(owner.Memory, owner);
            }

            private void Add(OwnedSequenceSegment frame)
            {
                if (_head is null)
                {
                    _head = frame;
                    _tail = frame;
                }
                else
                {
                    // Re-link the frame's memory and owner behind the tail, so the chain's running indexes are right.
                    var owner = frame.DetachOwner()!;
                    _tail = _tail!.Append(frame.Memory, owner);
                }

                _batchBytes += frame.Memory.Length;
                _batchFrames++;
            }

            /// <summary>
            /// Takes envelopes that are already in the lane channel, without waiting, until the channel is
            /// empty or a cap is reached.
            /// </summary>
            private void DrainAvailable()
            {
                var reader = _stage._reader;

                while (_batchFrames < _stage._maxFrames && _batchBytes < _stage._maxBytes && reader.TryRead(out var next))
                {
                    OwnedSequenceSegment frame;
                    try
                    {
                        frame = Encode(next);
                    }
                    catch (Exception e)
                    {
                        // Frames already in the batch were accepted before this envelope; deliver them first.
                        _deferredFailure = e;
                        return;
                    }

                    if (_batchBytes + frame.Memory.Length > _stage._maxBytes)
                    {
                        _pending = frame;
                        return;
                    }

                    Add(frame);
                }
            }

            private void PushBatch()
            {
                var head = _head!;
                var tail = _tail!;
                var sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);

                _head = null;
                _tail = null;
                _batchBytes = 0;
                _batchFrames = 0;

                // Ownership moves downstream before Push, so PostStop never disposes pushed frames.
                Push(_stage.Out, sequence);

                if (IsClosed(_stage.In) && _pending is null && _deferredFailure is null)
                    CompleteStage();
            }
        }
    }
}
