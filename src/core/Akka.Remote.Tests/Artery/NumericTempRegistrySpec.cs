//-----------------------------------------------------------------------
// <copyright file="NumericTempRegistrySpec.cs" company="Akka.NET Project">
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
using Akka.Remote.Artery;
using Akka.TestKit;
using Akka.Util;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// The per-id record of non-plain temp registrations (what lets a numeric Ask reply skip the path route):
    /// its bound under concurrent registration, and the textual forms of a local temp path that can reach the
    /// resolve cache.
    /// </summary>
    public class NumericTempRegistrySpec : AkkaSpec
    {
        public NumericTempRegistrySpec(ITestOutputHelper output) : base(output)
        {
        }

        private sealed class Slot : MinimalActorRef
        {
            private readonly ActorPath _path;
            private readonly IActorRefProvider _provider;

            public Slot(IActorRefProvider provider, ActorPath path)
            {
                _provider = provider;
                _path = path;
            }

            public override ActorPath Path => _path;
            public override IActorRefProvider Provider => _provider;
            protected override void TellInternal(object message, IActorRef sender)
            {
            }
        }

        private LocalActorRefProvider LocalProvider => (LocalActorRefProvider)((ExtendedActorSystem)Sys).Provider;

        private static long IdOf(ActorPath path)
        {
            Base64Encoding.TryBase64DecodeLong(path.Name.AsSpan(), out var id).Should().BeTrue();
            return id;
        }

        // ===================== the bound holds under concurrency =====================

        [Fact(DisplayName = "Should_never_hold_more_non_plain_ids_than_the_cap_and_fall_back_to_the_path_route_When_registered_concurrently")]
        public async Task Should_never_hold_more_non_plain_ids_than_the_cap_and_fall_back_to_the_path_route_When_registered_concurrently()
        {
            const int cap = 100;
            var provider = LocalProvider;
            var registry = (ITempIdRegistry)provider;
            var container = (VirtualPathContainer)provider.TempContainer;
            container.NonPlainIdCapacity = cap;
            var fresh = IdOf(provider.TempPath());

            // Many threads register distinct ids at once, far past the cap.
            using var gate = new System.Threading.ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                gate.Wait();
                for (var i = 0; i < 500; i++)
                {
                    var path = provider.TempPath();
                    container.AddChild(IdOf(path), new Slot(provider, path));
                }
            })).ToArray();
            gate.Set();
            await Task.WhenAll(tasks);

            container.NonPlainIdCount.Should().Be(cap, "the set is full and never exceeds its cap");
            registry.IsPlainOnlyTempId(fresh).Should().BeFalse("after overflow every id takes the path route");
        }

        [Fact(DisplayName = "Should_not_overflow_When_exactly_the_cap_of_distinct_ids_is_registered_concurrently")]
        public async Task Should_not_overflow_When_exactly_the_cap_of_distinct_ids_is_registered_concurrently()
        {
            const int cap = 160;
            var provider = LocalProvider;
            var registry = (ITempIdRegistry)provider;
            var container = (VirtualPathContainer)provider.TempContainer;
            container.NonPlainIdCapacity = cap;

            using var gate = new System.Threading.ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                gate.Wait();
                for (var i = 0; i < cap / 8; i++)
                {
                    var path = provider.TempPath();
                    container.AddChild(IdOf(path), new Slot(provider, path));
                }
            })).ToArray();
            gate.Set();
            await Task.WhenAll(tasks);

            container.NonPlainIdCount.Should().Be(cap);
            registry.IsPlainOnlyTempId(IdOf(provider.TempPath())).Should().BeTrue("a full set that did not overflow leaves other ids on the direct route");
        }

        // ===================== ordinary promise use does not exhaust the shortcut =====================

        [Fact(DisplayName = "Should_keep_numeric_delivery_direct_After_many_PromiseActorRef_paths_were_materialised")]
        public async Task Should_keep_numeric_delivery_direct_After_many_PromiseActorRef_paths_were_materialised()
        {
            var system = ActorSystem.Create("PromiseChurn", ArteryConfig());
            try
            {
                var provider = RARP.For(system).Provider;
                var registry = (ITempIdRegistry)provider;

                // GracefulStop, WatchAsync and friends register a promise's temp name when its path is first asked for.
                // Far more of them than the non-plain record could hold, each completing (and unregistering) as it goes.
                for (var i = 0; i < 70_000; i++)
                {
                    var promise = PromiseActorRef.Apply(provider, "churn");
                    _ = promise.Path;
                    promise.Tell("done", ActorRefs.NoSender);
                }

                var completion = new TaskCompletionSource<object>();
                var future = provider.CreateFutureRef(completion);
                var id = IdOf(future.Path);
                registry.RegisterTempActor(future, id);

                var transport = (ArteryRemoting)provider.Transport;
                var routes = new ConcurrentQueue<bool>();
                transport.NumericRouteObserverForTests = (_, direct) => routes.Enqueue(direct);
                var refs = NumericWireRefs.Of(null, ArteryNumericRef.ForTemporary(AddressUidExtension.Uid(system), id));
                transport.DispatchInbound(new InboundEnvelope("numeric", null, null, 42, 0, "", refs));

                routes.Should().Equal(true);
                (await completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("numeric");
            }
            finally
            {
                await system.Terminate();
            }
        }

        // ===================== textual forms of a local temp path =====================

        private static Config ArteryConfig() => ConfigurationFactory.ParseString("""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.numeric-refs = on
            """);

        [Fact(DisplayName = "Should_never_serve_a_stale_ref_for_any_textual_form_of_a_clean_local_temp_path")]
        public async Task Should_never_serve_a_stale_ref_for_any_textual_form_of_a_clean_local_temp_path()
        {
            var system = ActorSystem.Create("TempForms", ArteryConfig());
            try
            {
                var provider = RARP.For(system).Provider;
                var registry = (ITempIdRegistry)provider;
                var address = provider.DefaultAddress;

                var first = provider.CreateFutureRef(new TaskCompletionSource<object>());
                var id = IdOf(first.Path);
                var name = first.Path.Name;
                registry.RegisterTempActor(first, id);
                registry.IsPlainOnlyTempId(id).Should().BeTrue();

                // Forms of the same local temp path. Address equality is ordinal, and the transport has exactly one
                // address, so only the canonical address and the address-less (system name only) form are local; the
                // uid fragment, a trailing slash, another host spelling or another port change what, if anything,
                // resolves.
                var forms = new[]
                {
                    $"{address}/temp/{name}",
                    $"akka://{system.Name}/temp/{name}",
                    $"{address}/temp/{name}#0",
                    $"akka://{system.Name}/temp/{name}#0",
                    $"{address}/temp/{name}/",
                    $"{address}/temp/{name}#7",
                    $"akka://{system.Name}@LOCALHOST:{address.Port}/temp/{name}",
                    $"akka://{system.Name}@127.0.0.1:{address.Port + 1}/temp/{name}",
                };

                IActorRef Resolve(string form, bool withLocalAddress) => withLocalAddress
                    ? provider.ResolveActorRefWithLocalAddress(form, address)
                    : provider.ResolveActorRef(form);

                // Warm every cache the literal route has, on this thread, through both entry points.
                var before = forms.Select(f => (Form: f, Result: Resolve(f, true))).ToArray();
                foreach (var form in forms)
                    Resolve(form, false);

                // A clean id: whatever resolves locally to the registered future is the future itself, which the
                // resolve cache does not hold; forms that do not resolve locally are remote refs for another key.
                before.Where(b => ReferenceEquals(b.Result, first)).Select(b => b.Form)
                    .Should().Contain(new[] { forms[0], forms[1], forms[2], forms[3] });
                before.Where(b => b.Result is RemoteActorRef).Select(b => b.Form)
                    .Should().Contain(new[] { forms[6], forms[7] });
                before[5].Result.Should().NotBeSameAs(first, "a non-zero uid fragment names no registered child");

                // Complete the first future and give the same id a second one: no form may serve the first again.
                registry.UnregisterTempActor(id);
                var secondCompletion = new TaskCompletionSource<object>();
                var second = provider.CreateFutureRef(secondCompletion);
                registry.RegisterTempActor(second, id);

                foreach (var (form, result) in before)
                {
                    var now = Resolve(form, true);
                    now.Should().NotBeSameAs(first, $"[{form}] must not resolve to the completed future");
                    if (ReferenceEquals(result, first))
                        now.Should().BeSameAs(second, $"[{form}] resolves locally, so it must see the current incumbent");
                    Resolve(form, false).Should().NotBeSameAs(first, $"[{form}] via ResolveActorRef");
                }

                // And the numeric form agrees with the path forms: it reaches the current incumbent directly.
                var transport = (ArteryRemoting)provider.Transport;
                var routes = new ConcurrentQueue<bool>();
                transport.NumericRouteObserverForTests = (_, direct) => routes.Enqueue(direct);
                var refs = NumericWireRefs.Of(null, ArteryNumericRef.ForTemporary(AddressUidExtension.Uid(system), id));
                transport.DispatchInbound(new InboundEnvelope("numeric", null, null, 42, 0, "", refs));
                routes.Should().Equal(true);
                (await secondCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("numeric");
            }
            finally
            {
                await system.Terminate();
            }
        }
    }
}
