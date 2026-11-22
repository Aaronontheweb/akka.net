//-----------------------------------------------------------------------
// <copyright file="ArteryPairLaneSelectionSpec.cs" company="Akka.NET Project">
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
using Akka.Configuration;
using Akka.Remote.Artery;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// <c>akka.remote.artery.advanced.lane-selection = sender-recipient</c>: lane choice keyed on the
    /// (sender, recipient) pair, outbound (<see cref="Association.SelectPairLane"/>) and inbound
    /// (<see cref="ArteryInboundProcessingStage.PairLaneFor"/>). Assertions are order/progress based,
    /// with no wall-clock thresholds.
    /// </summary>
    public class ArteryPairLaneSelectionSpec : AkkaSpec
    {
        public ArteryPairLaneSelectionSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig(string laneSelection = "sender-recipient") => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.outbound-lanes = 4
            akka.remote.artery.advanced.inbound-lanes = 4
            akka.remote.artery.advanced.lane-selection = {{laneSelection}}
            akka.remote.artery.advanced.outbound-message-queue-size = 20000
            """);

        private static int BoundPort(ActorSystem system) => RARP.For(system).Provider.DefaultAddress.Port!.Value;

        private static string SelectionPath(ActorSystem system, string localName) =>
            $"akka://{system.Name}@127.0.0.1:{BoundPort(system)}/user/{localName}";

        private static Address RemoteAddressOf(ActorSystem system) =>
            new("akka", system.Name, "127.0.0.1", BoundPort(system));

        private static readonly Address TestAddress = new("akka", "sys", "127.0.0.1", 1234);

        public sealed class Echo : ReceiveActor
        {
            public Echo()
            {
                ReceiveAny(msg => Sender.Tell(msg));
            }
        }

        /// <summary>Records, per sender name, the sequence numbers received; completes a TCS when all arrived.</summary>
        private sealed class PerSenderRecorder : ReceiveActor
        {
            public PerSenderRecorder(Dictionary<string, List<int>> received, int expected, TaskCompletionSource<bool> done)
            {
                var total = 0;
                Receive<string>(s =>
                {
                    var key = Sender.Path.Name;
                    if (!received.TryGetValue(key, out var list))
                        received[key] = list = new List<int>();
                    list.Add(int.Parse(s));
                    if (++total == expected)
                        done.TrySetResult(true);
                });
            }
        }

        private sealed record Start(IActorRef Target, int Count);

        private sealed class Burster : ReceiveActor
        {
            public Burster()
            {
                Receive<Start>(s =>
                {
                    for (var i = 0; i < s.Count; i++)
                        s.Target.Tell(i.ToString()); // implicit Self as sender
                });
            }
        }

        // ----------------------------------------------------------------------------------
        // Settings
        // ----------------------------------------------------------------------------------

        [Fact(DisplayName = "Pair lanes: lane-selection defaults to recipient, parses sender-recipient, and rejects other values")]
        public void Should_Parse_LaneSelection_Setting()
        {
            ArterySettings Parse(string hocon) => new(ConfigurationFactory.ParseString(hocon)
                .WithFallback(RemoteConfigFactory.Default())
                .GetConfig("akka.remote.artery"));

            Parse("").LaneSelection.Should().Be(ArteryLaneSelection.Recipient);
            Parse("akka.remote.artery.advanced.lane-selection = recipient").LaneSelection.Should().Be(ArteryLaneSelection.Recipient);
            Parse("akka.remote.artery.advanced.lane-selection = sender-recipient").LaneSelection.Should().Be(ArteryLaneSelection.SenderRecipient);

            Assert.Throws<Akka.Configuration.ConfigurationException>(() => Parse("akka.remote.artery.advanced.lane-selection = sender"));
            Assert.Throws<Akka.Configuration.ConfigurationException>(() => Parse("akka.remote.artery.advanced.lane-selection = \"\""));
        }

        // ----------------------------------------------------------------------------------
        // Key functions
        // ----------------------------------------------------------------------------------

        private static ActorPath UserPath(string name, long uid) =>
            (new RootActorPath(TestAddress) / "user" / name).WithUid(uid);

        private static ActorPath TempPath(string name) =>
            new RootActorPath(TestAddress) / "temp" / name;

        [Fact(DisplayName = "Pair lanes: outbound key is deterministic, in range, and the same pair always maps to the same lane")]
        public void Outbound_Key_Should_Be_Deterministic_And_In_Range()
        {
            var recipient = UserPath("shard-region", 42);
            foreach (var uid in new[] { 1L, -1L, long.MinValue, long.MaxValue })
            {
                var sender = UserPath("s", uid);
                var lane = Association.SelectPairLane(recipient, sender, 4);
                lane.Should().BeInRange(0, 3);
                Association.SelectPairLane(recipient, UserPath("s", uid), 4).Should().Be(lane);
            }

            var temp = TempPath("$abc");
            Association.SelectPairLane(recipient, temp, 4).Should().Be(Association.SelectPairLane(recipient, TempPath("$abc"), 4));
            Association.SelectPairLane(recipient, null, 4).Should().Be(Association.SelectPairLane(UserPath("shard-region", 42), null, 4));
            Association.SelectPairLane(recipient, temp, 1).Should().Be(0);
        }

        [Fact(DisplayName = "Pair lanes: many senders to ONE recipient spread over every outbound lane; many Ask replies (temp recipient and temp sender) spread too")]
        public void Outbound_Key_Should_Spread_Many_Senders_To_One_Recipient()
        {
            var recipient = UserPath("shard-region", 424242);

            var userSenders = Enumerable.Range(0, 1000)
                .Select(i => Association.SelectPairLane(recipient, UserPath($"s{i}", 1_000_000_007L * (i + 1)), 4))
                .ToHashSet();
            userSenders.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });

            // Ask requests: temp sender (uid 0) -> fixed recipient
            var tempSenders = Enumerable.Range(0, 1000)
                .Select(i => Association.SelectPairLane(recipient, TempPath($"${i}"), 4))
                .ToHashSet();
            tempSenders.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });

            // Ask replies: uid-0 temp recipient, fixed sender. Under recipient-only selection these all land on lane 0.
            var replySender = UserPath("replier", 99);
            var tempRecipients = Enumerable.Range(0, 1000)
                .Select(i => Association.SelectPairLane(TempPath($"${i}"), replySender, 4))
                .ToHashSet();
            tempRecipients.Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });

            // No sender but temp recipients: still not all on lane 0.
            Enumerable.Range(0, 1000)
                .Select(i => Association.SelectPairLane(TempPath($"${i}"), null, 4))
                .ToHashSet().Count.Should().BeGreaterThan(1);
        }

        [Fact(DisplayName = "Pair lanes: inbound key is deterministic, in range, spreads senders for one recipient path, and falls back to the recipient alone without a sender")]
        public void Inbound_Key_Should_Spread_Senders_And_Be_Deterministic()
        {
            const string recipient = "akka://sys@127.0.0.1:1234/user/shard-region#424242";

            var lanes = Enumerable.Range(0, 1000)
                .Select(i => ArteryInboundProcessingStage.PairLaneFor(recipient, $"akka://other@127.0.0.1:9999/user/s{i}#{i * 7919L + 1}", 4))
                .ToList();
            lanes.Should().OnlyContain(l => l >= 0 && l < 4);
            lanes.ToHashSet().Should().BeEquivalentTo(new[] { 0, 1, 2, 3 });

            ArteryInboundProcessingStage.PairLaneFor(recipient, "akka://other@127.0.0.1:9999/user/s1#1", 4)
                .Should().Be(ArteryInboundProcessingStage.PairLaneFor(recipient, "akka://other@127.0.0.1:9999/user/s1#1", 4));
            ArteryInboundProcessingStage.PairLaneFor(recipient, null, 4)
                .Should().Be(ArteryInboundProcessingStage.PairLaneFor(recipient, null, 4));
            ArteryInboundProcessingStage.PairLaneFor(recipient, "x", 1).Should().Be(0);
        }

        // ----------------------------------------------------------------------------------
        // End to end
        // ----------------------------------------------------------------------------------

        [Fact(DisplayName = "Pair lanes: many senders bursting to one remote recipient each arrive in order, and the real sender/recipient pairs spread over lanes")]
        public async Task Should_Preserve_PerPair_Order_With_Many_Senders_To_One_Recipient()
        {
            const int senders = 24;
            const int perSender = 300;

            var config = ArteryConfig();
            var systemA = ActorSystem.Create("PairLanesOrderA", config);
            var systemB = ActorSystem.Create("PairLanesOrderB", config);
            try
            {
                var received = new Dictionary<string, List<int>>();
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                systemB.ActorOf(Props.Create(() => new PerSenderRecorder(received, senders * perSender, done)), "recorder");
                var target = await systemA.ActorSelection(SelectionPath(systemB, "recorder")).ResolveOne(10.Seconds());

                var bursters = Enumerable.Range(0, senders)
                    .Select(i => systemA.ActorOf(Props.Create(() => new Burster()), $"burster-{i}"))
                    .ToArray();

                // The actual (sender, recipient) pairs in this test must use more than one lane in each direction.
                var recipientPath = target.Path;
                var wireRecipient = recipientPath.ToSerializationFormatWithAddress(RemoteAddressOf(systemB));
                Enumerable.Range(0, senders)
                    .Select(i => Association.SelectPairLane(recipientPath, bursters[i].Path, 4)).Distinct().Count()
                    .Should().BeGreaterThan(1);
                bursters.Select(b => ArteryInboundProcessingStage.PairLaneFor(
                        wireRecipient, b.Path.ToSerializationFormatWithAddress(RARP.For(systemA).Provider.DefaultAddress), 4))
                    .Distinct().Count().Should().BeGreaterThan(1);

                foreach (var b in bursters)
                    b.Tell(new Start(target, perSender));

                var finished = await Task.WhenAny(done.Task, Task.Delay(60.Seconds()));
                finished.Should().BeSameAs(done.Task, "all messages must arrive");

                // The recorder actor has finished processing every message once `done` fired, and it
                // is only read after that point (TCS completion orders the reads after its writes).
                received.Count.Should().Be(senders);
                foreach (var (sender, list) in received)
                    list.Should().Equal(Enumerable.Range(0, perSender), $"sender {sender}'s messages must arrive in send order");
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }

        [Fact(DisplayName = "Pair lanes: concurrent Ask round trips (temp sender / temp recipient) all complete with the right reply")]
        public async Task Should_Complete_Ask_Round_Trips()
        {
            var config = ArteryConfig();
            var systemA = ActorSystem.Create("PairLanesAskA", config);
            var systemB = ActorSystem.Create("PairLanesAskB", config);
            try
            {
                systemB.ActorOf(Props.Create(() => new Echo()), "echo");
                var echo = await systemA.ActorSelection(SelectionPath(systemB, "echo")).ResolveOne(10.Seconds());

                var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
                {
                    for (var i = 0; i < 250; i++)
                    {
                        var expected = $"{t}-{i}";
                        (await echo.Ask<string>(expected, 20.Seconds())).Should().Be(expected);
                    }
                })).ToArray();

                await Task.WhenAll(tasks);
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }

        [Fact(DisplayName = "Pair lanes: remote deployment followed immediately by messages delivers every message in order")]
        public async Task Should_Deliver_First_Messages_To_Freshly_Remote_Deployed_Actor_In_Order()
        {
            var config = ArteryConfig();
            var systemA = ActorSystem.Create("PairLanesDeployA", config);
            var systemB = ActorSystem.Create("PairLanesDeployB", config);
            try
            {
                var remote = RemoteAddressOf(systemB);
                var probe = CreateTestProbe(systemA);

                for (var i = 0; i < 10; i++)
                {
                    var deployed = systemA.ActorOf(
                        Props.Create(() => new Echo()).WithDeploy(Deploy.None.WithScope(new RemoteScope(remote))),
                        $"deployed-{i}");

                    // Straight after the create, no wait.
                    for (var m = 0; m < 5; m++)
                        deployed.Tell(m.ToString(), probe.Ref);

                    for (var m = 0; m < 5; m++)
                        (await probe.ExpectMsgAsync<string>(20.Seconds())).Should().Be(m.ToString());
                }
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }
    }
}
