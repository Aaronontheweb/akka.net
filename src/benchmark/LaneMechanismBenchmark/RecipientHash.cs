//-----------------------------------------------------------------------
// <copyright file="RecipientHash.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

namespace LaneMechanismBenchmark
{
    /// <summary>
    /// The lane partition key: a STABLE hash of the recipient path string (never
    /// <see cref="string.GetHashCode()"/>, which is randomized per-process on .NET and would make
    /// lane assignment non-reproducible run to run). FNV-1a over the UTF-16 code units, matching the
    /// style of the Task 0 harness's <c>DeserializeKnob</c> (task0-results.md /
    /// <c>Akka.Benchmarks.Remoting.Artery.ArteryWireFormat</c>).
    /// </summary>
    internal static class RecipientHash
    {
        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime = 16777619;

        /// <summary>
        /// Maps <paramref name="recipientPath"/> to a lane index in <c>[0, laneCount)</c>. Computed
        /// entirely in <see cref="uint"/> arithmetic, so "abs(hash % lanes)" falls out for free --
        /// there is no int.MinValue / <c>Math.Abs</c> overflow edge case to guard against.
        /// </summary>
        public static int Lane(string recipientPath, int laneCount)
        {
            if (laneCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(laneCount), laneCount, "Lane count must be positive.");

            var hash = Fnv1A(recipientPath);
            return (int)(hash % (uint)laneCount);
        }

        private static uint Fnv1A(string value)
        {
            var hash = FnvOffsetBasis;
            foreach (var c in value)
            {
                hash = (hash ^ (byte)(c & 0xFF)) * FnvPrime;
                hash = (hash ^ (byte)(c >> 8)) * FnvPrime;
            }

            return hash;
        }
    }
}
