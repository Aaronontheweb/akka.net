// -----------------------------------------------------------------------
// <copyright file="ArteryInboundLaneConsumerSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.Remote.Artery;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.TestKit;
using Xunit;

namespace Akka.Remote.Tests.Artery;

/// <summary>
/// The inbound lane consumer loops must not swallow fatal exceptions, and a loop that ends while the connection
/// is still running must fail the connection instead of leaving the reader routing to a lane nobody drains.
/// <see cref="OutOfMemoryException"/> is what <c>TlsCertificateSource.IsFatal</c> treats as fatal that can be
/// constructed and thrown safely from a test.
/// </summary>
public sealed class ArteryInboundLaneConsumerSpec : AkkaSpec
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    public ArteryInboundLaneConsumerSpec(ITestOutputHelper output) : base(output) { }

    private static byte[] Preamble() => new byte[] { (byte)'A', (byte)'K', (byte)'K', (byte)'A', (byte)ArteryStreamId.Ordinary };

    private ArteryInboundProcessingStage CreateStage(int lanes, int laneBuffer, Action<object> deliver)
    {
        var context = new AssociationRegistryInboundContext(new AssociationRegistry(),
            new UniqueAddress(new Address("akka", "local", "127.0.0.1", 1234), 1), (_, _) => { });
        context.CompleteHandshake(new UniqueAddress(new Address("akka", "peer", "127.0.0.1", 4321), 42));
        return new ArteryInboundProcessingStage(65536, 65536, Sys.Serialization, Sys.Scheduler,
            inboundLanes: lanes, inboundLaneBufferSize: laneBuffer, inboundContext: context,
            dispatchOrdinary: (payload, _, _, _) => deliver(payload));
    }

    /// <summary>The wire bytes of one ordinary frame per payload, all to the same recipient (so the same lane).</summary>
    private byte[] Frames(params string[] payloads) => payloads.SelectMany(payload =>
    {
        using var encoded = ArteryEnvelopeCodec.Encode(Sys.Serialization, 42, null, "akka://local/user/target", payload);
        return encoded.WrittenSpan.ToArray();
    }).ToArray();

    private static (ISourceQueueWithComplete<ReadOnlySequence<byte>> Queue, Task Completion) Run(
        ArteryInboundProcessingStage stage, ActorMaterializer materializer)
    {
        var (queue, completion) = Source.Queue<ReadOnlySequence<byte>>(1, OverflowStrategy.Backpressure)
            .Via(stage)
            .ToMaterialized(Sink.Ignore<IInboundEnvelope>(), Keep.Both)
            .Run(materializer);
        return (queue, completion);
    }

    [Fact(DisplayName = "Should_FailConnectionWithFatalCause_When_LaneDispatchThrowsFatalException")]
    public async Task Should_FailConnectionWithFatalCause_When_LaneDispatchThrowsFatalException()
    {
        var fatal = new OutOfMemoryException("simulated");
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            var (queue, completion) = Run(CreateStage(2, 4, payload =>
            {
                if ("fatal".Equals(payload))
                    throw fatal;
            }), materializer);

            await queue.OfferAsync(new ReadOnlySequence<byte>(Preamble().Concat(Frames("fatal")).ToArray()));

            var failure = await Assert.ThrowsAsync<ArteryLaneConsumerException>(() => completion.WaitAsync(Bound));
            Assert.Same(fatal, failure.InnerException);
        }
        finally
        {
            materializer.Shutdown();
        }
    }

    [Fact(DisplayName = "Should_KeepConsumingAndKeepConnection_When_LaneDispatchThrowsNonFatalException")]
    public async Task Should_KeepConsumingAndKeepConnection_When_LaneDispatchThrowsNonFatalException()
    {
        var delivered = new ConcurrentQueue<object>();
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            var (queue, completion) = Run(CreateStage(2, 4, payload =>
            {
                if ("bad".Equals(payload))
                    throw new InvalidOperationException("non-fatal");
                delivered.Enqueue(payload);
            }), materializer);

            await queue.OfferAsync(new ReadOnlySequence<byte>(Preamble().Concat(Frames("bad", "good")).ToArray()));
            await AwaitAssertAsync(() => Assert.Equal(new object[] { "good" }, delivered.ToArray()), Bound);
            Assert.False(completion.IsCompleted);

            queue.Complete();
            await completion.WaitAsync(Bound);
        }
        finally
        {
            materializer.Shutdown();
        }
    }

    [Fact(DisplayName = "Should_FailConnectionPromptly_When_LaneConsumerEndsWhileWriterIsParkedOnFullLane")]
    public async Task Should_FailConnectionPromptly_When_LaneConsumerEndsWhileWriterIsParkedOnFullLane()
    {
        var entered = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var fatal = new OutOfMemoryException("simulated");
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            // Lane capacity 2, one message held inside dispatch: eight frames to one lane leave the stage's
            // write parked on the full channel, with frames still unread behind it.
            var (queue, completion) = Run(CreateStage(2, 2, _ =>
            {
                entered.TrySetResult(Done.Instance);
                release.Wait(TimeSpan.FromSeconds(30));
                throw fatal;
            }), materializer);

            await queue.OfferAsync(new ReadOnlySequence<byte>(
                Preamble().Concat(Frames(Enumerable.Range(0, 8).Select(i => $"m-{i}").ToArray())).ToArray()));
            await entered.Task.WaitAsync(Bound);
            Assert.False(completion.IsCompleted);

            release.Set();

            var failure = await Assert.ThrowsAsync<ArteryLaneConsumerException>(() => completion.WaitAsync(Bound));
            Assert.Same(fatal, failure.InnerException);
        }
        finally
        {
            release.Set();
            materializer.Shutdown();
        }
    }

    [Fact(DisplayName = "Should_FailConnection_When_LaneConsumerEndsAfterUpstreamFinishedWithWriteParked")]
    public async Task Should_FailConnection_When_LaneConsumerEndsAfterUpstreamFinishedWithWriteParked()
    {
        var entered = new TaskCompletionSource<Done>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim();
        var fatal = new OutOfMemoryException("simulated");
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            // Lane capacity 1: one frame inside dispatch, one in the channel, the third parked. The input is a
            // single chunk, so upstream has finished and the stage can only complete once the parked write
            // resolves. When the consumer then dies, the woken write must not complete the stage normally.
            var completion = Source.Single(new ReadOnlySequence<byte>(Preamble().Concat(Frames("a", "b", "c")).ToArray()))
                .Via(CreateStage(2, 1, _ =>
                {
                    entered.TrySetResult(Done.Instance);
                    release.Wait(TimeSpan.FromSeconds(30));
                    throw fatal;
                }))
                .RunWith(Sink.Ignore<IInboundEnvelope>(), materializer);

            await entered.Task.WaitAsync(Bound);
            await Task.Delay(300); // the stage reaches its parked state within microseconds; this is only settling time
            Assert.False(completion.IsCompleted);

            release.Set();

            var failure = await Assert.ThrowsAsync<ArteryLaneConsumerException>(() => completion.WaitAsync(Bound));
            Assert.Same(fatal, failure.InnerException);
        }
        finally
        {
            release.Set();
            materializer.Shutdown();
        }
    }

    [Fact(DisplayName = "Should_NotFailConnection_When_UpstreamCompletesNormallyWithLanes")]
    public async Task Should_NotFailConnection_When_UpstreamCompletesNormallyWithLanes()
    {
        var delivered = new ConcurrentQueue<object>();
        var errors = CreateTestProbe();
        Sys.EventStream.Subscribe(errors, typeof(Error));
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            var (queue, completion) = Run(CreateStage(2, 4, delivered.Enqueue), materializer);

            await queue.OfferAsync(new ReadOnlySequence<byte>(Preamble().Concat(Frames("a", "b", "c")).ToArray()));
            await AwaitAssertAsync(() => Assert.Equal(3, delivered.Count), Bound);

            queue.Complete();
            await completion.WaitAsync(Bound);
            await errors.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            materializer.Shutdown();
        }
    }

    [Fact(DisplayName = "Should_NotFailConnection_When_DownstreamCancelsWithLanes")]
    public async Task Should_NotFailConnection_When_DownstreamCancelsWithLanes()
    {
        var errors = CreateTestProbe();
        Sys.EventStream.Subscribe(errors, typeof(Error));
        var materializer = ActorMaterializer.Create(Sys);
        try
        {
            var ((queue, killSwitch), completion) = Source.Queue<ReadOnlySequence<byte>>(1, OverflowStrategy.Backpressure)
                .Via(CreateStage(2, 4, _ => { }))
                .ViaMaterialized(KillSwitches.Single<IInboundEnvelope>(), Keep.Both)
                .ToMaterialized(Sink.Ignore<IInboundEnvelope>(), Keep.Both).Run(materializer);

            await queue.OfferAsync(new ReadOnlySequence<byte>(Preamble().Concat(Frames("a")).ToArray()));
            killSwitch.Shutdown();
            await completion.WaitAsync(Bound);
            await errors.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            materializer.Shutdown();
        }
    }
}
