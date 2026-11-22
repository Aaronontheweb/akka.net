//-----------------------------------------------------------------------
// <copyright file="SystemMessageAckerStage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
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
    /// resending its whole unacknowledged window (immediately, on receiving the Nack).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Expectation = per-connection memory + shared state.</b> This stage keeps its per-connection
    /// expected-seq map exactly as before, so within one connection it never forgets, whatever handshakes do.
    /// In addition the <see cref="AssociationRegistry"/> keeps a shared expectation per sender
    /// <see cref="UniqueAddress"/> (address and uid, as in the reference implementation; via
    /// <see cref="IInboundContext.GetInboundSequenceEntry"/>), because the sender's
    /// <see cref="SystemMessageDeliveryState"/> survives reconnects while a new stage starts empty. The
    /// effective expectation is the larger of the two, so a NEW connection from the SAME sender resumes where the
    /// previous one stopped instead of at 1. The shared entry is created by the first admitted message of a sender
    /// that has completed a handshake and is never removed (one entry per distinct peer incarnation that delivered
    /// a system message, for the lifetime of the ActorSystem, as in the reference implementation), so a sender that
    /// is superseded and seen again still resumes. A sender without a completed handshake and without an entry
    /// uses the per-connection memory only.
    /// </para>
    ///
    /// <para>
    /// <b>Per-sender serialization.</b> A stage handles every system message of a sender with a shared entry while
    /// holding that entry's monitor (see <see cref="InboundSequenceEntry"/>): it reads the effective expectation,
    /// answers a duplicate or gap, and for the expected sequence number sends the Ack, pushes, and only then
    /// advances both its own memory and the shared value. A second connection of the same sender waits for the
    /// first and then sees either the advanced value (a duplicate, re-Acked) or the unchanged one (accepted and
    /// delivered), exactly as a single connection would; if sending or pushing throws, nothing was advanced and the
    /// stage fails as before. Two connections of one sender never deliver a sequence number twice. Strict order is
    /// guaranteed per connection, not across two simultaneously open connections of one sender (the downstream
    /// stages run after the handler returns, outside the monitor). Known limit: a sender that resets its numbering
    /// while the receiver's entry for it survives (for example after a stale handshake of an earlier incarnation of
    /// the peer) has a NEW seq 1 treated as a replay; without a numbering epoch on the wire that is
    /// indistinguishable from a replay.
    /// <see cref="ClearSystemMessageDelivery"/> deliberately never crosses the wire in this
    /// implementation (see its own type docs).
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

            /// <summary>Next expected seq number, per sender uid. Absent == 1 (nothing seen yet).</summary>
            private readonly Dictionary<long, long> _expectedByOriginUid = new();

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
                // Per-sender serialization: while this stage handles a frame of a sender, no other connection of
                // that sender decides on it, so the outcome is exactly what a single connection would see.
                // The entry is null for a sender that uses per-connection memory only (dev behavior).
                var entry = _stage.Context.GetInboundSequenceEntry(originUid, sme.AckReplyTo);
                if (entry == null)
                {
                    HandleSerialized(originUid, sme, null);
                    return;
                }

                lock (entry)
                    HandleSerialized(originUid, sme, entry);
            }

            private void HandleSerialized(long originUid, SystemMessageEnvelope sme, InboundSequenceEntry? entry)
            {
                var local = _expectedByOriginUid.TryGetValue(originUid, out var e) ? e : 1L;
                var expected = entry == null ? local : Math.Max(local, entry.Next);

                // The effective expectation is committed state only, and from now on also this connection's own
                // memory, so it still knows the prefix however the shared state changes later.
                if (expected > local)
                    _expectedByOriginUid[originUid] = expected;

                if (sme.SeqNo == expected)
                {
                    // If sending the Ack or pushing throws, nothing has been advanced and the exception
                    // propagates (the stage fails, as before).
                    SendAck(sme.AckReplyTo, expected);

                    // Re-emitted as an ORDINARY-shaped envelope -- IsControl computes to false since
                    // the inner message is not an IArteryControlMessage, so it flows straight through
                    // ArteryRemoting.DispatchInbound's existing ordinary-message path unchanged
                    // (that path is updated to dispatch via SendSystemMessage, not Tell, when the
                    // resolved Message is an ISystemMessage).
                    Push(_stage.Out, new InboundEnvelope(sme.Message, null, sme.RecipientPath, originUid, SerializerId: 0, Manifest: string.Empty));

                    // Acked and pushed: only now does the sequence number count as delivered.
                    _expectedByOriginUid[originUid] = expected + 1;
                    if (entry != null)
                        entry.Next = expected + 1;
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
