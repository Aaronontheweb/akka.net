//-----------------------------------------------------------------------
// <copyright file="SystemMessageAckerStage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using Akka.Streams;
using Akka.Streams.Stage;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// Inbound half of reliable system-message delivery (design.md gate G3, "Reliable
    /// system-message delivery"). A <c>GraphStage&lt;FlowShape&lt;IInboundEnvelope, IInboundEnvelope&gt;&gt;</c>
    /// composed on EVERY accepted (inbound) Artery connection, positioned right after
    /// <see cref="InboundHandshakeStage"/> and before the final dispatch sink (mirrors the reference
    /// pipeline in design.md's type-level "Inbound pipeline" diagram: <c>... InboundHandshake ->
    /// InboundQuarantineCheck -> [control only: SystemMessageAcker -- dedup + ACK] -> messageDispatcherSink</c>).
    ///
    /// <para>
    /// <b>Per-sender-uid expected seq, NO inbound reorder buffer (design.md, verified against
    /// Pekko's deliberately simpler Artery protocol -- see <see cref="SystemMessageEnvelope"/>'s
    /// type-level remarks).</b> For each inbound <see cref="SystemMessageEnvelope"/>:
    /// <list type="bullet">
    /// <item><description><c>seq == expected</c>: DELIVER downstream (re-emitted as a plain,
    /// non-control <see cref="IInboundEnvelope"/> wrapping the inner system message, so the existing
    /// dispatch sink's ordinary-message path handles it -- see <c>ArteryRemoting.DispatchInbound</c>'s
    /// <c>ISystemMessage</c> check, which calls <c>SendSystemMessage</c> instead of <c>Tell</c>),
    /// advance <c>expected</c>, reply <see cref="Ack"/>.</description></item>
    /// <item><description><c>seq &lt; expected</c>: duplicate -- drop, re-reply <see cref="Ack"/> for
    /// <c>expected - 1</c> (covers a lost Ack: the sender's resend timer will keep retrying until it
    /// sees this).</description></item>
    /// <item><description><c>seq &gt; expected</c>: gap -- drop, reply <see cref="Nack"/> for
    /// <c>expected - 1</c>. NO buffering of the out-of-order frame: the SENDER restores order by
    /// resending its whole unacknowledged window (immediately on the first Nack that acknowledges nothing and
    /// on any Nack that makes progress; repeated no-progress Nacks wait for the sender's resend timer).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>State lifetime.</b> The expected-seq state is NOT per connection: the sender's
    /// <see cref="SystemMessageDeliveryState"/> survives reconnects, so a fresh stage per accepted
    /// connection must resume at the same expectation. It lives in the <see cref="AssociationRegistry"/>
    /// (via <see cref="IInboundContext.AdvanceInboundSystemMessageSeq"/>), one entry per association for its
    /// CURRENT origin uid only. When a different uid replaces it, the entry is removed and the old uid is
    /// retired in <see cref="AssociationState.RetiredUids"/>: it can never become current again, so a late
    /// handshake of a superseded incarnation cannot revive it. Frames from any uid that is not the current,
    /// non-quarantined uid are dropped without Ack or Nack; the sender retains and resends them.
    /// Overlapping connections: exactly-once delivery is guaranteed, and so is strict order per connection,
    /// but not across two simultaneously open connections of one origin (see
    /// <see cref="AssociationRegistry.AdvanceInboundSystemMessageSeq"/>).
    /// <see cref="ClearSystemMessageDelivery"/> deliberately never crosses the wire.
    /// </para>
    /// </summary>
    internal sealed class SystemMessageAckerStage : GraphStage<FlowShape<IInboundEnvelope, IInboundEnvelope>>
    {
        public SystemMessageAckerStage(IInboundContext context)
        {
            Context = context;
            Shape = new FlowShape<IInboundEnvelope, IInboundEnvelope>(In, Out);
        }

        public IInboundContext Context { get; }

        public Inlet<IInboundEnvelope> In { get; } = new("SystemMessageAcker.in");
        public Outlet<IInboundEnvelope> Out { get; } = new("SystemMessageAcker.out");

        public override FlowShape<IInboundEnvelope, IInboundEnvelope> Shape { get; }

        protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

        private sealed class Logic : GraphStageLogic, IInHandler, IOutHandler
        {
            private readonly SystemMessageAckerStage _stage;

            public Logic(SystemMessageAckerStage stage) : base(stage.Shape)
            {
                _stage = stage;
                SetHandler(stage.In, this);
                SetHandler(stage.Out, this);
            }

            public void OnPush()
            {
                var envelope = Grab(_stage.In);

                if (envelope.Message is SystemMessageEnvelope sme)
                {
                    HandleSystemMessageEnvelope(envelope.OriginUid, sme);
                    return;
                }

                // Pass-through: ordinary messages, Ack/Nack replies (destined for this system's OWN
                // outbound SystemMessageDeliveryStage via ArteryRemoting's control-subscriber
                // broadcast), heartbeat, quarantine notices, etc.
                Push(_stage.Out, envelope);
            }

            public void OnPull() => Pull(_stage.In);

            public void OnUpstreamFinish() => CompleteStage();

            public void OnUpstreamFailure(System.Exception e) => FailStage(e);

            public void OnDownstreamFinish(System.Exception cause) => InternalOnDownstreamFinish(cause);

            private void HandleSystemMessageEnvelope(long originUid, SystemMessageEnvelope sme)
            {
                // Shared, connection-independent state; advanced atomically when seq == expected.
                var expected = _stage.Context.AdvanceInboundSystemMessageSeq(originUid, sme.SeqNo);

                if (expected == 0L)
                {
                    // Unknown, superseded or quarantined origin: nothing to acknowledge.
                    Pull(_stage.In);
                    return;
                }

                if (sme.SeqNo == expected)
                {
                    SendAck(sme.AckReplyTo, expected);

                    // Re-emitted as an ORDINARY-shaped envelope -- IsControl computes to false since
                    // the inner message is not an IArteryControlMessage, so it flows straight through
                    // ArteryRemoting.DispatchInbound's existing ordinary-message path unchanged
                    // (that path is updated to dispatch via SendSystemMessage, not Tell, when the
                    // resolved Message is an ISystemMessage).
                    Push(_stage.Out, new InboundEnvelope(sme.Message, null, sme.RecipientPath, originUid, SerializerId: 0, Manifest: string.Empty));
                    return;
                }

                if (sme.SeqNo < expected)
                {
                    // Duplicate -- covers a lost Ack: re-Ack so the sender's resend timer can
                    // eventually observe it and shrink its buffer.
                    SendAck(sme.AckReplyTo, expected - 1);
                }
                else
                {
                    // Gap -- no inbound reorder buffer; the sender restores order by resending its
                    // whole unacknowledged window on Nack.
                    SendNack(sme.AckReplyTo, expected - 1);
                }

                Pull(_stage.In);
            }

            private void SendAck(UniqueAddress replyTo, long seqNo) =>
                _stage.Context.SendControl(replyTo.Address, new Ack(seqNo, _stage.Context.LocalAddress));

            private void SendNack(UniqueAddress replyTo, long seqNo) =>
                _stage.Context.SendControl(replyTo.Address, new Nack(seqNo, _stage.Context.LocalAddress));
        }
    }
}
