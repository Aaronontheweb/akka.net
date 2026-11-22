//-----------------------------------------------------------------------
// <copyright file="ArteryInboundDeferredDecodeSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Tests for <c>akka.remote.artery.advanced.inbound-lane-deferred-decode</c>: with the setting on,
    /// the connection reader hashes the recipient literal's raw UTF-8 bytes to choose a lane and
    /// leaves manifest/recipient/sender decoding to the lane consumer.
    /// </summary>
    public class ArteryInboundDeferredDecodeSpec : AkkaSpec
    {
        private const long OriginUid = 42;
        private const int Lanes = 4;

        public ArteryInboundDeferredDecodeSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static Config ArteryConfig(int inboundLanes) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-lanes = {{inboundLanes}}
            akka.remote.artery.advanced.inbound-lane-deferred-decode = on
            akka.remote.artery.advanced.outbound-message-queue-size = 20000
            """);

        private static int BoundPort(ActorSystem system) => RARP.For(system).Provider.DefaultAddress.Port!.Value;

        private static string RemotePath(ActorSystem system, string localName) =>
            $"akka://{system.Name}@127.0.0.1:{BoundPort(system)}/user/{localName}";

        // ---------------------------------------------------------------- pure hash

        public static IEnumerable<object[]> AsciiPaths()
        {
            foreach (var lanes in new[] { 2, 3, 4, 7, 16 })
            {
                yield return new object[] { "akka://sys@127.0.0.1:25520/user/a", lanes };
                yield return new object[] { "akka://sys@127.0.0.1:25520/user/worker-17#123456789", lanes };
                yield return new object[] { "akka://sys@host.example:1/system/sharding/Orders/shard-9/entity-3", lanes };
                yield return new object[] { "", lanes };
            }
        }

        [Theory(DisplayName = "Deferred decode: the UTF-8 byte hash picks the same lane as the legacy char hash for ASCII paths, whether the literal is one segment or split")]
        [MemberData(nameof(AsciiPaths))]
        public void Byte_hash_equals_char_hash_for_ascii(string path, int lanes)
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            var expected = ArteryLaneHash.FromChars(path, lanes);

            ArteryLaneHash.FromUtf8(new ReadOnlySequence<byte>(bytes), lanes).Should().Be(expected);
            ArteryLaneHash.FromUtf8(Split(bytes), lanes).Should().Be(expected, "segment boundaries must not change the hash");
        }

        [Fact(DisplayName = "Deferred decode: non-ASCII recipient paths hash deterministically, independent of segmentation")]
        public void Byte_hash_is_deterministic_for_non_ascii()
        {
            var bytes = Encoding.UTF8.GetBytes("akka://sys@h:1/user/café-日本");
            var first = ArteryLaneHash.FromUtf8(new ReadOnlySequence<byte>(bytes), Lanes);

            for (var i = 0; i < 10; i++)
                ArteryLaneHash.FromUtf8(new ReadOnlySequence<byte>(bytes), Lanes).Should().Be(first);
            ArteryLaneHash.FromUtf8(Split(bytes), Lanes).Should().Be(first);
        }

        [Fact(DisplayName = "Deferred decode: 64 distinct recipients spread over every one of 4 lanes")]
        public void Byte_hash_spreads_recipients()
        {
            var used = Enumerable.Range(0, 64)
                .Select(i => ArteryLaneHash.FromUtf8(
                    new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes($"akka://sys@127.0.0.1:25520/user/recorder-{i}")), Lanes))
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            used.Should().Equal(0, 1, 2, 3);
        }

        private static ReadOnlySequence<byte> Split(byte[] bytes)
        {
            if (bytes.Length < 2)
                return new ReadOnlySequence<byte>(bytes);

            var first = new Segment(bytes.AsMemory(0, 1));
            var last = first.Append(bytes.AsMemory(1));
            return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

            public Segment Append(ReadOnlyMemory<byte> memory)
            {
                var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
                Next = next;
                return next;
            }
        }

        // ---------------------------------------------------------------- stage harness (raw frames)

        private sealed record Delivery(object Message, string? Sender, string Recipient);

        private static byte[] Preamble() => new[] { (byte)'A', (byte)'K', (byte)'K', (byte)'A', (byte)ArteryStreamId.Ordinary };

        /// <summary>Encodes one frame (length prefix included). <paramref name="mutate"/> may patch envelope bytes; offsets are relative to the envelope start.</summary>
        private byte[] Frame(string? recipient, object message, long origin = OriginUid, Action<byte[]>? mutate = null)
        {
            using var writer = ArteryEnvelopeCodec.Encode(Sys.Serialization, origin, "akka://local/user/sender", recipient, message);
            var bytes = writer.WrittenSpan.ToArray();
            mutate?.Invoke(bytes);
            return bytes;
        }

        private static void SetTag(byte[] frame, int tagOffset, uint tag) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4 + tagOffset), tag);

        private const long SentinelOrigin = 43;
        private const string SentinelRecipient = "akka://local/user/a";

        /// <summary>
        /// Pushes <paramref name="frames"/> through one lane-enabled, deferred-decode connection. Unless
        /// <paramref name="expectedCount"/> is given, a trailing sentinel frame from a second, always-known
        /// origin to <c>/user/a</c> marks the end: gating drops happen on the reader before the sentinel is
        /// routed, and lane drops precede it on the same lane, so once the sentinel arrives every earlier
        /// frame to <c>/user/a</c> has been processed. No wall-clock waits.
        /// </summary>
        private async Task<List<Delivery>> Run(bool knownOrigin, Action<AssociationRegistry>? arrange, int? expectedCount, params byte[][] frames)
        {
            var registry = new AssociationRegistry();
            var context = new AssociationRegistryInboundContext(registry,
                new UniqueAddress(new Address("akka", "local", "127.0.0.1", 1234), 1), (_, _) => { });
            if (knownOrigin)
                context.CompleteHandshake(new UniqueAddress(new Address("akka", "peer", "127.0.0.1", 4321), OriginUid));
            context.CompleteHandshake(new UniqueAddress(new Address("akka", "sentinel", "127.0.0.1", 4322), SentinelOrigin));
            arrange?.Invoke(registry);

            var delivered = new ConcurrentQueue<Delivery>();
            var stage = new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
                inboundLanes: Lanes, inboundLaneBufferSize: 64, inboundContext: context,
                dispatchOrdinary: (m, s, r) => delivered.Enqueue(new Delivery(m, s, r)),
                deferredDecode: true);

            var all = expectedCount is null ? frames.Append(Frame(SentinelRecipient, "sentinel", SentinelOrigin)).ToArray() : frames;
            var bytes = Preamble().Concat(all.SelectMany(f => f)).ToArray();
            var materializer = ActorMaterializer.Create(Sys);
            try
            {
                await Source.Single(new ReadOnlySequence<byte>(bytes)).Via(stage)
                    .RunWith(Sink.Ignore<IInboundEnvelope>(), materializer)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                if (expectedCount is { } count)
                    await AwaitAssertAsync(() => delivered.Count.Should().Be(count), 10.Seconds());
                else
                    await AwaitAssertAsync(() => delivered.Should().Contain(d => (string)d.Message == "sentinel"), 10.Seconds());
                return delivered.Where(d => !Equals(d.Message, "sentinel")).ToList();
            }
            finally
            {
                materializer.Shutdown();
            }
        }

        [Fact(DisplayName = "Deferred decode: the lane consumer decodes manifest, sender and recipient and delivers per-recipient in send order")]
        public async Task Lane_decodes_literals_and_preserves_order_per_recipient()
        {
            const int recipients = 8;
            const int perRecipient = 100;
            var frames = new List<byte[]>();
            for (var i = 0; i < perRecipient; i++)
                for (var r = 0; r < recipients; r++)
                    frames.Add(Frame($"akka://local/user/r-{r}", $"{r}:{i}"));

            var delivered = await Run(true, null, recipients * perRecipient, frames.ToArray());

            delivered.Should().HaveCount(recipients * perRecipient);
            delivered.Should().OnlyContain(d => d.Sender == "akka://local/user/sender");
            for (var r = 0; r < recipients; r++)
            {
                var forRecipient = delivered.Where(d => d.Recipient == $"akka://local/user/r-{r}").Select(d => (string)d.Message).ToArray();
                forRecipient.Should().Equal(Enumerable.Range(0, perRecipient).Select(i => $"{r}:{i}"));
            }
        }

        [Fact(DisplayName = "Deferred decode: a frame with a compressed recipient tag is dropped and later frames still arrive")]
        public async Task Compressed_recipient_tag_is_dropped()
        {
            var delivered = await Run(true, null, null,
                Frame("akka://local/user/a", "bad", mutate: f => SetTag(f, ArteryEnvelopeHeader.RecipientTagOffset, 0xFF00_0000u | 3u)),
                Frame("akka://local/user/a", "good"));

            delivered.Select(d => d.Message).Should().Equal("good");
        }

        [Fact(DisplayName = "Deferred decode: a frame with a compressed manifest tag is dropped and later frames still arrive")]
        public async Task Compressed_manifest_tag_is_dropped()
        {
            var delivered = await Run(true, null, null,
                Frame("akka://local/user/a", "bad", mutate: f => SetTag(f, ArteryEnvelopeHeader.ManifestTagOffset, 0xFF00_0000u | 3u)),
                Frame("akka://local/user/a", "good"));

            delivered.Select(d => d.Message).Should().Equal("good");
        }

        [Fact(DisplayName = "Deferred decode: a frame without a recipient is dropped")]
        public async Task Missing_recipient_is_dropped()
        {
            var delivered = await Run(true, null, null,
                Frame(null, "bad"),
                Frame("akka://local/user/a", "good"));

            delivered.Select(d => d.Message).Should().Equal("good");
        }

        [Fact(DisplayName = "Deferred decode: a malformed recipient literal drops only that frame (detected on the reader)")]
        public async Task Malformed_recipient_literal_drops_one_frame()
        {
            var delivered = await Run(true, null, null,
                Frame("akka://local/user/a", "one"),
                Frame("akka://local/user/a", "bad", mutate: f => SetTag(f, ArteryEnvelopeHeader.RecipientTagOffset, 8u)),
                Frame("akka://local/user/a", "bad-too", mutate: f => SetTag(f, ArteryEnvelopeHeader.RecipientTagOffset, 60_000u)),
                Frame("akka://local/user/a", "two"));

            delivered.Select(d => d.Message).Should().Equal("one", "two");
        }

        [Fact(DisplayName = "Deferred decode: a malformed sender or manifest literal drops only that frame (detected on the lane)")]
        public async Task Malformed_sender_or_manifest_literal_drops_one_frame()
        {
            var delivered = await Run(true, null, null,
                Frame("akka://local/user/a", "one"),
                Frame("akka://local/user/a", "bad-sender", mutate: f => SetTag(f, ArteryEnvelopeHeader.SenderTagOffset, 60_000u)),
                Frame("akka://local/user/a", "bad-manifest", mutate: f => SetTag(f, ArteryEnvelopeHeader.ManifestTagOffset, 8u)),
                Frame("akka://local/user/a", "two"));

            delivered.Select(d => d.Message).Should().Equal("one", "two");
        }

        [Fact(DisplayName = "Deferred decode: frames from an origin with no completed handshake are dropped")]
        public async Task Unknown_origin_is_dropped()
        {
            var delivered = await Run(false, null, null, Frame("akka://local/user/a", "x"));
            delivered.Should().BeEmpty();
        }

        [Fact(DisplayName = "Deferred decode: frames from a quarantined origin are dropped, frames after it are not affected for other origins")]
        public async Task Quarantined_origin_is_dropped()
        {
            var delivered = await Run(true, registry => registry.TryGetByUid(OriginUid)!.Quarantine(OriginUid), null,
                Frame("akka://local/user/a", "x"),
                Frame("akka://local/user/a", "y"));
            delivered.Should().BeEmpty();
        }

        // ---------------------------------------------------------------- two real systems

        [Fact(DisplayName = "Deferred decode: Tell, ActorSelection (exact and wildcard) and Ask round trips work between two systems at inbound-lanes=4")]
        public async Task Tell_Selection_And_Ask_Round_Trip()
        {
            var systemB = ActorSystem.Create("ArteryDeferredDecodeB", ArteryConfig(Lanes));
            var systemA = ActorSystem.Create("ArteryDeferredDecodeA", ArteryConfig(Lanes));
            try
            {
                var probeB = CreateTestProbe(systemB);
                systemB.ActorOf(Props.Create(() => new Echo()), "echo");
                systemB.ActorOf(Props.Create(() => new Forward(probeB)), "sel-1");
                systemB.ActorOf(Props.Create(() => new Forward(probeB)), "sel-2");

                var echo = await systemA.ActorSelection(RemotePath(systemB, "echo")).ResolveOne(10.Seconds());

                (await echo.Ask<string>("tell-me", 10.Seconds())).Should().Be("tell-me");
                (await echo.Ask<string>("ask-me", 10.Seconds())).Should().Be("ask-me");

                systemA.ActorSelection(RemotePath(systemB, "sel-1")).Tell("exact");
                await probeB.ExpectMsgAsync("exact", 10.Seconds());

                systemA.ActorSelection($"akka://{systemB.Name}@127.0.0.1:{BoundPort(systemB)}/user/sel-*").Tell("wild");
                await probeB.ExpectMsgAsync("wild", 10.Seconds());
                await probeB.ExpectMsgAsync("wild", 10.Seconds());
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(10.Seconds());
                await systemB.Terminate().AwaitWithTimeout(10.Seconds());
            }
        }

        private sealed class Echo : ReceiveActor
        {
            public Echo() => ReceiveAny(m => Sender.Tell(m));
        }

        private sealed class Forward : ReceiveActor
        {
            public Forward(IActorRef target) => ReceiveAny(m => target.Tell(m));
        }
    }

    /// <summary>Re-runs <see cref="ArteryInboundLanesSpec"/> with deferred decode on.</summary>
    public class ArteryInboundLanesDeferredDecodeSpec : ArteryInboundLanesSpec
    {
        public ArteryInboundLanesDeferredDecodeSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig(int inboundLanes = 1) =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.inbound-lane-deferred-decode = on")
                .WithFallback(base.ArteryConfig(inboundLanes));
    }

    /// <summary>Re-runs <see cref="ArteryInboundLanesQuarantineSpec"/> with deferred decode on.</summary>
    public class ArteryInboundLanesQuarantineDeferredDecodeSpec : ArteryInboundLanesQuarantineSpec
    {
        public ArteryInboundLanesQuarantineDeferredDecodeSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig(int inboundLanes = 1) =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.inbound-lane-deferred-decode = on")
                .WithFallback(base.ArteryConfig(inboundLanes));
    }
}
