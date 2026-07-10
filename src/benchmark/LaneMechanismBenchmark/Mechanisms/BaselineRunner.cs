//-----------------------------------------------------------------------
// <copyright file="BaselineRunner.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Diagnostics;
using Akka.Serialization;

namespace LaneMechanismBenchmark.Mechanisms
{
    /// <summary>
    /// Mechanism (a): the fused single-consumer loop -- today's shape, no fan-out. Decode header +
    /// deserialize payload inline, on one thread, message by message. The lane index is still
    /// computed (via <see cref="FrameDecoder.DecodeAndRoute"/>) so the per-message decode cost is
    /// IDENTICAL to the other two mechanisms (mechanism-fairness) even though nothing routes on it.
    /// </summary>
    internal static class BaselineRunner
    {
        public static IterationResult Run(
            Serialization serialization, byte[][] corpus, int lanes, int iteration, bool warmup)
        {
            long checksum = 0;
            var processed = 0;

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < corpus.Length; i++)
            {
                var item = FrameDecoder.DecodeAndRoute(corpus[i], lanes);
                var obj = serialization.Deserialize(item.Payload, item.SerializerId, item.Manifest);
                checksum += ((LaneBenchMessage)obj).Id;
                processed++;
            }

            sw.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            if (processed != corpus.Length)
                throw new InvalidOperationException(
                    $"Baseline drain mismatch: processed {processed} of {corpus.Length} frames.");

            return ResultFactory.Create("baseline", lanes, iteration, corpus.Length, sw, after - before, checksum, warmup);
        }
    }
}
