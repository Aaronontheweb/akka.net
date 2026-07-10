//-----------------------------------------------------------------------
// <copyright file="ChannelsRunner.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Serialization;

namespace LaneMechanismBenchmark.Mechanisms
{
    /// <summary>
    /// Mechanism (b): one bounded <see cref="Channel{T}"/> per lane (recipient-hash selected), with
    /// one long-running consumer <see cref="Task"/> per lane doing the real deserialize. The
    /// distributor -- decode header, compute lane, write to that lane's channel -- runs single-
    /// threaded on the calling (measurement) thread, mirroring the real Artery decode island; only
    /// the per-lane deserialize work is parallel.
    /// </summary>
    internal static class ChannelsRunner
    {
        public static async Task<IterationResult> RunAsync(
            Serialization serialization, byte[][] corpus, int lanes, int channelCapacity, int iteration, bool warmup)
        {
            var total = corpus.Length;
            long processed = 0;
            long checksum = 0;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var channels = new Channel<DecodedItem>[lanes];
            for (var i = 0; i < lanes; i++)
            {
                channels[i] = Channel.CreateBounded<DecodedItem>(new BoundedChannelOptions(channelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
            }

            var laneTasks = new Task[lanes];
            for (var i = 0; i < lanes; i++)
            {
                var reader = channels[i].Reader;
                laneTasks[i] = Task.Run(async () =>
                {
                    await foreach (var item in reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        var obj = serialization.Deserialize(item.Payload, item.SerializerId, item.Manifest);
                        Interlocked.Add(ref checksum, ((LaneBenchMessage)obj).Id);
                        if (Interlocked.Increment(ref processed) == total)
                            done.TrySetResult(true);
                    }
                });
            }

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();

            // Serial "decode island": header decode + lane hash on this thread only; never awaits a
            // slow consumer -- backpressure is a spin-retry TryWrite, matching the bounded-queue /
            // no-WriteAsync-await rule design.md lays down for the real outbound path (Decision 7).
            for (var i = 0; i < corpus.Length; i++)
            {
                var item = FrameDecoder.DecodeAndRoute(corpus[i], lanes);
                var writer = channels[item.Lane].Writer;
                while (!writer.TryWrite(item))
                    Thread.SpinWait(16);
            }

            foreach (var channel in channels)
                channel.Writer.Complete();

            await done.Task.ConfigureAwait(false);
            sw.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            // Outside the timed region: clean teardown only, not part of the measurement.
            await Task.WhenAll(laneTasks).ConfigureAwait(false);

            if (Interlocked.Read(ref processed) != total)
                throw new InvalidOperationException($"Channels drain mismatch: processed {processed} of {total} frames.");

            return ResultFactory.Create("channels", lanes, iteration, total, sw, after - before, checksum, warmup);
        }
    }
}
