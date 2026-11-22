//-----------------------------------------------------------------------
// <copyright file="ArteryOutboundBulkDrainSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.IO;
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
    /// Covers <c>akka.remote.artery.advanced.outbound-bulk-drain</c>: envelopes already queued in a lane
    /// channel leave the encode step as a few multi-frame elements instead of one element per frame, in
    /// order, within the byte and frame caps, and the setting changes nothing when it is off.
    /// </summary>
    public class ArteryOutboundBulkDrainSpec : AkkaSpec
    {
        private readonly ITestOutputHelper _output;

        public ArteryOutboundBulkDrainSpec(ITestOutputHelper output) : base(output)
        {
            _output = output;
        }

        // ---------------------------------------------------------------- stage level

        /// <summary>A message whose first four bytes carry <paramref name="index"/>, padded to <paramref name="payloadLength"/>.</summary>
        private static IOutboundEnvelope Envelope(int index, int payloadLength = 100)
        {
            var payload = new byte[payloadLength];
            BitConverter.GetBytes(index).CopyTo(payload, 0);
            return new OutboundEnvelope(payload, null, "akka://Target@127.0.0.1:1/user/target");
        }

        private int EncodedFrameLength(int payloadLength)
        {
            var envelope = Envelope(0, payloadLength);
            using var writer = ArteryEnvelopeCodec.Encode(
                Sys.Serialization, 1, envelope.SenderPath, envelope.RecipientPath, envelope.Message);
            return writer.WrittenCount;
        }

        private static Channel<IOutboundEnvelope> Queue(params IOutboundEnvelope[] envelopes)
        {
            var channel = Channel.CreateUnbounded<IOutboundEnvelope>();
            foreach (var envelope in envelopes)
                channel.Writer.TryWrite(envelope).Should().BeTrue();
            return channel;
        }

        private Source<ReadOnlySequence<byte>, NotUsed> Encoded(
            Channel<IOutboundEnvelope> channel, long maxBytes = 1 << 20, int maxFrames = 1024)
            => ChannelSource.FromReader(channel.Reader)
                .Via(Flow.FromGraph(new ArteryBulkEncodeStage(
                    Sys.Serialization, 1, channel.Reader, maxBytes, maxFrames)));

        /// <summary>One entry per element; each entry lists the frame lengths and payload indexes it carries.</summary>
        private static List<(int Frame, int Index)> Frames(ReadOnlySequence<byte> element)
        {
            var result = new List<(int, int)>();
            var segment = (ReadOnlySequenceSegment<byte>)element.Start.GetObject()!;
            var end = element.End.GetObject();
            while (true)
            {
                var frame = segment.Memory;
                var decoded = ArteryEnvelopeCodec.Decode(new ReadOnlySequence<byte>(frame.Slice(4)));
                result.Add((frame.Length, BitConverter.ToInt32(decoded.Payload.ToArray(), 0)));
                if (ReferenceEquals(segment, end))
                    break;
                segment = segment.Next!;
            }

            return result;
        }

        private async Task<List<List<(int Frame, int Index)>>> CollectAsync(Source<ReadOnlySequence<byte>, NotUsed> source)
        {
            var elements = await source.RunWith(Sink.Seq<ReadOnlySequence<byte>>(), Sys.Materializer())
                .WaitAsync(10.Seconds());
            var result = new List<List<(int, int)>>();
            foreach (var element in elements)
            {
                result.Add(Frames(element));
                element.DisposeOwnedSegments();
            }

            return result;
        }

        [Fact(DisplayName = "Bulk drain should emit pre-queued envelopes as a few multi-frame elements in FIFO order")]
        public async Task Should_EmitPreQueuedEnvelopesAsFewMultiFrameElements_InOrder()
        {
            const int count = 300;
            const int maxFrames = 128;
            var channel = Queue(Enumerable.Range(0, count).Select(i => Envelope(i)).ToArray());
            channel.Writer.Complete();

            var elements = await CollectAsync(Encoded(channel, maxFrames: maxFrames));

            // 300 queued frames -> 128 + 128 + 44: far fewer elements than frames, never above the frame cap.
            elements.Select(e => e.Count).Should().Equal(128, 128, 44);
            elements.SelectMany(e => e).Select(f => f.Index).Should().Equal(Enumerable.Range(0, count));
        }

        [Fact(DisplayName = "Bulk drain should never exceed the byte cap when a following frame would not fit")]
        public async Task Should_RespectByteCap_AndCarryTheNonFittingFrameToTheNextElement()
        {
            var frameLength = EncodedFrameLength(100);
            var channel = Queue(Enumerable.Range(0, 12).Select(i => Envelope(i)).ToArray());
            channel.Writer.Complete();

            // Room for exactly five frames; one byte more than that must not admit a sixth.
            var cap = 5L * frameLength + 1;
            var elements = await CollectAsync(Encoded(channel, maxBytes: cap));

            elements.Select(e => e.Count).Should().Equal(5, 5, 2);
            elements.Should().OnlyContain(e => e.Sum(f => (long)f.Frame) <= cap);
            elements.SelectMany(e => e).Select(f => f.Index).Should().Equal(Enumerable.Range(0, 12));
        }

        [Fact(DisplayName = "Bulk drain should still send a frame larger than the byte cap, on its own, in order")]
        public async Task Should_SendFrameLargerThanCap_Alone()
        {
            var small = EncodedFrameLength(100);
            var cap = 3L * small;

            // big last-in-batch position, then big first.
            var channel = Queue(Envelope(0), Envelope(1, 4000), Envelope(2), Envelope(3, 4000), Envelope(4), Envelope(5));
            channel.Writer.Complete();

            var elements = await CollectAsync(Encoded(channel, maxBytes: cap));

            elements.Select(e => e.Select(f => f.Index).ToArray()).Should().BeEquivalentTo(
                new[] { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4, 5 } },
                options => options.WithStrictOrdering());
            elements[1].Single().Frame.Should().BeGreaterThan((int)cap);
        }

        [Fact(DisplayName = "Bulk drain should deliver frames encoded before a failing envelope, then fail the stage")]
        public async Task Should_DeliverEarlierFramesThenFail_When_ALaterEnvelopeCannotBeEncoded()
        {
            // A recipient literal over 64KB cannot be encoded (ArteryEnvelopeException).
            var bad = new OutboundEnvelope(new byte[8], null, new string('a', 70_000));
            var channel = Queue(Envelope(0), Envelope(1), bad, Envelope(3));

            var probe = Encoded(channel).RunWith(this.SinkProbe<ReadOnlySequence<byte>>(), Sys.Materializer());
            probe.Request(1);
            var first = await probe.ExpectNextAsync();
            try { Frames(first).Select(f => f.Index).Should().Equal(0, 1); }
            finally { first.DisposeOwnedSegments(); }

            probe.Request(1);
            await probe.ExpectErrorAsync();
        }

        // ---------------------------------------------------------------- transport level

        private static Config TransportConfig(int outboundLanes, bool bulkDrain) => ConfigurationFactory.ParseString($$"""
            akka.loglevel = INFO
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.outbound-lanes = {{outboundLanes}}
            akka.remote.artery.advanced.outbound-bulk-drain = {{(bulkDrain ? "on" : "off")}}
            akka.remote.artery.advanced.flow-diagnostics = on
            akka.remote.artery.advanced.flow-diagnostics-interval = 300ms
            """);

        private sealed class Echo : ReceiveActor
        {
            public Echo() => ReceiveAny(msg => Sender.Tell(msg));
        }

        private static readonly Regex KeyValue = new(@"(\S+?)=(\S+)", RegexOptions.Compiled);

        private async Task<(long Writes, long Frames)> RunRoundTripAsync(int lanes, bool bulkDrain, int count, bool expectFlowTotals)
        {
            var config = TransportConfig(lanes, bulkDrain);
            var systemA = ActorSystem.Create("ArteryBulkA", config);
            var systemB = ActorSystem.Create("ArteryBulkB", config);
            try
            {
                systemB.ActorOf(Props.Create(() => new Echo()), "echo");
                var path = $"akka://{systemB.Name}@127.0.0.1:{RARP.For(systemB).Provider.DefaultAddress.Port!.Value}/user/echo";

                var infos = CreateTestProbe(systemA);
                systemA.EventStream.Subscribe(infos.Ref, typeof(Info));
                var replies = CreateTestProbe(systemA);

                // Sent immediately, before the association exists: everything after the first message
                // queues in the lane channel while the connection and handshake are set up.
                var selection = systemA.ActorSelection(path);
                for (var i = 0; i < count; i++)
                    selection.Tell(i, replies.Ref);

                for (var i = 0; i < count; i++)
                    (await replies.ExpectMsgAsync<int>(10.Seconds())).Should().Be(i, "per-recipient order must be preserved");

                if (!expectFlowTotals)
                    return (0, 0);

                long writes = 0, frames = 0;
                await infos.FishForMessageAsync(e =>
                {
                    if (e is not Info info || info.Message.ToString() is not { } text || !text.StartsWith("ARTERY-FLOW dir=out "))
                        return false;

                    _output.WriteLine(text);
                    foreach (Match m in KeyValue.Matches(text))
                    {
                        if (!long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                            continue;
                        if (m.Groups[1].Value == "out.writes") writes += v;
                        else if (m.Groups[1].Value == "out.frames") frames += v;
                    }

                    return frames >= count;
                }, 20.Seconds());

                return (writes, frames);
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }

        [Theory(DisplayName = "Bulk drain should round-trip in order over a real connection with one and four outbound lanes")]
        [InlineData(1)]
        [InlineData(4)]
        public async Task Should_RoundTripInOrder_When_BulkDrainIsOn(int lanes)
        {
            var (writes, frames) = await RunRoundTripAsync(lanes, bulkDrain: true, count: 400, expectFlowTotals: true);

            frames.Should().BeGreaterThanOrEqualTo(400);
            writes.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(frames);
        }

        [Fact(DisplayName = "Bulk drain should hand the connection many frames per write when a single lane queued a burst")]
        public async Task Should_WriteManyFramesPerWrite_When_SingleLaneBurstWasQueued()
        {
            var (writes, frames) = await RunRoundTripAsync(1, bulkDrain: true, count: 400, expectFlowTotals: true);

            writes.Should().BeLessThan(frames / 2,
                "the burst queued while the handshake was in progress must leave in multi-frame writes, not one write per frame");
        }

        [Fact(DisplayName = "Bulk drain off should keep one write per frame on a single lane")]
        public async Task Should_KeepOneWritePerFrame_When_BulkDrainIsOff()
        {
            var (writes, frames) = await RunRoundTripAsync(1, bulkDrain: false, count: 400, expectFlowTotals: true);

            // The counters are read without a lock, so a window may be torn by one write.
            writes.Should().BeInRange(frames - 2, frames);
        }
    }
}
