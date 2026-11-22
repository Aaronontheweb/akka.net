//-----------------------------------------------------------------------
// <copyright file="ArteryAskReplySenderSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
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
    /// An inbound Artery message addressed to a stock <see cref="FutureActorRef{T}"/> (the promise behind an Ask)
    /// is delivered without resolving its envelope sender, because that ref never reads it. Every other
    /// recipient still gets the resolved sender.
    /// </summary>
    public class ArteryAskReplySenderSpec : AkkaSpec
    {
        private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

        public ArteryAskReplySenderSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig() => ConfigurationFactory.ParseString("""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            """);

        private static Address AddressOf(ActorSystem system) => RARP.For(system).Provider.DefaultAddress;

        private static async Task WithSystemsAsync(Func<ActorSystem, ActorSystem, Task> test)
        {
            var systemA = ActorSystem.Create("SenderSkipA", ArteryConfig());
            var systemB = ActorSystem.Create("SenderSkipB", ArteryConfig());
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

        private static Task<IActorRef> ResolveOnB(ActorSystem from, ActorSystem to, string name) =>
            from.ActorSelection($"{AddressOf(to)}/user/{name}").ResolveOne(AskTimeout);

        /// <summary>A sender whose serialized path has a uid fragment that cannot be parsed.</summary>
        private sealed class MalformedSenderRef : MinimalActorRef
        {
            public MalformedSenderRef(Address address, IActorRefProvider provider)
            {
                Path = new ChildActorPath(new RootActorPath(address), "bad#uid", 0);
                Provider = provider;
            }

            public override ActorPath Path { get; }

            public override IActorRefProvider Provider { get; }
        }

        /// <summary>A future ref subclass, which may read the sender, so it must still receive a resolved one.</summary>
        private sealed class SenderRecordingFutureRef : FutureActorRef<object>
        {
            private readonly TaskCompletionSource<IActorRef> _sender;

            public SenderRecordingFutureRef(TaskCompletionSource<IActorRef> sender, ActorPath path, IActorRefProvider provider)
                : base(new TaskCompletionSource<object>(), path, provider)
            {
                _sender = sender;
            }

            protected override void TellInternal(object message, IActorRef sender) => _sender.TrySetResult(sender);
        }

        private sealed class Echo : ReceiveActor
        {
            public Echo()
            {
                ReceiveAny(msg => Sender.Tell(msg));
            }
        }

        private sealed class MalformedSenderReplier : ReceiveActor
        {
            public MalformedSenderReplier(Address address)
            {
                ReceiveAny(msg => Sender.Tell(msg, new MalformedSenderRef(address, ((ExtendedActorSystem)Context.System).Provider)));
            }
        }

        private sealed class DeferredReplier : ReceiveActor
        {
            public DeferredReplier()
            {
                IActorRef? asker = null;
                Receive<string>(s => s == "ask", _ => asker = Sender);
                Receive<string>(s => s == "release", _ => asker?.Tell("late"));
            }
        }

        private sealed class Watcher : ReceiveActor
        {
            public Watcher(IActorRef probe)
            {
                Receive<string>(_ => Context.Watch(Sender));
                Receive<Terminated>(t => probe.Tell(t.ActorRef.Path.Elements[0]));
            }
        }

        [Fact(DisplayName = "Should_CompleteAsk_When_RemoteReplyArrives")]
        public async Task Should_CompleteAsk_When_RemoteReplyArrives()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                b.ActorOf(Props.Create(() => new Echo()), "echo");
                var echo = await ResolveOnB(a, b, "echo");

                (await echo.Ask<string>("ping", AskTimeout)).Should().Be("ping");
            });
        }

        [Fact(DisplayName = "Should_CompleteAsk_When_ReplySenderPathIsMalformed")]
        public async Task Should_CompleteAsk_When_ReplySenderPathIsMalformed()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var address = AddressOf(b);
                b.ActorOf(Props.Create(() => new MalformedSenderReplier(address)), "replier");
                var replier = await ResolveOnB(a, b, "replier");

                (await replier.Ask<string>("ping", AskTimeout)).Should().Be("ping");
            });
        }

        [Fact(DisplayName = "Should_DeliverResolvedSender_When_RecipientIsOrdinaryActor")]
        public async Task Should_DeliverResolvedSender_When_RecipientIsOrdinaryActor()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var echoOnB = b.ActorOf(Props.Create(() => new Echo()), "echo");
                var echo = await ResolveOnB(a, b, "echo");
                var probe = CreateTestProbe(a);

                echo.Tell("ping", probe.Ref);

                await probe.ExpectMsgAsync("ping", AskTimeout);
                probe.LastSender.Path.Address.Should().Be(AddressOf(b));
                probe.LastSender.Path.ToStringWithoutAddress().Should().Be(echoOnB.Path.ToStringWithoutAddress());
            });
        }

        [Fact(DisplayName = "Should_DeliverResolvedSender_When_RecipientIsFutureActorRefSubclass")]
        public async Task Should_DeliverResolvedSender_When_RecipientIsFutureActorRefSubclass()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var echoOnB = b.ActorOf(Props.Create(() => new Echo()), "echo");
                var echo = await ResolveOnB(a, b, "echo");

                var provider = ((ExtendedActorSystem)a).Provider;
                var seen = new TaskCompletionSource<IActorRef>(TaskCreationOptions.RunContinuationsAsynchronously);
                var path = provider.TempPath();
                provider.RegisterTempActor(new SenderRecordingFutureRef(seen, path, provider), path);

                echo.Tell("ping", provider.ResolveActorRef(path));

                var sender = await seen.Task.WaitAsync(AskTimeout);
                sender.Path.ToStringWithoutAddress().Should().Be(echoOnB.Path.ToStringWithoutAddress());
            });
        }

        [Fact(DisplayName = "Should_DeliverSystemMessage_When_RecipientIsFutureActorRef")]
        public async Task Should_DeliverSystemMessage_When_RecipientIsFutureActorRef()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var probe = CreateTestProbe(b);
                b.ActorOf(Props.Create(() => new Watcher(probe.Ref)), "watcher");
                var watcher = await ResolveOnB(a, b, "watcher");

                // The Watch system message reaches the pending Ask; its Terminated reaches the watcher on timeout.
                var ask = watcher.Ask<string>("watch me", TimeSpan.FromMilliseconds(500));
                await Assert.ThrowsAsync<AskTimeoutException>(() => ask);

                await probe.ExpectMsgAsync("temp", TimeSpan.FromSeconds(10));
            });
        }

        [Fact(DisplayName = "Should_DeadLetterWithResolvedSender_When_ReplyArrivesAfterAskCompleted")]
        public async Task Should_DeadLetterWithResolvedSender_When_ReplyArrivesAfterAskCompleted()
        {
            await WithSystemsAsync(async (a, b) =>
            {
                var deferredOnB = b.ActorOf(Props.Create(() => new DeferredReplier()), "deferred");
                var deferred = await ResolveOnB(a, b, "deferred");
                var deadLetters = CreateTestProbe(a);
                a.EventStream.Subscribe(deadLetters.Ref, typeof(DeadLetter));

                await Assert.ThrowsAsync<AskTimeoutException>(() => deferred.Ask<string>("ask", TimeSpan.FromMilliseconds(500)));
                deferred.Tell("release");

                var dead = await deadLetters.ExpectMsgAsync<DeadLetter>(AskTimeout);
                dead.Message.Should().Be("late");
                dead.Recipient.Path.Elements[0].Should().Be("temp");
                dead.Sender.Path.Address.Should().Be(AddressOf(b));
                dead.Sender.Path.ToStringWithoutAddress().Should().Be(deferredOnB.Path.ToStringWithoutAddress());
            });
        }
    }
}
