//-----------------------------------------------------------------------
// <copyright file="LaneBenchMessage.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

namespace LaneMechanismBenchmark
{
    /// <summary>
    /// The corpus message. A small, "realistic" POCO -- a handful of scalar fields plus one
    /// short string and one short array -- serialized through Akka's DEFAULT serializer
    /// binding (<c>"System.Object" = json</c> -&gt; <see cref="Akka.Serialization.NewtonSoftJsonSerializer"/>,
    /// wrapped as a <see cref="Akka.Serialization.SerializerV2"/> via
    /// <see cref="Akka.Serialization.SerializerV1Adapter"/> -- see design.md Decision 4's note that
    /// <c>FindSerializerV2For</c> always returns a V2-shaped serializer, native or adapted).
    ///
    /// <para>
    /// This is a deliberate, documented choice (task requirements allow "a small POCO with a few
    /// fields serialized via the default serializer"): it exercises REAL reflection-based
    /// JSON.NET serialize/deserialize (with <c>TypeNameHandling.All</c>, so the manifest tag is
    /// legitimately ABSENT -- <see cref="Akka.Serialization.NewtonSoftJsonSerializer.IncludeManifest"/>
    /// is <see langword="false"/> -- and the payload itself carries the <c>$type</c> token needed to
    /// round-trip). A source-generated <c>Akka.Serialization.V2</c> MessagePack message would be
    /// cheaper per-message, but this benchmark's job is to compare FAN-OUT MECHANISMS under a
    /// representative deserialize cost, not to find the fastest possible serializer -- and JSON.NET's
    /// reflection-based path is the actual default a user gets today, so it under-states rather than
    /// over-states the lanes' payoff.
    /// </para>
    /// </summary>
    public sealed class LaneBenchMessage
    {
        public long Id { get; set; }

        public string CorrelationId { get; set; } = string.Empty;

        public double Value { get; set; }

        public long TimestampTicks { get; set; }

        /// <summary>A short filler string standing in for a realistic small business payload field.</summary>
        public string Payload { get; set; } = string.Empty;

        /// <summary>A short array field, so deserialization walks a JSON array token, not just scalars.</summary>
        public int[] Tags { get; set; } = System.Array.Empty<int>();
    }
}
