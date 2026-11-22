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
using System.Threading;
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

        private static Config ArteryConfig(int inboundLanes, bool deferred = true) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.inbound-lanes = {{inboundLanes}}
            akka.remote.artery.advanced.inbound-lane-deferred-decode = {{(deferred ? "on" : "off")}}
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

        [Fact(DisplayName = "Deferred decode: the byte hash is identical for every two-segment split of the recipient literal, including inside multi-byte characters")]
        public void Byte_hash_is_independent_of_every_split_offset()
        {
            var path = "akka://sys@127.0.0.1:25520/user/caf\u00e9-\u65e5\u672c-worker";
            var bytes = Encoding.UTF8.GetBytes(path);
            var expected = ArteryLaneHash.FromUtf8(new ReadOnlySequence<byte>(bytes), Lanes);

            for (var offset = 1; offset < bytes.Length; offset++)
                ArteryLaneHash.FromUtf8(TwoSegments(bytes, offset), Lanes).Should().Be(expected, $"split at {offset}");
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

        private static ReadOnlySequence<byte> Split(byte[] bytes) => TwoSegments(bytes, Math.Min(1, bytes.Length));

        /// <summary>A two-segment sequence over <paramref name="bytes"/> split at <paramref name="offset"/> (1 .. Length-1; otherwise a single segment).</summary>
        private static ReadOnlySequence<byte> TwoSegments(byte[] bytes, int offset)
        {
            if (offset <= 0 || offset >= bytes.Length)
                return new ReadOnlySequence<byte>(bytes);

            var first = new Segment(bytes.AsMemory(0, offset));
            var last = first.Append(bytes.AsMemory(offset));
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

        private ActorMaterializer? _materializer;

        private const long SentinelOrigin = 43;
        private const string SentinelRecipient = "akka://local/user/a";

        private static AssociationRegistryInboundContext NewContext(bool knownOrigin, Action<AssociationRegistry>? arrange)
        {
            var registry = new AssociationRegistry();
            var context = new AssociationRegistryInboundContext(registry,
                new UniqueAddress(new Address("akka", "local", "127.0.0.1", 1234), 1), (_, _) => { });
            if (knownOrigin)
                context.CompleteHandshake(new UniqueAddress(new Address("akka", "peer", "127.0.0.1", 4321), OriginUid));
            context.CompleteHandshake(new UniqueAddress(new Address("akka", "sentinel", "127.0.0.1", 4322), SentinelOrigin));
            arrange?.Invoke(registry);
            return context;
        }

        private Task<List<Delivery>> Run(bool knownOrigin, Action<AssociationRegistry>? arrange, int? expectedCount, params byte[][] frames) =>
            RunCore(knownOrigin, arrange, expectedCount, null, frames);

        /// <summary>
        /// Pushes <paramref name="frames"/> through one lane-enabled, deferred-decode connection, delivered
        /// as the chunks <paramref name="split"/> produces from the whole byte stream (default: one chunk).
        /// Unless <paramref name="expectedCount"/> is given, a trailing sentinel frame from a second,
        /// always-known origin to <c>/user/a</c> marks the end: gating drops happen on the reader before the
        /// sentinel is routed, and lane drops precede it on the same lane, so once the sentinel arrives every
        /// earlier frame to <c>/user/a</c> has been processed. No wall-clock waits besides a failure timeout.
        /// </summary>
        private async Task<List<Delivery>> RunCore(
            bool knownOrigin, Action<AssociationRegistry>? arrange, int? expectedCount,
            Func<byte[], IEnumerable<byte[]>>? split, byte[][] frames)
        {
            var context = NewContext(knownOrigin, arrange);
            var delivered = new ConcurrentQueue<Delivery>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stage = new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
                inboundLanes: Lanes, inboundLaneBufferSize: 64, inboundContext: context,
                dispatchOrdinary: (m, s, r) =>
                {
                    delivered.Enqueue(new Delivery(m, s, r));
                    if (expectedCount is { } count ? delivered.Count >= count : Equals(m, "sentinel"))
                        done.TrySetResult(true);
                },
                deferredDecode: true);

            var all = expectedCount is null ? frames.Append(Frame(SentinelRecipient, "sentinel", SentinelOrigin)).ToArray() : frames;
            var bytes = Preamble().Concat(all.SelectMany(f => f)).ToArray();
            var chunks = split is null ? new[] { bytes } : split(bytes).ToArray();
            chunks.SelectMany(c => c).Should().Equal(bytes, "the split must preserve the byte stream");

            // One materializer per test instance, reused by every run (a test may run hundreds of
            // streams); it is released with the actor system when the spec is disposed.
            _materializer ??= ActorMaterializer.Create(Sys);
            await Source.From(chunks.Select(c => new ReadOnlySequence<byte>(c))).Via(stage)
                .RunWith(Sink.Ignore<IInboundEnvelope>(), _materializer)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return delivered.Where(d => !Equals(d.Message, "sentinel")).ToList();
        }

        [Fact(DisplayName = "Deferred decode: the stage honours the flag only when lanes > 1")]
        public void Stage_flag_requires_lanes_above_one()
        {
            var context = NewContext(true, null);
            ArteryInboundProcessingStage Create(int lanes, bool deferred) =>
                new(65536, 65536, Sys.Serialization, Sys.Scheduler, inboundLanes: lanes, inboundLaneBufferSize: 4,
                    inboundContext: context, dispatchOrdinary: (_, _, _) => { }, deferredDecode: deferred);

            Create(4, true).DeferredDecode.Should().BeTrue();
            Create(4, false).DeferredDecode.Should().BeFalse();
            Create(1, true).DeferredDecode.Should().BeFalse("lanes = 1 never materializes lane machinery");
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

        // ---------------------------------------------------------------- multi-segment frame bodies

        private static string Describe(IEnumerable<Delivery> delivered) => string.Join("|",
            delivered.GroupBy(d => d.Recipient)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key + "=>" + string.Join(",", g.Select(d => $"{d.Message}@{d.Sender}"))));

        [Fact(DisplayName = "Deferred decode: Decode and the recipient byte hash agree for every two-segment split of a frame body (lane-side Decode and reader-side hash on multi-segment input)")]
        public void Decode_over_every_split_of_a_frame_body_gives_same_literals_and_lane()
        {
            const string recipient = "akka://local/user/caf\u00e9-\u65e5-worker-17";
            var frame = Frame(recipient, "payload");
            var body = frame[4..]; // strip the length prefix the frame parser consumes
            var baselineSequence = new ReadOnlySequence<byte>(body);
            var baseline = ArteryEnvelopeCodec.Decode(baselineSequence);
            baseline.TryGetManifest(out var manifest);
            var expectedLane = ArteryLaneHash.FromUtf8(baseline.GetRecipientLiteralBytes(), Lanes);

            for (var offset = 1; offset < body.Length; offset++)
            {
                var decoded = ArteryEnvelopeCodec.Decode(TwoSegments(body, offset));

                ArteryLaneHash.FromUtf8(decoded.GetRecipientLiteralBytes(), Lanes).Should().Be(expectedLane, $"split at {offset}");
                decoded.TryGetRecipientPath(out var r).Should().BeTrue();
                r.Should().Be(recipient, $"split at {offset}");
                decoded.TryGetSenderPath(out var sender).Should().BeTrue();
                sender.Should().Be("akka://local/user/sender", $"split at {offset}");
                decoded.TryGetManifest(out var m).Should().BeTrue();
                m.Should().Be(manifest, $"split at {offset}");
                decoded.Payload.ToArray().Should().Equal(baseline.Payload.ToArray(), $"split at {offset}");
            }
        }

        [Fact(DisplayName = "Deferred decode: the stage delivers identically when the byte stream is cut in two at every offset (inside the preamble, length fields, header and each literal)")]
        public async Task Two_chunks_at_every_offset_deliver_identically()
        {
            var frames = new[]
            {
                Frame("akka://local/user/r-0", "m0"),
                Frame("akka://local/user/caf\u00e9-\u65e5", "m1"),
                Frame("akka://local/user/r-0", "m2")
            };

            var baseline = Describe(await RunCore(true, null, 3, null, frames));
            baseline.Should().Contain("m0@akka://local/user/sender,m2@akka://local/user/sender");

            var total = Preamble().Length + frames.Sum(f => f.Length);
            for (var offset = 1; offset < total; offset++)
            {
                var o = offset;
                var actual = Describe(await RunCore(true, null, 3, b => new[] { b[..o], b[o..] }, frames));
                actual.Should().Be(baseline, $"split at {o}");
            }
        }

        [Fact(DisplayName = "Deferred decode: seeded random chunkings and one-byte chunks deliver identically, in order per recipient")]
        public async Task Random_chunkings_deliver_identically()
        {
            const int recipients = 8;
            const int perRecipient = 5;
            var frames = new List<byte[]>();
            for (var i = 0; i < perRecipient; i++)
                for (var r = 0; r < recipients; r++)
                    frames.Add(Frame($"akka://local/user/r-{r}", $"{r}:{i}"));

            var count = recipients * perRecipient;
            var baseline = Describe(await RunCore(true, null, count, null, frames.ToArray()));

            for (var seed = 0; seed < 25; seed++)
            {
                var random = new Random(seed);
                var actual = Describe(await RunCore(true, null, count, b => RandomChunks(b, random), frames.ToArray()));
                actual.Should().Be(baseline, $"seed {seed}");
            }

            var oneByte = Describe(await RunCore(true, null, count, b => b.Select(x => new[] { x }), frames.ToArray()));
            oneByte.Should().Be(baseline);
        }

        private static IEnumerable<byte[]> RandomChunks(byte[] bytes, Random random)
        {
            var position = 0;
            while (position < bytes.Length)
            {
                var size = Math.Min(random.Next(1, 23), bytes.Length - position);
                yield return bytes[position..(position + size)];
                position += size;
            }
        }

        [Fact(DisplayName = "Deferred decode: items parked behind a blocked lane still decode to the original values after hundreds of later frames were parsed across chunk boundaries")]
        public async Task Delayed_lane_items_keep_valid_buffers()
        {
            // Contract guarded: the stage's input contract is that upstream NEVER mutates or reuses a chunk
            // after delivering it (production: TcpConnection.PipeReadToActor.Read hands out a fresh
            // never-mutated byte[] per read). This test therefore does NOT overwrite delivered chunks.
            // It guards the stage's own side: queued lane items hold the frame body (header, literals and
            // payload) by reference, so the frame parser and the reader must not recycle segments or scratch
            // buffers while those items wait. One lane is blocked inside dispatch; the whole stream is parsed
            // (in 7-byte chunks, so most frames span chunk boundaries) while earlier items still wait; only
            // then is the lane released and every delayed item's sender, recipient, manifest and payload must
            // still decode to the original values, in order.
            const string gatedRecipient = "akka://local/user/gated";
            const int gatedFrames = 150;
            const int otherRecipients = 8;
            const int otherPerRecipient = 50;

            var expected = new Dictionary<string, List<string>>();
            var frames = new List<byte[]>();
            void Add(string recipient, string message)
            {
                if (!expected.TryGetValue(recipient, out var list))
                    expected[recipient] = list = new List<string>();
                list.Add(message);
                frames.Add(Frame(recipient, message));
            }

            for (var i = 0; i < Math.Max(gatedFrames, otherPerRecipient); i++)
            {
                if (i < gatedFrames)
                    Add(gatedRecipient, $"gated:{i}:{new string('g', i % 31)}");
                if (i < otherPerRecipient)
                    for (var r = 0; r < otherRecipients; r++)
                        Add($"akka://local/user/other-{r}", $"other-{r}:{i}:{new string('o', (i + r) % 29)}");
            }

            var total = frames.Count;
            using var gate = new ManualResetEventSlim(false);
            var delivered = new ConcurrentQueue<Delivery>();
            var allDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stage = new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
                inboundLanes: Lanes, inboundLaneBufferSize: 4096, inboundContext: NewContext(true, null),
                dispatchOrdinary: (m, s, r) =>
                {
                    if (r == gatedRecipient)
                        gate.Wait(TimeSpan.FromSeconds(60));
                    delivered.Enqueue(new Delivery(m, s, r));
                    if (delivered.Count >= total)
                        allDelivered.TrySetResult(true);
                },
                deferredDecode: true);

            var bytes = Preamble().Concat(frames.SelectMany(f => f)).ToArray();
            var chunks = Enumerable.Range(0, (bytes.Length + 6) / 7)
                .Select(i => new ReadOnlySequence<byte>(bytes[(i * 7)..Math.Min(bytes.Length, i * 7 + 7)]))
                .ToArray();

            var materializer = ActorMaterializer.Create(Sys);
            try
            {
                // The reader parses and routes every frame while the gated lane is still blocked.
                await Source.From(chunks).Via(stage).RunWith(Sink.Ignore<IInboundEnvelope>(), materializer)
                    .WaitAsync(TimeSpan.FromSeconds(30));

                delivered.Should().NotContain(d => d.Recipient == gatedRecipient, "the gated lane is blocked on its first item");

                gate.Set();
                await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(30));

                foreach (var (recipient, messages) in expected)
                {
                    var actual = delivered.Where(d => d.Recipient == recipient).ToArray();
                    actual.Select(d => (string)d.Message).Should().Equal(messages, recipient);
                    actual.Should().OnlyContain(d => d.Sender == "akka://local/user/sender");
                }
            }
            finally
            {
                gate.Set();
                materializer.Shutdown();
            }
        }

        // ---------------------------------------------------------------- observable difference between the modes

        [Theory(DisplayName = "Deferred decode: a frame from an unknown origin with a malformed manifest literal logs the unknown-origin warning when on and the transient-decode warning when off")]
        [InlineData(true, "from unknown origin", "Transient error decoding an inbound Artery frame")]
        [InlineData(false, "Transient error decoding an inbound Artery frame", "from unknown origin")]
        public async Task Malformed_manifest_from_unknown_origin_logs_differently_per_mode(bool deferred, string expectedWarning, string unexpectedWarning)
        {
            var system = ActorSystem.Create($"ArteryDeferredLog{(deferred ? "On" : "Off")}", ArteryConfig(Lanes, deferred));
            try
            {
                var warnings = CreateTestProbe(system);
                system.EventStream.Subscribe(warnings.Ref, typeof(Akka.Event.Warning));

                var frame = Frame("akka://local/user/a", "x", origin: 999,
                    mutate: f => SetTag(f, ArteryEnvelopeHeader.ManifestTagOffset, 8u)); // offset inside the fixed header

                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", BoundPort(system));
                var bytes = Preamble().Concat(frame).ToArray();
                await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
                await client.GetStream().FlushAsync();

                var seen = new List<string>();
                await warnings.FishForMessageAsync(m =>
                {
                    var text = ((Akka.Event.Warning)m).Message.ToString() ?? string.Empty;
                    seen.Add(text);
                    return text.Contains(expectedWarning);
                }, 10.Seconds());

                seen.Should().NotContain(t => t.Contains(unexpectedWarning));
            }
            finally
            {
                await system.Terminate().AwaitWithTimeout(10.Seconds());
            }
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
