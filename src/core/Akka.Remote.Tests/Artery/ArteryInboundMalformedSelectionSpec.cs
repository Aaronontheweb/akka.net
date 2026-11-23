// -----------------------------------------------------------------------
// <copyright file="ArteryInboundMalformedSelectionSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch.SysMsg;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// A selection aimed at the remote daemon whose child name is malformed (<c>bad#uid</c>) is handled by the
    /// daemon synchronously on the inbound path: with one inbound lane that is the connection's own sink, so an
    /// exception from it would fail the stream and abort the connection. The daemon treats the name as an unknown
    /// child instead. <c>Sys</c> is the receiving system; a second system sends.
    /// </summary>
    public abstract class ArteryInboundMalformedSelectionSpecBase : AkkaSpec
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);
        private const int Around = 20;

        private static Config ArteryConfig(int lanes) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-lanes = {{lanes}}
            """);

        private readonly int _lanes;

        protected ArteryInboundMalformedSelectionSpecBase(int lanes, ITestOutputHelper output)
            : base(ArteryConfig(lanes), output)
        {
            _lanes = lanes;
        }

        private Address ReceiverAddress => RARP.For(Sys).Provider.DefaultAddress;

        private sealed class Forward : ReceiveActor
        {
            public Forward(IActorRef target)
            {
                ReceiveAny(msg => target.Tell(msg));
            }
        }

        private static RemoteActorRef RemoteRefOn(ActorSystem from, ActorPath path)
        {
            var provider = (RemoteActorRefProvider)((ExtendedActorSystem)from).Provider;
            return new RemoteActorRef(provider.Transport, RARP.For(from).Provider.DefaultAddress, path,
                ActorRefs.Nobody, Props.None, Deploy.None);
        }

        [Fact(DisplayName = "Should_DropSelectionAndKeepConnection_When_DaemonSelectionChildNameIsMalformed")]
        public async Task Should_DropSelectionAndKeepConnection_When_DaemonSelectionChildNameIsMalformed()
        {
            var sender = ActorSystem.Create("MalformedSelectionSender", ArteryConfig(_lanes));
            try
            {
                Sys.ActorOf(Props.Create(() => new Forward(TestActor)), "forward");
                var forward = await sender.ActorSelection($"{ReceiverAddress}/user/forward").ResolveOne(10.Seconds());

                // Establish the association, so the burst below runs on a connection that exists.
                forward.Tell("warmup");
                await ExpectMsgAsync("warmup", 10.Seconds());

                // The anchor is the receiver's /remote path, so the selection resolves to the daemon.
                var daemon = RemoteRefOn(sender, new RootActorPath(ReceiverAddress) / "remote");
                var malformed = new ActorSelection(daemon, new SelectionPathElement[] { new SelectChildName("bad#uid") });

                await EventFilter.Warning(contains: "malformed child name").ExpectOneAsync(async () =>
                {
                    for (var i = 0; i < Around; i++)
                        forward.Tell($"m-{i}");

                    malformed.Tell("bad");

                    for (var i = Around; i < 2 * Around; i++)
                        forward.Tell($"m-{i}");

                    // A connection failure would drop or reorder these (and tear the association down).
                    for (var i = 0; i < 2 * Around; i++)
                        await ExpectMsgAsync($"m-{i}", Prompt);
                });

                forward.Tell("after");
                await ExpectMsgAsync("after", Prompt);
            }
            finally
            {
                await sender.Terminate().AwaitWithTimeout(10.Seconds());
            }
        }

        [Fact(DisplayName = "Should_NotThrowFromDaemon_When_SelectionHasMalformedNameBelowAValidChild")]
        public async Task Should_NotThrowFromDaemon_When_SelectionHasMalformedNameBelowAValidChild()
        {
            var daemon = ((RemoteActorRefProvider)((ExtendedActorSystem)Sys).Provider).RemoteDaemon;
            var selection = new ActorSelectionMessage("x", new SelectionPathElement[]
            {
                new SelectChildName("akka"), new SelectChildName("sys@127.0.0.1:1"), new SelectChildName("user"), new SelectChildName("bad#uid")
            }, false);

            await EventFilter.Warning(contains: "malformed child name").ExpectOneAsync(() =>
            {
                Record.Exception(() => daemon.Tell(selection, TestActor)).Should().BeNull();
                return Task.CompletedTask;
            });
        }

        [Fact(DisplayName = "Should_NotThrowFromDaemon_When_DaemonMsgCreatePathIsMalformed")]
        public async Task Should_NotThrowFromDaemon_When_DaemonMsgCreatePathIsMalformed()
        {
            var daemon = ((RemoteActorRefProvider)((ExtendedActorSystem)Sys).Provider).RemoteDaemon;
            var create = new DaemonMsgCreate(Props.Create<Forward>(TestActor), Deploy.None,
                $"{ReceiverAddress}/remote/akka/other@127.0.0.1:1/user/bad#uid", daemon);

            await EventFilter.Warning(contains: "malformed path").ExpectOneAsync(() =>
            {
                Record.Exception(() => daemon.Tell(create)).Should().BeNull();
                return Task.CompletedTask;
            });
        }

        [Fact(DisplayName = "Should_DropCreate_When_IntermediatePathSegmentHasMalformedUid")]
        public async Task Should_DropCreate_When_IntermediatePathSegmentHasMalformedUid()
        {
            var daemon = ((RemoteActorRefProvider)((ExtendedActorSystem)Sys).Provider).RemoteDaemon;

            // TryParse reads the uid of the last segment only; rebuilding the path parses every segment.
            var create = new DaemonMsgCreate(Props.Create<Forward>(TestActor), Deploy.None,
                $"{ReceiverAddress}/remote/akka/bad#uid/user/target", daemon);

            await EventFilter.Warning(contains: "malformed path").ExpectOneAsync(() =>
            {
                Record.Exception(() => daemon.Tell(create, TestActor)).Should().BeNull();
                return Task.CompletedTask;
            });
        }

        [Fact(DisplayName = "Should_DropSelection_When_WirePatternTypeIsUnsupported")]
        public async Task Should_DropSelection_When_WirePatternTypeIsUnsupported()
        {
            var serializer = new Akka.Remote.Serialization.MessageContainerSerializer((ExtendedActorSystem)Sys);
            var original = new ActorSelectionMessage("payload", new SelectionPathElement[] { new SelectChildName("missing") });
            var wire = Akka.Remote.Serialization.Proto.Msg.SelectionEnvelope.Parser.ParseFrom(serializer.ToBinary(original));

            // A pattern of an unsupported kind deserializes to a null element.
            wire.Pattern[0].Type = (Akka.Remote.Serialization.Proto.Msg.Selection.Types.PatternType)0;
            var decoded = (ActorSelectionMessage)serializer.FromBinary(
                Google.Protobuf.MessageExtensions.ToByteArray(wire), typeof(ActorSelectionMessage));

            var daemon = ((RemoteActorRefProvider)((ExtendedActorSystem)Sys).Provider).RemoteDaemon;
            await EventFilter.Warning(contains: "unsupported element").ExpectOneAsync(() =>
            {
                Record.Exception(() => daemon.Tell(decoded, TestActor)).Should().BeNull();
                return Task.CompletedTask;
            });
        }

        /// <summary>A daemon child whose receive throws, standing for any parse of peer input that was missed.</summary>
        private sealed class ThrowingTellRef : MinimalActorRef
        {
            public ThrowingTellRef(IActorRefProvider provider, ActorPath path)
            {
                Provider = provider;
                Path = path;
            }

            public override ActorPath Path { get; }

            public override IActorRefProvider Provider { get; }

            protected override void TellInternal(object message, IActorRef sender) =>
                throw new InvalidOperationException("simulated missed parse");
        }

        [Fact(DisplayName = "Should_DropMessageAndKeepGoing_When_DaemonReceiveThrowsUnexpectedly")]
        public async Task Should_DropMessageAndKeepGoing_When_DaemonReceiveThrowsUnexpectedly()
        {
            var daemon = (RemoteSystemDaemon)((RemoteActorRefProvider)((ExtendedActorSystem)Sys).Provider).RemoteDaemon;
            daemon.AddChild("throwing", new ThrowingTellRef(daemon.Provider, daemon.Path / "throwing"));
            try
            {
                var selection = new ActorSelectionMessage("x", new SelectionPathElement[] { new SelectChildName("throwing") }, false);

                await EventFilter.Warning(contains: "because handling it failed").ExpectOneAsync(() =>
                {
                    Record.Exception(() => daemon.Tell(selection, TestActor)).Should().BeNull();
                    return Task.CompletedTask;
                });
            }
            finally
            {
                daemon.RemoveChild("throwing");
            }
        }
    }

    /// <summary>One inbound lane (the default): dispatch runs inline in the connection's sink.</summary>
    public sealed class ArteryInboundMalformedSelectionInlineSpec : ArteryInboundMalformedSelectionSpecBase
    {
        public ArteryInboundMalformedSelectionInlineSpec(ITestOutputHelper output) : base(1, output) { }
    }

    /// <summary>Several inbound lanes: dispatch runs on a lane consumer loop.</summary>
    public sealed class ArteryInboundMalformedSelectionLanesSpec : ArteryInboundMalformedSelectionSpecBase
    {
        public ArteryInboundMalformedSelectionLanesSpec(ITestOutputHelper output) : base(2, output) { }
    }
}
