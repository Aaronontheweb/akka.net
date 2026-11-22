//-----------------------------------------------------------------------
// <copyright file="OutboundWriteCoalescingStage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using Akka.IO;
using Akka.Streams;
using Akka.Streams.Stage;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API. Coalesces already-encoded outbound frames into one contiguous pooled buffer
    /// while downstream (the restart-wrapped TCP connection flow) has not pulled, and emits that
    /// buffer as a single element on the next pull. Enabled by
    /// <c>akka.remote.artery.advanced.outbound-write-coalescing</c>.
    ///
    /// <para>
    /// The connection flow is wrapped in <c>RestartFlow</c>, whose sub-stream boundary hands
    /// elements across one at a time through async callbacks. While one element is crossing,
    /// this stage keeps pulling upstream (the MergeHub / encode stage, fused with it) and copies
    /// every frame that arrives into the current buffer, so the next crossing carries everything
    /// that queued up meanwhile instead of a single frame.
    /// </para>
    ///
    /// <para>
    /// Rules:
    /// <list type="bullet">
    /// <item>Nothing buffered and downstream already pulled: the frame is pushed through unchanged
    /// (no copy, no delay). There are no timers.</item>
    /// <item>Frames are copied in arrival order; the emitted buffer is their exact concatenation.</item>
    /// <item>At most <c>maxBytes</c> are buffered. A frame that does not fit is held (one frame) and
    /// upstream is not pulled again until the buffer is emitted -- that is the back-pressure bound.
    /// A held frame larger than <c>maxBytes</c> is emitted on its own, unchanged.</item>
    /// <item>On upstream completion the buffer and any held frame are flushed before completing.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal sealed class OutboundWriteCoalescingStage : GraphStage<FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>>>
    {
        private readonly int _maxBytes;
        private readonly Action<long>? _onDropped;
        private readonly FlowCounterBlock? _flow;

        /// <param name="maxBytes">Upper bound on the bytes copied into one coalesced element.</param>
        /// <param name="onDropped">
        /// Invoked once from <c>PostStop</c> with the number of buffered/held bytes discarded when the
        /// stage stops before it could emit them (same contract as <see cref="LaneWriteBatchStage"/>).
        /// </param>
        /// <param name="flow">
        /// Flow-diagnostics write counters, or <see langword="null"/> (the default) when disabled. When set, each
        /// element emitted downstream is recorded as one write with its frame count and byte length.
        /// </param>
        public OutboundWriteCoalescingStage(int maxBytes, Action<long>? onDropped = null, FlowCounterBlock? flow = null)
        {
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _maxBytes = maxBytes;
            _onDropped = onDropped;
            _flow = flow;
            Shape = new FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>>(In, Out);
        }

        private Inlet<ReadOnlySequence<byte>> In { get; } = new("OutboundWriteCoalescing.in");
        private Outlet<ReadOnlySequence<byte>> Out { get; } = new("OutboundWriteCoalescing.out");

        public override FlowShape<ReadOnlySequence<byte>, ReadOnlySequence<byte>> Shape { get; }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

        /// <summary>
        /// Pooled buffer handed downstream inside an <see cref="OwnedSequenceSegment"/>. Akka.IO's
        /// <c>TcpConnection</c> disposes it once the bytes are copied into the socket pipe.
        /// </summary>
        private sealed class PooledBuffer : IMemoryOwner<byte>
        {
            private byte[]? _array;

            public PooledBuffer(int size) => _array = ArrayPool<byte>.Shared.Rent(size);

            public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

            public void Dispose()
            {
                var array = _array;
                _array = null;
                if (array is not null)
                    ArrayPool<byte>.Shared.Return(array);
            }
        }

        private sealed class Logic : GraphStageLogic, IInHandler, IOutHandler
        {
            private readonly OutboundWriteCoalescingStage _stage;
            private PooledBuffer? _buffer;
            private int _length;
            private ReadOnlySequence<byte> _held;
            private bool _hasHeld;
            private bool _upstreamFinished;
            private int _bufferedFrames; // frames copied into _buffer; only used when flow diagnostics are on

            public Logic(OutboundWriteCoalescingStage stage) : base(stage.Shape)
            {
                _stage = stage;
                SetHandler(stage.In, this);
                SetHandler(stage.Out, this);
            }

            public override void PreStart() => Pull(_stage.In);

            public void OnPush()
            {
                var frame = Grab(_stage.In);

                if (_length == 0 && IsAvailable(_stage.Out))
                {
                    // Idle path: downstream is waiting and nothing is queued -- pass through as-is.
                    if (_stage._flow is { } idleFlow)
                        ArteryFlowDiagnostics.RecordWrite(idleFlow, 1, frame.Length);
                    Push(_stage.Out, frame);
                }
                else if (_length + frame.Length <= _stage._maxBytes)
                {
                    Append(frame);
                    _stage._flow?.Inc(ArteryFlowDiagnostics.W_WaitDemand); // a frame arrived while downstream had no demand
                }
                else
                {
                    // Does not fit: hold it and stop pulling until the buffer is emitted.
                    _held = frame;
                    _hasHeld = true;
                    _stage._flow?.Inc(ArteryFlowDiagnostics.W_WaitDemand);
                }

                PullIfNeeded();
            }

            public void OnPull()
            {
                if (_length > 0)
                {
                    EmitBuffer();
                    if (_hasHeld && _held.Length <= _stage._maxBytes)
                    {
                        var held = _held;
                        _held = default;
                        _hasHeld = false;
                        Append(held);
                    }
                }
                else if (_hasHeld)
                {
                    var held = _held;
                    _held = default;
                    _hasHeld = false;
                    if (_stage._flow is { } heldFlow)
                        ArteryFlowDiagnostics.RecordWrite(heldFlow, 1, held.Length);
                    Push(_stage.Out, held);
                }

                if (_upstreamFinished)
                {
                    if (_length == 0 && !_hasHeld)
                        CompleteStage();
                }
                else
                    PullIfNeeded();
            }

            public void OnUpstreamFinish()
            {
                _upstreamFinished = true;
                if (_length == 0 && !_hasHeld)
                    CompleteStage();
                // else: OnPull drains the buffer, then the held frame, then completes.
            }

            public void OnUpstreamFailure(Exception e) => FailStage(e);

            public void OnDownstreamFinish(Exception cause) => InternalOnDownstreamFinish(cause);

            public override void PostStop()
            {
                long lost = _length + (_hasHeld ? _held.Length : 0);

                _buffer?.Dispose();
                _buffer = null;
                _length = 0;

                if (_hasHeld)
                {
                    _held.DisposeOwnedSegments();
                    _held = default;
                    _hasHeld = false;
                }

                if (lost > 0)
                    _stage._onDropped?.Invoke(lost);
            }

            private void Append(ReadOnlySequence<byte> frame)
            {
                _buffer ??= new PooledBuffer(_stage._maxBytes);
                frame.CopyTo(_buffer.Memory.Span.Slice(_length));
                _length += (int)frame.Length;
                _bufferedFrames++;

                // The bytes now live in our buffer; return the encode stage's pooled frame.
                frame.DisposeOwnedSegments();
            }

            private void EmitBuffer()
            {
                var buffer = _buffer!;
                var length = _length;
                _buffer = null;
                _length = 0;

                if (_stage._flow is { } flow)
                {
                    ArteryFlowDiagnostics.RecordWrite(flow, _bufferedFrames, length);
                    if (!_hasHeld)
                        flow.Inc(ArteryFlowDiagnostics.W_PartialPush); // flushed without a non-fitting frame waiting
                }

                _bufferedFrames = 0;

                // Ownership moves downstream with the element.
                Push(_stage.Out, OwnedSequenceSegment.Create(buffer, length));
            }

            private void PullIfNeeded()
            {
                if (!_upstreamFinished && !_hasHeld && !HasBeenPulled(_stage.In) && !IsClosed(_stage.In))
                    Pull(_stage.In);
            }
        }
    }
}
