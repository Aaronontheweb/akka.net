//-----------------------------------------------------------------------
// <copyright file="TcpWritePathCopyBenchmarks.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Akka.Benchmarks.Configurations;
using Akka.IO;
using BenchmarkDotNet.Attributes;

namespace Akka.Benchmarks.IO
{
    /// <summary>
    /// <b>Headline benchmark for the TCP write-path defensive-copy investigation.</b>
    /// Compares the per-message HOT-PATH cost of TWO ways to move an already-owned buffer of
    /// bytes toward the socket:
    /// <list type="bullet">
    /// <item><description>
    /// <b>(a) TODAY:</b> <see cref="TcpTransportConnection.WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>
    /// -- the REAL production type, unmodified -- calls <c>PipeWriter.Write(span)</c> (a memcpy
    /// into the output <see cref="Pipe"/>'s internal pooled segment) followed by
    /// <c>FlushAsync()</c>. This runs for EVERY <c>Tcp.Write</c> on EVERY connection today (see
    /// <c>TcpConnection.EnqueueWrite</c> -&gt; <c>TcpTransportConnection.WriteAsync</c>, and
    /// <c>RunWritePumpAsync</c>, which drains the pipe to the actual <see cref="Stream"/>/socket).
    /// </description></item>
    /// <item><description>
    /// <b>(b) ZERO-COPY ALTERNATIVE:</b> what an ownership-transfer redesign would do instead --
    /// the caller's already-owned buffer (here, a freshly-rented <see cref="IMemoryOwner{T}"/>
    /// standing in for whatever upstream step -- encode, pre-registration copy, etc. -- already
    /// produced and filled it) is hard-passed BY REFERENCE into a bounded
    /// <see cref="Channel{T}"/>, which plays the exact same architectural role the output
    /// <see cref="Pipe"/> plays today (bounded buffering + async backpressure). A single
    /// background drain loop -- the same role <c>RunWritePumpAsync</c> plays today -- dequeues,
    /// writes the memory directly to the stream, and disposes the owner. No memcpy anywhere on
    /// this path.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both arms write to <see cref="Stream.Null"/> (a real, unmodified <see cref="Stream"/> that
    /// discards writes and reports immediate EOF on read) so neither arm pays for real socket
    /// syscalls/kernel-buffer latency -- the benchmark isolates the PIPE-LAYER bookkeeping and
    /// memcpy cost, which is the thing an ownership-transfer redesign would eliminate. Arm (a)
    /// constructs a REAL <see cref="TcpTransportConnection"/> against a real (but never
    /// I/O-touched -- Stream.Null handles all reads/writes) <see cref="Socket"/>, so this is not a
    /// re-implementation of the write loop: it is the production code, unmodified, exercised in
    /// isolation.
    /// </para>
    /// <para>
    /// The bounded channel capacity (32 items) in arm (b) is a fixed, arbitrary queue depth -- it
    /// is NOT an attempt to reproduce the output pipe's byte-precise
    /// <c>PauseWriterThreshold</c>/<c>ResumeWriterThreshold</c> backpressure timing (65536/32768
    /// bytes by default). The point of this benchmark is the presence/absence of the memcpy, not
    /// exact backpressure-trigger parity between the two designs.
    /// </para>
    /// <para>
    /// Arm (b) rents its buffer from <see cref="SimpleBufferPool"/>, a minimal
    /// <c>ConcurrentQueue&lt;byte[]&gt;</c>-backed <see cref="IMemoryOwner{T}"/> pool -- NOT
    /// <see cref="MemoryPool{T}.Shared"/>. An earlier version of this benchmark used
    /// <c>MemoryPool&lt;byte&gt;.Shared</c> (which is itself backed by <c>ArrayPool&lt;byte&gt;.Shared</c>)
    /// and measured a full, payload-size-scaling allocation on EVERY call -- i.e. a ~100% pool-miss
    /// rate -- under BenchmarkDotNet's heavily-async, many-worker-thread execution model (rent on
    /// one pool-partition/thread, return on another, repeated across a long-running, thread-churning
    /// benchmark). That is a real, reproducible property of <c>ArrayPool&lt;byte&gt;.Shared</c>'s
    /// per-thread/per-core partitioning under this specific access pattern, but it is a red herring
    /// for THIS benchmark's question (does the handoff mechanism itself allocate?), so a simple,
    /// unpartitioned, obviously-correct pool is used instead to isolate that question cleanly.
    /// </para>
    /// </summary>
    [Config(typeof(MicroBenchmarkConfig))]
    public class TcpWritePathCopyBenchmarks
    {
        private const int HandoffChannelCapacity = 32;

        [Params(256, 4096, 65536)]
        public int PayloadBytes { get; set; }

        private byte[] _sourceBuffer = null!;
        private ReadOnlyMemory<byte> _sourceMemory;

        // --- (a) today's real WriteAsync-through-Pipe path ---
        private TcpTransportConnection _transport = null!;

        // --- (b) zero-copy handoff path ---
        private SimpleBufferPool _bufferPool = null!;
        private Channel<(IMemoryOwner<byte> Owner, int Length)> _handoffChannel = null!;
        private Task _handoffDrainTask = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _sourceBuffer = new byte[PayloadBytes];
            new Random(42).NextBytes(_sourceBuffer);
            _sourceMemory = _sourceBuffer;

            // The Socket is never used for actual I/O (Stream.Null handles all reads/writes) --
            // it exists only because the real TcpTransportConnection constructor requires one,
            // and DisposeAsync() needs something to Dispose() during GlobalCleanup. No listener,
            // no connect, no syscalls beyond the socket handle allocation itself.
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _transport = new TcpTransportConnection(socket, Stream.Null);

            _bufferPool = new SimpleBufferPool(PayloadBytes);
            // Pre-seed the pool so warmup (uncounted) -- not the measured iterations -- pays for
            // the buffers' initial allocation.
            var seed = new IMemoryOwner<byte>[HandoffChannelCapacity * 2];
            for (var i = 0; i < seed.Length; i++)
                seed[i] = _bufferPool.Rent();
            foreach (var owner in seed)
                owner.Dispose();
        }

        [GlobalCleanup]
        public async Task GlobalCleanup()
        {
            await _transport.DisposeAsync();
        }

        [IterationSetup(Target = nameof(Handoff_ChannelDirectWrite))]
        public void SetupHandoff()
        {
            _handoffChannel = Channel.CreateBounded<(IMemoryOwner<byte>, int)>(
                new BoundedChannelOptions(HandoffChannelCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true
                });
            _handoffDrainTask = DrainHandoffAsync(_handoffChannel.Reader);
        }

        // NOTE: BenchmarkDotNet's source-generated glue binds [IterationSetup]/[IterationCleanup]
        // (even Target-qualified ones) to a plain `Action`, unlike [GlobalSetup]/[GlobalCleanup]
        // which do support async Task methods elsewhere in this project -- an async Task method
        // here fails to compile against the generated harness (CS0407). This blocking wait runs
        // only at iteration boundaries, never in the timed path, matching the existing
        // `Task.WhenAll(...).Wait()` convention already used for the same purpose in
        // TcpOperationsBenchmarks.IterationCleanup.
        [IterationCleanup(Target = nameof(Handoff_ChannelDirectWrite))]
        public void CleanupHandoff()
        {
            _handoffChannel.Writer.Complete();
            _handoffDrainTask.GetAwaiter().GetResult();
        }

        private static async Task DrainHandoffAsync(ChannelReader<(IMemoryOwner<byte> Owner, int Length)> reader)
        {
            await foreach (var (owner, length) in reader.ReadAllAsync())
            {
                await Stream.Null.WriteAsync(owner.Memory.Slice(0, length));
                owner.Dispose();
            }
        }

        /// <summary>
        /// (a) TODAY: the real, unmodified <see cref="TcpTransportConnection.WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>
        /// -- <c>PipeWriter.Write</c> (memcpy into the pipe's pooled segment) + <c>FlushAsync</c>.
        /// This is the exact per-message hot-path code that runs for every write on every
        /// connection today.
        /// </summary>
        [Benchmark(Baseline = true)]
        public async ValueTask<FlushResult> Copy_PipeWriteAsync()
        {
            return await _transport.WriteAsync(_sourceMemory);
        }

        /// <summary>
        /// (b) ZERO-COPY ALTERNATIVE: hand the already-owned buffer forward BY REFERENCE through a
        /// bounded queue instead of copying its bytes. <see cref="SimpleBufferPool.Rent"/> stands
        /// in for whatever upstream step already allocated/filled the buffer (its content is
        /// irrelevant here -- only the "already exists, already owned, now transferred" shape
        /// matters). The background drain loop performs the actual <c>Stream.WriteAsync</c> and
        /// disposes (returns) the owner once the write completes -- exactly what an
        /// ownership-transfer redesign of <c>RunWritePumpAsync</c> would do.
        /// </summary>
        [Benchmark]
        public async ValueTask Handoff_ChannelDirectWrite()
        {
            var owner = _bufferPool.Rent();
            await _handoffChannel.Writer.WriteAsync((owner, PayloadBytes));
        }

        /// <summary>
        /// Minimal, obviously-correct pooled <see cref="IMemoryOwner{T}"/> backed by a
        /// <see cref="ConcurrentQueue{T}"/> of same-sized arrays -- see the class-level remarks
        /// on why <see cref="MemoryPool{T}.Shared"/> isn't used here.
        /// </summary>
        private sealed class SimpleBufferPool
        {
            private readonly ConcurrentQueue<byte[]> _pool = new();
            private readonly int _bufferSize;

            public SimpleBufferPool(int bufferSize) => _bufferSize = bufferSize;

            public IMemoryOwner<byte> Rent()
            {
                if (!_pool.TryDequeue(out var array))
                    array = new byte[_bufferSize];
                return new PooledOwner(array, this);
            }

            private void Return(byte[] array) => _pool.Enqueue(array);

            private sealed class PooledOwner : IMemoryOwner<byte>
            {
                private byte[]? _array;
                private readonly SimpleBufferPool _pool;

                public PooledOwner(byte[] array, SimpleBufferPool pool)
                {
                    _array = array;
                    _pool = pool;
                }

                public Memory<byte> Memory => _array ?? throw new ObjectDisposedException(nameof(PooledOwner));

                public void Dispose()
                {
                    var array = _array;
                    _array = null;
                    if (array is not null)
                        _pool.Return(array);
                }
            }
        }
    }
}
