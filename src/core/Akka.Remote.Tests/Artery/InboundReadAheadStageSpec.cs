//-----------------------------------------------------------------------
// <copyright file="InboundReadAheadStageSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Tests for <see cref="InboundReadAheadStage"/> (<c>akka.remote.artery.advanced.inbound-read-batching</c>):
    /// stage-level order/back-pressure/completion via stream probes, plus an end-to-end round trip
    /// over the real Artery TCP transport with the switch on.
    /// </summary>
    public class InboundReadAheadStageSpec : AkkaSpec
    {
        public InboundReadAheadStageSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static ReadOnlySequence<byte> Chunk(int index, int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i + 4 <= length; i += 4)
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i), index);
            return new ReadOnlySequence<byte>(bytes);
        }

        private (TestPublisher.Probe<ReadOnlySequence<byte>> Pub, TestSubscriber.Probe<ReadOnlySequence<byte>> Sub) Materialize(long maxBytes)
        {
            return this.SourceProbe<ReadOnlySequence<byte>>()
                .Via(new InboundReadAheadStage(maxBytes))
                .ToMaterialized(this.SinkProbe<ReadOnlySequence<byte>>(), Keep.Both)
                // One-element boundary buffers so upstream demand mirrors the stage's own pulls.
                .WithAttributes(Attributes.CreateInputBuffer(1, 1))
                .Run(Sys.Materializer());
        }

        private static List<int> DecodeIndices(IEnumerable<ReadOnlySequence<byte>> batches)
        {
            var result = new List<int>();
            foreach (var batch in batches)
            {
                var bytes = batch.ToArray();
                for (var i = 0; i + 4 <= bytes.Length; i += 4)
                    result.Add(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i)));
            }

            return result;
        }

        [Fact(DisplayName = "Should_PreserveChunkOrderAndCoalesce_When_LargeBurstArrivesWithoutDemand")]
        public async Task Should_PreserveChunkOrderAndCoalesce_When_LargeBurstArrivesWithoutDemand()
        {
            const int chunks = 500;
            const int chunkLength = 16;
            var (pub, sub) = Materialize(maxBytes: 1024 * 1024);
            await sub.EnsureSubscriptionAsync();

            for (var i = 0; i < chunks; i++)
                await pub.SendNextAsync(Chunk(i, chunkLength));

            var received = new List<ReadOnlySequence<byte>>();
            long total = 0;
            while (total < chunks * chunkLength)
            {
                await sub.RequestAsync(1);
                var batch = await sub.ExpectNextAsync(TimeSpan.FromSeconds(5));
                received.Add(batch);
                total += batch.Length;
            }

            total.Should().Be(chunks * chunkLength);
            received.Count.Should().BeLessThan(chunks, "chunks buffered while downstream had no demand are handed over together");
            var expected = Enumerable.Range(0, chunks).SelectMany(i => Enumerable.Repeat(i, chunkLength / 4)).ToList();
            DecodeIndices(received).Should().Equal(expected);

            await pub.SendCompleteAsync();
            await sub.RequestAsync(1);
            await sub.ExpectCompleteAsync();
        }

        [Fact(DisplayName = "Should_StopPullingUpstream_When_BufferedBytesReachTheBound")]
        public async Task Should_StopPullingUpstream_When_BufferedBytesReachTheBound()
        {
            const int chunkLength = 40;
            const long maxBytes = 100;
            var (pub, sub) = Materialize(maxBytes);
            await sub.EnsureSubscriptionAsync();

            var sent = 0;
            while (sent < 50)
            {
                if (pub.Pending == 0)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try
                    {
                        await pub.ExpectRequestAsync(cts.Token);
                    }
                    catch (Exception)
                    {
                        break; // no more demand: back-pressured
                    }
                }

                await pub.SendNextAsync(Chunk(sent, chunkLength));
                sent++;
            }

            // Stage buffers until >= maxBytes (3 chunks), plus at most one element held by the
            // one-element boundary buffer in front of it.
            sent.Should().BeInRange(3, 4, "read-ahead must stop once the byte bound is reached");

            await sub.RequestAsync(1);
            var batch = await sub.ExpectNextAsync(TimeSpan.FromSeconds(5));
            batch.Length.Should().BeGreaterOrEqualTo(3 * chunkLength);

            // Handing the buffer downstream frees capacity, so reading resumes.
            if (pub.Pending == 0)
                await pub.ExpectRequestAsync();

            await pub.SendCompleteAsync();
            var rest = new List<ReadOnlySequence<byte>> { batch };
            while (true)
            {
                await sub.RequestAsync(1);
                var ev = await sub.ExpectEventAsync();
                if (ev is TestSubscriber.OnNext<ReadOnlySequence<byte>> next)
                    rest.Add(next.Element);
                else
                {
                    ev.Should().BeOfType<TestSubscriber.OnComplete>();
                    break;
                }
            }

            DecodeIndices(rest).Should().Equal(Enumerable.Range(0, sent).SelectMany(i => Enumerable.Repeat(i, chunkLength / 4)));
        }

        [Fact(DisplayName = "Should_DeliverLoneChunkPromptly_When_DownstreamIsWaiting")]
        public async Task Should_DeliverLoneChunkPromptly_When_DownstreamIsWaiting()
        {
            var (pub, sub) = Materialize(maxBytes: 256 * 1024);
            await sub.RequestAsync(1);
            await pub.SendNextAsync(Chunk(7, 8));
            var batch = await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));
            DecodeIndices(new[] { batch }).Should().Equal(7, 7);
            await sub.CancelAsync();
            await pub.ExpectCancellationAsync();
        }

        [Fact(DisplayName = "Should_FlushBufferedBytesBeforeCompleting_When_UpstreamFinishes")]
        public async Task Should_FlushBufferedBytesBeforeCompleting_When_UpstreamFinishes()
        {
            var (pub, sub) = Materialize(maxBytes: 256 * 1024);
            await sub.EnsureSubscriptionAsync();
            await pub.SendNextAsync(Chunk(1, 8));
            await pub.SendNextAsync(Chunk(2, 8));
            await pub.SendCompleteAsync();
            await sub.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(200));

            await sub.RequestAsync(1);
            var batch = await sub.ExpectNextAsync(TimeSpan.FromSeconds(3));
            DecodeIndices(new[] { batch }).Should().Equal(1, 1, 2, 2);
            await sub.ExpectCompleteAsync();
        }

        [Fact(DisplayName = "Should_FailImmediately_When_UpstreamFails")]
        public async Task Should_FailImmediately_When_UpstreamFails()
        {
            var (pub, sub) = Materialize(maxBytes: 256 * 1024);
            await sub.EnsureSubscriptionAsync();
            await pub.SendNextAsync(Chunk(1, 8));
            var boom = new Exception("boom");
            await pub.SendErrorAsync(boom);
            (await sub.ExpectErrorAsync()).Should().BeSameAs(boom);
        }

        private static Config ArteryConfig(bool batching) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-read-batching = {{(batching ? "on" : "off")}}
            akka.remote.artery.advanced.inbound-read-ahead-max-bytes = 64k
            akka.remote.artery.advanced.outbound-message-queue-size = 20000
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
                Receive<string>(s => Sender.Tell(s));
                Receive<GetHistory>(_ => Sender.Tell(new History(_received.ToArray())));
            }
        }

        [Fact(DisplayName = "Should_PreserveOrderAndReplyPromptly_When_InboundReadBatchingIsOnEndToEnd")]
        public async Task Should_PreserveOrderAndReplyPromptly_When_InboundReadBatchingIsOnEndToEnd()
        {
            const int messages = 5000;
            var systemB = ActorSystem.Create("InboundReadAheadB", ArteryConfig(batching: true));
            var systemA = ActorSystem.Create("InboundReadAheadA", ArteryConfig(batching: true));
            try
            {
                var recorder = systemB.ActorOf(Props.Create(() => new OrderRecorder()), "recorder");
                var remote = await systemA.ActorSelection(RemotePath(systemB, "recorder")).ResolveOne(TimeSpan.FromSeconds(10));

                // Lone request/reply in both directions (both sides' inbound connections batch).
                (await remote.Ask<string>("ping", TimeSpan.FromSeconds(5))).Should().Be("ping");

                for (var i = 0; i < messages; i++)
                    remote.Tell(i, ActorRefs.NoSender);

                var expected = Enumerable.Range(0, messages).ToArray();
                var probe = CreateTestProbe(systemA);
                await AwaitAssertAsync(async () =>
                {
                    remote.Tell(new GetHistory(), probe.Ref);
                    var history = await probe.ExpectMsgAsync<History>(TimeSpan.FromSeconds(5));
                    history.Values.Should().Equal(expected);
                }, TimeSpan.FromSeconds(30));

                (await remote.Ask<string>("pong", TimeSpan.FromSeconds(5))).Should().Be("pong");
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }

            systemA.WhenTerminated.IsCompleted.Should().BeTrue();
            systemB.WhenTerminated.IsCompleted.Should().BeTrue();
        }
    }
}
