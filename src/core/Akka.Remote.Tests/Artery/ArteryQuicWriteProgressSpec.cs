// -----------------------------------------------------------------------
// <copyright file="ArteryQuicWriteProgressSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Akka.Remote.Artery;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("osx")]
    public sealed class ArteryQuicWriteProgressSpec
    {
        [Theory(DisplayName = "Write diagnostics inspect readiness once and consume pending input once without returning its rental early")]
        [InlineData(false, "success")]
        [InlineData(true, "success")]
        [InlineData(false, "fault")]
        [InlineData(true, "fault")]
        [InlineData(false, "cancel")]
        [InlineData(true, "cancel")]
        public async Task Should_PreserveConsumptionAndRentalLifetime_When_ProgressIsOptional(bool enabled, string outcome)
        {
            var counters = new ArteryQuicWriteProgress(1);
            var probe = enabled ? new ArteryQuicWriteProgress.Writer(counters) : null;
            var pool = new Pool();
            using var stream = new PendingStream();
            using var cancellation = new CancellationTokenSource();
            var write = ArteryQuicWriteStage.WriteCoalescedAsync(stream, new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }),
                pool, cancellation.Token, probe);
            Assert.False(write.IsCompleted);
            Assert.Equal(enabled ? 2 : 1, stream.StatusReads);
            Assert.Equal(0, stream.Consumptions);
            Assert.Equal(0, pool.Returns);
            Assert.Equal(new byte[] { 1, 2, 3 }, stream.Input.ToArray());
            Assert.Equal(enabled ? 128 : 0, counters.Read().CurrentRentedCapacityBytes);
            if (outcome == "success")
            {
                stream.Settle(null);
                await write;
            }
            else if (outcome == "fault")
            {
                stream.Settle(new IOException("injected pending fault"));
                await Assert.ThrowsAsync<IOException>(async () => await write);
            }
            else
            {
                cancellation.Cancel();
                stream.Settle(new OperationCanceledException(cancellation.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await write);
            }
            Assert.Equal(1, stream.Consumptions);
            Assert.Equal(1, pool.Returns);
            var snapshot = counters.Read();
            Assert.Equal(0, snapshot.CurrentRentedCapacityBytes);
            Assert.Equal(enabled ? 128 : 0, snapshot.LifetimePeakRentedCapacityBytes);
            Assert.Equal(enabled ? 1 : 0, snapshot.Calls);
            Assert.Equal(enabled ? 3 : 0, snapshot.Bytes);
            Assert.Equal(enabled ? 3 : 0, snapshot.CopiedBytes);
            Assert.Equal(enabled ? 1 : 0, snapshot.RentOperations);
            Assert.Equal(0, snapshot.ReadyOnInspection);
            Assert.Equal(enabled && outcome == "success" ? 1 : 0, snapshot.Completed);
            Assert.Equal(enabled && outcome == "cancel" ? 1 : 0, snapshot.Canceled);
            Assert.Equal(enabled && outcome == "fault" ? 1 : 0, snapshot.Faulted);
            Assert.Equal(enabled && outcome == "success" ? 1 : 0, snapshot.Samples);
            Assert.True(snapshot.CompletionTicks >= snapshot.InvocationTicks);
        }

        [Fact(DisplayName = "A successfully ready public write and its rented capacity are reported without changing disposal")]
        public async Task Should_CountReadyWrite_When_PublicCallCompletesImmediately()
        {
            var counters = new ArteryQuicWriteProgress(1);
            var pool = new Pool();
            using var stream = new MemoryStream();
            var write = ArteryQuicWriteStage.WriteCoalescedAsync(stream, new ReadOnlySequence<byte>(new byte[] { 4, 5 }),
                pool, CancellationToken.None, new ArteryQuicWriteProgress.Writer(counters));
            await write;
            var snapshot = counters.Read();
            Assert.Equal(1, snapshot.ReadyOnInspection);
            Assert.Equal(1, snapshot.Completed);
            Assert.Equal(1, snapshot.Samples);
            Assert.Equal(0, snapshot.CurrentRentedCapacityBytes);
            Assert.Equal(new byte[] { 4, 5 }, stream.ToArray());
            Assert.Equal(1, pool.Returns);
        }

        [Fact(DisplayName = "Sampling covers every writer and pooled high-water remains a lifetime observation")]
        public void Should_SampleEachWriterAndKeepLifetimePeak_When_CountersAreShared()
        {
            var counters = new ArteryQuicWriteProgress(2);
            var first = new ArteryQuicWriteProgress.Writer(counters);
            var second = new ArteryQuicWriteProgress.Writer(counters);
            foreach (var writer in new[] { first, second })
                for (var i = 0; i < 2; i++)
                {
                    writer.BeginBatch();
                    writer.BeforeInvocation(10);
                    writer.InvocationReturned(true);
                    writer.Completed();
                    writer.CallbackPosted();
                    writer.CallbackHandled();
                }
            first.Rented(128);
            second.Rented(64);
            first.Returned(128);
            var before = counters.Read();
            second.Returned(64);
            var difference = counters.Read().Since(before);
            Assert.Equal(0, difference.CurrentRentedCapacityBytes);
            Assert.Equal(192, difference.LifetimePeakRentedCapacityBytes);
            Assert.Equal(4, before.Calls);
            Assert.Equal(2, before.Samples);
            Assert.Equal(2, before.CallbackSamples);
        }

        private sealed class Pool : ArrayPool<byte>
        {
            private readonly byte[] _buffer = new byte[128];
            internal int Returns { get; private set; }
            public override byte[] Rent(int minimumLength) => _buffer;
            public override void Return(byte[] array, bool clearArray = false)
            {
                Assert.Same(_buffer, array);
                Returns++;
                Array.Fill(array, (byte)0xEE);
            }
        }

        private sealed class PendingStream : Stream, IValueTaskSource
        {
            private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };
            internal int StatusReads { get; private set; }
            internal int Consumptions { get; private set; }
            internal ReadOnlyMemory<byte> Input { get; private set; }
            internal void Settle(Exception? error)
            {
                if (error is null) _source.SetResult(true);
                else _source.SetException(error);
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Input = buffer;
                return new ValueTask(this, _source.Version);
            }
            public ValueTaskSourceStatus GetStatus(short token)
            {
                StatusReads++;
                return _source.GetStatus(token);
            }
            public void GetResult(short token)
            {
                Consumptions++;
                _source.GetResult(token);
            }
            public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
                _source.OnCompleted(continuation, state, token, flags);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
