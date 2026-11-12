// -----------------------------------------------------------------------
// <copyright file="PacedTimelineSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://dotnetfoundation.org/>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace RemotePingPong.Tests
{
    public sealed class PacedTimelineSpec : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "akka-rtt-" + Guid.NewGuid());

        [Fact(DisplayName = "RTT timeline reconstructs sampled times without modifying existing samples")]
        public void Should_PreserveSamplesAndExportKnownTimes_When_PhaseIsComplete()
        {
            var outcomes = new[] { new ValidatedBenchmark.Outcome(129), new ValidatedBenchmark.Outcome(129) };
            var step = PacedBenchmark.IntervalTicks(2);
            var origin = 100L;
            foreach (var outcome in outcomes)
                for (var i = 0; i < 129; i++) outcome.Samples[i] = new PacedBenchmark.Sample(10, 30);
            var before = outcomes.Select(o => o.Samples.ToArray()).ToArray();
            var releases = new PacedBenchmark.Releases(origin,
                Enumerable.Range(1, 129).Select(i => origin + step * i + 5).ToArray(), 2);
            var phase = Guid.NewGuid();
            var export = PacedTimeline.Write(_directory, phase, 1, outcomes, releases, CancellationToken.None);
            using var document = JsonDocument.Parse(File.ReadAllText(export.Path));
            var data = document.RootElement;
            Assert.Equal(phase, data.GetProperty("phaseId").GetGuid());
            Assert.Equal(Stopwatch.Frequency, data.GetProperty("frequency").GetInt64());
            Assert.True(data.GetProperty("anchorAfter").GetInt64() >= data.GetProperty("anchorBefore").GetInt64());
            Assert.Equal(origin + step * 129 + 30, data.GetProperty("lastEcho").GetInt64());
            var rows = data.GetProperty("samples").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetInt64()).ToArray()).ToArray();
            Assert.Equal(new long[] { 0, 64, 128, 1, 65 }, rows.Select(r => r[1]));
            Assert.All(rows, r => Assert.Equal(new long[] { 10, 30 }, r.Skip(2)));
            Assert.Equal(5, export.Samples);
            Assert.Equal(258, export.TotalSamples);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(export.Path))).ToLowerInvariant(), export.Sha256);
            for (var client = 0; client < outcomes.Length; client++)
                Assert.Equal(before[client], outcomes[client].Samples);
        }

        [Fact(DisplayName = "Timeline phase files cannot overwrite an earlier observation")]
        public void Should_KeepExistingEvidence_When_PhaseFileAlreadyExists()
        {
            var phase = Guid.NewGuid();
            Directory.CreateDirectory(_directory);
            var path = System.IO.Path.Combine(_directory, phase.ToString("D") + ".json");
            File.WriteAllText(path, "retained observation");
            var outcome = new ValidatedBenchmark.Outcome(2);
            outcome.Samples[0] = outcome.Samples[1] = new PacedBenchmark.Sample(1, 2);
            var step = PacedBenchmark.IntervalTicks(1);
            Assert.Throws<IOException>(() => PacedTimeline.Write(_directory, phase, 1, new[] { outcome },
                new PacedBenchmark.Releases(1, new[] { step + 1, step * 2 + 1 }, 1), CancellationToken.None));
            Assert.Equal("retained observation", File.ReadAllText(path));
        }

        [Theory(DisplayName = "Timeline rejects incomplete or contradictory existing timestamps")]
        [InlineData("incomplete")]
        [InlineData("early-release")]
        [InlineData("dispatch-before-release")]
        public void Should_RejectExport_When_ExistingSamplesAreInvalid(string fault)
        {
            var step = PacedBenchmark.IntervalTicks(1);
            var outcome = new ValidatedBenchmark.Outcome(fault == "incomplete" ? 1 : 2);
            outcome.Samples[0] = new PacedBenchmark.Sample(fault == "dispatch-before-release" ? 0 : 2, 10);
            if (outcome.Samples.Length == 2) outcome.Samples[1] = new PacedBenchmark.Sample(2, 10);
            var releases = new PacedBenchmark.Releases(100,
                new[] { 100 + step + (fault == "early-release" ? -1 : 1), 100 + step * 2 + 1 }, 1);
            var error = Record.Exception(() => PacedTimeline.Write(_directory, Guid.NewGuid(), 1,
                new[] { outcome }, releases, CancellationToken.None));
            Assert.NotNull(error);
            if (fault == "incomplete") Assert.IsType<ArgumentException>(error);
            else Assert.IsType<InvalidOperationException>(error);
        }

        [Fact(DisplayName = "Cancelled timeline export does not create an output directory")]
        public void Should_StopBeforeWriting_When_AlreadyCancelled()
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => PacedTimeline.Write(_directory, Guid.NewGuid(), 1,
                Array.Empty<ValidatedBenchmark.Outcome>(), new PacedBenchmark.Releases(1, Array.Empty<long>(), 1), cancelled.Token));
            Assert.False(Directory.Exists(_directory));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
