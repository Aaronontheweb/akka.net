//-----------------------------------------------------------------------
// <copyright file="Program.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Streams;
using LaneMechanismBenchmark.Mechanisms;

namespace LaneMechanismBenchmark
{
    /// <summary>
    /// G5-entry lane-mechanism re-baseline harness (openspec/changes/artery-tcp-remoting/design.md).
    /// Compares the fused-single-consumer baseline against bounded-Channel and Akka.Streams
    /// PartitionHub inbound-lane fan-out, on the real <c>ArteryEnvelopeCodec</c> encode/decode path
    /// and real Akka <c>Serialization</c>, at lane counts {1, 2, 4, 8}.
    ///
    /// <para>
    /// This binary intentionally runs ONE mechanism per process invocation (see CLI below) so a
    /// quiet-box protocol can launch each (mechanism, lane-count) combination as its own clean
    /// process -- no shared-process warm-up/GC-generation carryover between mechanisms.
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var options = CliOptions.Parse(args);
            if (options is null)
            {
                CliOptions.PrintUsage();
                return 1;
            }

            Console.WriteLine(
                $"# LaneMechanismBenchmark mechanism={options.Mechanism.ToString().ToLowerInvariant()} " +
                $"lanes={options.Lanes} messages={options.Messages} iterations={options.Iterations} " +
                $"warmup={options.WarmupIterations} recipients={options.Recipients} " +
                $"channel-capacity={options.ChannelCapacity} hub-buffer={options.HubBuffer} seed={options.Seed}");

            using var system = ActorSystem.Create("lane-mechanism-benchmark");
            var materializer = options.Mechanism == Mechanism.PartitionHub ? system.Materializer() : null;

            try
            {
                Console.WriteLine("# building corpus (real ArteryEnvelopeCodec.Encode + real Akka Serialization, default 'json' binding)...");
                var buildSw = Stopwatch.StartNew();
                var corpus = FrameCorpus.Build(system, options.Messages, options.Recipients, options.Seed);
                buildSw.Stop();
                Console.WriteLine(
                    $"# corpus built: {corpus.Length} frames in {buildSw.Elapsed.TotalMilliseconds:F1} ms " +
                    $"(avg frame size {FrameCorpus.AverageFrameSize(corpus):F0} bytes)");

                var serialization = system.Serialization;
                var measured = new List<IterationResult>();
                var totalRuns = options.WarmupIterations + options.Iterations;

                Console.WriteLine("phase,mechanism,lanes,iteration,messages,elapsed_ms,msgs_per_sec,alloc_bytes,alloc_bytes_per_msg,checksum");

                for (var run = 1; run <= totalRuns; run++)
                {
                    var warmup = run <= options.WarmupIterations;
                    var iterationLabel = warmup ? run : run - options.WarmupIterations;

                    var result = options.Mechanism switch
                    {
                        Mechanism.Baseline => BaselineRunner.Run(serialization, corpus, options.Lanes, iterationLabel, warmup),
                        Mechanism.Channels => await ChannelsRunner.RunAsync(serialization, corpus, options.Lanes, options.ChannelCapacity, iterationLabel, warmup),
                        Mechanism.PartitionHub => await PartitionHubRunner.RunAsync(materializer!, serialization, corpus, options.Lanes, options.HubBuffer, iterationLabel, warmup),
                        _ => throw new InvalidOperationException($"Unhandled mechanism {options.Mechanism}.")
                    };

                    PrintRow(result);
                    if (!warmup)
                        measured.Add(result);
                }

                PrintSummary(measured);
                return 0;
            }
            finally
            {
                materializer?.Dispose();
                await system.Terminate();
            }
        }

        private static void PrintRow(IterationResult r)
        {
            Console.WriteLine(string.Join(",",
                r.Warmup ? "warmup" : "measured",
                r.Mechanism,
                r.Lanes.ToString(CultureInfo.InvariantCulture),
                r.Iteration.ToString(CultureInfo.InvariantCulture),
                r.Messages.ToString(CultureInfo.InvariantCulture),
                r.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                r.MsgsPerSec.ToString("F1", CultureInfo.InvariantCulture),
                r.AllocBytes.ToString(CultureInfo.InvariantCulture),
                r.AllocBytesPerMsg.ToString("F2", CultureInfo.InvariantCulture),
                r.Checksum.ToString(CultureInfo.InvariantCulture)));
        }

        private static void PrintSummary(IReadOnlyList<IterationResult> measured)
        {
            if (measured.Count == 0)
            {
                Console.WriteLine("# no measured iterations (warmup-only run)");
                return;
            }

            var mechanism = measured[0].Mechanism;
            var lanes = measured[0].Lanes;
            var messages = measured[0].Messages;
            var meanElapsedMs = measured.Average(r => r.ElapsedMs);
            var meanMsgsPerSec = measured.Average(r => r.MsgsPerSec);
            var meanAllocBytes = measured.Average(r => r.AllocBytes);
            var meanAllocPerMsg = measured.Average(r => r.AllocBytesPerMsg);

            Console.WriteLine(string.Join(",",
                "summary",
                mechanism,
                lanes.ToString(CultureInfo.InvariantCulture),
                measured.Count.ToString(CultureInfo.InvariantCulture) + "_iterations",
                messages.ToString(CultureInfo.InvariantCulture),
                meanElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                meanMsgsPerSec.ToString("F1", CultureInfo.InvariantCulture),
                meanAllocBytes.ToString("F0", CultureInfo.InvariantCulture),
                meanAllocPerMsg.ToString("F2", CultureInfo.InvariantCulture),
                ""));
        }
    }
}
