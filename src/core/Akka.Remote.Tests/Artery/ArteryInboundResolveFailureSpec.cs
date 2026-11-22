//-----------------------------------------------------------------------
// <copyright file="ArteryInboundResolveFailureSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// An inbound Artery message whose sender or recipient path cannot be resolved is a failure of that one
    /// message: it is dropped with a warning, and the connection and the messages around it are unaffected.
    /// Runs with one inbound lane (dispatch inline in the connection's sink) and with two (dispatch in the lane).
    /// </summary>
    public abstract class ArteryInboundResolveFailureSpecBase : AkkaSpec
    {
        private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);
        private const int Around = 20;

        private readonly int _lanes;

        protected ArteryInboundResolveFailureSpecBase(int lanes, ITestOutputHelper output) : base(output)
        {
            _lanes = lanes;
        }

        private Config ArteryConfig() => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.loggers = ["Akka.TestKit.TestEventListener, Akka.TestKit"]
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-lanes = {{_lanes}}
            """);

        private static Address AddressOf(ActorSystem system) => RARP.For(system).Provider.DefaultAddress;

        private async Task WithSystemsAsync(Func<ActorSystem, ActorSystem, Task> test)
        {
            var name = $"InboundResolveL{_lanes}";
            var systemA = ActorSystem.Create(name + "A", ArteryConfig());
            var systemB = ActorSystem.Create(name + "B", ArteryConfig());
            try
            {
                await test(systemA, systemB);
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(10.Seconds());
                await systemB.Terminate().AwaitWithTimeout(10.Seconds());
            }
        }

        /// <summary>A ref whose serialized path has a uid fragment that cannot be parsed.</summary>
        private sealed class MalformedRef : MinimalActorRef
        {
            public MalformedRef(Address address, IActorRefProvider provider)
            {
                Path = new ChildActorPath(new RootActorPath(address), "bad#uid", 0);
                Provider = provider;
            }

            public override ActorPath Path { get; }

            public override IActorRefProvider Provider { get; }
        }

        private sealed class Forward : ReceiveActor
        {
            public Forward(IActorRef target)
            {
                ReceiveAny(msg => target.Tell(msg));
            }
        }

        /// <summary>Answers "bad" with a reply whose sender path cannot be parsed, anything else normally.</summary>
        private sealed class Replier : ReceiveActor
        {
            public Replier(Address address)
            {
                var provider = ((ExtendedActorSystem)Context.System).Provider;
                Receive<string>(s => s == "bad", s => Sender.Tell(s, new MalformedRef(address, provider)));
                Receive<string>(s => Sender.Tell(s));
            }
        }

        private static async Task<IActorRef> ResolveOnB(ActorSystem from, ActorSystem to, string name) =>
            await from.ActorSelection($"{AddressOf(to)}/user/{name}").ResolveOne(10.Seconds());

        /// <summary>
        /// Sends a burst with one bad message in the middle, then expects every good message in order.
        /// </summary>
        private async Task BurstWithOneBadMessage(string expectedWarning, Func<ActorSystem, ActorSystem, IActorRef, Action<object>> badSend)
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var probe = CreateTestProbe(b);
                b.ActorOf(Props.Create(() => new Forward(probe.Ref)), "forward");
                var forward = await ResolveOnB(a, b, "forward");

                // Establish the association first, so the burst below runs on a connection that exists.
                forward.Tell("warmup");
                await probe.ExpectMsgAsync("warmup", 10.Seconds());

                var sendBad = badSend(a, b, forward);

                await CreateEventFilter(b).Warning(contains: expectedWarning).ExpectOneAsync(async () =>
                {
                    for (var i = 0; i < Around; i++)
                        forward.Tell($"m-{i}");

                    sendBad("bad");

                    for (var i = Around; i < 2 * Around; i++)
                        forward.Tell($"m-{i}");

                    for (var i = 0; i < 2 * Around; i++)
                        await probe.ExpectMsgAsync($"m-{i}", Within);
                });

                // The connection is still usable, and nothing was delivered for the bad message.
                forward.Tell("after");
                await probe.ExpectMsgAsync("after", Within);
                await probe.ExpectNoMsgAsync(100.Milliseconds());
            });
        }

        [Fact(DisplayName = "Should_KeepConnectionAndDeliverOtherMessages_When_SenderPathIsMalformed")]
        public Task Should_KeepConnectionAndDeliverOtherMessages_When_SenderPathIsMalformed() =>
            BurstWithOneBadMessage("cannot resolve sender path", (a, b, forward) =>
            {
                var malformed = new MalformedRef(AddressOf(a), ((ExtendedActorSystem)a).Provider);
                return msg => forward.Tell(msg, malformed);
            });

        [Fact(DisplayName = "Should_KeepConnectionAndDeliverOtherMessages_When_RecipientPathIsMalformed")]
        public Task Should_KeepConnectionAndDeliverOtherMessages_When_RecipientPathIsMalformed() =>
            BurstWithOneBadMessage("cannot resolve recipient path", (a, b, forward) =>
            {
                var provider = (RemoteActorRefProvider)((ExtendedActorSystem)a).Provider;
                var path = new ChildActorPath(new RootActorPath(AddressOf(b)), "bad#uid", 0);
                var recipient = new RemoteActorRef(provider.Transport, AddressOf(a), path, ActorRefs.Nobody, Props.None, Deploy.None);
                return msg => recipient.Tell(msg);
            });

        [Fact(DisplayName = "Should_KeepConnectionAndCompleteOtherAsks_When_ReplySenderPathIsMalformed")]
        public async Task Should_KeepConnectionAndCompleteOtherAsks_When_ReplySenderPathIsMalformed()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                b.ActorOf(Props.Create(() => new Replier(AddressOf(b))), "replier");
                var replier = await ResolveOnB(a, b, "replier");
                (await replier.Ask<string>("warmup", 10.Seconds())).Should().Be("warmup");

                await CreateEventFilter(a).Warning(contains: "cannot resolve sender path").ExpectOneAsync(async () =>
                {
                    var before = Enumerable.Range(0, Around).Select(i => replier.Ask<string>($"m-{i}", 10.Seconds())).ToArray();
                    var bad = replier.Ask<string>("bad", 2.Seconds());
                    var after = Enumerable.Range(Around, Around).Select(i => replier.Ask<string>($"m-{i}", 10.Seconds())).ToArray();

                    // The reply with the unparseable sender is dropped, so its Ask times out; the rest complete.
                    await Assert.ThrowsAsync<AskTimeoutException>(() => bad);

                    var replies = await Task.WhenAll(before.Concat(after)).WaitAsync(Within);
                    replies.Should().Equal(Enumerable.Range(0, 2 * Around).Select(i => $"m-{i}"));
                });

                // A later Ask on the same association completes without waiting for a reconnect.
                (await replier.Ask<string>("after", Within)).Should().Be("after");
            });
        }
    }

    public sealed class ArteryInboundResolveFailureSingleLaneSpec : ArteryInboundResolveFailureSpecBase
    {
        public ArteryInboundResolveFailureSingleLaneSpec(ITestOutputHelper output) : base(1, output)
        {
        }
    }

    public sealed class ArteryInboundResolveFailureTwoLanesSpec : ArteryInboundResolveFailureSpecBase
    {
        public ArteryInboundResolveFailureTwoLanesSpec(ITestOutputHelper output) : base(2, output)
        {
        }
    }
}
