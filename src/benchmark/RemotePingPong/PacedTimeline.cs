// -----------------------------------------------------------------------
// <copyright file="PacedTimeline.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://dotnetfoundation.org/>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace RemotePingPong
{
    /// <summary>Exports existing samples after timing; never called by the per-message path.</summary>
    internal static class PacedTimeline
    {
        internal const int SamplingInterval = 64;
        internal sealed record Export(Guid PhaseId, string Path, string Sha256, int Samples,
            int TotalSamples, int SamplingInterval, int FormatVersion = 1);

        internal static Export Write(string directory, Guid phaseId, int window,
            IReadOnlyList<ValidatedBenchmark.Outcome> outcomes, PacedBenchmark.Releases releases,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = releases.Actual.Length;
            if (phaseId == Guid.Empty || releases.Origin <= 0 || window < 1 || messages < 2
                || outcomes.Count < 1 || (long)messages * outcomes.Count > PacedBenchmark.MaximumSamples
                || releases.IntervalMs is < 1 or > 1000 || outcomes.Any(o => o.Samples.Length != messages))
                throw new ArgumentException("Timeline requires a complete bounded paced phase.");

            var step = PacedBenchmark.IntervalTicks(releases.IntervalMs);
            var lastEcho = outcomes.Max(o => checked(releases.Origin + step * messages + o.Samples[^1].RoundTrip));
            var clock = ClockEvents.Log;
            // The trace event timestamp lies between these bounds. No assumption about
            // TraceEvent's private QPC epoch or wall-clock synchronization is needed.
            var anchorBefore = Stopwatch.GetTimestamp();
            clock.ClockAnchor(phaseId.ToString("D"), anchorBefore);
            var anchorAfter = Stopwatch.GetTimestamp();

            Directory.CreateDirectory(directory);
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, phaseId.ToString("D") + ".json"));
            var count = 0;
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", 1);
                writer.WriteString("phaseId", phaseId);
                writer.WriteNumber("frequency", Stopwatch.Frequency);
                writer.WriteNumber("origin", releases.Origin);
                writer.WriteNumber("lastEcho", lastEcho);
                writer.WriteNumber("anchorBefore", anchorBefore);
                writer.WriteNumber("anchorAfter", anchorAfter);
                writer.WriteNumber("clients", outcomes.Count);
                writer.WriteNumber("messagesPerClient", messages);
                writer.WriteNumber("window", window);
                writer.WriteNumber("intervalMs", releases.IntervalMs);
                writer.WriteNumber("samplingInterval", SamplingInterval);
                writer.WriteStartArray("releases");
                for (var i = 0; i < messages; i++)
                {
                    if (releases.Actual[i] < checked(releases.Origin + step * (i + 1L))
                        || (i > 0 && releases.Actual[i] < releases.Actual[i - 1]))
                        throw new InvalidOperationException("Timeline release timestamps are inconsistent.");
                    writer.WriteNumberValue(releases.Actual[i]);
                }
                writer.WriteEndArray();
                writer.WriteStartArray("samples");
                for (var client = 0; client < outcomes.Count; client++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var index = client % SamplingInterval; index < messages; index += SamplingInterval)
                    {
                        var sample = outcomes[client].Samples[index];
                        var due = checked(releases.Origin + step * (index + 1L));
                        if (sample.DispatchDelay < releases.Actual[index] - due || sample.RoundTrip <= 0
                            || sample.RoundTrip < sample.DispatchDelay || checked(due + sample.RoundTrip) > lastEcho)
                            throw new InvalidOperationException("Timeline RTT timestamps are inconsistent.");
                        writer.WriteStartArray();
                        writer.WriteNumberValue(client);
                        writer.WriteNumberValue(index);
                        writer.WriteNumberValue(sample.DispatchDelay);
                        writer.WriteNumberValue(sample.RoundTrip);
                        writer.WriteEndArray();
                        count++;
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            using var input = File.OpenRead(path);
            var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            return new Export(phaseId, path, digest, count, checked(messages * outcomes.Count), SamplingInterval);
        }

        [EventSource(Name = "RemotePingPong-PacedTimeline")]
        private sealed class ClockEvents : EventSource
        {
            internal static readonly ClockEvents Log = new();

            [Event(1, Level = EventLevel.Informational)]
            public void ClockAnchor(string phaseId, long beforeTicks) => WriteEvent(1, phaseId, beforeTicks);
        }
    }
}
