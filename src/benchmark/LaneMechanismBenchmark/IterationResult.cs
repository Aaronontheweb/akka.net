//-----------------------------------------------------------------------
// <copyright file="IterationResult.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System.Diagnostics;

namespace LaneMechanismBenchmark
{
    /// <summary>One mechanism/iteration's measured (or warmup) drain result.</summary>
    internal sealed record IterationResult(
        string Mechanism,
        int Lanes,
        int Iteration,
        int Messages,
        double ElapsedMs,
        double MsgsPerSec,
        long AllocBytes,
        double AllocBytesPerMsg,
        long Checksum,
        bool Warmup);

    /// <summary>Shared result construction so every mechanism runner computes the same derived metrics the same way.</summary>
    internal static class ResultFactory
    {
        public static IterationResult Create(
            string mechanism, int lanes, int iteration, int messages, Stopwatch stopwatch,
            long allocBytesDelta, long checksum, bool warmup)
        {
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var msgsPerSec = elapsedMs > 0 ? messages / (elapsedMs / 1000.0) : double.NaN;
            var allocPerMsg = messages > 0 ? (double)allocBytesDelta / messages : double.NaN;

            return new IterationResult(
                mechanism, lanes, iteration, messages, elapsedMs, msgsPerSec,
                allocBytesDelta, allocPerMsg, checksum, warmup);
        }
    }
}
