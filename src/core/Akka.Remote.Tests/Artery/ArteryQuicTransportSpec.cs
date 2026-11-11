// -----------------------------------------------------------------------
// <copyright file="ArteryQuicTransportSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Event;
using Akka.IO;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.TestKit;
using FluentAssertions;
using Xunit;
using static Akka.Remote.Tests.Artery.ArteryTlsTransportSpec;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>Native byte-adapter failure tests; actor/protocol parity is exercised by ArteryTlsTransportSpec.</summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("osx")]
    public sealed class ArteryQuicTransportSpec : AkkaSpec
    {
        public ArteryQuicTransportSpec(ITestOutputHelper output) : base(output) { }

        [Theory(DisplayName = "QUIC sends FIN when upstream finishes before connection opening completes")]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Should_CompleteWrites_When_UpstreamFinishesDuringConnect(int roleValue)
        {
            var role = (ArteryStreamId)roleValue;
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.ServerOnly(certificate, TrustCertificate(certificate));
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), Logging.GetLogger(Sys, GetType()));
            var materializer = ActorMaterializer.Create(Sys);
            var allowHandshake = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { ArteryQuicTransport.ApplicationProtocol },
                ConnectionOptionsCallback = async (_, _, cancellationToken) =>
                {
                    await allowHandshake.Task.WaitAsync(cancellationToken);
                    return new QuicServerConnectionOptions
                    {
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = certificate,
                            EnabledSslProtocols = SslProtocols.Tls13,
                            ApplicationProtocols = new() { ArteryQuicTransport.ApplicationProtocol }
                        },
                        DefaultCloseErrorCode = 1,
                        DefaultStreamErrorCode = 1,
                        // The sender reserves a control stream before opening ordinary.
                        MaxInboundUnidirectionalStreams = 2
                    };
                }
            }, deadline.Token);
            try
            {
                var ((upstreamFinished, connected), writerFinished) = Source.Empty<ReadOnlySequence<byte>>()
                    .WatchTermination(Keep.Right)
                    .ViaMaterialized(sender.Outgoing(listener.LocalEndPoint, role), Keep.Both)
                    .ToMaterialized(Sink.Ignore<ReadOnlySequence<byte>>(), Keep.Both)
                    .Run(materializer);

                // Keep TLS opening pending until the upstream completion has reached the graph.
                await upstreamFinished.WaitAsync(deadline.Token);
                allowHandshake.SetResult(Done.Instance);
                await using var connection = await listener.AcceptConnectionAsync(deadline.Token);
                await using var first = await connection.AcceptInboundStreamAsync(deadline.Token);
                // Opening the ordinary stream also exposes the reserved, unwritten
                // control stream to the peer. Its empty read is intentionally idle.
                await using var ordinary = role == ArteryStreamId.Ordinary
                    ? await connection.AcceptInboundStreamAsync(deadline.Token)
                    : null;
                var stream = ordinary ?? first;
                stream.Id.Should().Be(role == ArteryStreamId.Ordinary ? 6 : 2);
                await connected.WaitAsync(deadline.Token);
                (await stream.ReadAsync(new byte[1], deadline.Token)).Should().Be(0, "an empty upstream still sends a graceful FIN");
                await writerFinished.WaitAsync(deadline.Token);
            }
            finally
            {
                allowHandshake.TrySetCanceled();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "QUIC accumulates ordinary frame bytes with bounded native submissions")]
        [InlineData(4096, 0, 1)]
        [InlineData(8192, 8192, 1)]
        [InlineData(8192, 8193, 2)]
        public async Task Should_AccumulateOrdinaryFrames_When_WritingOwnedSegments(
            int firstSize, int secondSize, int expectedWrites)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, enableIoStatistics: true);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var received = new TaskCompletionSource<Task<IImmutableList<ReadOnlySequence<byte>>>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = new TrackingOwner(firstSize, 0x13);
            var second = secondSize == 0 ? null : new TrackingOwner(secondSize, 0xB1);
            var input = OwnedSequenceSegment.Create(first, firstSize);
            if (second is not null)
                input = ArteryRemoting.AppendFrameToBatch(input, OwnedSequenceSegment.Create(second, secondSize));
            var expected = input.ToArray();
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    received.TrySetResult(source.RunWith(Sink.Seq<ReadOnlySequence<byte>>(), materializer));
                    admitted();
                });
                var preamble = Preamble(ArteryStreamId.Ordinary);
                await Source.Single(new ReadOnlySequence<byte>(preamble)).Concat(Source.Single(input))
                    .Via(sender.Outgoing(endpoint, ArteryStreamId.Ordinary))
                    .RunWith(Sink.Ignore<ReadOnlySequence<byte>>(), materializer).WaitAsync(deadline.Token);
                var chunks = await (await received.Task.WaitAsync(deadline.Token)).WaitAsync(deadline.Token);
                chunks.SelectMany(chunk => chunk.ToArray()).Should().Equal(preamble.Concat(expected));
                first.DisposeCount.Should().Be(1);
                if (second is not null)
                    second.DisposeCount.Should().Be(1);
                sender.IoSnapshot.WriteBytes.Should().Be(preamble.Length + expected.Length);
                sender.IoSnapshot.WriteOperations.Should().BeInRange(1, 1 + expectedWrites);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "QUIC writer cancellation settles after repeated writes on one channel")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_SettleWriter_When_StoppedAfterRepeatedWrites(bool closePeer)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, enableIoStatistics: true);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owners = Enumerable.Range(0, 128).Select(_ => new TrackingOwner(4096, 0x13)).ToArray();
            var frames = owners.Select(owner => OwnedSequenceSegment.Create(owner, 4096)).ToArray();
            var preamble = Preamble(ArteryStreamId.Ordinary);
            var expectedBytes = preamble.Length + owners.Length * 4096;
            var receivedBytes = 0L;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    source.RunWith(Sink.ForEach<ReadOnlySequence<byte>>(bytes =>
                    {
                        if (Interlocked.Add(ref receivedBytes, bytes.Length) == expectedBytes)
                            received.TrySetResult();
                    }), materializer);
                    admitted();
                });
                var ((killSwitch, connected), writerFinished) = Source.Single(new ReadOnlySequence<byte>(preamble))
                    .Concat(Source.From(frames))
                    .ConcatMaterialized(Source.Maybe<ReadOnlySequence<byte>>(), Keep.Left)
                    .ViaMaterialized(KillSwitches.Single<ReadOnlySequence<byte>>(), Keep.Right)
                    .ViaMaterialized(sender.Outgoing(endpoint, ArteryStreamId.Ordinary), Keep.Both)
                    .ToMaterialized(Sink.Ignore<ReadOnlySequence<byte>>(), Keep.Both)
                    .Run(materializer);
                await connected.WaitAsync(deadline.Token);
                await received.Task.WaitAsync(deadline.Token);
                await AwaitAssertAsync(() =>
                {
                    sender.IoSnapshot.WriteBytes.Should().Be(expectedBytes);
                    sender.IoSnapshot.WriteOperations.Should().BeGreaterThan(1);
                }, TimeSpan.FromSeconds(3));
                writerFinished.IsCompleted.Should().BeFalse();

                if (closePeer)
                    await receiver.ShutdownAsync(deadline.Token);
                else
                    killSwitch.Abort(new IOException("Stop the writer after repeated writes."));

                var stopped = async () => await writerFinished.WaitAsync(deadline.Token);
                await stopped.Should().ThrowAsync<Exception>();
                await sender.ShutdownAsync(deadline.Token);
                foreach (var owner in owners)
                    owner.DisposeCount.Should().Be(1);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "QUIC control writer retains a frame through cancellation after the native copy begins")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_ReleaseFrame_When_WriterOrPeerStopsDuringNativeWrite(bool closePeer)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var pressure = new ObservedWriteOwner(32 * 1024 * 1024);
            var owner = new ObservedWriteOwner(32 * 1024 * 1024);
            var pressureFrame = OwnedSequenceSegment.Create(pressure, pressure.Memory.Length);
            var frame = OwnedSequenceSegment.Create(owner, owner.Memory.Length);
            owner.ObserveReads();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((_, _, admitted) => admitted());
                var ((killSwitch, connected), writerFinished) = Source.Single(new ReadOnlySequence<byte>(Preamble(ArteryStreamId.Control)))
                    // MsQuic can buffer one request past its ideal target. Fill that buffer before
                    // copying the next request while the peer remains unread.
                    .Concat(Source.Single(pressureFrame))
                    .Concat(Source.Single(frame))
                    .ConcatMaterialized(Source.Maybe<ReadOnlySequence<byte>>(), Keep.Left)
                    .ViaMaterialized(KillSwitches.Single<ReadOnlySequence<byte>>(), Keep.Right)
                    .ViaMaterialized(sender.Outgoing(endpoint, ArteryStreamId.Control), Keep.Both)
                    .ToMaterialized(Sink.Ignore<ReadOnlySequence<byte>>(), Keep.Both)
                    .Run(materializer);
                await connected.WaitAsync(deadline.Token);
                await owner.ReadStarted.WaitAsync(deadline.Token);
                await AwaitAssertAsync(() => pressure.DisposeCount.Should().Be(1), TimeSpan.FromSeconds(3));
                owner.DisposeCount.Should().Be(0, "the outstanding write must retain its owner while the peer has no reader");

                if (closePeer)
                    await receiver.ShutdownAsync(deadline.Token);
                else
                    killSwitch.Abort(new IOException("Stop the writer during its native write."));

                var stopped = async () => await writerFinished.WaitAsync(deadline.Token);
                await stopped.Should().ThrowAsync<Exception>();
                await sender.ShutdownAsync(deadline.Token);
                owner.DisposeCount.Should().Be(1);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        private sealed class ObservedWriteOwner(int length) : MemoryManager<byte>
        {
            private readonly byte[] _bytes = new byte[length];
            private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _observeReads;
            private int _disposeCount;

            internal Task ReadStarted => _readStarted.Task;
            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            internal void ObserveReads() => Volatile.Write(ref _observeReads, 1);

            public override Span<byte> GetSpan()
            {
                if (Volatile.Read(ref _observeReads) != 0)
                    _readStarted.TrySetResult();
                return _bytes;
            }

            public override MemoryHandle Pin(int elementIndex = 0) => _bytes.AsMemory(elementIndex).Pin();

            // The returned memory handle owns the array pin.
            public override void Unpin() { }

            protected override void Dispose(bool disposing) => Interlocked.Increment(ref _disposeCount);
        }

        [Theory(DisplayName = "QUIC ordinary writer releases an incrementally copied frame when an unread peer stops")]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_ReleaseFrame_When_StoppedDuringIncrementalPipeCopy(bool closePeer)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, enableIoStatistics: true);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var owner = new ObservedWriteOwner(32 * 1024 * 1024);
            var frame = OwnedSequenceSegment.Create(owner, owner.Memory.Length);
            owner.ObserveReads();
            var dropped = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dropReports = 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((_, _, admitted) => admitted());
                var ((killSwitch, connected), writerFinished) = Source.Single(new ReadOnlySequence<byte>(Preamble(ArteryStreamId.Ordinary)))
                    .Concat(Source.Single(frame))
                    .ConcatMaterialized(Source.Maybe<ReadOnlySequence<byte>>(), Keep.Left)
                    .ViaMaterialized(KillSwitches.Single<ReadOnlySequence<byte>>(), Keep.Right)
                    .ViaMaterialized(sender.Outgoing(endpoint, ArteryStreamId.Ordinary, onDropped: count =>
                    {
                        Interlocked.Increment(ref dropReports);
                        dropped.TrySetResult(count);
                    }), Keep.Both)
                    .ToMaterialized(Sink.Ignore<ReadOnlySequence<byte>>(), Keep.Both)
                    .Run(materializer);
                await connected.WaitAsync(deadline.Token);
                await owner.ReadStarted.WaitAsync(deadline.Token);
                await AwaitAssertAsync(() => sender.IoSnapshot.WriteOperations.Should().BeGreaterThan(0), TimeSpan.FromSeconds(3));
                owner.DisposeCount.Should().Be(0, "the unread peer must prevent copying the entire large frame into the bounded pipe");

                if (closePeer)
                    await receiver.ShutdownAsync(deadline.Token);
                else
                    killSwitch.Abort(new IOException("Stop incremental frame copying."));

                var stopped = async () => await writerFinished.WaitAsync(deadline.Token);
                await stopped.Should().ThrowAsync<Exception>();
                await sender.ShutdownAsync(deadline.Token);
                owner.DisposeCount.Should().Be(1);
                var droppedBytes = await dropped.Task.WaitAsync(deadline.Token);
                droppedBytes.Should().BeGreaterThan(0);
                droppedBytes.Should().Be(frame.Length + Preamble(ArteryStreamId.Ordinary).Length - sender.IoSnapshot.WriteBytes,
                    "every admitted byte without successful local write completion is reported once");
                dropReports.Should().Be(1);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        private sealed class TrackingOwner(int length, byte fill) : IMemoryOwner<byte>
        {
            private readonly byte[] _bytes = Enumerable.Repeat(fill, length).ToArray();
            private int _disposeCount;

            public Memory<byte> Memory => _bytes;
            public int DisposeCount => Volatile.Read(ref _disposeCount);

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                Array.Fill(_bytes, (byte)0xEE);
            }
        }

        [Theory(DisplayName = "QUIC output pipe retains memory until a pending native write settles")]
        [InlineData("success")]
        [InlineData("cancellation")]
        [InlineData("deferred-cancellation")]
        [InlineData("failure")]
        public async Task Should_RetainPipeMemory_When_NativeWriteIsPending(string outcome)
        {
            var bytes = new byte[] { 0x13, 0xB1, 0x2C };
            using var pool = new TrackingMemoryPool();
            using var stream = new PendingWriteStream(outcome != "deferred-cancellation");
            using var cancellation = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var writes = 0;
            var output = new ArteryQuicOutputPipe(stream, _ => Interlocked.Increment(ref writes),
                cancellation.Token, CancellationToken.None, pool);
            try
            {
                await output.WriteAsync(new ReadOnlySequence<byte>(bytes));
                await stream.SubmittedReady.Task.WaitAsync(deadline.Token);
                output.Complete();

                output.Completion.IsCompleted.Should().BeFalse();
                pool.ReturnCount.Should().Be(0);
                stream.Submitted.ToArray().Should().Equal(bytes);
                stream.Submitted.Length.Should().Be(bytes.Length, "a rental can be larger than the write");

                switch (outcome)
                {
                    case "success":
                        stream.Completion.SetResult();
                        await output.Completion.WaitAsync(deadline.Token);
                        writes.Should().Be(1);
                        break;
                    case "cancellation":
                        cancellation.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output.Completion.WaitAsync(deadline.Token));
                        writes.Should().Be(0);
                        break;
                    case "deferred-cancellation":
                        cancellation.Cancel();
                        output.Completion.IsCompleted.Should().BeFalse();
                        pool.ReturnCount.Should().Be(0);
                        stream.Submitted.ToArray().Should().Equal(bytes);
                        stream.Completion.SetCanceled(cancellation.Token);
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output.Completion.WaitAsync(deadline.Token));
                        writes.Should().Be(0);
                        break;
                    case "failure":
                        var error = new IOException("write failed");
                        stream.Completion.SetException(error);
                        (await Assert.ThrowsAsync<IOException>(() => output.Completion.WaitAsync(deadline.Token))).Should().BeSameAs(error);
                        writes.Should().Be(0);
                        break;
                }

                pool.ReturnCount.Should().Be(1);
            }
            finally
            {
                cancellation.Cancel();
                stream.Completion.TrySetCanceled();
                output.Complete();
                // Assertions above establish the expected outcome; cleanup also
                // settles a fake write when an earlier assertion failed.
                await output.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        [Fact(DisplayName = "QUIC output pipe sends successive page-edge batches once and in order")]
        public async Task Should_PreserveBatchWriteShape_When_ReusingPipePages()
        {
            using var stream = new RecordingWriteStream();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = new ArteryQuicOutputPipe(stream, size => completed.TrySetResult(size),
                cancellation.Token, CancellationToken.None);
            var sizes = new[] { 1, 8192, 16384, 16383, 2, 10240, 6145 };
            try
            {
                foreach (var size in sizes)
                {
                    var owner = new TrackingOwner(size, (byte)(size % 251));
                    var input = OwnedSequenceSegment.Create(owner, size);
                    await output.WriteAsync(input);
                    input.DisposeOwnedSegments();
                    (await completed.Task.WaitAsync(cancellation.Token)).Should().Be(size);
                    owner.DisposeCount.Should().Be(1);
                    completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                output.Complete();
                await output.Completion.WaitAsync(cancellation.Token);
                stream.Sizes.Should().Equal(sizes);
                stream.ToArray().Should().Equal(sizes.SelectMany(size => Enumerable.Repeat((byte)(size % 251), size)));
            }
            finally
            {
                cancellation.Cancel();
                output.Complete();
                await output.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        private sealed class RecordingWriteStream : MemoryStream
        {
            internal System.Collections.Generic.List<int> Sizes { get; } = new();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Sizes.Add(buffer.Length);
                return base.WriteAsync(buffer, cancellationToken);
            }
        }

        private sealed class PendingWriteStream(bool observeCancellation = true) : MemoryStream
        {
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SubmittedReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public ReadOnlyMemory<byte> Submitted { get; private set; }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Submitted = buffer;
                SubmittedReady.TrySetResult();
                return new ValueTask(observeCancellation ? Completion.Task.WaitAsync(cancellationToken) : Completion.Task);
            }
        }

        private sealed class TrackingMemoryPool : MemoryPool<byte>
        {
            private int _returns;
            private int _rents;
            private int _activeBytes;
            public int PeakActiveBytes { get; private set; }
            public int MaximumRequest { get; private set; }
            public override int MaxBufferSize => int.MaxValue;
            public int ReturnCount => Volatile.Read(ref _returns);
            public int RentCount => Volatile.Read(ref _rents);

            public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
            {
                var size = Math.Max(16384, minBufferSize);
                MaximumRequest = Math.Max(MaximumRequest, minBufferSize);
                Interlocked.Increment(ref _rents);
                PeakActiveBytes = Math.Max(PeakActiveBytes, Interlocked.Add(ref _activeBytes, size));
                return new Owner(this, size);
            }

            protected override void Dispose(bool disposing) { }

            private sealed class Owner(TrackingMemoryPool pool, int size) : IMemoryOwner<byte>
            {
                private readonly byte[] _bytes = new byte[size];
                public Memory<byte> Memory => _bytes;

                public void Dispose()
                {
                    Array.Fill(_bytes, (byte)0xEE);
                    Interlocked.Add(ref pool._activeBytes, -size);
                    Interlocked.Increment(ref pool._returns);
                }
            }
        }

        [Fact(DisplayName = "QUIC pipe flush waits for capacity and drains before completion")]
        public async Task Should_BackpressureAndDrain_When_NativeWriterIsPending()
        {
            using var stream = new ControlledWriteStream();
            using var pool = new TrackingMemoryPool();
            using var stop = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = new ArteryQuicOutputPipe(stream, _ => { }, stop.Token, CancellationToken.None, pool);
            Task? pendingFlush = null;
            try
            {
                await output.WriteAsync(new ReadOnlySequence<byte>(new byte[8192]));
                var first = await stream.NextAsync(deadline.Token);
                var flush = output.WriteAsync(new ReadOnlySequence<byte>(new byte[8192])).AsTask();
                pendingFlush = flush;
                flush.IsCompleted.Should().BeFalse("16 KiB reaches the pause threshold while native writing is held");
                output.Completion.IsCompleted.Should().BeFalse();
                pool.ReturnCount.Should().Be(0);

                first.Completion.SetResult();
                var second = await stream.NextAsync(deadline.Token);
                flush.IsCompleted.Should().BeFalse("8 KiB remaining has not dropped below the resume threshold");
                second.Completion.SetResult();
                (await flush).IsCanceled.Should().BeFalse();
                await output.WriteAsync(ReadOnlySequence<byte>.Empty);
                await output.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));
                var last = await stream.NextAsync(deadline.Token);
                output.Complete();
                output.Completion.IsCompleted.Should().BeFalse("the final native write is still outstanding");
                last.Completion.SetResult();
                await output.Completion.WaitAsync(deadline.Token);
                output.UnsentBytes.Should().Be(0);
                stream.MaximumConcurrency.Should().Be(1);
                pool.ReturnCount.Should().Be(pool.RentCount);
            }
            finally
            {
                stop.Cancel();
                stream.Abort();
                if (pendingFlush is not null)
                    await pendingFlush.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                output.Complete();
                await output.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        [Fact(DisplayName = "QUIC pipe copies oversized segmented frames incrementally without reordering")]
        public async Task Should_BoundStorageAndPreserveOrder_When_CopyingOversizedFrame()
        {
            using var stream = new ControlledWriteStream();
            using var pool = new TrackingMemoryPool();
            using var stop = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var output = new ArteryQuicOutputPipe(stream, _ => { }, stop.Token, CancellationToken.None, pool);
            var first = new TrackingOwner(3, 0x13);
            var tail = new TrackingOwner(7, 0xB1);
            var left = new TrackingOwner(50000, 0x2C);
            var right = new TrackingOwner(50000, 0x2C);
            var largeBytes = ArteryRemoting.AppendFrameToBatch(OwnedSequenceSegment.Create(left, 50000), OwnedSequenceSegment.Create(right, 50000));
            var expected = Enumerable.Repeat((byte)0x13, 3).Concat(Enumerable.Repeat((byte)0x2C, 100000))
                .Concat(Enumerable.Repeat((byte)0xB1, 7)).ToArray();

            async Task ProduceAsync()
            {
                var a = OwnedSequenceSegment.Create(first, 3);
                try
                {
                    await output.WriteAsync(a);
                }
                finally
                {
                    a.DisposeOwnedSegments();
                }
                try
                {
                    await output.WriteAsync(largeBytes);
                }
                finally
                {
                    largeBytes.DisposeOwnedSegments();
                }
                var c = OwnedSequenceSegment.Create(tail, 7);
                try
                {
                    await output.WriteAsync(c);
                }
                finally
                {
                    c.DisposeOwnedSegments();
                }
                output.Complete();
            }

            var producer = ProduceAsync();
            try
            {
                var consumed = 0;
                while (consumed < expected.Length)
                {
                    var pending = await stream.NextAsync(deadline.Token);
                    pending.Bytes.Length.Should().BeInRange(1, 16384);
                    pending.Bytes.ToArray().Should().Equal(expected.Skip(consumed).Take(pending.Bytes.Length));
                    consumed += pending.Bytes.Length;
                    pending.Completion.SetResult();
                }
                await producer.WaitAsync(deadline.Token);
                await output.Completion.WaitAsync(deadline.Token);
                stream.ToArray().Should().Equal(expected);
                stream.MaximumConcurrency.Should().Be(1);
                pool.MaximumRequest.Should().BeLessOrEqualTo(16384);
                pool.PeakActiveBytes.Should().BeLessOrEqualTo(3 * 16384, "bounded chunk overshoot and physical page slack are included");
                pool.ReturnCount.Should().Be(pool.RentCount);
                output.UnsentBytes.Should().Be(0);
                first.DisposeCount.Should().Be(1);
                left.DisposeCount.Should().Be(1);
                right.DisposeCount.Should().Be(1);
                tail.DisposeCount.Should().Be(1);
            }
            finally
            {
                stop.Cancel();
                stream.Abort();
                await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                output.Complete();
                await output.Completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        private sealed class ControlledWriteStream : MemoryStream
        {
            internal sealed record Pending(ReadOnlyMemory<byte> Bytes)
            {
                internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            private readonly Channel<Pending> _calls = Channel.CreateUnbounded<Pending>();
            private Pending? _current;
            private int _active;
            private int _aborted;
            internal int MaximumConcurrency { get; private set; }

            internal ValueTask<Pending> NextAsync(CancellationToken cancellationToken) => _calls.Reader.ReadAsync(cancellationToken);
            internal void Abort()
            {
                Volatile.Write(ref _aborted, 1);
                Volatile.Read(ref _current)?.Completion.TrySetCanceled();
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var current = new Pending(buffer);
                Volatile.Write(ref _current, current);
                if (Volatile.Read(ref _aborted) != 0)
                    current.Completion.TrySetCanceled();
                MaximumConcurrency = Math.Max(MaximumConcurrency, Interlocked.Increment(ref _active));
                _calls.Writer.TryWrite(current);
                try
                {
                    // Deliberately ignore cancellation to exercise storage lifetime.
                    await current.Completion.Task;
                    base.Write(buffer.Span);
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }
        }

        [Fact(DisplayName = "QUIC receive chunks retain their bytes across subsequent reads and FIN")]
        public async Task Should_RetainDistinctChunks_When_NativeReaderReusesScratchBuffer()
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, enableIoStatistics: true);
            var materializer = ActorMaterializer.Create(Sys);
            var received = new TaskCompletionSource<Task<IImmutableList<ReadOnlySequence<byte>>>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    // Keep every chunk alive until EOF, rather than consuming/copying each on arrival.
                    received.TrySetResult(source.RunWith(Sink.Seq<ReadOnlySequence<byte>>(), materializer));
                    admitted();
                });
                var channel = await sender.OpenAsync(endpoint, ArteryStreamId.Ordinary, deadline.Token);
                var preamble = Preamble(ArteryStreamId.Ordinary);
                var payload = Enumerable.Range(0, 3 * 16 * 1024 + 29)
                    .Select(index => (byte)((index * 31 + index / 251) % 256)).ToArray();
                await channel.Stream.WriteAsync(preamble, deadline.Token);
                await channel.Stream.WriteAsync(payload, deadline.Token);
                channel.Stream.CompleteWrites();
                await channel.Stream.WritesClosed.WaitAsync(deadline.Token);
                var chunks = await (await received.Task.WaitAsync(deadline.Token)).WaitAsync(deadline.Token);
                chunks.Count.Should().BeGreaterOrEqualTo(5, "the payload exceeds three native read buffers, plus its preamble");
                chunks.SelectMany(chunk => chunk.ToArray()).Should().Equal(preamble.Concat(payload));
                var io = receiver.IoSnapshot;
                io.ReadBytes.Should().Be(payload.Length);
                io.ReadOperations.Should().Be(chunks.Count - 1);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "QUIC retires every sibling channel and reconnects when an incoming data stream is reset")]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public async Task Should_CloseSiblingChannels_When_DataStreamResets(int streams)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var policy = TrustCertificate(certificate);
            var tls = ArteryTlsSettings.Mutual(certificate, policy);
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, ordinaryStreams: streams);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    // This test isolates native channel retirement. Protocol admission itself is
                    // covered by real actors; here the consumer acknowledges admission explicitly.
                    source.RunWith(Sink.Ignore<ReadOnlySequence<byte>>(), materializer);
                    admitted();
                });
                var control = await sender.OpenAsync(endpoint, ArteryStreamId.Control, deadline.Token);
                await control.Stream.WriteAsync(Preamble(ArteryStreamId.Control), deadline.Token);
                var ordinary = new ArteryQuicTransport.Channel[streams];
                for (var i = 0; i < streams; i++)
                {
                    ordinary[i] = await sender.OpenAsync(endpoint, ArteryStreamId.Ordinary, deadline.Token, i);
                    await ordinary[i].Stream.WriteAsync(Preamble(ArteryStreamId.Ordinary), deadline.Token);
                }
                ordinary[^1].Stream.Abort(QuicAbortDirection.Write, 42);

                foreach (var sibling in ordinary.Take(streams - 1).Prepend(control))
                {
                    var closed = async () => await sibling.Stream.WritesClosed.WaitAsync(deadline.Token);
                    await closed.Should().ThrowAsync<QuicException>();
                }
                control.Retire();
                var replacement = await sender.OpenAsync(endpoint, ArteryStreamId.Control, deadline.Token);
                await replacement.Stream.WriteAsync(Preamble(ArteryStreamId.Control), deadline.Token);
                replacement.Stream.Should().NotBeSameAs(control.Stream);
                for (var i = 0; i < streams; i++)
                {
                    var channel = await sender.OpenAsync(endpoint, ArteryStreamId.Ordinary, deadline.Token, i);
                    await channel.Stream.WriteAsync(Preamble(ArteryStreamId.Ordinary), deadline.Token);
                }
                sender.OrdinaryStreamCounts().Values.Should().ContainSingle().Which.Should().Be(streams);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Theory(DisplayName = "QUIC rejects duplicate control and excess ordinary channels")]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(2)]
        public async Task Should_RejectExtraRole_When_AdmissionLimitIsExceeded(int roleValue)
        {
            var role = (ArteryStreamId)roleValue;
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var policy = TrustCertificate(certificate);
            var tls = ArteryTlsSettings.Mutual(certificate, policy);
            var log = Logging.GetLogger(Sys, GetType());
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, maximumInboundOrdinaryStreams: 1);
            var materializer = ActorMaterializer.Create(Sys);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    source.RunWith(Sink.Ignore<ReadOnlySequence<byte>>(), materializer);
                    admitted();
                });
                var authentication = TlsClientSettings.Mutual(certificate, policy)
                    .CreateAuthenticationOptions("127.0.0.1", "127.0.0.1", log);
                authentication.ApplicationProtocols = new() { ArteryQuicTransport.ApplicationProtocol };
                await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = endpoint,
                    ClientAuthenticationOptions = authentication,
                    DefaultCloseErrorCode = 1,
                    DefaultStreamErrorCode = 1
                }, deadline.Token);
                await using var first = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                await first.WriteAsync(Preamble(role), deadline.Token);
                await using var extra = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                await extra.WriteAsync(Preamble(role), deadline.Token);
                var closed = async () => await first.WritesClosed.WaitAsync(deadline.Token);
                await closed.Should().ThrowAsync<QuicException>();
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Fact(DisplayName = "QUIC control and a healthy ordinary stream progress while another data reader is blocked")]
        public async Task Should_ProgressOtherStreams_When_OneOrdinaryReaderIsBlocked()
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log, ordinaryStreams: 2);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var blockedAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var healthyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var controlReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accepted = 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task? blockedWrite = null;
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, admitted) =>
                {
                    var index = Interlocked.Increment(ref accepted);
                    if (index == 1)
                    {
                        // Do not materialize this reader: the native receive window must fill.
                        blockedAccepted.TrySetResult();
                    }
                    else
                    {
                        source.RunWith(Sink.ForEach<ReadOnlySequence<byte>>(bytes =>
                        {
                            var data = bytes.ToArray();
                            if (data.Contains((byte)127)) healthyReceived.TrySetResult();
                            if (data.Contains((byte)126)) controlReceived.TrySetResult();
                        }), materializer);
                    }
                    admitted();
                });
                var blocked = await sender.OpenAsync(endpoint, ArteryStreamId.Ordinary, deadline.Token, 0);
                await blocked.Stream.WriteAsync(Preamble(ArteryStreamId.Ordinary), deadline.Token);
                await blockedAccepted.Task.WaitAsync(deadline.Token);
                blockedWrite = FillBlockedStreamAsync();
                async Task FillBlockedStreamAsync()
                {
                    await blocked.Stream.WriteAsync(new byte[32 * 1024 * 1024], deadline.Token);
                    // Native sends can buffer and complete WriteAsync before delivery. FIN's
                    // completion proves that flow-controlled bytes actually reached the peer.
                    blocked.Stream.CompleteWrites();
                    await blocked.Stream.WritesClosed.WaitAsync(deadline.Token);
                }
                var healthy = await sender.OpenAsync(endpoint, ArteryStreamId.Ordinary, deadline.Token, 1);
                await healthy.Stream.WriteAsync(Preamble(ArteryStreamId.Ordinary), deadline.Token);
                await healthy.Stream.WriteAsync(new byte[] { 127 }, deadline.Token);
                var control = await sender.OpenAsync(endpoint, ArteryStreamId.Control, deadline.Token);
                await control.Stream.WriteAsync(Preamble(ArteryStreamId.Control), deadline.Token);
                await control.Stream.WriteAsync(new byte[] { 126 }, deadline.Token);
                await Task.WhenAll(healthyReceived.Task, controlReceived.Task).WaitAsync(deadline.Token);
                blockedWrite.IsCompleted.Should().BeFalse($"the stalled reader must still exert native flow control (status {blockedWrite.Status}, error {blockedWrite.Exception})");
                blocked.Retire();
                var aborted = async () => await blockedWrite.WaitAsync(deadline.Token);
                (await aborted.Should().ThrowAsync<Exception>()).Where(ex => ex is QuicException || ex is OperationCanceledException);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                if (blockedWrite is not null && !blockedWrite.IsCompletedSuccessfully)
                {
                    var settled = async () => await blockedWrite;
                    await settled.Should().ThrowAsync<Exception>();
                }
                materializer.Shutdown();
            }
        }

        [Fact(DisplayName = "QUIC closes a peer that sends a preamble but never completes the Artery handshake")]
        public async Task Should_EnforceAdmissionDeadline_When_PeerSendsOnlyPreamble()
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromMilliseconds(500), log);
            var materializer = ActorMaterializer.Create(Sys);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, _) =>
                    source.RunWith(Sink.Ignore<ReadOnlySequence<byte>>(), materializer));
                var channel = await sender.OpenAsync(endpoint, ArteryStreamId.Control, deadline.Token);
                await channel.Stream.WriteAsync(Preamble(ArteryStreamId.Control), deadline.Token);

                var closed = async () => await channel.Stream.WritesClosed.WaitAsync(deadline.Token);
                await closed.Should().ThrowAsync<QuicException>();
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }

        [Fact(DisplayName = "QUIC cancellation interrupts a dial to an unresponsive UDP endpoint")]
        public async Task Should_CancelPendingDial_When_CallerCancels()
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var tls = ArteryTlsSettings.Mutual(certificate, TrustCertificate(certificate));
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), Logging.GetLogger(Sys, GetType()));
            using var blackhole = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            blackhole.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var dial = async () => await sender.OpenAsync(blackhole.LocalEndPoint!, ArteryStreamId.Control, deadline.Token);
                await dial.Should().ThrowAsync<OperationCanceledException>();
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
            }
        }

        private static byte[] Preamble(ArteryStreamId role)
        {
            var bytes = new byte[ArteryConnectionHeader.Length];
            ArteryConnectionHeader.WriteTo(bytes, role);
            return bytes;
        }

        [Theory(DisplayName = "QUIC rejects incompatible protocol bytes and continues accepting valid peers")]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Should_RejectIncompatibleProtocol_When_PeerUsesWrongAlpnOrPreamble(bool wrongAlpn)
        {
            RequireTransportSupport("quic");
            using var certificate = CreateCertificate();
            var policy = TrustCertificate(certificate);
            var tls = ArteryTlsSettings.Mutual(certificate, policy);
            var log = Logging.GetLogger(Sys, GetType());
            var sender = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var receiver = new ArteryQuicTransport(tls, TimeSpan.FromSeconds(10), log);
            var materializer = ActorMaterializer.Create(Sys);
            var admitted = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var endpoint = await receiver.BindAsync("127.0.0.1", 0, deadline.Token);
                receiver.StartAccepting((source, _, accepted) =>
                {
                    source.RunWith(Sink.Ignore<ReadOnlySequence<byte>>(), materializer);
                    accepted();
                    admitted.TrySetResult(Done.Instance);
                });
                var authentication = TlsClientSettings.Mutual(certificate, policy)
                    .CreateAuthenticationOptions("127.0.0.1", "127.0.0.1", log);
                authentication.ApplicationProtocols = new()
                {
                    wrongAlpn ? new SslApplicationProtocol("incompatible") : ArteryQuicTransport.ApplicationProtocol
                };
                var options = new QuicClientConnectionOptions
                {
                    RemoteEndPoint = endpoint,
                    ClientAuthenticationOptions = authentication,
                    DefaultCloseErrorCode = 1,
                    DefaultStreamErrorCode = 1
                };
                if (wrongAlpn)
                {
                    var connect = async () => await QuicConnection.ConnectAsync(options, deadline.Token);
                    var failure = await connect.Should().ThrowAsync<Exception>();
                    failure.Where(ex => ex is AuthenticationException || ex is QuicException);
                }
                else
                {
                    await using var connection = await QuicConnection.ConnectAsync(options, deadline.Token);
                    await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                    await stream.WriteAsync(new byte[] { 0, 0, 0, 0, 1 }, deadline.Token);
                    var rejected = async () => await stream.WritesClosed.WaitAsync(deadline.Token);
                    await rejected.Should().ThrowAsync<QuicException>();
                }

                admitted.Task.IsCompleted.Should().BeFalse("incompatible protocol data must not be admitted");
                var valid = await sender.OpenAsync(endpoint, ArteryStreamId.Control, deadline.Token);
                await valid.Stream.WriteAsync(Preamble(ArteryStreamId.Control), deadline.Token);
                await admitted.Task.WaitAsync(deadline.Token);
            }
            finally
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sender.ShutdownAsync(shutdown.Token);
                await receiver.ShutdownAsync(shutdown.Token);
                materializer.Shutdown();
            }
        }
    }
}
