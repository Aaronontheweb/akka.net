// -----------------------------------------------------------------------
// <copyright file="UnfoldResourceReadCallbackSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit;
using Akka.Util;
using FluentAssertions;
using Xunit;

namespace Akka.Streams.Tests.Dsl
{
    public sealed class UnfoldResourceReadCallbackSpec : AkkaSpec
    {
        public UnfoldResourceReadCallbackSpec(ITestOutputHelper output) : base(output) { }

        [Fact(DisplayName = "Async resource reads preserve demand and order across immediate and deferred completions")]
        public async Task Should_CompleteInOrder_When_AlternatingImmediateAndDeferredReads()
        {
            var gates = Enumerable.Range(0, 16).Select(_ => (
                Started: new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                Result: new TaskCompletionSource<Option<int>>(TaskCreationOptions.RunContinuationsAsynchronously)))
                .ToArray();
            var readCount = 0;
            var closeCount = 0;
            var probe = Source.UnfoldResourceAsync(
                    () => Task.FromResult(Done.Instance),
                    _ =>
                    {
                        var ordinal = Interlocked.Increment(ref readCount);
                        if (ordinal > 32)
                            return Task.FromResult(Option<int>.None);
                        if (ordinal % 2 != 0)
                            return Task.FromResult(Option<int>.Create(ordinal));
                        var gate = gates[ordinal / 2 - 1];
                        gate.Started.TrySetResult();
                        return gate.Result.Task;
                    },
                    _ =>
                    {
                        Interlocked.Increment(ref closeCount);
                        return Task.FromResult(Done.Instance);
                    })
                .RunWith(this.SinkProbe<int>(), Sys.Materializer());

            for (var ordinal = 1; ordinal <= 32; ordinal++)
            {
                await probe.RequestAsync(1);
                if (ordinal % 2 == 0)
                {
                    var gate = gates[ordinal / 2 - 1];
                    await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    gate.Result.SetResult(Option<int>.Create(ordinal));
                }
                await probe.ExpectNextAsync(ordinal);
            }
            await probe.RequestAsync(1);
            await probe.ExpectCompleteAsync();
            readCount.Should().Be(33);
            closeCount.Should().Be(1);
        }
    }
}
