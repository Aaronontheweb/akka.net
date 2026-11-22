//-----------------------------------------------------------------------
// <copyright file="ArteryInboundLaneSpinSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
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
using Akka.Remote.Configuration;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Tests for <c>akka.remote.artery.advanced.inbound-lane-spin-iterations</c>: the bounded spin an
    /// inbound lane consumer performs before parking. Assertions are about delivery, order and
    /// termination only, never timing.
    /// </summary>
    public class ArteryInboundLaneSpinSpec : AkkaSpec
    {
        public ArteryInboundLaneSpinSpec(ITestOutputHelper output) : base(output)
        {
        }

        private const int SpinIterations = 100;

        private static Config ArteryConfig(int inboundLanes, int spinIterations) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-lanes = {{inboundLanes}}
            akka.remote.artery.advanced.inbound-lane-spin-iterations = {{spinIterations}}
            akka.remote.artery.advanced.outbound-message-queue-size = 20000
            akka.loggers = ["Akka.TestKit.TestEventListener, Akka.TestKit"]
            """);

        private static string RemotePath(ActorSystem system, string localName) =>
            $"akka://{system.Name}@127.0.0.1:{RARP.For(system).Provider.DefaultAddress.Port!.Value}/user/{localName}";

        private sealed record GetHistory;
        private sealed record History(int[] Values);

        private sealed class OrderRecorder : ReceiveActor
        {
            private readonly List<int> _received = new();

            public OrderRecorder()
            {
                Receive<int>(i => _received.Add(i));
                Receive<GetHistory>(_ => Sender.Tell(new History(_received.ToArray())));
            }
        }

        [Fact(DisplayName = "Inbound lane spin: setting defaults to 0 and parses a positive value")]
        public void Should_Parse_Spin_Iterations()
        {
            new ArterySettings(RemoteConfigFactory.Default().GetConfig("akka.remote.artery"))
                .InboundLaneSpinIterations.Should().Be(0);

            var configured = ConfigurationFactory.ParseString("akka.remote.artery.advanced.inbound-lane-spin-iterations = 250")
                .WithFallback(RemoteConfigFactory.Default());
            new ArterySettings(configured.GetConfig("akka.remote.artery"))
                .InboundLaneSpinIterations.Should().Be(250);
        }

        [Fact(DisplayName = "Inbound lane spin: with inbound-lanes=2 messages arrive in per-recipient order across an idle gap and under a burst, and shutdown completes")]
        public async Task Should_Deliver_In_Order_Across_Idle_Gap_And_Burst_And_Shut_Down()
        {
            const int recipientCount = 4;
            const int burstPerRecipient = 200;

            var systemB = ActorSystem.Create("ArteryLaneSpinB", ArteryConfig(inboundLanes: 2, spinIterations: SpinIterations));
            var systemA = ActorSystem.Create("ArteryLaneSpinA", ArteryConfig(inboundLanes: 1, spinIterations: 0));
            try
            {
                var recorders = new IActorRef[recipientCount];
                var remoteRefs = new IActorRef[recipientCount];
                for (var r = 0; r < recipientCount; r++)
                    recorders[r] = systemB.ActorOf(Props.Create(() => new OrderRecorder()), $"recorder-{r}");
                for (var r = 0; r < recipientCount; r++)
                    remoteRefs[r] = await systemA.ActorSelection(RemotePath(systemB, $"recorder-{r}")).ResolveOne(10.Seconds());

                async Task AssertHistory(int expectedCount)
                {
                    var expected = Enumerable.Range(0, expectedCount).ToArray();
                    for (var r = 0; r < recipientCount; r++)
                    {
                        var probe = CreateTestProbe(systemA);
                        await AwaitAssertAsync(async () =>
                        {
                            recorders[r].Tell(new GetHistory(), probe.Ref);
                            var history = await probe.ExpectMsgAsync<History>(5.Seconds());
                            history.Values.Should().Equal(expected, $"recorder-{r} must see its sends in send order");
                        }, 30.Seconds());
                    }
                }

                // Phase 1: one message per recipient, then wait for delivery so every lane consumer drains and parks.
                for (var r = 0; r < recipientCount; r++)
                    remoteRefs[r].Tell(0, ActorRefs.NoSender);
                await AssertHistory(1);

                // Idle gap: long enough for the consumers to exhaust their spin and park.
                await Task.Delay(500.Milliseconds());

                // Phase 2: a second message after the gap, then a burst.
                for (var r = 0; r < recipientCount; r++)
                    remoteRefs[r].Tell(1, ActorRefs.NoSender);
                await AssertHistory(2);

                await Task.Delay(500.Milliseconds());

                var sendTasks = Enumerable.Range(0, recipientCount).Select(r => Task.Run(() =>
                {
                    for (var i = 2; i < burstPerRecipient + 2; i++)
                        remoteRefs[r].Tell(i, ActorRefs.NoSender);
                })).ToArray();
                await Task.WhenAll(sendTasks).WaitAsync(30.Seconds());
                await AssertHistory(burstPerRecipient + 2);
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }
    }
}
