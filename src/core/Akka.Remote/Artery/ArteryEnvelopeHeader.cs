//-----------------------------------------------------------------------
// <copyright file="ArteryEnvelopeHeader.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// Wire-layout constants for the Artery envelope's fixed 32-byte header, per
    /// <c>openspec/changes/artery-tcp-remoting/design.md</c> ("Envelope wire layout", Decision 4;
    /// sub-decisions CLOSED at G1, 2026-07-04). All multi-byte fields are little-endian.
    ///
    /// <para>
    /// The 4-byte frame-length field that precedes the envelope on the wire
    /// (<c>[u32 LE frame length][envelope]</c>) belongs to the TCP framing layer (design.md
    /// Decision 3), which is owned by a parallel work item (framing parser / connection header).
    /// <see cref="ArteryEnvelopeCodec"/> only needs to know its length to lay the envelope out
    /// correctly relative to it, so that single constant (<see cref="FrameLengthFieldLength"/>) is
    /// duplicated locally here rather than taking a cross-cutting dependency on the framing
    /// parser's types. Duplication accepted at G1 -- consider consolidating once both land.
    /// </para>
    /// </summary>
    internal static class ArteryEnvelopeHeader
    {
        /// <summary>Length, in bytes, of the frame-length field that precedes every envelope on the wire.</summary>
        public const int FrameLengthFieldLength = 4;

        /// <summary>Length, in bytes, of the fixed envelope header (offsets 0..32).</summary>
        public const int HeaderLength = 32;

        /// <summary>
        /// The only version this codec understands; the decoder rejects any other value. Version 2
        /// adds the TEMP tag kind (<see cref="TempTagKind"/>); the fixed header is otherwise unchanged.
        /// </summary>
        public const byte CurrentVersion = 2;

        /// <summary>Flags bit 0: a metadata section is present immediately after the fixed header.</summary>
        public const byte MetadataPresentFlag = 0b0000_0001;

        /// <summary>Flags bits 1-7: reserved, must be zero. The decoder rejects any set reserved bit.</summary>
        public const byte ReservedFlagsMask = 0b1111_1110;

        /// <summary>Tag value meaning ABSENT: no sender / no recipient / empty manifest.</summary>
        public const uint AbsentTag = 0x0000_0000u;

        /// <summary>
        /// Top-byte mask; the top byte is the tag KIND: 0x00 ABSENT / LITERAL, <see cref="TempTagKindByte"/>
        /// TEMP, any other non-zero value COMPRESSED (reserved).
        /// </summary>
        public const uint CompressedTagMask = 0xFF00_0000u;

        /// <summary>Top-byte value (0x01) of a TEMP tag.</summary>
        public const uint TempTagKindByte = 0x01u;

        /// <summary>
        /// A TEMP tag's top byte, already shifted into place. A TEMP tag is <c>TempTagKind | offset</c>,
        /// where the low 24 bits (<see cref="TempTagOffsetMask"/>) are the absolute byte offset of a
        /// fixed-size tail record -- <see cref="TempSenderRecordLength"/> bytes in the sender position,
        /// <see cref="TempRecipientRecordLength"/> bytes in the recipient position -- laid out among the
        /// literals, before the payload offset. A TEMP tag is never valid in the manifest position.
        /// </summary>
        public const uint TempTagKind = TempTagKindByte << 24;

        /// <summary>Low 24 bits of a TEMP tag: the tail record's absolute byte offset.</summary>
        public const uint TempTagOffsetMask = 0x00FF_FFFFu;

        /// <summary>
        /// Length of the TEMP record in the SENDER position: <c>i64 LE tempId</c>. The owning system is
        /// implicitly the frame's origin (<see cref="OriginUidOffset"/>).
        /// </summary>
        public const int TempSenderRecordLength = 8;

        /// <summary>
        /// Length of the TEMP record in the RECIPIENT position: <c>i64 LE ownerUid</c> then
        /// <c>i64 LE tempId</c>.
        /// </summary>
        public const int TempRecipientRecordLength = 16;

        /// <summary>Low 16 bits of a COMPRESSED tag hold the compression-table index.</summary>
        public const uint CompressedIndexMask = 0x0000_FFFFu;

        /// <summary>
        /// Exclusive upper bound for a LITERAL tag's byte offset -- keeps literal offsets clear of
        /// the COMPRESSED discriminator's top byte. Derived constraint from design.md: keep
        /// <c>maximum-frame-size</c> well under 16 MB so this bound can never be reached in practice.
        /// </summary>
        public const uint LiteralOffsetExclusiveMax = 0x00FF_FFFFu;

        /// <summary>Maximum encodable literal length in UTF-8 bytes -- an unsigned 16-bit length prefix: 64KB - 1.</summary>
        public const int MaxLiteralByteLength = ushort.MaxValue;

        /// <summary>Length, in bytes, of a literal's length prefix (u16 LE).</summary>
        public const int LiteralLengthPrefixSize = sizeof(ushort);

        /// <summary>Length, in bytes, of the optional metadata section's length prefix (u32 LE).</summary>
        public const int MetadataLengthPrefixSize = sizeof(uint);

        // Fixed-header field offsets, relative to envelope offset 0 (i.e. AFTER the 4-byte
        // frame-length field -- see the type-level remarks above).
        public const int VersionOffset = 0;
        public const int FlagsOffset = 1;
        public const int ActorRefTableVersionOffset = 2;
        public const int ManifestTableVersionOffset = 3;
        public const int OriginUidOffset = 4;
        public const int SerializerIdOffset = 12;
        public const int SenderTagOffset = 16;
        public const int RecipientTagOffset = 20;
        public const int ManifestTagOffset = 24;
        public const int PayloadOffsetFieldOffset = 28;

        /// <summary>Offset (relative to envelope offset 0) where an optional metadata section's u32 LE length field begins.</summary>
        public const int MetadataSectionOffset = HeaderLength;
    }

    /// <summary>
    /// INTERNAL API.
    ///
    /// The decoded fixed 32-byte Artery envelope header. Sender/recipient/manifest tags are left
    /// undecoded here (raw <see cref="uint"/>) -- classifying and resolving them (ABSENT / LITERAL /
    /// COMPRESSED) is the job of <see cref="ArteryEnvelopeDecoded"/>, which also needs the envelope
    /// body to resolve a LITERAL tag to a string. This type is a readonly struct so decoding the
    /// fixed header never allocates.
    /// </summary>
    internal readonly struct ArteryEnvelopeFixedHeader
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ArteryEnvelopeFixedHeader"/> struct.
        /// </summary>
        public ArteryEnvelopeFixedHeader(
            byte version,
            byte flags,
            byte actorRefTableVersion,
            byte manifestTableVersion,
            long originUid,
            int serializerId,
            uint senderTag,
            uint recipientTag,
            uint manifestTag,
            long payloadOffset)
        {
            Version = version;
            Flags = flags;
            ActorRefTableVersion = actorRefTableVersion;
            ManifestTableVersion = manifestTableVersion;
            OriginUid = originUid;
            SerializerId = serializerId;
            SenderTag = senderTag;
            RecipientTag = recipientTag;
            ManifestTag = manifestTag;
            PayloadOffset = payloadOffset;
        }

        /// <summary>Envelope wire-layout version. Always <see cref="ArteryEnvelopeHeader.CurrentVersion"/> in a successfully decoded header.</summary>
        public byte Version { get; }

        /// <summary>Envelope flags byte. Bit 0 = metadata section present; bits 1-7 are reserved-must-be-zero.</summary>
        public byte Flags { get; }

        /// <summary>Actor-ref compression-table version. Always 0 until compression (deferred) lands.</summary>
        public byte ActorRefTableVersion { get; }

        /// <summary>Manifest compression-table version. Always 0 until compression (deferred) lands.</summary>
        public byte ManifestTableVersion { get; }

        /// <summary>The origin (sending) system's UID.</summary>
        public long OriginUid { get; }

        /// <summary>The <see cref="Akka.Serialization.Serializer"/> id used to serialize the payload.</summary>
        public int SerializerId { get; }

        /// <summary>The raw, undecoded sender-ref tag. See <see cref="ArteryEnvelopeDecoded.SenderKind"/> / <see cref="ArteryEnvelopeDecoded.TryGetSenderPath"/>.</summary>
        public uint SenderTag { get; }

        /// <summary>The raw, undecoded recipient-ref tag. See <see cref="ArteryEnvelopeDecoded.RecipientKind"/> / <see cref="ArteryEnvelopeDecoded.TryGetRecipientPath"/>.</summary>
        public uint RecipientTag { get; }

        /// <summary>The raw, undecoded manifest tag. See <see cref="ArteryEnvelopeDecoded.ManifestKind"/> / <see cref="ArteryEnvelopeDecoded.TryGetManifest"/>.</summary>
        public uint ManifestTag { get; }

        /// <summary>
        /// The absolute byte offset (from envelope offset 0) where the payload begins. Explicit and
        /// O(1) -- does not need to be derived from frame length minus a variable-length tail.
        /// </summary>
        public long PayloadOffset { get; }

        /// <summary><see langword="true"/> iff <see cref="Flags"/> bit 0 is set (a metadata section follows the fixed header).</summary>
        public bool HasMetadataSection => (Flags & ArteryEnvelopeHeader.MetadataPresentFlag) != 0;
    }
}
