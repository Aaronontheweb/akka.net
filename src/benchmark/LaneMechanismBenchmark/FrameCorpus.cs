//-----------------------------------------------------------------------
// <copyright file="FrameCorpus.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using Akka.Actor;
using Akka.Remote.Artery;

namespace LaneMechanismBenchmark
{
    /// <summary>
    /// Builds the pre-encoded frame corpus using the REAL Artery encode path
    /// (<see cref="ArteryEnvelopeCodec.Encode(Akka.Serialization.Serialization,long,string?,string?,object,System.Buffers.ArrayPool{byte}?)"/>)
    /// and the REAL Akka <see cref="Akka.Serialization.Serialization"/> extension -- not a synthetic
    /// wire format or a synthetic deserialize-cost knob. This runs entirely BEFORE any mechanism's
    /// timed drain: encoding must not pollute the measurement (task requirement).
    /// </summary>
    internal static class FrameCorpus
    {
        private const long OriginUid = 0x1122334455667788L;
        private const string SenderPath = "/user/bench/sender";

        /// <summary>
        /// Builds <paramref name="messageCount"/> pre-encoded frames, round-robining across
        /// <paramref name="recipientCount"/> distinct recipient path strings so lane assignment is
        /// reproducible and (for a recipient count that divides <paramref name="messageCount"/>
        /// evenly, e.g. the default 100,000 / 32) exactly balanced per recipient.
        /// </summary>
        public static byte[][] Build(ActorSystem system, int messageCount, int recipientCount, int seed)
        {
            if (messageCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(messageCount));
            if (recipientCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(recipientCount));

            var serialization = system.Serialization;

            var recipientPaths = new string[recipientCount];
            for (var r = 0; r < recipientCount; r++)
                recipientPaths[r] = $"/user/bench/recipient-{r:D3}";

            // seed reserved for future corpus randomization (e.g. non-uniform recipient skew);
            // round-robin assignment below is already deterministic without it.
            _ = seed;

            var frames = new byte[messageCount][];
            for (var i = 0; i < messageCount; i++)
            {
                var message = new LaneBenchMessage
                {
                    Id = i,
                    CorrelationId = $"corr-{i:D7}",
                    Value = i * 0.5d,
                    TimestampTicks = i,
                    Payload = "the-quick-brown-fox-jumps-over-the-lazy-dog-0123",
                    Tags = new[] { i % 7, i % 11, i % 13, i % 17 }
                };

                var recipientPath = recipientPaths[i % recipientCount];

                using var writer = ArteryEnvelopeCodec.Encode(
                    serialization, OriginUid, SenderPath, recipientPath, message);

                frames[i] = writer.WrittenSpan.ToArray();
            }

            return frames;
        }

        public static double AverageFrameSize(byte[][] corpus)
        {
            if (corpus.Length == 0)
                return 0;

            long total = 0;
            foreach (var frame in corpus)
                total += frame.Length;

            return (double)total / corpus.Length;
        }
    }
}
