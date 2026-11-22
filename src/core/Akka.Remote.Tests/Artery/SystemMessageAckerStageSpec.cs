//-----------------------------------------------------------------------
// <copyright file="SystemMessageAckerStageSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Dispatch.SysMsg;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit;
using FluentAssertions;
using Xunit;
// Akka.Remote.SysAck/Akka.Remote.SysNack (classic AckedDelivery.cs) are enclosing-namespace types of
// Akka.Remote.Tests.Artery and would otherwise shadow Akka.Remote.Artery.SysAck/SysNack (design.md gate
// G3) in unqualified lookups -- alias to the Artery ones explicitly.
using SysAck = Akka.Remote.Artery.Ack;
using SysNack = Akka.Remote.Artery.Nack;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Stage-level tests for <see cref="SystemMessageAckerStage"/>, the INBOUND half of reliable
    /// system-message delivery (design.md gate G3). Uses <c>TestSource</c>/<c>TestSink</c> probes
    /// to drive/observe the stage directly (mirrors <c>ArteryHandshakeSpec</c>'s patterns) -- no
    /// TCP, no real association transport.
    /// </summary>
    public class SystemMessageAckerStageSpec : AkkaSpec
    {
        public SystemMessageAckerStageSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static UniqueAddress NewLocal() => new(new Address("akka", "local-sys", "local-host", 2551), 111L);

        private static UniqueAddress NewRemotePeer(long uid = 222L) => new(new Address("akka", "remote-sys", "remote-host", 2552), uid);

        private sealed record TestSystemMessage(string Name) : ISystemMessage;

        private static IInboundEnvelope Envelope(SystemMessageEnvelope sme, long originUid) =>
            new InboundEnvelope(sme, null, null, originUid, SerializerId: 0, Manifest: "test-manifest");

        private static SystemMessageEnvelope Sme(long seqNo, UniqueAddress ackReplyTo, string name = "m", string recipientPath = "akka://local-sys@local-host:2551/user/target") =>
            new(new TestSystemMessage(name), seqNo, ackReplyTo, recipientPath);

        /// <summary>A context over a registry in which <paramref name="peer"/> has completed its handshake.</summary>
        private static (AssociationRegistry Registry, AssociationRegistryInboundContext Context, List<(Address To, object Message)> SentControl) NewContext(UniqueAddress peer)
        {
            var sentControl = new List<(Address To, object Message)>();
            var registry = new AssociationRegistry();
            registry.CompleteHandshake(peer.Address, peer);
            return (registry, new AssociationRegistryInboundContext(registry, NewLocal(), (to, msg) => sentControl.Add((to, msg))), sentControl);
        }

        /// <summary>Materializes one acker stage, i.e. what one accepted control connection gets.</summary>
        private (TestPublisher.Probe<IInboundEnvelope> Pub, TestSubscriber.Probe<IInboundEnvelope> Sub) Materialize(IInboundContext context) =>
            this.SourceProbe<IInboundEnvelope>()
                .ViaMaterialized(Flow.FromGraph(new SystemMessageAckerStage(context)), Keep.Left)
                .ToMaterialized(this.SinkProbe<IInboundEnvelope>(), Keep.Both)
                .Run(ActorMaterializer.Create(Sys));

        private (List<(Address To, object Message)> SentControl, TestPublisher.Probe<IInboundEnvelope> Pub, TestSubscriber.Probe<IInboundEnvelope> Sub) BuildHarness()
        {
            var (_, context, sentControl) = NewContext(NewRemotePeer());
            var (pub, sub) = Materialize(context);
            return (sentControl, pub, sub);
        }

        [Fact(DisplayName = "happy-path: seq == expected(1) delivers the inner message (non-control, resolved recipient path) and replies SysAck(1)")]
        public async Task Should_Deliver_On_Expected_Seq_And_Ack()
        {
            var (sentControl, pub, sub) = BuildHarness();
            var peer = NewRemotePeer();

            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1", "akka://local-sys@local-host:2551/user/watcher"), peer.Uid));

            var delivered = await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));
            delivered.IsControl.Should().BeFalse("a delivered system message re-emits as a plain (non-control) envelope so ordinary dispatch handles it");
            delivered.Message.Should().BeOfType<TestSystemMessage>();
            ((TestSystemMessage)delivered.Message).Name.Should().Be("m1");
            delivered.RecipientPath.Should().Be("akka://local-sys@local-host:2551/user/watcher");

            sentControl.Should().ContainSingle();
            sentControl[0].To.Should().Be(peer.Address);
            sentControl[0].Message.Should().BeOfType<SysAck>();
            ((SysAck)sentControl[0].Message).SeqNo.Should().Be(1L);
            ((SysAck)sentControl[0].Message).From.Should().Be(NewLocal());
        }

        [Fact(DisplayName = "happy-path: successive in-order envelopes advance `expected` and each get their own SysAck")]
        public async Task Should_Advance_Expected_And_Ack_Each_In_Order_Envelope()
        {
            var (sentControl, pub, sub) = BuildHarness();
            var peer = NewRemotePeer();

            await sub.RequestAsync(2);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            await pub.SendNextAsync(Envelope(Sme(2L, peer, "m2"), peer.Uid));

            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m1");
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m2");

            sentControl.Should().HaveCount(2);
            ((SysAck)sentControl[0].Message).SeqNo.Should().Be(1L);
            ((SysAck)sentControl[1].Message).SeqNo.Should().Be(2L);
        }

        [Fact(DisplayName = "duplicate: seq < expected is dropped (not delivered) and re-Acked at expected-1 (covers a lost SysAck)")]
        public async Task Should_Drop_Duplicate_And_ReAck()
        {
            var (sentControl, pub, sub) = BuildHarness();
            var peer = NewRemotePeer();

            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3)); // expected is now 2

            // Re-send seq 1 -- a duplicate (the sender never saw our first SysAck).
            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1-dup"), peer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300)); // a duplicate must never be delivered downstream

            sentControl.Should().HaveCount(2, "the duplicate must be re-Acked (not silently ignored) so the sender's resend timer can eventually observe it");
            sentControl[1].Message.Should().BeOfType<SysAck>();
            ((SysAck)sentControl[1].Message).SeqNo.Should().Be(1L, "re-SysAck must be at expected-1, i.e. the highest CONTIGUOUS seq actually delivered");
        }

        [Fact(DisplayName = "gap: seq > expected is dropped (not delivered) and NACK'd at expected-1, with NO inbound reorder buffer")]
        public async Task Should_Drop_Gap_And_Nack()
        {
            var (sentControl, pub, sub) = BuildHarness();
            var peer = NewRemotePeer();

            // Nothing delivered yet -- expected is still 1. Seq 3 arrives first: a gap.
            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(3L, peer, "m3"), peer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300)); // a gap must never be delivered downstream (no inbound reorder buffer)

            sentControl.Should().ContainSingle();
            sentControl[0].Message.Should().BeOfType<SysNack>();
            ((SysNack)sentControl[0].Message).SeqNo.Should().Be(0L, "SysNack must carry expected-1, i.e. the highest contiguous seq actually delivered so far (none yet)");
        }

        [Fact(DisplayName = "reordering tolerated: out-of-order arrival (1, 3, 2, resent-3) is restored to in-order delivery purely by the SENDER's resend, with no inbound buffering of the gap")]
        public async Task Should_Tolerate_Reordering_Via_Sender_Resend_Without_Inbound_Buffer()
        {
            var (sentControl, pub, sub) = BuildHarness();
            var peer = NewRemotePeer();

            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m1"); // expected -> 2

            // Seq 3 arrives before seq 2 -- a gap (expected is 2). Dropped, NOT buffered, SysNack(1).
            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(3L, peer, "m3-early"), peer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));

            // Seq 2 now arrives (this is what the SENDER's SysNack-triggered resend restores order
            // with) -- matches expected, delivered, expected -> 3.
            await pub.SendNextAsync(Envelope(Sme(2L, peer, "m2"), peer.Uid));
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m2");

            // The sender's resend (triggered by the SysNack) now redelivers seq 3 -- matches expected(3), delivered.
            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(3L, peer, "m3-resent"), peer.Uid));
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m3-resent");

            // Exactly one SysNack was sent (for the early/out-of-order seq 3), alongside three Acks
            // (seq 1, seq 2, seq 3) -- proves the stage never buffered anything itself; ordering was
            // restored entirely by what the "sender" (the test) chose to redeliver.
            sentControl.Should().HaveCount(4);
            sentControl[0].Message.Should().BeOfType<SysAck>();
            sentControl[1].Message.Should().BeOfType<SysNack>();
            sentControl[2].Message.Should().BeOfType<SysAck>();
            sentControl[3].Message.Should().BeOfType<SysAck>();
        }

        [Fact(DisplayName = "pass-through: ordinary messages and unrelated control messages (e.g. ArteryHeartbeat) flow through unchanged")]
        public async Task Should_Pass_Through_Unrelated_Messages_Unchanged()
        {
            var (_, pub, sub) = BuildHarness();

            var ordinary = new InboundEnvelope("hello", null, "akka://local-sys@local-host:2551/user/x", 1L, 0, "test-manifest");
            var heartbeat = new InboundEnvelope(new ArteryHeartbeat(), null, null, 1L, 0, ArteryControlMessageSerializer.HeartbeatManifest);

            await sub.RequestAsync(2);
            await pub.SendNextAsync(ordinary);
            await pub.SendNextAsync(heartbeat);

            (await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Should().Be(ordinary);
            (await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Should().Be(heartbeat);
        }

        [Fact(DisplayName = "Should_ResumeExpectedSeq_When_NewStageIsMaterializedForSameOrigin")]
        public async Task Should_Resume_Expected_Seq_On_New_Stage_For_Same_Origin()
        {
            var (_, context, sentControl) = NewContext(NewRemotePeer());
            var peer = NewRemotePeer();
            var (pub1, sub1) = Materialize(context);

            await sub1.RequestAsync(2);
            await pub1.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            await pub1.SendNextAsync(Envelope(Sme(2L, peer, "m2"), peer.Uid));
            await sub1.ExpectNextAsync(TimeSpan.FromSeconds(3));
            await sub1.ExpectNextAsync(TimeSpan.FromSeconds(3));
            pub1.SendComplete(); // the connection is gone; the sender's window survives it

            var (pub2, sub2) = Materialize(context);
            await sub2.RequestAsync(2);
            await pub2.SendNextAsync(Envelope(Sme(2L, peer, "m2-replay"), peer.Uid)); // delivered, Ack lost
            await sub2.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            sentControl[^1].Message.Should().Be(new SysAck(2L, NewLocal()), "a replay below the shared expectation is re-Acked at expected-1");

            await pub2.SendNextAsync(Envelope(Sme(3L, peer, "m3"), peer.Uid));
            ((TestSystemMessage)(await sub2.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("m3");
            sentControl.Select(x => x.Message).OfType<SysNack>().Should().BeEmpty();
        }

        [Fact(DisplayName = "Should_DeliverReplayedSeqOneOnce_When_NewStageIsMaterialized")]
        public async Task Should_Deliver_Replayed_Seq_One_Only_Once_Across_Stages()
        {
            var (_, context, sentControl) = NewContext(NewRemotePeer());
            var peer = NewRemotePeer();
            var (pub1, sub1) = Materialize(context);
            await sub1.RequestAsync(1);
            await pub1.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            await sub1.ExpectNextAsync(TimeSpan.FromSeconds(3));
            pub1.SendComplete();

            var (pub2, sub2) = Materialize(context);
            await sub2.RequestAsync(1);
            await pub2.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid)); // the sender's unacked window, replayed
            await sub2.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            sentControl.Select(x => x.Message).OfType<SysAck>().Select(a => a.SeqNo).Should().Equal(1L, 1L);
        }

        [Fact(DisplayName = "Should_StartAtSeqOneAndDropOldUid_When_PeerRestartsWithNewUid")]
        public async Task Should_Start_New_Uid_At_Seq_One_And_Drop_Superseded_Uid()
        {
            var oldPeer = NewRemotePeer(222L);
            var newPeer = NewRemotePeer(223L);
            var (registry, context, sentControl) = NewContext(oldPeer);
            var (pub, sub) = Materialize(context);
            await sub.RequestAsync(3);
            await pub.SendNextAsync(Envelope(Sme(1L, oldPeer, "old1"), oldPeer.Uid));
            await pub.SendNextAsync(Envelope(Sme(2L, oldPeer, "old2"), oldPeer.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));

            registry.CompleteHandshake(newPeer.Address, newPeer); // the peer restarted under the same address
            await pub.SendNextAsync(Envelope(Sme(3L, oldPeer, "old3"), oldPeer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            sentControl.Should().HaveCount(2, "a superseded uid is neither delivered nor acknowledged");

            await pub.SendNextAsync(Envelope(Sme(1L, newPeer, "new1"), newPeer.Uid));
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("new1");
        }

        [Fact(DisplayName = "Should_DropWithoutAckOrNack_When_OriginUidIsNotRegistered")]
        public async Task Should_DropWithoutAckOrNack_When_OriginUidIsNotRegistered()
        {
            var (_, context, sentControl) = NewContext(NewRemotePeer(222L));
            var stranger = NewRemotePeer(999L);
            var (pub, sub) = Materialize(context);

            await sub.RequestAsync(1);
            await pub.SendNextAsync(Envelope(Sme(1L, stranger, "stranger"), stranger.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            sentControl.Should().BeEmpty();
        }

        [Fact(DisplayName = "Should_DropWithoutAckOrNack_When_OriginUidIsQuarantined")]
        public async Task Should_DropWithoutAckOrNack_When_OriginUidIsQuarantined()
        {
            var peer = NewRemotePeer();
            var (registry, context, sentControl) = NewContext(peer);
            var (pub, sub) = Materialize(context);

            await sub.RequestAsync(2);
            await pub.SendNextAsync(Envelope(Sme(1L, peer, "m1"), peer.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));

            registry.AssociationFor(peer.Address).Quarantine(peer.Uid).Should().BeTrue();
            await pub.SendNextAsync(Envelope(Sme(2L, peer, "m2"), peer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            sentControl.Should().HaveCount(1, "only the Ack of seq 1; the quarantined uid's next frame is neither delivered nor answered");
        }

        [Fact(DisplayName = "Should_DeliverEachSeqOnce_When_TwoStagesRunConcurrentlyForSameOrigin")]
        public async Task Should_DeliverEachSeqOnce_When_TwoStagesRunConcurrentlyForSameOrigin()
        {
            const int count = 200;
            var peer = NewRemotePeer();
            var (registry, _, _) = NewContext(peer);
            // A no-op reply hook: the stages run on different threads, which a plain List would not tolerate.
            var context = new AssociationRegistryInboundContext(registry, NewLocal(), (_, _) => { });
            var delivered = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var materializer = ActorMaterializer.Create(Sys);

            TestPublisher.Probe<IInboundEnvelope> Start() =>
                this.SourceProbe<IInboundEnvelope>()
                    .Via(Flow.FromGraph(new SystemMessageAckerStage(context)))
                    .To(Sink.ForEach<IInboundEnvelope>(e => delivered.Enqueue(((TestSystemMessage)e.Message).Name)))
                    .Run(materializer);

            // Each connection sends the same ascending window, like an old and a new connection overlapping.
            var pubs = new[] { Start(), Start() };
            await Task.WhenAll(pubs.Select(pub => Task.Run(async () =>
            {
                for (var seq = 1L; seq <= count; seq++)
                    await pub.SendNextAsync(Envelope(Sme(seq, peer, seq.ToString()), peer.Uid));
            })));

            await AwaitAssertAsync(() => delivered.Count.Should().BeGreaterOrEqualTo(count), TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            delivered.Select(long.Parse).OrderBy(x => x).Should().Equal(Enumerable.Range(1, count).Select(x => (long)x));
        }

        [Fact(DisplayName = "Should_Not_Redeliver_When_Superseded_Handshake_Arrives_Late")]
        public async Task Should_Not_Redeliver_When_Superseded_Handshake_Arrives_Late()
        {
            var oldPeer = NewRemotePeer(222L);
            var newPeer = NewRemotePeer(223L);
            var (registry, context, _) = NewContext(oldPeer);
            var (pub, sub) = Materialize(context);

            await sub.RequestAsync(2);
            await pub.SendNextAsync(Envelope(Sme(1L, oldPeer, "m1"), oldPeer.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));

            registry.CompleteHandshake(newPeer.Address, newPeer);
            registry.CompleteHandshake(oldPeer.Address, oldPeer); // a delayed handshake of the superseded incarnation

            await pub.SendNextAsync(Envelope(Sme(1L, oldPeer, "m1-replay"), oldPeer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
        }

        [Fact(DisplayName = "Should_AcceptNextSeq_When_HandshakesOscillateBetweenIncarnations")]
        public async Task Should_AcceptNextSeq_When_HandshakesOscillateBetweenIncarnations()
        {
            var a0 = NewRemotePeer(222L);
            var a1 = NewRemotePeer(223L);
            var (registry, context, sentControl) = NewContext(a0);
            registry.CompleteHandshake(a1.Address, a1);
            var (pub, sub) = Materialize(context);

            await sub.RequestAsync(3);
            await pub.SendNextAsync(Envelope(Sme(1L, a1, "k1"), a1.Uid));
            await pub.SendNextAsync(Envelope(Sme(2L, a1, "k2"), a1.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));

            registry.CompleteHandshake(a0.Address, a0);
            registry.CompleteHandshake(a1.Address, a1);

            await pub.SendNextAsync(Envelope(Sme(3L, a1, "k3"), a1.Uid));
            ((TestSystemMessage)(await sub.ExpectNextAsync(TimeSpan.FromSeconds(3))).Message).Name.Should().Be("k3");
            sentControl.Select(x => x.Message).OfType<SysNack>().Should().BeEmpty();
            sentControl[^1].Message.Should().Be(new SysAck(3L, NewLocal()));
        }

        /// <summary>Runs <paramref name="older"/> and pauses it, from inside a handshake listener, right after its state transition.</summary>
        private static async Task<(Task Older, System.Threading.ManualResetEventSlim Release)> StartPausedCompletionAsync(
            AssociationRegistry registry, Association association, UniqueAddress older)
        {
            var entered = new System.Threading.ManualResetEventSlim();
            var release = new System.Threading.ManualResetEventSlim();
            var armed = 1;
            association.SubscribeHandshakeStateChanged(new object(), () =>
            {
                if (System.Threading.Interlocked.Exchange(ref armed, 0) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            });

            var task = Task.Run(() => registry.CompleteHandshake(older.Address, older));
            (await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3)))).Should().BeTrue();
            return (task, release);
        }

        [Fact(DisplayName = "Should_KeepCurrentUidEligible_When_OlderCompletionIsPausedAfterItsTransition")]
        public async Task Should_KeepCurrentUidEligible_When_OlderCompletionIsPausedAfterItsTransition()
        {
            var a = NewRemotePeer(222L);
            var b = NewRemotePeer(223L);
            var registry = new AssociationRegistry();
            registry.CompleteHandshake(a.Address, a);
            registry.AdvanceInboundSystemMessageSeq(a.Uid, 1L).Should().Be(1L);
            var association = registry.AssociationFor(a.Address);

            // A -> B completes its transition and then pauses; B -> A completes while it is paused.
            var (older, release) = await StartPausedCompletionAsync(registry, association, b);
            try
            {
                registry.CompleteHandshake(a.Address, a);
            }
            finally
            {
                release.Set();
                await older;
            }

            var currentUid = association.CurrentState.UniqueRemoteAddress!.Value.Uid;
            registry.TryGetByUid(currentUid).Should().BeSameAs(association, "the association's current uid must stay indexed");
            registry.AdvanceInboundSystemMessageSeq(currentUid, 1L).Should().NotBe(0L, "the current uid must stay eligible");
        }

        [Fact(DisplayName = "Should_KeepOneIndexAndSequenceEntry_When_DelayedNoOpCompletionsInterleaveWithRestarts")]
        public async Task Should_KeepOneIndexAndSequenceEntry_When_DelayedNoOpCompletionsInterleaveWithRestarts()
        {
            var registry = new AssociationRegistry();
            var first = NewRemotePeer(500L);
            registry.CompleteHandshake(first.Address, first);
            var association = registry.AssociationFor(first.Address);

            var current = first;
            for (var i = 1; i <= 5; i++)
            {
                registry.AdvanceInboundSystemMessageSeq(current.Uid, 1L).Should().Be(1L);
                // A same-uid completion is paused after its transition while the peer restarts.
                var (noOp, release) = await StartPausedCompletionAsync(registry, association, current);
                var next = NewRemotePeer(500L + i);
                try
                {
                    registry.CompleteHandshake(next.Address, next);
                }
                finally
                {
                    release.Set();
                    await noOp;
                }

                current = next;
            }

            registry.AdvanceInboundSystemMessageSeq(current.Uid, 1L).Should().Be(1L);
            registry.UidIndexCount.Should().Be(1);
            registry.InboundSequenceEntryCount.Should().Be(1);
        }

        [Theory(DisplayName = "Should_IgnoreHandshake_When_SupersededUidArrivesLate")]
        [InlineData(false)]
        [InlineData(true)]
        public void Should_IgnoreHandshake_When_SupersededUidArrivesLate(bool responsePath)
        {
            var a = NewRemotePeer(222L);
            var b = NewRemotePeer(223L);
            var registry = new AssociationRegistry();
            registry.CompleteHandshake(a.Address, a);
            registry.CompleteHandshake(b.Address, b);
            var association = registry.AssociationFor(a.Address);
            var incarnation = association.CurrentState.Incarnation;
            var generation = association.HandshakeGeneration;
            var notifications = 0;
            association.SubscribeHandshakeStateChanged(new object(), () => notifications++);

            var state = responsePath ? registry.CompleteOutboundHandshake(a.Address, a) : registry.CompleteHandshake(a.Address, a);

            state.UniqueRemoteAddress.Should().Be(b);
            association.CurrentState.Incarnation.Should().Be(incarnation, "a superseded uid must not start another incarnation");
            association.HandshakeGeneration.Should().Be(generation);
            notifications.Should().Be(0);
            registry.TryGetByUid(a.Uid).Should().BeNull();
            registry.TryGetByUid(b.Uid).Should().BeSameAs(association);
            registry.UidIndexCount.Should().Be(1);
        }

        [Fact(DisplayName = "Should_NotRedeliver_When_RetiredUidHandshakesAgainAfterManyRestarts")]
        public async Task Should_NotRedeliver_When_RetiredUidHandshakesAgainAfterManyRestarts()
        {
            var oldPeer = NewRemotePeer(222L);
            var (registry, context, _) = NewContext(oldPeer);
            var (pub, sub) = Materialize(context);

            await sub.RequestAsync(2);
            await pub.SendNextAsync(Envelope(Sme(1L, oldPeer, "m1"), oldPeer.Uid));
            await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));

            for (var i = 1; i <= 20; i++)
            {
                var peer = NewRemotePeer(222L + i);
                registry.CompleteHandshake(peer.Address, peer);
            }

            registry.CompleteHandshake(oldPeer.Address, oldPeer);
            registry.AssociationFor(oldPeer.Address).CurrentState.UniqueRemoteAddress!.Value.Uid.Should().Be(242L, "a retired uid must not become current again");

            await pub.SendNextAsync(Envelope(Sme(1L, oldPeer, "m1-replay"), oldPeer.Uid));
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(300));
            registry.InboundSequenceEntryCount.Should().BeLessOrEqualTo(1);
        }

        [Fact(DisplayName = "Should_KeepUidIneligible_When_QuarantinedUidIsRetiredAndHandshakesAgain")]
        public void Should_KeepUidIneligible_When_QuarantinedUidIsRetiredAndHandshakesAgain()
        {
            var a = NewRemotePeer(222L);
            var b = NewRemotePeer(223L);
            var registry = new AssociationRegistry();
            registry.CompleteHandshake(a.Address, a);
            registry.AdvanceInboundSystemMessageSeq(a.Uid, 1L).Should().Be(1L);
            registry.AssociationFor(a.Address).Quarantine(a.Uid).Should().BeTrue();

            registry.CompleteHandshake(b.Address, b);
            registry.CompleteHandshake(a.Address, a);

            registry.AdvanceInboundSystemMessageSeq(a.Uid, 2L).Should().Be(0L);
            registry.AssociationFor(a.Address).CurrentState.IsQuarantined(a.Uid).Should().BeTrue();
        }
    }
}
