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

        [Fact(DisplayName = "Should_classify_the_define_flag_and_unknown_flags_as_unreadable_rather_than_numeric")]
        public void Should_classify_the_define_flag_and_unknown_flags_as_unreadable_rather_than_numeric()
        {
            var define = ArteryEnvelopeHeader.NumericTagFlag | ArteryEnvelopeHeader.DefineTagFlag | HeaderLength;
            var unknownBit = ArteryEnvelopeHeader.NumericTagFlag | (0x08u << 24) | HeaderLength;
            var ownerWithoutNumeric = ArteryEnvelopeHeader.OwnerPresentTagFlag | HeaderLength;

            var decoded = ArteryEnvelopeCodec.Decode(Raw(define, unknownBit, HeaderLength + 16));
            Assert.Equal(ArteryTagKind.NumericDefine, decoded.SenderKind);
            Assert.Equal(ArteryTagKind.Compressed, decoded.RecipientKind);
            Assert.False(decoded.TryGetSenderNumeric(out _));
            Assert.False(decoded.TryGetRecipientNumeric(out _));
            Assert.False(decoded.TryGetSenderPath(out _));

            Assert.Equal(ArteryTagKind.Compressed, ArteryEnvelopeCodec.Decode(Raw(ownerWithoutNumeric, 0, HeaderLength)).SenderKind);
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

        [Fact(DisplayName = "Should_pick_the_same_lane_for_the_numeric_and_literal_forms_of_a_temporary_ref")]
        public void Should_pick_the_same_lane_for_the_numeric_and_literal_forms_of_a_temporary_ref()
        {
            foreach (var lanes in new[] { 2, 3, 4, 8 })
            {
                for (long counter = 1; counter < 500; counter++)
                {
                    var literal = $"akka://Sys@127.0.0.1:2552/temp/{Base64Encoding.Base64Encode(counter)}";
                    Assert.True(ArteryTempRefs.TryGetTempIdFromLiteral(literal, out var parsed), literal);

                    var numeric = Association.SelectLane(ArteryLaneKey.For(ArteryNumericRef.ForTemporary(99, counter)), lanes);
                    var fromLiteral = Association.SelectLane(ArteryLaneKey.ForTemporary(parsed), lanes);
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
                Assert.False(ArteryTempRefs.TryGetTempIdFromLiteral(path, out _), path);
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

        // ===================== inbound stage: bad refs drop one frame, not the stream =====================

        private static byte[] Preamble() => new byte[] { (byte)'A', (byte)'K', (byte)'K', (byte)'A', (byte)ArteryStreamId.Ordinary };

        private byte[] FrameWithTags(Action<byte[]> patch, string payload, ArteryNumericRef? recipientRef, string? recipientPath)
        {
            using var writer = ArteryEnvelopeCodec.Encode(Sys.Serialization, 42, null, null, recipientPath, recipientRef, payload);
            var bytes = writer.WrittenSpan.ToArray();
            patch(bytes);
            return bytes;
        }

        private static void PatchRecipientTag(byte[] frame, Func<uint, uint> change)
        {
            var span = frame.AsSpan(FrameLengthFieldLength + 20, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(span, change(BinaryPrimitives.ReadUInt32LittleEndian(span)));
        }

        [Theory(DisplayName = "Should_drop_a_frame_with_a_define_flag_or_malformed_numeric_ref_and_keep_delivering_When_lanes_are_off_or_on")]
        [InlineData(1)]
        [InlineData(4)]
        public async Task Should_drop_a_frame_with_a_define_flag_or_malformed_numeric_ref_and_keep_delivering(int lanes)
        {
            var numeric = ArteryNumericRef.ForTemporary(1, 7);
            var define = FrameWithTags(f => PatchRecipientTag(f, t => t | ArteryEnvelopeHeader.DefineTagFlag), "define", numeric, null);
            var intoHeader = FrameWithTags(f => PatchRecipientTag(f, _ => ArteryEnvelopeHeader.NumericTagFlag | 4u), "bad-offset", numeric, null);
            var good = FrameWithTags(_ => { }, "good-numeric", numeric, null);
            var literal = FrameWithTags(_ => { }, "good-literal", null, "akka://local/user/target");

            var delivered = new ConcurrentQueue<(object Message, long? RecipientId)>();
            var context = new AssociationRegistryInboundContext(new AssociationRegistry(),
                new UniqueAddress(new Address("akka", "local", "127.0.0.1", 1234), 1), (_, _) => { });
            context.CompleteHandshake(new UniqueAddress(new Address("akka", "peer", "127.0.0.1", 4321), 42));
            var stage = new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
                inboundLanes: lanes, inboundLaneBufferSize: 4, inboundContext: context,
                dispatchOrdinary: (message, refs) => delivered.Enqueue((message, refs.RecipientRef?.Id)));

            var bytes = Preamble().Concat(define).Concat(intoHeader).Concat(good).Concat(literal).ToArray();
            var materializer = ActorMaterializer.Create(Sys);
            try
            {
                await Source.Single(new ReadOnlySequence<byte>(bytes)).Via(stage)
                    .RunWith(Sink.ForEach<IInboundEnvelope>(e => delivered.Enqueue((e.Message, e.RecipientRef?.Id))), materializer)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                await AwaitAssertAsync(() => Assert.Equal(2, delivered.Count));
                var got = delivered.ToArray();
                Assert.Contains(got, d => (string)d.Message == "good-numeric" && d.RecipientId == numeric.Id);
                Assert.Contains(got, d => (string)d.Message == "good-literal" && d.RecipientId is null);
            }
            finally
            {
                materializer.Shutdown();
            }
        }
    }
}
