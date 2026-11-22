//-----------------------------------------------------------------------
// <copyright file="ArteryNumericRefCodecSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.TestKit;
using Akka.Util;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// The shared ref wire format -- ABSENT / LITERAL / NUMERIC, plus the reserved define flag -- at the
    /// codec and inbound-stage level, and the lane-key function both shapes share.
    /// </summary>
    public class ArteryNumericRefCodecSpec : AkkaSpec
    {
        private const int FrameLengthFieldLength = 4;
        private const int HeaderLength = 32;
        private const long Origin = 0x0123_4567_89AB_CDEFL;

        public ArteryNumericRefCodecSpec(ITestOutputHelper output) : base(output)
        {
        }

        private Akka.Serialization.PooledPayloadWriter Encode(
            string? senderPath, ArteryNumericRef? senderRef, string? recipientPath, ArteryNumericRef? recipientRef) =>
            ArteryEnvelopeCodec.Encode(Sys.Serialization, Origin, senderPath, senderRef, recipientPath, recipientRef, "payload");

        private static ReadOnlySequence<byte> Body(Akka.Serialization.PooledPayloadWriter writer) =>
            new(writer.WrittenMemory.Slice(FrameLengthFieldLength));

        // ===================== shapes =====================

        [Fact(DisplayName = "Should_round_trip_absent_literal_and_numeric_shapes_in_either_position")]
        public void Should_round_trip_absent_literal_and_numeric_shapes_in_either_position()
        {
            var temporary = ArteryNumericRef.ForTemporary(Origin, 77);

            using (var absent = Encode(null, null, null, null))
            {
                var decoded = ArteryEnvelopeCodec.Decode(Body(absent));
                Assert.Equal(ArteryTagKind.Absent, decoded.SenderKind);
                Assert.Equal(ArteryTagKind.Absent, decoded.RecipientKind);
            }

            using (var literalSenderNumericRecipient = Encode("akka://S@h:1/user/a", null, null, temporary))
            {
                var decoded = ArteryEnvelopeCodec.Decode(Body(literalSenderNumericRecipient));
                Assert.Equal(ArteryTagKind.Literal, decoded.SenderKind);
                Assert.True(decoded.TryGetSenderPath(out var sender));
                Assert.Equal("akka://S@h:1/user/a", sender);
                Assert.False(decoded.TryGetSenderNumeric(out _));

                Assert.Equal(ArteryTagKind.Numeric, decoded.RecipientKind);
                Assert.True(decoded.TryGetRecipientNumeric(out var recipient));
                Assert.Equal(temporary.OwnerUid, recipient.OwnerUid);
                Assert.Equal(temporary.Id, recipient.Id);
                Assert.False(decoded.TryGetRecipientPath(out var path));
                Assert.Null(path);
            }

            using (var numericSenderLiteralRecipient = Encode(null, temporary, "akka://S@h:1/user/b", null))
            {
                var decoded = ArteryEnvelopeCodec.Decode(Body(numericSenderLiteralRecipient));
                Assert.Equal(ArteryTagKind.Numeric, decoded.SenderKind);
                Assert.True(decoded.TryGetSenderNumeric(out var sender));
                Assert.Equal(temporary.Id, sender.Id);
                Assert.Equal(ArteryTagKind.Literal, decoded.RecipientKind);
                Assert.True(decoded.TryGetRecipientPath(out var recipient));
                Assert.Equal("akka://S@h:1/user/b", recipient);
            }
        }

        [Fact(DisplayName = "Should_omit_the_owner_uid_When_it_is_the_frame_origin_and_carry_it_explicitly_otherwise")]
        public void Should_omit_the_owner_uid_When_it_is_the_frame_origin_and_carry_it_explicitly_otherwise()
        {
            var ownedByOrigin = ArteryNumericRef.ForTemporary(Origin, 5);
            var ownedByOther = ArteryNumericRef.ForTemporary(-42L, 9);

            using var writer = Encode(null, ownedByOrigin, null, ownedByOther);
            var decoded = ArteryEnvelopeCodec.Decode(Body(writer));

            // Sender: 8-byte record (id only), no owner flag. Recipient: 16-byte record (owner, id), owner flag.
            Assert.Equal(0u, decoded.Header.SenderTag & ArteryEnvelopeHeader.OwnerPresentTagFlag);
            Assert.Equal(ArteryEnvelopeHeader.OwnerPresentTagFlag, decoded.Header.RecipientTag & ArteryEnvelopeHeader.OwnerPresentTagFlag);
            Assert.Equal(HeaderLength, (int)(decoded.Header.SenderTag & ArteryEnvelopeHeader.TagOffsetMask));
            Assert.Equal(HeaderLength + 8, (int)(decoded.Header.RecipientTag & ArteryEnvelopeHeader.TagOffsetMask));

            Assert.True(decoded.TryGetSenderNumeric(out var sender));
            Assert.Equal(Origin, sender.OwnerUid);
            Assert.Equal(ownedByOrigin.Id, sender.Id);

            Assert.True(decoded.TryGetRecipientNumeric(out var recipient));
            Assert.Equal(-42L, recipient.OwnerUid);
            Assert.Equal(ownedByOther.Id, recipient.Id);
            Assert.True(recipient.IsTemporary);
            Assert.Equal(9L, recipient.TemporaryCounter);
        }

        [Fact(DisplayName = "Should_set_the_namespace_bit_for_temporary_ids_and_keep_the_full_64_bit_id")]
        public void Should_set_the_namespace_bit_for_temporary_ids_and_keep_the_full_64_bit_id()
        {
            var temporary = ArteryNumericRef.ForTemporary(1, long.MaxValue);
            Assert.True(temporary.IsTemporary);
            Assert.Equal(long.MaxValue, temporary.TemporaryCounter);

            var other = new ArteryNumericRef(1, 0x1234_5678_9ABC_DEF0L);
            Assert.False(other.IsTemporary);

            using var writer = Encode(null, other, null, temporary);
            var decoded = ArteryEnvelopeCodec.Decode(Body(writer));
            Assert.True(decoded.TryGetSenderNumeric(out var s));
            Assert.Equal(0x1234_5678_9ABC_DEF0L, s.Id);
            Assert.True(decoded.TryGetRecipientNumeric(out var r));
            Assert.Equal(-1L, r.Id);
        }

        // ===================== version =====================

        [Theory(DisplayName = "Should_reject_every_envelope_version_other_than_the_current_one")]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(255)]
        public void Should_reject_every_envelope_version_other_than_the_current_one(int version)
        {
            var header = ArteryEnvelopeCodecSpec.BuildRawHeader((byte)version, 0, 0, 0, 0, 0, 0, HeaderLength);
            Assert.NotEqual(ArteryEnvelopeHeader.CurrentVersion, (byte)version);
            Assert.Throws<ArteryEnvelopeException>(() => ArteryEnvelopeCodec.Decode(new ReadOnlySequence<byte>(header)));
        }

        // ===================== malformed and reserved tags =====================

        [Fact(DisplayName = "Should_throw_ArteryEnvelopeException_When_a_numeric_record_is_outside_the_literal_region")]
        public void Should_throw_ArteryEnvelopeException_When_a_numeric_record_is_outside_the_literal_region()
        {
            // Offset inside the fixed header.
            var inHeader = Raw(ArteryEnvelopeHeader.NumericTagFlag | 4u, 0, HeaderLength + 8);
            Assert.Throws<ArteryEnvelopeException>(() => ArteryEnvelopeCodec.Decode(inHeader).TryGetSenderNumeric(out _));

            // An explicit-owner record needs 16 bytes but the payload starts after 8.
            var pastPayload = Raw(0, ArteryEnvelopeHeader.NumericTagFlag | ArteryEnvelopeHeader.OwnerPresentTagFlag | HeaderLength, HeaderLength + 8);
            Assert.Throws<ArteryEnvelopeException>(() => ArteryEnvelopeCodec.Decode(pastPayload).TryGetRecipientNumeric(out _));
        }

        [Fact(DisplayName = "Should_classify_unknown_flag_bits_as_unreadable_rather_than_numeric")]
        public void Should_classify_unknown_flag_bits_as_unreadable_rather_than_numeric()
        {
            var unknownBit = ArteryEnvelopeHeader.NumericTagFlag | (0x08u << 24) | HeaderLength;
            var ownerWithoutNumeric = ArteryEnvelopeHeader.OwnerPresentTagFlag | HeaderLength;

            var decoded = ArteryEnvelopeCodec.Decode(Raw(unknownBit, ownerWithoutNumeric, HeaderLength + 16));
            Assert.Equal(ArteryTagKind.Compressed, decoded.SenderKind);
            Assert.Equal(ArteryTagKind.Compressed, decoded.RecipientKind);
            Assert.False(decoded.TryGetSenderNumeric(out _));
            Assert.False(decoded.TryGetSenderPath(out _));
            Assert.False(decoded.TryGetRecipientPath(out _));
        }

        // ===================== define flag: numeric record + attached path =====================

        private abstract record Part;
        private sealed record AbsentPart : Part;
        private sealed record LiteralPart(string Path) : Part;
        private sealed record NumericPart(ArteryNumericRef Ref) : Part;
        private sealed record DefinePart(ArteryNumericRef Ref, string Path) : Part;

        /// <summary>
        /// Builds a frame by hand so any shape -- including the define flag, which the encoder never writes --
        /// can be tested. The payload and manifest are those of a real encoded message.
        /// </summary>
        private byte[] BuildFrame(long origin, Part sender, Part recipient, string message)
        {
            using var plain = ArteryEnvelopeCodec.Encode(Sys.Serialization, origin, null, null, message);
            var plainDecoded = ArteryEnvelopeCodec.Decode(Body(plain));
            Assert.True(plainDecoded.TryGetManifest(out var manifest));
            var payload = plainDecoded.Payload.ToArray();

            var body = new List<byte>();
            uint Append(Part part)
            {
                var offset = HeaderLength + body.Count;
                switch (part)
                {
                    case LiteralPart l:
                        AppendLiteral(body, l.Path);
                        return (uint)offset;
                    case NumericPart n:
                        var flags = ArteryEnvelopeHeader.NumericTagFlag | OwnerFlag(n.Ref, origin);
                        AppendRecord(body, n.Ref, origin);
                        return flags | (uint)offset;
                    case DefinePart d:
                        var defineFlags = ArteryEnvelopeHeader.NumericTagFlag | ArteryEnvelopeHeader.DefineTagFlag | OwnerFlag(d.Ref, origin);
                        AppendRecord(body, d.Ref, origin);
                        AppendLiteral(body, d.Path);
                        return defineFlags | (uint)offset;
                    default:
                        return 0u;
                }
            }

            var senderTag = Append(sender);
            var recipientTag = Append(recipient);
            var manifestTag = string.IsNullOrEmpty(manifest) ? 0u : Append(new LiteralPart(manifest));
            var payloadOffset = (uint)(HeaderLength + body.Count);

            var header = ArteryEnvelopeCodecSpec.BuildRawHeader(
                ArteryEnvelopeHeader.CurrentVersion, 0, origin, plainDecoded.Header.SerializerId, senderTag, recipientTag, manifestTag, payloadOffset);
            var envelope = header.Concat(body).Concat(payload).ToArray();
            var frame = new byte[FrameLengthFieldLength + envelope.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)envelope.Length);
            envelope.CopyTo(frame, FrameLengthFieldLength);
            return frame;
        }

        private static uint OwnerFlag(ArteryNumericRef value, long origin) =>
            value.OwnerUid == origin ? 0u : ArteryEnvelopeHeader.OwnerPresentTagFlag;

        private static void AppendRecord(List<byte> body, ArteryNumericRef value, long origin)
        {
            var buffer = new byte[8];
            if (value.OwnerUid != origin)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, value.OwnerUid);
                body.AddRange(buffer);
            }

            BinaryPrimitives.WriteInt64LittleEndian(buffer, value.Id);
            body.AddRange(buffer);
        }

        private static void AppendLiteral(List<byte> body, string path)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(path);
            body.Add((byte)(bytes.Length & 0xFF));
            body.Add((byte)(bytes.Length >> 8));
            body.AddRange(bytes);
        }

        private ReadOnlySequence<byte> EnvelopeBody(byte[] frame) => new(frame.AsMemory(FrameLengthFieldLength));

        [Fact(DisplayName = "Should_resolve_a_define_flagged_ref_through_its_attached_path_as_if_it_were_a_literal")]
        public void Should_resolve_a_define_flagged_ref_through_its_attached_path_as_if_it_were_a_literal()
        {
            var frame = BuildFrame(Origin,
                new DefinePart(new ArteryNumericRef(Origin, 0x42), "akka://S@h:1/user/sender"),
                new DefinePart(ArteryNumericRef.ForTemporary(-9L, 3), "akka://R@h:2/temp/d"),
                "m");

            var decoded = ArteryEnvelopeCodec.Decode(EnvelopeBody(frame));

            Assert.Equal(ArteryTagKind.NumericDefine, decoded.SenderKind);
            Assert.Equal(ArteryTagKind.NumericDefine, decoded.RecipientKind);
            Assert.True(decoded.TryGetSenderPath(out var sender));
            Assert.Equal("akka://S@h:1/user/sender", sender);
            Assert.True(decoded.TryGetRecipientPath(out var recipient));
            Assert.Equal("akka://R@h:2/temp/d", recipient);

            // The id is ignored by a receiver that keeps no definitions: it is not offered as a numeric ref.
            Assert.False(decoded.TryGetSenderNumeric(out _));
            Assert.False(decoded.TryGetRecipientNumeric(out _));
            Assert.True(decoded.TryGetManifest(out var manifest));
            Assert.NotNull(manifest);
        }

        [Fact(DisplayName = "Should_throw_ArteryEnvelopeException_When_a_define_record_or_its_attached_path_is_out_of_bounds")]
        public void Should_throw_ArteryEnvelopeException_When_a_define_record_or_its_attached_path_is_out_of_bounds()
        {
            const uint define = ArteryEnvelopeHeader.NumericTagFlag | ArteryEnvelopeHeader.DefineTagFlag;

            // Record offset inside the fixed header.
            Assert.Throws<ArteryEnvelopeException>(() =>
                ArteryEnvelopeCodec.Decode(Raw(define | 4u, 0, HeaderLength + 16)).TryGetSenderPath(out _));

            // The 8-byte record fits but there is no room for the path's length prefix.
            Assert.Throws<ArteryEnvelopeException>(() =>
                ArteryEnvelopeCodec.Decode(Raw(define | HeaderLength, 0, HeaderLength + 8)).TryGetSenderPath(out _));

            // The path declares more bytes than remain before the payload offset.
            var raw = Raw(define | HeaderLength, 0, HeaderLength + 8 + 2 + 3).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(HeaderLength + 8), 40);
            Assert.Throws<ArteryEnvelopeException>(() =>
                ArteryEnvelopeCodec.Decode(new ReadOnlySequence<byte>(raw)).TryGetSenderPath(out _));

            // An explicit owner makes the record 16 bytes, which does not fit.
            Assert.Throws<ArteryEnvelopeException>(() =>
                ArteryEnvelopeCodec.Decode(Raw(0, define | ArteryEnvelopeHeader.OwnerPresentTagFlag | HeaderLength, HeaderLength + 8))
                    .TryGetRecipientPath(out _));

            // A define-flagged tag is not a manifest.
            Assert.False(ArteryEnvelopeCodec.Decode(RawWithManifest(define | HeaderLength, HeaderLength + 16)).TryGetManifest(out _));
        }

        private static ReadOnlySequence<byte> RawWithManifest(uint manifestTag, uint payloadOffset)
        {
            var header = ArteryEnvelopeCodecSpec.BuildRawHeader(
                ArteryEnvelopeHeader.CurrentVersion, 0, Origin, 0, 0, 0, manifestTag, payloadOffset);
            var frame = new byte[payloadOffset];
            header.CopyTo(frame, 0);
            return new ReadOnlySequence<byte>(frame);
        }

        private static ReadOnlySequence<byte> Raw(uint senderTag, uint recipientTag, uint payloadOffset)
        {
            var header = ArteryEnvelopeCodecSpec.BuildRawHeader(
                ArteryEnvelopeHeader.CurrentVersion, 0, Origin, 0, senderTag, recipientTag, 0, payloadOffset);
            var frame = new byte[payloadOffset];
            header.CopyTo(frame, 0);
            return new ReadOnlySequence<byte>(frame);
        }

        // ===================== lane key =====================

        [Fact(DisplayName = "Should_pick_the_same_inbound_lane_for_the_numeric_and_literal_forms_of_a_temporary_ref")]
        public void Should_pick_the_same_inbound_lane_for_the_numeric_and_literal_forms_of_a_temporary_ref()
        {
            foreach (var lanes in new[] { 2, 3, 4, 8 })
            {
                for (long counter = 1; counter < 500; counter++)
                {
                    var literal = $"akka://Sys@127.0.0.1:2552/temp/{Base64Encoding.Base64Encode(counter)}";

                    // The inbound stage picks the lane of a numeric recipient with SelectLane(For(ref)) and of a
                    // literal one with TrySelectTemporaryLiteral; both must agree.
                    Assert.True(ArteryLaneKey.TrySelectTemporaryLiteral(literal, lanes, out var fromLiteral), literal);
                    var numeric = Association.SelectLane(ArteryLaneKey.For(ArteryNumericRef.ForTemporary(99, counter)), lanes);
                    Assert.Equal(numeric, fromLiteral);
                }
            }

            // Not canonical temp literals: stable refs, deeper temp children, a uid fragment, other names.
            foreach (var path in new[]
            {
                "akka://Sys@127.0.0.1:2552/user/temp/a", "akka://Sys@127.0.0.1:2552/temp/a/b", "akka://Sys@127.0.0.1:2552/temp/a#5",
                "akka://Sys@127.0.0.1:2552/temp/zz-top", "akka://Sys@127.0.0.1:2552/temp/", "akka://Sys@127.0.0.1:2552/user/x",
                "temp/a", "/temp/a"
            })
                Assert.False(ArteryLaneKey.TrySelectTemporaryLiteral(path, 4, out _), path);
        }

        [Fact(DisplayName = "Should_not_alias_a_periodic_id_pattern_onto_one_lane")]
        public void Should_not_alias_a_periodic_id_pattern_onto_one_lane()
        {
            // Every fourth id is an "entity" (the other three are its children): with a plain modulus and four
            // lanes every entity would share one lane.
            var lanes = Enumerable.Range(0, 400).Select(i => Association.SelectLane(i * 4L, 4)).ToArray();
            Assert.Equal(4, lanes.Distinct().Count());
            Assert.All(Enumerable.Range(0, 4), lane => Assert.InRange(lanes.Count(l => l == lane), 60, 140));

            foreach (var key in new[] { long.MinValue, long.MaxValue, 0L, -1L })
                Assert.InRange(Association.SelectLane(key, 4), 0, 3);
        }

        // ===================== inbound stage =====================

        private static byte[] Preamble() => new byte[] { (byte)'A', (byte)'K', (byte)'K', (byte)'A', (byte)ArteryStreamId.Ordinary };

        private const long PeerUid = 42;

        private (ArteryInboundProcessingStage Stage, ConcurrentQueue<(object Message, string? Recipient, string? Sender, long? RecipientId)> Delivered) CreateStage(int lanes)
        {
            var delivered = new ConcurrentQueue<(object, string?, string?, long?)>();
            var context = new AssociationRegistryInboundContext(new AssociationRegistry(),
                new UniqueAddress(new Address("akka", "local", "127.0.0.1", 1234), 1), (_, _) => { });
            context.CompleteHandshake(new UniqueAddress(new Address("akka", "peer", "127.0.0.1", 4321), PeerUid));
            var stage = new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
                inboundLanes: lanes, inboundLaneBufferSize: 4, inboundContext: context,
                dispatchOrdinary: (object message, in InboundWireRefs refs) =>
                    delivered.Enqueue((message, refs.RecipientPath, refs.ResolveSenderPath(), refs.RecipientRef?.Id)));
            return (stage, delivered);
        }

        private async Task<Exception?> RunThroughStage(
            ArteryInboundProcessingStage stage, ConcurrentQueue<(object Message, string? Recipient, string? Sender, long? RecipientId)> delivered, params byte[][] frames)
        {
            var bytes = Preamble().Concat(frames.SelectMany(f => f)).ToArray();
            var materializer = ActorMaterializer.Create(Sys);
            try
            {
                return await Record.ExceptionAsync(() => Source.Single(new ReadOnlySequence<byte>(bytes)).Via(stage)
                    .RunWith(Sink.ForEach<IInboundEnvelope>(e => delivered.Enqueue((e.Message, e.RecipientPath, e.SenderPath, e.RecipientRef?.Id))), materializer)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "Should_drop_a_frame_with_a_malformed_numeric_ref_and_keep_delivering_When_lanes_are_off_or_on")]
        [InlineData(1)]
        [InlineData(4)]
        public async Task Should_drop_a_frame_with_a_malformed_numeric_ref_and_keep_delivering(int lanes)
        {
            var numeric = ArteryNumericRef.ForTemporary(1, 7);
            var intoHeader = BuildFrame(PeerUid, new AbsentPart(), new NumericPart(numeric), "bad-offset");
            BinaryPrimitives.WriteUInt32LittleEndian(intoHeader.AsSpan(FrameLengthFieldLength + 20),
                ArteryEnvelopeHeader.NumericTagFlag | 4u);
            var badDefine = BuildFrame(PeerUid, new AbsentPart(), new DefinePart(new ArteryNumericRef(PeerUid, 7), "akka://local/user/x"), "bad-define");
            BinaryPrimitives.WriteUInt16LittleEndian(badDefine.AsSpan(FrameLengthFieldLength + HeaderLength + 8), 60000);
            var good = BuildFrame(PeerUid, new AbsentPart(), new NumericPart(numeric), "good-numeric");
            var literal = BuildFrame(PeerUid, new AbsentPart(), new LiteralPart("akka://local/user/target"), "good-literal");

            var (stage, delivered) = CreateStage(lanes);
            var failure = await RunThroughStage(stage, delivered, intoHeader, badDefine, good, literal);

            Assert.Null(failure);
            await AwaitAssertAsync(() => Assert.Equal(2, delivered.Count));
            var got = delivered.ToArray();
            Assert.Contains(got, d => (string)d.Message == "good-numeric" && d.RecipientId == numeric.Id);
            Assert.Contains(got, d => (string)d.Message == "good-literal" && d.RecipientId is null);
        }

        [Theory(DisplayName = "Should_deliver_through_the_attached_path_When_the_recipient_and_sender_carry_the_define_flag")]
        [InlineData(1)]
        [InlineData(4)]
        public async Task Should_deliver_through_the_attached_path_When_the_recipient_and_sender_carry_the_define_flag(int lanes)
        {
            var defined = BuildFrame(PeerUid,
                new DefinePart(new ArteryNumericRef(PeerUid, 11), "akka://peer@127.0.0.1:4321/user/asker"),
                new DefinePart(new ArteryNumericRef(1, 12), "akka://local@127.0.0.1:1234/user/target"),
                "defined");
            // A plain literal sender: in lane mode it stays undecoded until asked for.
            var plain = BuildFrame(PeerUid,
                new LiteralPart("akka://peer@127.0.0.1:4321/user/plain"),
                new LiteralPart("akka://local@127.0.0.1:1234/user/target"),
                "plain");

            var (stage, delivered) = CreateStage(lanes);
            var failure = await RunThroughStage(stage, delivered, defined, plain);

            Assert.Null(failure);
            await AwaitAssertAsync(() => Assert.Equal(2, delivered.Count));
            var got = delivered.ToArray();
            Assert.Contains(got, d => (string)d.Message == "defined"
                && d.Recipient == "akka://local@127.0.0.1:1234/user/target"
                && d.Sender == "akka://peer@127.0.0.1:4321/user/asker"
                && d.RecipientId is null);
            Assert.Contains(got, d => (string)d.Message == "plain"
                && d.Recipient == "akka://local@127.0.0.1:1234/user/target"
                && d.Sender == "akka://peer@127.0.0.1:4321/user/plain");
        }

        [Theory(DisplayName = "Should_fail_the_connection_once_When_the_peer_speaks_another_envelope_version")]
        [InlineData(1)]
        [InlineData(4)]
        public async Task Should_fail_the_connection_once_When_the_peer_speaks_another_envelope_version(int lanes)
        {
            var good = BuildFrame(PeerUid, new AbsentPart(), new LiteralPart("akka://local/user/target"), "before");
            var oldVersion = BuildFrame(PeerUid, new AbsentPart(), new LiteralPart("akka://local/user/target"), "old-1");
            oldVersion[FrameLengthFieldLength + ArteryEnvelopeHeader.VersionOffset] = 1;
            var oldVersion2 = BuildFrame(PeerUid, new AbsentPart(), new LiteralPart("akka://local/user/target"), "old-2");
            oldVersion2[FrameLengthFieldLength + ArteryEnvelopeHeader.VersionOffset] = 1;
            var after = BuildFrame(PeerUid, new AbsentPart(), new LiteralPart("akka://local/user/target"), "after");

            var (stage, delivered) = CreateStage(lanes);
            var failure = await RunThroughStage(stage, delivered, good, oldVersion, oldVersion2, after);

            var envelope = Assert.IsType<ArteryEnvelopeException>(failure);
            Assert.Equal((byte)1, envelope.ReceivedVersion);
            Assert.DoesNotContain(delivered.ToArray(), d => (string)d.Message == "after");
        }

        // ===================== decoder fuzz =====================

        [Fact(DisplayName = "Should_only_ever_decode_or_throw_ArteryEnvelopeException_When_valid_frames_of_every_shape_are_randomly_mutated")]
        public void Should_only_ever_decode_or_throw_ArteryEnvelopeException_When_valid_frames_of_every_shape_are_randomly_mutated()
        {
            var numeric = ArteryNumericRef.ForTemporary(Origin, 5);
            var foreign = ArteryNumericRef.ForTemporary(-3, 9);
            var seeds = new[]
            {
                BuildFrame(Origin, new AbsentPart(), new AbsentPart(), "a"),
                BuildFrame(Origin, new LiteralPart("akka://S@h:1/user/s"), new LiteralPart("akka://R@h:2/user/r"), "b"),
                BuildFrame(Origin, new NumericPart(numeric), new NumericPart(foreign), "c"),
                BuildFrame(Origin, new LiteralPart("akka://S@h:1/user/s"), new NumericPart(foreign), "d"),
                BuildFrame(Origin, new DefinePart(numeric, "akka://S@h:1/user/s"), new DefinePart(foreign, "akka://R@h:2/temp/x"), "e"),
            };

            var random = new Random(20260101);
            var decodedFrames = 0;
            for (var i = 0; i < 6000; i++)
            {
                var bytes = seeds[random.Next(seeds.Length)].AsSpan(FrameLengthFieldLength).ToArray();
                switch (random.Next(4))
                {
                    case 0: // flip a few random bytes anywhere
                        for (var n = random.Next(1, 5); n > 0; n--)
                            bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                        break;
                    case 1: // corrupt the tag / payload-offset words of the header
                        for (var n = random.Next(1, 3); n > 0; n--)
                            bytes[random.Next(HeaderLength - 16, HeaderLength)] = (byte)random.Next(256);
                        break;
                    case 2: // truncate
                        bytes = bytes.AsSpan(0, random.Next(bytes.Length + 1)).ToArray();
                        break;
                    default: // flip one bit
                        bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                        break;
                }

                var sequence = bytes.Length > 2 && random.Next(2) == 0
                    ? SplitInTwo(bytes, random.Next(1, bytes.Length))
                    : new ReadOnlySequence<byte>(bytes);

                try
                {
                    var decoded = ArteryEnvelopeCodec.Decode(sequence);
                    decodedFrames++;
                    Touch(() => decoded.TryGetSenderPath(out _));
                    Touch(() => decoded.TryGetRecipientPath(out _));
                    Touch(() => decoded.TryGetManifest(out _));
                    Touch(() => decoded.TryGetSenderNumeric(out _));
                    Touch(() => decoded.TryGetRecipientNumeric(out _));
                    Touch(() => decoded.TryGetSenderLiteralUtf8(out _));
                    _ = decoded.Payload.Length;
                }
                catch (ArteryEnvelopeException)
                {
                }
            }

            Assert.True(decodedFrames > 100, "the mutations should leave a healthy share of frames decodable");
        }

        private static void Touch(Action read)
        {
            try
            {
                read();
            }
            catch (ArteryEnvelopeException)
            {
            }
        }

        private static ReadOnlySequence<byte> SplitInTwo(byte[] bytes, int at)
        {
            var first = new Segment(bytes.AsMemory(0, at));
            var second = first.Append(bytes.AsMemory(at));
            return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
        }

        private sealed class Segment : ReadOnlySequenceSegment<byte>
        {
            public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

            public Segment Append(ReadOnlyMemory<byte> memory)
            {
                var segment = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
                Next = segment;
                return segment;
            }
        }
    }
}
