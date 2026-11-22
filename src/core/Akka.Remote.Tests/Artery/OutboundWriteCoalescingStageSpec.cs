//-----------------------------------------------------------------------
// <copyright file="OutboundWriteCoalescingStageSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.IO;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit;
using FluentAssertions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Unit tests for <see cref="OutboundWriteCoalescingStage"/>: byte-identical in-order coalescing,
    /// no delay / no copy when idle, the maxBytes back-pressure bound, oversized-frame handling and
    /// exactly-once disposal / drop reporting.
    /// </summary>
    public class OutboundWriteCoalescingStageSpec : AkkaSpec
    {
        private static readonly TimeSpan Short = TimeSpan.FromSeconds(3);

        public OutboundWriteCoalescingStageSpec(ITestOutputHelper output) : base(output)
        {
        }

        private sealed class TrackingOwner : IMemoryOwner<byte>
        {
            private readonly byte[] _bytes;

            public TrackingOwner(byte[] bytes) => _bytes = bytes;

            public int DisposeCount { get; private set; }

            public Memory<byte> Memory => _bytes;

            public void Dispose() => DisposeCount++;
        }

        private static byte[] Pattern(int length, byte seed)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
                bytes[i] = unchecked((byte)(seed + i));
            return bytes;
        }

        private static (ReadOnlySequence<byte> Frame, TrackingOwner Owner, byte[] Bytes) Frame(int length, byte seed)
        {
            var bytes = Pattern(length, seed);
            var owner = new TrackingOwner(bytes);
            return (OwnedSequenceSegment.Create(owner, bytes.Length), owner, bytes);
        }

        /// <summary>Next demand signalled to the publisher probe, or 0 if none arrives within <paramref name="wait"/>.</summary>
        private static async Task<long> NextDemandAsync(TestPublisher.Probe<ReadOnlySequence<byte>> publisher, TimeSpan wait)
        {
            while (true)
            {
                var msg = await publisher.Probe.ReceiveOneAsync(wait, CancellationToken.None);
                if (msg is null)
                    return 0;
                if (msg is TestPublisher.RequestMore more)
                    return more.NrOfElements;
            }
        }

        /// <summary>Receives elements until <paramref name="expectedBytes"/> were seen, disposing each after copying.</summary>
        private static async Task<(List<byte> Bytes, int Elements)> CollectAsync(
            TestSubscriber.Probe<ReadOnlySequence<byte>> sink, int expectedBytes)
        {
            var bytes = new List<byte>();
            var elements = 0;
            while (bytes.Count < expectedBytes)
            {
                var element = await sink.ExpectNextAsync(Short);
                elements++;
                bytes.AddRange(element.ToArray());
                element.DisposeOwnedSegments();
            }
            return (bytes, elements);
        }

        [Fact(DisplayName = "Should_PreserveOrderAndCoalesce_When_BurstArrivesWithoutDownstreamDemand")]
        public async Task Should_PreserveOrderAndCoalesce_When_BurstArrivesWithoutDownstreamDemand()
        {
            var frames = Enumerable.Range(0, 50).Select(i => Frame(37 + i * 3, (byte)i)).ToArray();
            var expected = frames.SelectMany(f => f.Bytes).ToArray();

            var sink = Source.From(frames.Select(f => f.Frame).ToArray())
                .Via(new OutboundWriteCoalescingStage(4096))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());

            try
            {
                await Task.Delay(300); // let the stage buffer while nothing is requested
                sink.Request(1000);

                var (bytes, elements) = await CollectAsync(sink, expected.Length);
                await sink.ExpectCompleteAsync();

                bytes.Should().Equal(expected);
                elements.Should().BeLessThan(frames.Length);
                frames.Should().OnlyContain(f => f.Owner.DisposeCount == 1);
            }
            finally { sink.Cancel(); }
        }

        [Fact(DisplayName = "Should_PassFrameThroughUnchanged_When_DownstreamIsWaitingAndBufferIsEmpty")]
        public async Task Should_PassFrameThroughUnchanged_When_DownstreamIsWaitingAndBufferIsEmpty()
        {
            var publisher = this.CreatePublisherProbe<ReadOnlySequence<byte>>();
            var sink = Source.FromPublisher(publisher)
                .Via(new OutboundWriteCoalescingStage(4096))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());
            var (frame, owner, bytes) = Frame(120, 9);

            try
            {
                sink.Request(1);
                await publisher.ExpectRequestAsync();
                await Task.Delay(100); // make sure the stage has seen the downstream pull

                await publisher.SendNextAsync(frame);
                var received = await sink.ExpectNextAsync(TimeSpan.FromSeconds(1));

                received.ToArray().Should().Equal(bytes);
                received.Start.GetObject().Should().BeSameAs(frame.Start.GetObject());
                owner.DisposeCount.Should().Be(0);
                received.DisposeOwnedSegments();
                owner.DisposeCount.Should().Be(1);
            }
            finally { sink.Cancel(); }
        }

        [Fact(DisplayName = "Should_StopPullingUpstream_When_BufferIsFullAndDownstreamHasNotPulled")]
        public async Task Should_StopPullingUpstream_When_BufferIsFullAndDownstreamHasNotPulled()
        {
            var publisher = this.CreatePublisherProbe<ReadOnlySequence<byte>>();
            var sink = Source.FromPublisher(publisher)
                .Via(new OutboundWriteCoalescingStage(1024))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());

            try
            {
                await publisher.EnsureSubscriptionAsync();
                long demand = 0, sent = 0;
                while (sent < 500)
                {
                    var more = await NextDemandAsync(publisher, TimeSpan.FromMilliseconds(300));
                    if (more == 0)
                        break;
                    demand += more;
                    for (; sent < demand; sent++)
                        publisher.Subscription.SendNext(Frame(100, 1).Frame);
                }

                // ~10 frames fit in the buffer, one is held; the rest is whatever the fused
                // stream boundary pre-fetched (its input buffer, at most 16 by default).
                sent.Should().BeGreaterThan(10);
                sent.Should().BeLessOrEqualTo(1024 / 100 + 2 + 16);

                sink.Request(100);
                (await NextDemandAsync(publisher, Short)).Should().BeGreaterThan(0);
            }
            finally { sink.Cancel(); }
        }

        [Fact(DisplayName = "Should_EmitOversizedFrameAfterBufferUnchanged_When_FrameExceedsMaxBytes")]
        public async Task Should_EmitOversizedFrameAfterBufferUnchanged_When_FrameExceedsMaxBytes()
        {
            var small = Frame(50, 1);
            var big = Frame(1000, 77);
            var tail = Frame(30, 200);

            var sink = Source.From(new[] { small.Frame, big.Frame, tail.Frame })
                .Via(new OutboundWriteCoalescingStage(256))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());

            try
            {
                await Task.Delay(300); // small is buffered, big is held
                sink.Request(3);

                var first = await sink.ExpectNextAsync(Short);
                first.ToArray().Should().Equal(small.Bytes);
                first.DisposeOwnedSegments();

                var second = await sink.ExpectNextAsync(Short);
                second.ToArray().Should().Equal(big.Bytes);
                second.Start.GetObject().Should().BeSameAs(big.Frame.Start.GetObject());
                second.DisposeOwnedSegments();

                var third = await sink.ExpectNextAsync(Short);
                third.ToArray().Should().Equal(tail.Bytes);
                third.DisposeOwnedSegments();

                await sink.ExpectCompleteAsync();
                new[] { small, big, tail }.Should().OnlyContain(f => f.Owner.DisposeCount == 1);
            }
            finally { sink.Cancel(); }
        }

        [Fact(DisplayName = "Should_FlushBufferThenComplete_When_UpstreamCompletesWithBufferedFrames")]
        public async Task Should_FlushBufferThenComplete_When_UpstreamCompletesWithBufferedFrames()
        {
            long dropped = 0;
            var publisher = this.CreatePublisherProbe<ReadOnlySequence<byte>>();
            var sink = Source.FromPublisher(publisher)
                .Via(new OutboundWriteCoalescingStage(4096, n => Interlocked.Add(ref dropped, n)))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());
            var frames = Enumerable.Range(0, 5).Select(i => Frame(100, (byte)(i * 20))).ToArray();

            try
            {
                foreach (var f in frames)
                    await publisher.SendNextAsync(f.Frame);
                await publisher.SendCompleteAsync();
                await Task.Delay(300);

                sink.Request(1);
                var (bytes, _) = await CollectAsync(sink, 500);
                await sink.ExpectCompleteAsync();

                bytes.Should().Equal(frames.SelectMany(f => f.Bytes));
                frames.Should().OnlyContain(f => f.Owner.DisposeCount == 1);
                Interlocked.Read(ref dropped).Should().Be(0);
            }
            finally { sink.Cancel(); }
        }

        [Fact(DisplayName = "Should_ReportDroppedBytesAndDisposeOwners_When_DownstreamCancelsWithBufferedFrames")]
        public async Task Should_ReportDroppedBytesAndDisposeOwners_When_DownstreamCancelsWithBufferedFrames()
        {
            long dropped = 0;
            var calls = 0;
            var publisher = this.CreatePublisherProbe<ReadOnlySequence<byte>>();
            var sink = Source.FromPublisher(publisher)
                .Via(new OutboundWriteCoalescingStage(4096, n =>
                {
                    Interlocked.Add(ref dropped, n);
                    Interlocked.Increment(ref calls);
                }))
                .RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());
            var frames = Enumerable.Range(0, 5).Select(i => Frame(100, (byte)i)).ToArray();

            foreach (var f in frames)
                await publisher.SendNextAsync(f.Frame);
            await Task.Delay(300); // all five are copied into the buffer; nothing was requested

            sink.Cancel();

            await AwaitAssertAsync(
                () =>
                {
                    Interlocked.Read(ref dropped).Should().Be(500);
                    Volatile.Read(ref calls).Should().Be(1);
                    frames.Should().OnlyContain(f => f.Owner.DisposeCount == 1);
                },
                Short,
                TimeSpan.FromMilliseconds(20));
        }
    }
}
