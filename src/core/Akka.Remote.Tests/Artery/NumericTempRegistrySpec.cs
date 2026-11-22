//-----------------------------------------------------------------------
// <copyright file="NumericTempRegistrySpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.TestKit;
using Akka.Util;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// The per-id record of non-plain temp registrations (what lets a numeric Ask reply skip the path route):
    /// its bound under concurrent registration.
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
    }
}
