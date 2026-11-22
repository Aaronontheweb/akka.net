//-----------------------------------------------------------------------
// <copyright file="NumericTempParityWitnessSpec.cs" company="Akka.NET Project">
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
using FluentAssertions.Execution;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    public class NumericTempParityWitnessSpec : AkkaSpec
    {
        public NumericTempParityWitnessSpec(ITestOutputHelper output) : base(output) { }
        private LocalActorRefProvider Provider => (LocalActorRefProvider)((ExtendedActorSystem)Sys).Provider;
        private static long Id(ActorPath path)
        {
            Base64Encoding.TryBase64DecodeLong(path.Name.AsSpan(), out var id).Should().BeTrue();
            return id;
        }

        private sealed class CustomRef : MinimalActorRef
        {
            private readonly ActorPath _path;
            private readonly IActorRefProvider _provider;
            private readonly Func<IActorRef> _empty;
            private readonly Action<object, IActorRef> _tell;
            public CustomRef(IActorRefProvider provider, ActorPath path, Func<IActorRef> empty,
                Action<object, IActorRef> tell)
            { _provider = provider; _path = path; _empty = empty; _tell = tell; }
            public int EmptyCalls { get; private set; }
            public override ActorPath Path => _path;
            public override IActorRefProvider Provider => _provider;
            protected override void TellInternal(object message, IActorRef sender) => _tell(message, sender);
            public override IActorRef GetChild(IReadOnlyList<string> names)
            {
                names.Count.Should().Be(0);
                EmptyCalls++;
                return _empty();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Should_keep_one_first_winner_and_not_resurrect_a_loser(bool numericFirst)
        {
            var registry = (ITempIdRegistry)Provider;
            var numeric = Provider.CreateFutureRef(new TaskCompletionSource<object>());
            var literal = Provider.CreateFutureRef(new TaskCompletionSource<object>());
            var path = numeric.Path;
            var id = Id(path);
            try
            {
                if (numericFirst) registry.RegisterTempActor(numeric, id);
                Provider.RegisterTempActor(literal, path);
                if (!numericFirst) registry.RegisterTempActor(numeric, id);
                registry.TryGetTempActor(id, out var found).Should().BeTrue();
                var traversed = Provider.TempContainer.GetChild(new[] { path.Name });
                registry.UnregisterTempActor(id);
                var afterCleanup = Provider.TempContainer.GetChild(new[] { path.Name });
                using var scope = new AssertionScope();
                found.Should().BeSameAs(numericFirst ? numeric : literal);
                traversed.Should().BeSameAs(numericFirst ? numeric : literal);
                afterCleanup.Should().BeSameAs(ActorRefs.Nobody, "a rejected second registration must not reappear");
            }
            finally { registry.UnregisterTempActor(id); Provider.UnregisterTempActor(path); }
        }

        [Fact]
        public async Task Should_not_reveal_a_late_custom_registration_after_public_Ask_cleanup()
        {
            var captured = new TaskCompletionSource<IActorRef>();
            var target = new CustomRef(Provider, Provider.RootPath / "user" / "capture", () => ActorRefs.Nobody,
                (_, sender) => captured.TrySetResult(sender));
            var pending = target.Ask<string>("hold", TimeSpan.FromSeconds(10));
            var sender = await captured.Task;
            var path = sender.Path;
            var registry = (ITempIdRegistry)Provider;
            var sentinel = new FutureActorRef<object>(new TaskCompletionSource<object>(), path, Provider);
            var loser = new CustomRef(Provider, path, () => sentinel, (_, _) => captured.TrySetResult(sentinel));
            try
            {
                Provider.RegisterTempActor(loser, path);
                Provider.TempContainer.GetChild(new[] { path.Name }).Should().BeSameAs(sender);
                sender.Tell("done");
                (await pending).Should().Be("done");
                await AwaitAssertAsync(() => registry.TryGetTempActor(Id(path), out var found)
                    .Should().BeFalse($"the completed Ask must remove its name; found {found}"));
                Provider.TempContainer.GetChild(new[] { path.Name }).Should().BeSameAs(ActorRefs.Nobody);
            }
            finally { sender.Tell("done"); Provider.UnregisterTempActor(path); }
        }

        private static Config ArteryConfig(bool numeric) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.numeric-refs = {{(numeric ? "on" : "off")}}
            akka.remote.artery.advanced.inbound-lanes = 2
            akka.remote.artery.advanced.outbound-lanes = 4
            """);

        private sealed class Hold : ReceiveActor
        {
            public Hold(IActorRef report) => ReceiveAny(_ => report.Tell(Sender));
        }

        [Theory]
        [InlineData(false, "forward")]
        [InlineData(true, "forward")]
        [InlineData(false, "throw")]
        [InlineData(true, "throw")]
        [InlineData(false, "nobody")]
        [InlineData(true, "nobody")]
        public async Task Should_preserve_custom_empty_tail_delivery_for_a_held_real_reply(bool numeric, string behavior)
        {
            var asker = ActorSystem.Create("ParityAsker", ArteryConfig(numeric));
            var peer = ActorSystem.Create("ParityPeer", ArteryConfig(numeric));
            IInternalActorRef? original = null;
            Task<string>? pending = null;
            ActorPath? path = null;
            try
            {
                var report = CreateTestProbe(peer);
                peer.ActorOf(Props.Create(() => new Hold(report.Ref)), "hold");
                var address = RARP.For(peer).Provider.DefaultAddress;
                pending = asker.ActorSelection($"{address}/user/hold").Ask<string>("hold", TimeSpan.FromSeconds(10));
                var reply = await report.ExpectMsgAsync<IActorRef>(TimeSpan.FromSeconds(5));
                (reply is RemoteTempActorRef).Should().Be(numeric);
                var provider = RARP.For(asker).Provider;
                path = provider.TempContainer.Path / reply.Path.Name;
                ((ITempIdRegistry)provider).TryGetTempActor(Id(path), out original).Should().BeTrue();
                provider.UnregisterTempActor(path);
                var seen = CreateTestProbe(asker);
                var warnings = CreateTestProbe(asker);
                asker.EventStream.Subscribe(warnings.Ref, typeof(Warning));
                asker.EventStream.Subscribe(seen.Ref, typeof(DeadLetter));
                var marker = new InvalidOperationException("empty-tail parity marker");
                var sentinel = new CustomRef(provider, provider.TempPath(), () => ActorRefs.Nobody,
                    (_, _) => seen.Ref.Tell("sentinel"));
                var custom = new CustomRef(provider, path, () =>
                {
                    seen.Ref.Tell("traversal");
                    return behavior == "throw" ? throw marker : behavior == "nobody" ? ActorRefs.Nobody : sentinel;
                }, (_, _) => seen.Ref.Tell("raw"));
                provider.RegisterTempActor(custom, path);
                reply.Tell("payload");
                await seen.ExpectMsgAsync("traversal", TimeSpan.FromSeconds(5));
                if (behavior == "forward")
                    await seen.ExpectMsgAsync("sentinel", TimeSpan.FromSeconds(5));
                else if (behavior == "throw")
                    await warnings.FishForMessageAsync<Warning>(w => ReferenceEquals(w.Cause, marker), TimeSpan.FromSeconds(5));
                else
                    await seen.FishForMessageAsync<DeadLetter>(d => d.Message is "payload", TimeSpan.FromSeconds(5));
                await seen.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100));
                custom.EmptyCalls.Should().Be(1);
            }
            finally
            {
                if (path is not null) RARP.For(asker).Provider.UnregisterTempActor(path);
                original?.Tell("finish");
                if (pending is not null) await pending;
                await Task.WhenAll(asker.Terminate(), peer.Terminate());
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Should_preserve_the_same_thread_literal_cache_winner(bool numeric, bool replacement)
        {
            var receiver = ActorSystem.Create("ParityCache", ArteryConfig(true));
            try
            {
                var provider = RARP.For(receiver).Provider;
                var transport = (ArteryRemoting)provider.Transport;
                var registry = (ITempIdRegistry)provider;
                var completion = new TaskCompletionSource<object>();
                var future = provider.CreateFutureRef(completion);
                var path = future.Path;
                var id = Id(path);
                var wire = path.ToStringWithAddress(provider.DefaultAddress);
                var received = new List<object>();
                var incumbent = new FunctionRef(path, provider, receiver.EventStream, (_, message) => received.Add(message));
                provider.RegisterTempActor(incumbent, path);
                try
                {
                    var thread = Environment.CurrentManagedThreadId;
                    transport.DispatchInbound(new InboundEnvelope("warm", null, wire, 42, 0, ""));
                    received.Should().Equal("warm");
                    provider.UnregisterTempActor(path);
                    if (replacement) registry.RegisterTempActor(future, id);
                    var refs = numeric ? NumericWireRefs.Of(null, ArteryNumericRef.ForTemporary(AddressUidExtension.Uid(receiver), id)) : null;
                    transport.DispatchInbound(new InboundEnvelope("after", null, numeric ? null : wire, 42, 0, "", refs));
                    Environment.CurrentManagedThreadId.Should().Be(thread);
                    received.Should().Equal("warm", "after");
                    completion.Task.IsCompleted.Should().BeFalse();
                }
                finally { registry.UnregisterTempActor(id); provider.UnregisterTempActor(path); }
            }
            finally { await receiver.Terminate(); }
        }
    }
}
