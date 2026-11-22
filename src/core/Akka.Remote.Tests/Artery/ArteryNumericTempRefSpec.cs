//-----------------------------------------------------------------------
// <copyright file="ArteryNumericTempRefSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.Artery;
using Akka.TestKit;
using Akka.Util;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// End-to-end coverage of numeric temp refs (<c>akka.remote.artery.advanced.numeric-temp-refs</c>): an
    /// Ask sender and a reply recipient travel as TEMP records instead of path literals, with the switch on,
    /// off and mixed, across forwarding, payload serialization, ActorSelection, Watch, ordering over lanes,
    /// timed-out Asks and owner-uid fencing.
    /// </summary>
    public class ArteryNumericTempRefSpec : AkkaSpec
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public ArteryNumericTempRefSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig(bool numeric, int port = 0, int lanes = 1) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = {{port}}
            akka.remote.artery.advanced.numeric-temp-refs = {{(numeric ? "on" : "off")}}
            akka.remote.artery.advanced.outbound-lanes = {{lanes}}
            akka.remote.artery.advanced.inbound-lanes = {{lanes}}
            akka.remote.artery.advanced.outbound-restart-backoff = 300ms
            akka.remote.artery.advanced.control-heartbeat-interval = 500ms
            akka.remote.watch-failure-detector.heartbeat-interval = 200ms
            """);

        private static Address AddressOf(ActorSystem system) => RARP.For(system).Provider.DefaultAddress;

        private static ArteryRemoting TransportOf(ActorSystem system) => (ArteryRemoting)RARP.For(system).Provider.Transport;

        private static long UidOf(ActorSystem system) => AddressUidExtension.Uid(system);

        /// <summary>Replies to / records / forwards what it is asked; reports its observations through <c>report</c>.</summary>
        private sealed class Responder : ReceiveActor
        {
            public Responder(Action<object> report)
            {
                Receive<string>(s => s == "ping", _ =>
                {
                    report(Sender);
                    Sender.Tell("pong");
                });
                Receive<string>(s => s == "hold", _ => report(Sender));
                Receive<string>(s => s == "watch-and-reply", _ =>
                {
                    Context.Watch(Sender);
                    Sender.Tell("pong");
                });
                Receive<string>(s => s == "select", _ =>
                {
                    report(Sender);
                    Context.ActorSelection(Sender.Path).Tell("pong");
                });
                Receive<string>(s => s.StartsWith("forward:"), s =>
                    Context.ActorSelection(s.Substring("forward:".Length)).Tell("ping", Sender));
                Receive<string>(s => s.StartsWith("payload:"), s =>
                    // The ask sender travels inside a message payload to an actor on the asking system.
                    Context.ActorSelection(s.Substring("payload:".Length)).Tell(Sender));
                Receive<Terminated>(t => report(t));
            }
        }

        private sealed class Recorder : MinimalActorRef
        {
            private readonly ActorPath _path;
            private readonly IActorRefProvider _provider;
            private readonly ConcurrentQueue<object> _received;

            public Recorder(IActorRefProvider provider, ActorPath path, ConcurrentQueue<object> received)
            {
                _provider = provider;
                _path = path;
                _received = received;
            }

            public override ActorPath Path => _path;
            public override IActorRefProvider Provider => _provider;
            protected override void TellInternal(object message, IActorRef sender) => _received.Enqueue(message);
        }

        private static async Task<T> Next<T>(BlockingCollection<object> reports)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (reports.TryTake(out var item, 100) && item is T typed)
                    return typed;
                await Task.Yield();
            }

            throw new TimeoutException($"No {typeof(T).Name} reported within {Timeout}.");
        }

        private static string ResponderPath(ActorSystem system) =>
            $"{AddressOf(system)}/user/responder";

        private (ActorSystem Asker, ActorSystem Responder, BlockingCollection<object> Reports) StartPair(
            bool askerNumeric, bool responderNumeric, int lanes = 1)
        {
            var asker = ActorSystem.Create("Asker", ArteryConfig(askerNumeric, lanes: lanes));
            var responder = ActorSystem.Create("Responder", ArteryConfig(responderNumeric, lanes: lanes));
            var reports = new BlockingCollection<object>();
            responder.ActorOf(Props.Create(() => new Responder(o => reports.Add(o))), "responder");
            return (asker, responder, reports);
        }

        private static Task ShutdownAll(params ActorSystem[] systems) => Task.WhenAll(systems.Select(s => s.Terminate()));

        private static Task<object> AskResponder(ActorSystem asker, ActorSystem responder, string message, TimeSpan? timeout = null) =>
            asker.ActorSelection(ResponderPath(responder)).Ask<object>(message, timeout ?? Timeout);

        // ===================== on / off / mixed =====================

        [Theory(DisplayName = "Should_complete_an_Ask_over_Artery_When_numeric_temp_refs_are_on_off_or_mixed")]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Should_complete_an_Ask_over_Artery_When_numeric_temp_refs_are_on_off_or_mixed(bool askerNumeric, bool responderNumeric)
        {
            var (asker, responder, reports) = StartPair(askerNumeric, responderNumeric);
            try
            {
                for (var i = 0; i < 20; i++)
                    (await AskResponder(asker, responder, "ping")).Should().Be("pong");

                var sender = await Next<IActorRef>(reports);

                // The sender is the lightweight numeric ref exactly when the ASKING side writes the numeric form.
                if (askerNumeric)
                    sender.Should().BeOfType<RemoteTempActorRef>();
                else
                    sender.Should().NotBeOfType<RemoteTempActorRef>();
                sender.Path.Elements.Should().HaveCount(2);
                sender.Path.Elements[0].Should().Be("temp");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== path / equality / hash =====================

        [Fact(DisplayName = "Should_expose_the_same_path_equality_and_hash_as_a_ref_resolved_from_the_literal_path_When_the_sender_is_a_numeric_temp_ref")]
        public async Task Should_expose_the_same_path_equality_and_hash_as_a_ref_resolved_from_the_literal_path_When_the_sender_is_a_numeric_temp_ref()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                var name = sender.TempId.Base64Encode();
                var literal = $"{AddressOf(asker)}/temp/{name}";
                sender.Path.ToStringWithAddress().Should().Be(literal);
                sender.Path.Uid.Should().Be(0);

                var resolved = RARP.For(responder).Provider.ResolveActorRefWithLocalAddress(literal, AddressOf(responder));
                resolved.Should().NotBeOfType<RemoteTempActorRef>();
                sender.Equals(resolved).Should().BeTrue();
                resolved.Equals(sender).Should().BeTrue();
                sender.GetHashCode().Should().Be(resolved.GetHashCode());
                sender.ToString().Should().Be(resolved.ToString());
                sender.OwnerUid.Should().Be(UidOf(asker));
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== late reply =====================

        [Fact(DisplayName = "Should_dead_letter_the_reply_When_it_arrives_after_the_Ask_timed_out")]
        public async Task Should_dead_letter_the_reply_When_it_arrives_after_the_Ask_timed_out()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(asker);
                asker.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                var ask = AskResponder(asker, responder, "hold", TimeSpan.FromMilliseconds(500));
                var sender = await Next<IActorRef>(reports);
                await Assert.ThrowsAsync<AskTimeoutException>(() => ask);

                sender.Tell("late-reply", ActorRefs.NoSender);

                var dead = await deadLetters.FishForMessageAsync<DeadLetter>(
                    d => d.Message is "late-reply", Timeout);
                dead.Message.Should().Be("late-reply");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== owner uid fence =====================

        [Fact(DisplayName = "Should_not_deliver_a_reply_to_a_live_Ask_When_the_frame_names_a_different_owner_uid")]
        public async Task Should_not_deliver_a_reply_to_a_live_Ask_When_the_frame_names_a_different_owner_uid()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(asker);
                asker.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                var ask = AskResponder(asker, responder, "hold");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                // A reply frame for the SAME temp id but a different owner uid: what a previous incarnation of
                // the asker's address would have been addressed with. Injected below the RemoteTempActorRef
                // send-side fence, so the receiver's own check is what is under test.
                var association = TransportOf(responder).Registry.AssociationFor(AddressOf(asker));
                association.TryEnqueueOutbound(new OutboundEnvelope("stale-reply", null, null, -1, sender.OwnerUid + 1, sender.TempId), 0)
                    .Should().BeTrue();

                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "stale-reply", Timeout);
                ask.IsCompleted.Should().BeFalse("a reply naming another owner uid must not reach the live Ask with the same temp id");

                // The same frame with the right owner uid completes it.
                association.TryEnqueueOutbound(new OutboundEnvelope("fresh-reply", null, null, -1, sender.OwnerUid, sender.TempId), 0)
                    .Should().BeTrue();
                (await ask).Should().Be("fresh-reply");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_dead_letter_a_reply_on_the_sending_side_When_the_association_peer_uid_is_not_the_temp_ref_owner")]
        public async Task Should_dead_letter_a_reply_on_the_sending_side_When_the_association_peer_uid_is_not_the_temp_ref_owner()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(responder);
                responder.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                var stale = new RemoteTempActorRef(
                    TransportOf(responder), AddressOf(responder), sender.Association, AddressOf(asker), sender.OwnerUid + 1, sender.TempId);
                stale.Tell("to-old-incarnation", ActorRefs.NoSender);

                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "to-old-incarnation", Timeout);
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== restart with a new uid =====================

        [Fact(DisplayName = "Should_not_deliver_a_reply_to_the_new_incarnation_When_the_asker_restarts_on_the_same_port")]
        public async Task Should_not_deliver_a_reply_to_the_new_incarnation_When_the_asker_restarts_on_the_same_port()
        {
            var asker = ActorSystem.Create("Asker", ArteryConfig(true));
            var responder = ActorSystem.Create("Responder", ArteryConfig(true));
            var reports = new BlockingCollection<object>();
            responder.ActorOf(Props.Create(() => new Responder(o => reports.Add(o))), "responder");
            ActorSystem? restarted = null;
            try
            {
                var port = AddressOf(asker).Port!.Value;
                var ask = AskResponder(asker, responder, "hold");
                var oldSender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                await asker.Terminate();

                // A new incarnation on the same address: its first Ask reuses the first temp ids.
                for (var attempt = 0; restarted is null; attempt++)
                {
                    try
                    {
                        restarted = ActorSystem.Create("Asker", ArteryConfig(true, port));
                    }
                    catch when (attempt < 40)
                    {
                        await Task.Delay(250);
                    }
                }

                var newAsk = AskResponder(restarted, responder, "hold");
                var newSender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                newSender.TempId.Should().Be(oldSender.TempId, "a restarted asker reuses counter values");
                newSender.OwnerUid.Should().NotBe(oldSender.OwnerUid);

                // A late reply for the OLD incarnation's ask must not complete the NEW incarnation's Ask.
                oldSender.Tell("for-the-old-incarnation", ActorRefs.NoSender);
                await Task.Delay(1000);
                newAsk.IsCompleted.Should().BeFalse();

                newSender.Tell("for-the-new-incarnation", ActorRefs.NoSender);
                (await newAsk).Should().Be("for-the-new-incarnation");
            }
            finally
            {
                await ShutdownAll(new[] { responder, restarted! }.Where(s => s is not null).ToArray());
                if (!asker.WhenTerminated.IsCompleted)
                    await asker.Terminate();
            }
        }

        // ===================== forwarding / payload / selection / watch =====================

        [Fact(DisplayName = "Should_complete_the_Ask_When_the_request_is_forwarded_through_a_third_system")]
        public async Task Should_complete_the_Ask_When_the_request_is_forwarded_through_a_third_system()
        {
            var (asker, forwarder, forwarderReports) = StartPair(true, true);
            var third = ActorSystem.Create("Third", ArteryConfig(true));
            var thirdReports = new BlockingCollection<object>();
            third.ActorOf(Props.Create(() => new Responder(o => thirdReports.Add(o))), "responder");
            try
            {
                var reply = await asker.ActorSelection(ResponderPath(forwarder))
                    .Ask<object>($"forward:{ResponderPath(third)}", Timeout);
                reply.Should().Be("pong");

                // The third system sees the asker's temp ref as an ordinary literal path ref: the forwarder does
                // not own it, so it could not be written numerically.
                var seenByThird = await Next<IActorRef>(thirdReports);
                seenByThird.Should().NotBeOfType<RemoteTempActorRef>();
                seenByThird.Path.Address.Should().Be(AddressOf(asker));
            }
            finally
            {
                await ShutdownAll(asker, forwarder, third);
            }
        }

        [Fact(DisplayName = "Should_resolve_the_sender_on_the_asking_system_When_it_is_serialized_inside_a_payload")]
        public async Task Should_resolve_the_sender_on_the_asking_system_When_it_is_serialized_inside_a_payload()
        {
            var (asker, responder, _) = StartPair(true, true);
            try
            {
                var probe = CreateTestProbe(asker);
                var probePath = probe.Ref.Path.ToStringWithAddress(AddressOf(asker));

                var ask = AskResponder(asker, responder, $"payload:{probePath}");
                var received = await probe.ExpectMsgAsync<IActorRef>(Timeout);

                // On the asker the deserialized ref is the pending Ask itself: telling it completes the Ask.
                received.Path.Address.Should().Be(AddressOf(asker));
                received.Tell("via-payload", ActorRefs.NoSender);
                (await ask).Should().Be("via-payload");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_deliver_to_the_pending_Ask_When_an_ActorSelection_targets_Sender_Path")]
        public async Task Should_deliver_to_the_pending_Ask_When_an_ActorSelection_targets_Sender_Path()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "select")).Should().Be("pong");
                (await Next<IActorRef>(reports)).Should().BeOfType<RemoteTempActorRef>();
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_notify_the_watcher_When_a_remote_temp_ref_is_watched_and_the_Ask_completes")]
        public async Task Should_notify_the_watcher_When_a_remote_temp_ref_is_watched_and_the_Ask_completes()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "watch-and-reply")).Should().Be("pong");
                var terminated = await Next<Terminated>(reports);
                terminated.ActorRef.Path.Elements[0].Should().Be("temp");
                terminated.ActorRef.Path.Address.Should().Be(AddressOf(asker));
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== ordering over lanes and representations =====================

        [Fact(DisplayName = "Should_preserve_order_When_replies_to_one_temp_ref_alternate_between_numeric_and_literal_forms_over_lanes")]
        public async Task Should_preserve_order_When_replies_to_one_temp_ref_alternate_between_numeric_and_literal_forms_over_lanes()
        {
            var (asker, responder, reports) = StartPair(true, true, lanes: 4);
            try
            {
                // Warm up the association and obtain the numeric ref to a long-lived temp registration.
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var numericProbe = (RemoteTempActorRef)await Next<IActorRef>(reports);

                var received = new ConcurrentQueue<object>();
                var provider = RARP.For(asker).Provider;
                var tempPath = provider.TempPath();
                provider.RegisterTempActor(new Recorder(provider, tempPath, received), tempPath);
                var tempId = tempPath.Name;
                Base64Decode(tempId, out var id);

                var numeric = new RemoteTempActorRef(
                    TransportOf(responder), AddressOf(responder), numericProbe.Association, AddressOf(asker), numericProbe.OwnerUid, id);
                var literal = RARP.For(responder).Provider.ResolveActorRefWithLocalAddress(
                    $"{AddressOf(asker)}/temp/{tempId}", AddressOf(responder));
                literal.Should().NotBeOfType<RemoteTempActorRef>();

                const int count = 400;
                for (var i = 0; i < count; i++)
                    (i % 2 == 0 ? (IActorRef)numeric : literal).Tell(i, ActorRefs.NoSender);

                var deadline = DateTime.UtcNow + Timeout;
                while (received.Count < count && DateTime.UtcNow < deadline)
                    await Task.Delay(50);

                received.Cast<int>().Should().Equal(Enumerable.Range(0, count));
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_complete_many_concurrent_Asks_When_lanes_are_enabled_and_numeric_temp_refs_are_on")]
        public async Task Should_complete_many_concurrent_Asks_When_lanes_are_enabled_and_numeric_temp_refs_are_on()
        {
            var (asker, responder, _) = StartPair(true, true, lanes: 4);
            try
            {
                var replies = await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => AskResponder(asker, responder, "ping")));
                replies.Should().OnlyContain(r => (string)r == "pong");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== sender is not resolved for an Ask reply =====================

        [Theory(DisplayName = "Should_complete_the_Ask_When_the_reply_carries_an_unresolvable_sender_path")]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Should_complete_the_Ask_When_the_reply_carries_an_unresolvable_sender_path(bool numericRecipient)
        {
            // Before the sender of a reply to an Ask was skipped, an unparseable sender literal made the
            // inbound dispatch throw (ActorPath.TryParse fails, and a RemoteActorRef cannot be built from a
            // null path), so the reply was lost. The Ask promise never reads its sender, so it now completes.
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var ask = AskResponder(asker, responder, "hold");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                var association = TransportOf(responder).Registry.AssociationFor(AddressOf(asker));

                var garbage = "this is :: not an actor path ###";
                var envelope = numericRecipient
                    ? new OutboundEnvelope("reply-with-garbage-sender", garbage, null, -1, sender.OwnerUid, sender.TempId)
                    : new OutboundEnvelope("reply-with-garbage-sender", garbage, sender.Path.ToStringWithAddress());
                association.TryEnqueueOutbound(envelope, 0).Should().BeTrue();

                (await ask).Should().Be("reply-with-garbage-sender");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        private static void Ignore(object _)
        {
        }

        private sealed class SenderReporter : ReceiveActor
        {
            public SenderReporter(Action<object> report) => ReceiveAny(m => report((m, Sender)));
        }

        [Fact(DisplayName = "Should_deliver_the_resolved_sender_When_the_recipient_is_an_ordinary_actor")]
        public async Task Should_deliver_the_resolved_sender_When_the_recipient_is_an_ordinary_actor()
        {
            var (asker, responder, _) = StartPair(true, true);
            try
            {
                var reports = new BlockingCollection<object>();
                asker.ActorOf(Props.Create(() => new SenderReporter(o => reports.Add(o))), "reporter");
                var from = responder.ActorOf(Props.Create(() => new SenderReporter(Ignore)), "from");

                responder.ActorSelection($"{AddressOf(asker)}/user/reporter").Tell("hello", from);

                var (message, sender) = await Next<(object, IActorRef)>(reports);
                message.Should().Be("hello");
                sender.Path.ToStringWithAddress().Should().Be(from.Path.ToStringWithAddress(AddressOf(responder)));
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        private static void Base64Decode(string name, out long id)
        {
            Base64Encoding.TryBase64DecodeLong(name.AsSpan(), out id).Should().BeTrue();
        }
    }
}
