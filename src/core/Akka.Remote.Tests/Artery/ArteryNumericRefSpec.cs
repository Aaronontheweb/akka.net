//-----------------------------------------------------------------------
// <copyright file="ArteryNumericRefSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Artery;
using Akka.TestKit;
using Akka.Util;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Two-system coverage of numeric refs (<c>akka.remote.artery.advanced.numeric-refs</c>): an Ask sender
    /// and the reply recipient travel as numeric records instead of path literals, with the switch on, off and
    /// mixed, and the stage-1 failure rows -- owner-UID fencing, lane ordering across shapes, forwarding
    /// through a third node, path/equality of the lazily-pathed ref, quarantine.
    /// </summary>
    public class ArteryNumericRefSpec : AkkaSpec
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        public ArteryNumericRefSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig(bool numeric, int port = 0, int lanes = 1, string extra = "") => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = {{port}}
            akka.remote.artery.advanced.numeric-refs = {{(numeric ? "on" : "off")}}
            akka.remote.artery.advanced.outbound-lanes = {{lanes}}
            akka.remote.artery.advanced.inbound-lanes = {{lanes}}
            akka.remote.artery.advanced.outbound-restart-backoff = 300ms
            akka.remote.artery.advanced.control-heartbeat-interval = 500ms
            akka.remote.watch-failure-detector.heartbeat-interval = 200ms
            {{extra}}
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

        private static string ResponderPath(ActorSystem system) => $"{AddressOf(system)}/user/responder";

        private (ActorSystem Asker, ActorSystem Responder, BlockingCollection<object> Reports) StartPair(
            bool askerNumeric, bool responderNumeric, int lanes = 1, string extra = "")
        {
            var asker = ActorSystem.Create("Asker", ArteryConfig(askerNumeric, lanes: lanes, extra: extra));
            var responder = ActorSystem.Create("Responder", ArteryConfig(responderNumeric, lanes: lanes, extra: extra));
            var reports = new BlockingCollection<object>();
            responder.ActorOf(Props.Create(() => new Responder(o => reports.Add(o))), "responder");
            return (asker, responder, reports);
        }

        private static Task ShutdownAll(params ActorSystem[] systems) => Task.WhenAll(systems.Select(s => s.Terminate()));

        private static Task<object> AskResponder(ActorSystem asker, ActorSystem responder, string message, TimeSpan? timeout = null) =>
            asker.ActorSelection(ResponderPath(responder)).Ask<object>(message, timeout ?? Timeout);

        // ===================== on / off / mixed =====================

        [Theory(DisplayName = "Should_complete_an_Ask_over_Artery_When_numeric_refs_are_on_off_or_mixed")]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Should_complete_an_Ask_over_Artery_When_numeric_refs_are_on_off_or_mixed(bool askerNumeric, bool responderNumeric)
        {
            var (asker, responder, reports) = StartPair(askerNumeric, responderNumeric);
            try
            {
                for (var i = 0; i < 20; i++)
                    (await AskResponder(asker, responder, "ping")).Should().Be("pong");

                var sender = await Next<IActorRef>(reports);

                // The sender is the lightweight numeric ref exactly when the ASKING side writes numeric shapes.
                if (askerNumeric)
                    sender.Should().BeOfType<RemoteTempActorRef>();
                else
                    sender.Should().NotBeOfType<RemoteTempActorRef>();
                sender.Path.Elements.Should().Equal("temp", sender.Path.Name);
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_complete_many_concurrent_Asks_When_lanes_are_enabled_and_numeric_refs_are_on")]
        public async Task Should_complete_many_concurrent_Asks_When_lanes_are_enabled_and_numeric_refs_are_on()
        {
            var (asker, responder, _) = StartPair(true, true, lanes: 4);
            try
            {
                var replies = await Task.WhenAll(Enumerable.Range(0, 300).Select(_ => AskResponder(asker, responder, "ping")));
                replies.Should().OnlyContain(r => (string)r == "pong");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_write_literals_only_When_the_large_message_stream_is_enabled")]
        public async Task Should_write_literals_only_When_the_large_message_stream_is_enabled()
        {
            var (asker, responder, reports) = StartPair(true, true,
                extra: "akka.remote.artery.large-message-destinations = [\"/user/large\"]");
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                (await Next<IActorRef>(reports)).Should().NotBeOfType<RemoteTempActorRef>();
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== lazily-pathed ref: path / equality / hash (row 11) =====================

        [Fact(DisplayName = "Should_equal_and_hash_like_a_ref_resolved_from_the_literal_path_When_the_sender_is_a_numeric_ref")]
        public async Task Should_equal_and_hash_like_a_ref_resolved_from_the_literal_path_When_the_sender_is_a_numeric_ref()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                var literal = $"{AddressOf(asker)}/temp/{sender.TempId.Base64Encode()}";
                sender.Path.ToStringWithAddress().Should().Be(literal);
                sender.Path.Uid.Should().Be(0);
                sender.OwnerUid.Should().Be(UidOf(asker));

                var resolved = RARP.For(responder).Provider.ResolveActorRefWithLocalAddress(literal, AddressOf(responder));
                resolved.Should().NotBeOfType<RemoteTempActorRef>();
                sender.Equals(resolved).Should().BeTrue();
                resolved.Equals(sender).Should().BeTrue();
                sender.GetHashCode().Should().Be(resolved.GetHashCode());
                sender.ToString().Should().Be(resolved.ToString());

                new HashSet<IActorRef> { resolved }.Contains(sender).Should().BeTrue();
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_materialise_the_path_once_When_many_threads_touch_a_numeric_ref_first_at_the_same_time")]
        public async Task Should_materialise_the_path_once_When_many_threads_touch_a_numeric_ref_first_at_the_same_time()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                using var gate = new ManualResetEventSlim(false);
                var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                {
                    gate.Wait();
                    return sender.Path;
                })).ToArray();
                gate.Set();
                var paths = await Task.WhenAll(tasks);

                paths.Should().OnlyContain(p => ReferenceEquals(p, paths[0]));
            }
            finally
            {
                await ShutdownAll(asker, responder);
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
                received.Path.Elements[0].Should().Be("temp");
                received.Tell("via-payload", ActorRefs.NoSender);
                (await ask).Should().Be("via-payload");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_build_a_selection_on_the_asking_system_When_ActorSelection_targets_Sender_Path")]
        public async Task Should_build_a_selection_on_the_asking_system_When_ActorSelection_targets_Sender_Path()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                var selection = responder.ActorSelection(sender.Path);
                selection.PathString.Should().Be($"/temp/{sender.TempId.Base64Encode()}");
                selection.Anchor.Path.Address.Should().Be(AddressOf(asker));
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_notify_the_watcher_When_a_numeric_sender_is_watched_and_the_Ask_completes")]
        public async Task Should_notify_the_watcher_When_a_numeric_sender_is_watched_and_the_Ask_completes()
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

        // ===================== undeliverable replies =====================

        [Fact(DisplayName = "Should_dead_letter_the_reply_When_it_is_sent_to_an_Ask_that_already_completed")]
        public async Task Should_dead_letter_the_reply_When_it_is_sent_to_an_Ask_that_already_completed()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(asker);
                asker.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                sender.Tell("late-reply", ActorRefs.NoSender);

                var dead = await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "late-reply", Timeout);
                dead.Message.Should().Be("late-reply");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_dead_letter_a_numeric_recipient_in_the_actor_namespace_When_no_resolver_handles_it")]
        public async Task Should_dead_letter_a_numeric_recipient_in_the_actor_namespace_When_no_resolver_handles_it()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(asker);
                asker.EventStream.Subscribe(deadLetters, typeof(DeadLetter));
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                await Next<IActorRef>(reports);

                // An id in the namespace nothing resolves yet, owned by the asker: must not reach any actor.
                var association = TransportOf(responder).Registry.AssociationFor(AddressOf(asker));
                association.TryEnqueueOutbound(
                    new OutboundEnvelope("unresolvable", null, null, null, new ArteryNumericRef(UidOf(asker), 1)), 0).Should().BeTrue();

                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "unresolvable", Timeout);
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        // ===================== owner UID fence (row 3) =====================

        [Fact(DisplayName = "Should_not_deliver_a_reply_to_a_live_Ask_When_the_frame_names_a_different_owner_uid")]
        public async Task Should_not_deliver_a_reply_to_a_live_Ask_When_the_frame_names_a_different_owner_uid()
        {
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                var deadLetters = CreateTestProbe(asker);
                asker.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                await Next<IActorRef>(reports);
                var ask = AskResponder(asker, responder, "hold");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                // A reply frame for the SAME counter but a different owner uid: what a previous incarnation of
                // the asker's address would have been addressed with. Injected below the RemoteTempActorRef
                // send-side fence, so the receiver's own check is what is under test.
                var association = TransportOf(responder).Registry.AssociationFor(AddressOf(asker));
                association.TryEnqueueOutbound(
                    new OutboundEnvelope("stale-reply", null, null, null, ArteryNumericRef.ForTemporary(sender.OwnerUid + 1, sender.TempId)), 0)
                    .Should().BeTrue();

                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "stale-reply", Timeout);
                ask.IsCompleted.Should().BeFalse("a reply naming another owner uid must not reach the live Ask with the same counter");

                // The same frame with the right owner uid completes it.
                association.TryEnqueueOutbound(
                    new OutboundEnvelope("fresh-reply", null, null, null, ArteryNumericRef.ForTemporary(sender.OwnerUid, sender.TempId)), 0)
                    .Should().BeTrue();
                (await ask).Should().Be("fresh-reply");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }

        [Fact(DisplayName = "Should_dead_letter_a_reply_on_the_sending_side_When_the_association_peer_uid_is_not_the_ref_owner")]
        public async Task Should_dead_letter_a_reply_on_the_sending_side_When_the_association_peer_uid_is_not_the_ref_owner()
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

        private static async Task<ActorSystem> RestartOnSamePort(int port)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return ActorSystem.Create("Asker", ArteryConfig(true, port));
                }
                catch when (attempt < 40)
                {
                    await Task.Delay(250);
                }
            }
        }

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
                _ = AskResponder(asker, responder, "hold");
                var oldSender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                await asker.Terminate();

                // A new incarnation on the same address: its first Ask reuses the first counter values.
                restarted = await RestartOnSamePort(port);
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

        // ===================== quarantine (row 12) =====================

        [Fact(DisplayName = "Should_not_resolve_a_pre_quarantine_ref_When_the_asker_is_quarantined_and_comes_back_as_a_new_incarnation")]
        public async Task Should_not_resolve_a_pre_quarantine_ref_When_the_asker_is_quarantined_and_comes_back_as_a_new_incarnation()
        {
            var asker = ActorSystem.Create("Asker", ArteryConfig(true));
            var responder = ActorSystem.Create("Responder", ArteryConfig(true));
            var reports = new BlockingCollection<object>();
            responder.ActorOf(Props.Create(() => new Responder(o => reports.Add(o))), "responder");
            ActorSystem? restarted = null;
            try
            {
                var deadLetters = CreateTestProbe(responder);
                responder.EventStream.Subscribe(deadLetters, typeof(DeadLetter));

                var port = AddressOf(asker).Port!.Value;
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var oldSender = (RemoteTempActorRef)await Next<IActorRef>(reports);

                TransportOf(responder).Quarantine(AddressOf(asker), oldSender.OwnerUid);
                oldSender.Tell("to-quarantined", ActorRefs.NoSender);
                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "to-quarantined", Timeout);

                await asker.Terminate();
                restarted = await RestartOnSamePort(port);

                var newAsk = AskResponder(restarted, responder, "hold");
                var newSender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                newSender.OwnerUid.Should().NotBe(oldSender.OwnerUid);

                oldSender.Tell("to-old-id", ActorRefs.NoSender);
                await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "to-old-id", Timeout);
                newAsk.IsCompleted.Should().BeFalse();

                newSender.Tell("to-new-id", ActorRefs.NoSender);
                (await newAsk).Should().Be("to-new-id");
            }
            finally
            {
                await ShutdownAll(new[] { responder, restarted! }.Where(s => s is not null).ToArray());
                if (!asker.WhenTerminated.IsCompleted)
                    await asker.Terminate();
            }
        }

        // ===================== forwarding (row 10) =====================

        [Fact(DisplayName = "Should_complete_the_Ask_When_the_request_is_forwarded_through_a_third_system")]
        public async Task Should_complete_the_Ask_When_the_request_is_forwarded_through_a_third_system()
        {
            var (asker, forwarder, _) = StartPair(true, true);
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

        // ===================== lane ordering across shapes (row 6) =====================

        [Fact(DisplayName = "Should_preserve_order_When_replies_to_one_temp_ref_alternate_between_numeric_and_literal_shapes_over_lanes")]
        public async Task Should_preserve_order_When_replies_to_one_temp_ref_alternate_between_numeric_and_literal_shapes_over_lanes()
        {
            var (asker, responder, reports) = StartPair(true, true, lanes: 4);
            try
            {
                // Warm up the association and obtain a numeric ref to learn the owner uid and association.
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                var numericProbe = (RemoteTempActorRef)await Next<IActorRef>(reports);

                // A long-lived temp registration on the asker that records everything it receives.
                var received = new ConcurrentQueue<object>();
                var provider = RARP.For(asker).Provider;
                var tempPath = provider.TempPath();
                provider.RegisterTempActor(new Recorder(provider, tempPath, received), tempPath);
                Base64Encoding.TryBase64DecodeLong(tempPath.Name.AsSpan(), out var counter).Should().BeTrue();

                var numeric = new RemoteTempActorRef(
                    TransportOf(responder), AddressOf(responder), numericProbe.Association, AddressOf(asker), numericProbe.OwnerUid, counter);
                var literal = RARP.For(responder).Provider.ResolveActorRefWithLocalAddress(
                    $"{AddressOf(asker)}/temp/{tempPath.Name}", AddressOf(responder));
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

        // ===================== sender is not resolved for an Ask reply =====================

        [Theory(DisplayName = "Should_complete_the_Ask_When_the_reply_carries_an_unresolvable_sender_path")]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Should_complete_the_Ask_When_the_reply_carries_an_unresolvable_sender_path(bool numericRecipient)
        {
            // The Ask promise never reads its sender, so a reply is not lost because the sender literal cannot
            // be parsed into a ref.
            var (asker, responder, reports) = StartPair(true, true);
            try
            {
                (await AskResponder(asker, responder, "ping")).Should().Be("pong");
                await Next<IActorRef>(reports);
                var ask = AskResponder(asker, responder, "hold");
                var sender = (RemoteTempActorRef)await Next<IActorRef>(reports);
                var association = TransportOf(responder).Registry.AssociationFor(AddressOf(asker));

                var garbage = "this is :: not an actor path ###";
                var envelope = numericRecipient
                    ? new OutboundEnvelope("reply-with-garbage-sender", garbage, null, null, ArteryNumericRef.ForTemporary(sender.OwnerUid, sender.TempId))
                    : new OutboundEnvelope("reply-with-garbage-sender", garbage, sender.Path.ToStringWithAddress());
                association.TryEnqueueOutbound(envelope, 0).Should().BeTrue();

                (await ask).Should().Be("reply-with-garbage-sender");
            }
            finally
            {
                await ShutdownAll(asker, responder);
            }
        }
    }
}
