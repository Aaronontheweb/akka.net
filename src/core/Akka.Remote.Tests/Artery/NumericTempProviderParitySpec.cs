//-----------------------------------------------------------------------
// <copyright file="NumericTempProviderParitySpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
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
    /// A provider that derives from the stock remote provider and re-implements the public temp registration
    /// members, and overrides the protected remote-ref factory: the extension points a numeric shortcut must not
    /// bypass.
    /// </summary>
    public sealed class NumericParityHookProvider : RemoteActorRefProvider, IActorRefProvider
    {
        public int Registrations;
        public int Unregistrations;

        public NumericParityHookProvider(string name, Settings settings, EventStream events)
            : base(name, settings, events)
        {
        }

        void IActorRefProvider.RegisterTempActor(IInternalActorRef actor, ActorPath path)
        {
            global::System.Threading.Interlocked.Increment(ref Registrations);
            base.RegisterTempActor(actor, path);
        }

        void IActorRefProvider.UnregisterTempActor(ActorPath path)
        {
            global::System.Threading.Interlocked.Increment(ref Unregistrations);
            base.UnregisterTempActor(path);
        }

        protected override IInternalActorRef CreateRemoteRef(ActorPath path, Address localAddress)
        {
            var inner = base.CreateRemoteRef(path, localAddress);
            return path.Parent is { Name: "temp", Parent: RootActorPath }
                ? new NumericParityRef(this, path, () => inner, (message, sender) => inner.Tell("hook:" + message, sender))
                : inner;
        }
    }

    internal sealed class NumericParityRef : MinimalActorRef
    {
        private readonly ActorPath _path;
        private readonly IActorRefProvider _provider;
        private readonly Func<IActorRef> _empty;
        private readonly Action<object, IActorRef> _tell;

        public NumericParityRef(IActorRefProvider provider, ActorPath path, Func<IActorRef> empty, Action<object, IActorRef> tell)
        {
            _provider = provider;
            _path = path;
            _empty = empty;
            _tell = tell;
        }

        public override ActorPath Path => _path;
        public override IActorRefProvider Provider => _provider;
        protected override void TellInternal(object message, IActorRef sender) => _tell(message, sender);

        public override IActorRef GetChild(IReadOnlyList<string> names) => names.Count == 0 ? this : _empty();
    }

    /// <summary>
    /// Numeric delivery and sending agree with the literal form where a public extension point can tell them apart:
    /// provider hooks, the remote-ref factory, a loopback ref, a future that is nobody, a replaced root temp
    /// container, and the retry buffer of a remote-deployed recipient; and the switch being off leaves lane choice alone.
    /// </summary>
    public class NumericTempProviderParitySpec : AkkaSpec
    {
        public NumericTempProviderParitySpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig(bool numeric, int lanes = 1) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.numeric-refs = {{(numeric ? "on" : "off")}}
            akka.remote.artery.advanced.outbound-lanes = {{lanes}}
            akka.loglevel = ERROR
            """);

        private static Config HookConfig(bool numeric) => ConfigurationFactory.ParseString(
            $"akka.actor.provider = \"{typeof(NumericParityHookProvider).AssemblyQualifiedName}\"").WithFallback(ArteryConfig(numeric));

        private static long Id(ActorPath path)
        {
            Base64Encoding.TryBase64DecodeLong(path.Name.AsSpan(), out var id).Should().BeTrue();
            return id;
        }

        private sealed class Hold : ReceiveActor
        {
            public Hold(IActorRef report) => ReceiveAny(_ => report.Tell(Sender));
        }

        // ===================== A: public provider registration hooks =====================

        [Theory(DisplayName = "Should_call_the_public_registration_members_of_a_derived_provider_for_every_Ask")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_call_the_public_registration_members_of_a_derived_provider_for_every_Ask(bool numeric)
        {
            var system = ActorSystem.Create("RegistrationHooks", HookConfig(numeric));
            try
            {
                var provider = (NumericParityHookProvider)((ExtendedActorSystem)system).Provider;
                var registrations = provider.Registrations;
                var unregistrations = provider.Unregistrations;
                var target = new NumericParityRef(provider, provider.RootPath / "user" / "inline", () => ActorRefs.Nobody,
                    (_, sender) => sender.Tell("reply"));

                (await target.Ask<string>("request", TimeSpan.FromSeconds(5))).Should().Be("reply");

                provider.Registrations.Should().Be(registrations + 1);
                await AwaitAssertAsync(() => provider.Unregistrations.Should().Be(unregistrations + 1));
            }
            finally
            {
                await system.Terminate();
            }
        }

        // ===================== B: the protected remote-ref factory =====================

        [Fact(DisplayName = "Should_resolve_a_numeric_Ask_sender_through_the_remote_ref_factory_of_a_derived_provider")]
        public async Task Should_resolve_a_numeric_Ask_sender_through_the_remote_ref_factory_of_a_derived_provider()
        {
            var asker = ActorSystem.Create("FactoryAsker", ArteryConfig(true));
            var peer = ActorSystem.Create("FactoryPeer", HookConfig(true));
            try
            {
                var reports = CreateTestProbe(peer);
                peer.ActorOf(Props.Create(() => new Hold(reports.Ref)), "hold");
                var address = RARP.For(peer).Provider.DefaultAddress;
                var pending = asker.ActorSelection($"{address}/user/hold").Ask<string>("hold", TimeSpan.FromSeconds(5));

                var replyTo = await reports.ExpectMsgAsync<IActorRef>(TimeSpan.FromSeconds(5));
                replyTo.Tell("reply");

                (await pending).Should().Be("hook:reply");
            }
            finally
            {
                await Task.WhenAll(asker.Terminate(), peer.Terminate());
            }
        }

        // ===================== C: a loopback remote ref =====================

        [Fact(DisplayName = "Should_resolve_a_loopback_Ask_sender_to_the_local_future")]
        public async Task Should_resolve_a_loopback_Ask_sender_to_the_local_future()
        {
            var system = ActorSystem.Create("LoopbackParity", ArteryConfig(true));
            IActorRef? captured = null;
            try
            {
                var provider = RARP.For(system).Provider;
                var reports = CreateTestProbe(system);
                var target = system.ActorOf(Props.Create(() => new Hold(reports.Ref)), "hold");
                var path = ActorPath.Parse(target.Path.ToSerializationFormatWithAddress(provider.DefaultAddress));
                var remote = new RemoteActorRef(provider.Transport, provider.DefaultAddress, path, ActorRefs.Nobody, Props.None, Deploy.None);

                Func<IActorRef, object> factory = replyTo =>
                {
                    captured = replyTo;
                    return "hold";
                };
                var pending = remote.Ask<string>(factory, TimeSpan.FromSeconds(5), System.Threading.CancellationToken.None);

                var observed = await reports.ExpectMsgAsync<IActorRef>(TimeSpan.FromSeconds(5));
                observed.Should().BeSameAs(captured);
                observed.Equals(captured).Should().BeTrue();
                ((IInternalActorRef)observed).IsLocal.Should().BeTrue();

                observed.Tell("done");
                (await pending).Should().Be("done");
            }
            finally
            {
                captured?.Tell("done");
                await system.Terminate();
            }
        }

        // ===================== D: a future that is nobody (false); a replaced root temp container (true) =====================

        [Theory(DisplayName = "Should_match_literal_delivery_for_a_future_that_is_nobody_and_for_a_replaced_root_temp_container")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_match_literal_delivery_for_a_future_that_is_nobody_and_for_a_replaced_root_temp_container(bool redirectRoot)
        {
            var receiver = ActorSystem.Create("DirectParity", ArteryConfig(true));
            var provider = RARP.For(receiver).Provider;
            var root = (RootGuardianActorRef)provider.RootGuardian;
            var slot = provider.TempPath();
            try
            {
                var completion = new TaskCompletionSource<object>();
                var future = new FutureActorRef<object>(completion, redirectRoot ? slot : provider.DeadLetters.Path, provider);
                provider.RegisterTempActor(future, slot);

                if (redirectRoot)
                {
                    receiver.ActorOf(Props.Empty, slot.Name);
                    root.SetTempContainer(provider.Guardian);
                }

                var transport = (ArteryRemoting)provider.Transport;
                var wire = slot.ToSerializationFormatWithAddress(provider.DefaultAddress);

                transport.DispatchInbound(new InboundEnvelope("literal", null, wire, 42, 0, ""));
                completion.Task.IsCompleted.Should().BeFalse();

                var numeric = NumericWireRefs.Of(null, ArteryNumericRef.ForTemporary(AddressUidExtension.Uid(receiver), Id(slot)));
                transport.DispatchInbound(new InboundEnvelope("numeric", null, null, 42, 0, "", numeric));

                completion.Task.IsCompleted.Should().BeFalse("numeric delivery must preserve the literal route's target");
            }
            finally
            {
                root.SetTempContainer(provider.TempContainer);
                provider.UnregisterTempActor(slot);
                await receiver.Terminate();
            }
        }

        [Fact(DisplayName = "Should_match_literal_delivery_for_a_real_Ask_When_the_root_temp_container_has_been_replaced")]
        public async Task Should_match_literal_delivery_for_a_real_Ask_When_the_root_temp_container_has_been_replaced()
        {
            var receiver = ActorSystem.Create("ReplacedRoot", ArteryConfig(true));
            var provider = RARP.For(receiver).Provider;
            var root = (RootGuardianActorRef)provider.RootGuardian;
            IActorRef? sender = null;
            try
            {
                var captured = new TaskCompletionSource<IActorRef>();
                var target = new NumericParityRef(provider, provider.RootPath / "user" / "capture", () => ActorRefs.Nobody,
                    (_, replyTo) => captured.TrySetResult(replyTo));
                var pending = target.Ask<string>("hold", TimeSpan.FromSeconds(10));
                sender = await captured.Task;
                var slot = sender.Path;

                // The path route now reaches /user/<name>; the numeric route must follow it, not the original container.
                receiver.ActorOf(Props.Empty, slot.Name);
                root.SetTempContainer(provider.Guardian);

                var transport = (ArteryRemoting)provider.Transport;
                var numeric = NumericWireRefs.Of(null, ArteryNumericRef.ForTemporary(AddressUidExtension.Uid(receiver), Id(slot)));
                transport.DispatchInbound(new InboundEnvelope("numeric", null, null, 42, 0, "", numeric));

                pending.IsCompleted.Should().BeFalse("the literal form reaches the installed container, not the Ask");
            }
            finally
            {
                root.SetTempContainer(provider.TempContainer);
                sender?.Tell("done");
                await receiver.Terminate();
            }
        }

        // ===================== E: a sender buffered for a remote-deployed recipient =====================

        [Fact(DisplayName = "Should_resolve_a_buffered_sender_when_the_retry_buffer_is_flushed")]
        public async Task Should_resolve_a_buffered_sender_when_the_retry_buffer_is_flushed()
        {
            var config = ConfigurationFactory.ParseString("akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"")
                .WithFallback(ArteryConfig(false));
            var receiver = ActorSystem.Create("BufferedSenderParity", config);
            var provider = RARP.For(receiver).Provider;
            var slot = provider.TempPath();
            try
            {
                var deadLetters = CreateTestProbe(receiver);
                receiver.EventStream.Subscribe(deadLetters.Ref, typeof(DeadLetter));
                var future = new FutureActorRef<object>(new TaskCompletionSource<object>(), slot, provider);
                var transport = (ArteryRemoting)provider.Transport;
                var senderWire = slot.ToSerializationFormatWithAddress(provider.DefaultAddress);
                var recipientWire = (provider.RootPath / "remote" / "missing").ToSerializationFormatWithAddress(provider.DefaultAddress);

                transport.DispatchInbound(new InboundEnvelope("buffered-marker", senderWire, recipientWire, 42, 0, ""));

                provider.RegisterTempActor(future, slot);
                var scheduler = (TestScheduler)receiver.Scheduler;
                for (var i = 0; i < 20; i++)
                    scheduler.Advance(TimeSpan.FromMilliseconds(50));

                var dead = await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "buffered-marker", TimeSpan.FromSeconds(5));
                dead.Sender.Should().BeSameAs(future);
            }
            finally
            {
                provider.UnregisterTempActor(slot);
                await receiver.Terminate();
            }
        }

        // ===================== a buffered sender keeps the routing it had when the frame was accepted =====================

        [Fact(DisplayName = "Should_preserve_the_buffered_sender_When_the_origin_incarnation_changes")]
        public async Task Should_preserve_the_buffered_sender_When_the_origin_incarnation_changes()
        {
            var config = ConfigurationFactory.ParseString("akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"")
                .WithFallback(ArteryConfig(true));
            var receiver = ActorSystem.Create("BufferedNumericOrigin", config);
            try
            {
                var provider = RARP.For(receiver).Provider;
                var transport = (ArteryRemoting)provider.Transport;
                var peer = new Address("akka", "Peer", "127.0.0.1", 25591);
                const long oldUid = 111;
                const long newUid = 112;
                const long counter = 7;

                transport.Registry.CompleteHandshake(peer, new UniqueAddress(peer, oldUid));

                var deadLetters = CreateTestProbe(receiver);
                receiver.EventStream.Subscribe(deadLetters.Ref, typeof(DeadLetter));

                var senderPath = (new RootActorPath(peer) / "temp" / Base64Encoding.Base64Encode(counter)).ToSerializationFormat();
                var recipientPath = (provider.RootPath / "remote" / "not-created").ToSerializationFormatWithAddress(provider.DefaultAddress);

                // Both frames reach the retry buffer while the old origin is known.
                transport.DispatchInbound(new InboundEnvelope("literal-marker", senderPath, recipientPath, oldUid, 0, ""));
                transport.DispatchInbound(new InboundEnvelope(
                    "numeric-marker", null, recipientPath, oldUid, 0, "",
                    NumericWireRefs.Of(ArteryNumericRef.ForTemporary(oldUid, counter), null)));

                transport.Registry.CompleteHandshake(peer, new UniqueAddress(peer, newUid));

                var scheduler = (TestScheduler)receiver.Scheduler;
                for (var i = 0; i < 20; i++)
                    scheduler.Advance(TimeSpan.FromMilliseconds(50));

                var literal = await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "literal-marker", TimeSpan.FromSeconds(5));
                var numeric = await deadLetters.FishForMessageAsync<DeadLetter>(d => d.Message is "numeric-marker", TimeSpan.FromSeconds(5));

                numeric.Sender.Should().Be(literal.Sender);
            }
            finally
            {
                await receiver.Terminate();
            }
        }

        // ===================== lane choice with the switch off =====================

        [Fact(DisplayName = "Should_choose_outbound_lanes_for_ordinary_and_temp_recipients_exactly_as_before_When_numeric_refs_are_off")]
        public async Task Should_choose_outbound_lanes_for_ordinary_and_temp_recipients_exactly_as_before_When_numeric_refs_are_off()
        {
            const int lanes = 4;
            var system = ActorSystem.Create("OffLanes", ArteryConfig(false, lanes));
            try
            {
                var provider = RARP.For(system).Provider;
                var transport = (ArteryRemoting)provider.Transport;
                var remote = new Address("akka", "Peer", "127.0.0.1", 1);
                var association = transport.Registry.AssociationFor(remote);

                foreach (var uid in new long[] { 1, 2, 3, 4, 5, 7, 8, 100, 4096, 12345, int.MaxValue, long.MaxValue })
                {
                    var recipient = (RemoteActorRef)provider.ResolveActorRefWithLocalAddress($"{remote}/user/x#{uid}", provider.DefaultAddress);
                    recipient.Path.Uid.Should().Be(uid);
                    transport.ResolveSendRoute(recipient, association)
                        .Should().Be((int)(unchecked((ulong)uid) % lanes), $"uid {uid}: uid % lanes, as it has always been");
                }

                // A temp ref has no uid and rides lane 0, whatever its counter.
                for (var counter = 1; counter < 40; counter++)
                {
                    var temp = (RemoteActorRef)provider.ResolveActorRefWithLocalAddress($"{remote}/temp/{Base64Encoding.Base64Encode(counter)}", provider.DefaultAddress);
                    transport.ResolveSendRoute(temp, association).Should().Be(0);
                }
            }
            finally
            {
                await system.Terminate();
            }
        }
    }
}
