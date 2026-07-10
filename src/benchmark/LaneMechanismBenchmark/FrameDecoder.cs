//-----------------------------------------------------------------------
// <copyright file="FrameDecoder.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System.Buffers;
using Akka.Remote.Artery;

namespace LaneMechanismBenchmark
{
    /// <summary>
    /// The result of the shared "serial decode island" step, identical across all three fan-out
    /// mechanisms (design.md: "the header is parsed before any payload deserialization ... [and]
    /// carries the recipient (-&gt; which lane) and the serializer-id + manifest (-&gt; how to
    /// deserialize)"). A <see langword="readonly struct"/> so it never allocates on its own account --
    /// any allocation a mechanism incurs moving it across a boundary (a box for stock
    /// <c>PartitionHub&lt;T&gt;</c>'s <c>object</c>-typed internal queue; none for
    /// <c>Channel&lt;DecodedItem&gt;</c>, which is fully generic) is real, mechanism-specific cost,
    /// not an artifact of this type.
    /// </summary>
    internal readonly struct DecodedItem
    {
        public DecodedItem(int serializerId, string manifest, ReadOnlySequence<byte> payload, int lane)
        {
            SerializerId = serializerId;
            Manifest = manifest;
            Payload = payload;
            Lane = lane;
        }

        public int SerializerId { get; }

        public string Manifest { get; }

        public ReadOnlySequence<byte> Payload { get; }

        public int Lane { get; }
    }

    /// <summary>
    /// Decodes one pre-encoded Artery frame using the REAL product codec
    /// (<see cref="ArteryEnvelopeCodec"/> in <c>src/core/Akka.Remote/Artery</c>) and computes the
    /// lane partition key from the recipient path -- the one step every mechanism (baseline,
    /// channels, partitionhub) performs identically, so that only the fan-out mechanism itself
    /// differs between runs (mechanism-fairness).
    /// </summary>
    internal static class FrameDecoder
    {
        /// <summary>
        /// <paramref name="frame"/> is the FULL encoded frame as produced by
        /// <see cref="ArteryEnvelopeCodec.Encode(Akka.Serialization.Serialization,long,string?,string?,object,ArrayPool{byte}?)"/>
        /// -- i.e. <c>[u32 LE frame length][envelope]</c>. The 4-byte length prefix is TCP-framing
        /// territory (design.md Decision 3) that a real inbound pipeline strips before handing the
        /// frame body to the codec; this harness bypasses the socket/framing stage entirely (the
        /// corpus is pre-encoded in memory), so it strips that prefix itself before calling
        /// <see cref="ArteryEnvelopeCodec.Decode"/>.
        /// </summary>
        public static DecodedItem DecodeAndRoute(byte[] frame, int laneCount)
        {
            var frameBody = new System.Buffers.ReadOnlySequence<byte>(
                frame, ArteryEnvelopeHeader.FrameLengthFieldLength, frame.Length - ArteryEnvelopeHeader.FrameLengthFieldLength);

            var decoded = ArteryEnvelopeCodec.Decode(frameBody);

            // Only what the real inbound decode island resolves on the hot path: recipient (routing)
            // and manifest (needed to deserialize). Sender is NOT resolved here -- design.md's decode
            // order only requires recipient + serializer id + manifest before fan-out; sender
            // resolution happens later in the pipeline (handshake/quarantine stages), not on this
            // island, so this harness does not pay for it here either.
            decoded.TryGetRecipientPath(out var recipientPath);
            decoded.TryGetManifest(out var manifest);

            var lane = RecipientHash.Lane(recipientPath ?? string.Empty, laneCount);

            return new DecodedItem(decoded.Header.SerializerId, manifest, decoded.Payload, lane);
        }
    }
}
