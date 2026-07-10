//-----------------------------------------------------------------------
// <copyright file="PartitionHubRunner.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Akka.Serialization;
using Akka.Streams;
using Akka.Streams.Dsl;

namespace LaneMechanismBenchmark.Mechanisms
{
    /// <summary>
    /// Mechanism (c): stock Akka.Streams <see cref="PartitionHub"/> (src/core/Akka.Streams/Dsl/Hub.cs)
    /// with N separately-materialized lane sinks doing the deserialize -- the canonical JVM Artery
    /// inbound-lane shape (design.md: <c>MergeHub.source -&gt; inboundFlow -&gt; FixedSizePartitionHub
    /// -&gt; per-lane sinks</c>), using the stock .NET <see cref="Dsl.PartitionHub"/> rather than a
    /// hand-rolled fixed-size variant.
    ///
    /// <para>
    /// <b>Start-of-flow gating.</b> <c>PartitionHub</c>'s upstream is only pulled once
    /// <c>startAfterNrOfConsumers</c> lane sinks have registered, but that registration is itself
    /// asynchronous (actor-callback dispatched), so "the last <c>RunWith</c> call returned" does not
    /// guarantee "the hub has not yet started pulling". Rather than race the <see cref="Stopwatch"/>
    /// against that registration, the corpus is wrapped in an <see cref="IEnumerable{T}"/> that
    /// blocks on a gate (<see cref="ManualResetEventSlim"/>) before yielding its first element --
    /// paid once, before any timed message flows, never per-message -- so the timed region is exactly
    /// [gate opened .. last message processed], regardless of how registration interleaves.
    /// </para>
    /// </summary>
    internal static class PartitionHubRunner
    {
        public static async Task<IterationResult> RunAsync(
            IMaterializer materializer, Serialization serialization, byte[][] corpus,
            int lanes, int hubBufferSize, int iteration, bool warmup)
        {
            var total = corpus.Length;
            long processed = 0;
            long checksum = 0;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new ManualResetEventSlim(false);

            var hubSource = Source.From(GatedCorpus(corpus, gate))
                .Select(frame => FrameDecoder.DecodeAndRoute(frame, lanes))
                .RunWith(
                    PartitionHub.Sink<DecodedItem>(
                        (_, item) => item.Lane,
                        startAfterNrOfConsumers: lanes,
                        bufferSize: hubBufferSize),
                    materializer);

            var laneTasks = new Task[lanes];
            for (var i = 0; i < lanes; i++)
            {
                laneTasks[i] = hubSource.RunWith(
                    Sink.ForEach<DecodedItem>(item =>
                    {
                        var obj = serialization.Deserialize(item.Payload, item.SerializerId, item.Manifest);
                        Interlocked.Add(ref checksum, ((LaneBenchMessage)obj).Id);
                        if (Interlocked.Increment(ref processed) == total)
                            done.TrySetResult(true);
                    }),
                    materializer);
            }

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();

            gate.Set();
            await done.Task.ConfigureAwait(false);

            sw.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            // Outside the timed region: let the graph finish tearing itself down cleanly.
            await Task.WhenAll(laneTasks).ConfigureAwait(false);

            if (Interlocked.Read(ref processed) != total)
                throw new InvalidOperationException($"PartitionHub drain mismatch: processed {processed} of {total} frames.");

            return ResultFactory.Create("partitionhub", lanes, iteration, total, sw, after - before, checksum, warmup);
        }

        private static IEnumerable<byte[]> GatedCorpus(byte[][] corpus, ManualResetEventSlim gate)
        {
            gate.Wait();
            foreach (var frame in corpus)
                yield return frame;
        }
    }
}
