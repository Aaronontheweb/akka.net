//-----------------------------------------------------------------------
// <copyright file="ArteryFlowDiagnostics.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Akka.Actor;
using Akka.Event;

namespace Akka.Remote.Artery
{
    /// <summary>
    /// Switchable flow counters for the ordinary Artery stream, enabled with
    /// <c>akka.remote.artery.advanced.flow-diagnostics = on</c>. When disabled no instance exists and
    /// every call site is a single null check. When enabled, a scheduler tick calls
    /// <see cref="EmitSnapshot"/>, which logs one <c>ARTERY-FLOW</c> info line per direction (outbound:
    /// one per remote association) and starts a new accumulation window.
    ///
    /// <para>
    /// <b>Threading.</b> Counters written by a single thread (a stage's own logic, a lane consumer
    /// task) are plain fields in a <see cref="FlowCounterBlock"/>; the snapshot thread reads them and
    /// turns them into per-window deltas against its own "last seen" copy, so the writer is never
    /// reset from outside. Counters written by many threads (outbound enqueue) use
    /// <see cref="Interlocked"/>. Snapshots are NOT atomic across counters: a line may be slightly
    /// torn (for example a frame counted in <c>out.frames</c> but not yet in its histogram), and a
    /// high-water mark reset can lose a concurrent update. Totals are never lost across windows.
    /// </para>
    /// </summary>
    internal sealed class ArteryFlowDiagnostics
    {
        // ---- bucket labels (the index order is the bucket order) ----
        internal static readonly string[] FramesPerWriteLabels = { "1", "2_3", "4_7", "8_15", "16_31", "32_63", "64plus" };
        internal static readonly string[] BytesLabels = { "lt256", "lt1k", "lt4k", "lt16k", "16kplus" };
        internal static readonly string[] FramesPerChunkLabels = { "0", "1", "2_3", "4_7", "8_15", "16plus" };
        internal static readonly string[] ItemsPerWakeLabels = { "1", "2_3", "4_7", "8_15", "16plus" };

        // ---- outbound write block layout (FlowCounterBlock, written by the write-batching stage / write tap) ----
        internal const int W_Writes = 0;
        internal const int W_Frames = 1;
        internal const int W_Bytes = 2;
        internal const int W_FramesHist = 3;                         // 7 buckets
        internal const int W_BytesHist = W_FramesHist + 7;           // 5 buckets
        internal const int W_WaitDemand = W_BytesHist + 5;
        internal const int W_PartialPush = W_WaitDemand + 1;
        internal const int W_Size = W_PartialPush + 1;

        // ---- inbound stage block layout (one per accepted ordinary connection) ----
        internal const int I_Chunks = 0;
        internal const int I_ChunkBytes = 1;
        internal const int I_Frames = 2;
        internal const int I_Msgs = 3;
        internal const int I_ChunkBytesHist = 4;                     // 5 buckets
        internal const int I_FramesHist = I_ChunkBytesHist + 5;      // 6 buckets
        internal const int I_LaneMsgs = I_FramesHist + 6;            // [lanes]
        // then [lanes] depth high-water marks (max slots)

        // ---- inbound lane consumer block layout (one per lane per accepted ordinary connection) ----
        internal const int C_Items = 0;
        internal const int C_Wakes = 1;
        internal const int C_Suspends = 2;
        internal const int C_WakeHist = 3;                           // 5 buckets
        internal const int C_Size = C_WakeHist + 5;

        private readonly ILoggingAdapter _log;
        private readonly int _outboundLanes;
        private readonly int _inboundLanes;
        private readonly ConcurrentDictionary<Address, ArteryOutboundFlow> _outbound = new();
        private readonly object _inboundGate = new();
        private readonly List<FlowCounterBlock> _inboundBlocks = new();
        private readonly List<FlowCounterBlock> _consumerBlocks = new();
        private readonly Stopwatch _window = Stopwatch.StartNew();

        public ArteryFlowDiagnostics(ILoggingAdapter log, int outboundLanes, int inboundLanes)
        {
            _log = log;
            _outboundLanes = outboundLanes;
            _inboundLanes = inboundLanes;
        }

        public ArteryOutboundFlow OutboundFor(Address remote) =>
            _outbound.GetOrAdd(remote, static (addr, lanes) => new ArteryOutboundFlow(addr, lanes), _outboundLanes);

        /// <summary>Registers the counters of one accepted ordinary inbound connection.</summary>
        public FlowCounterBlock RegisterInbound()
        {
            var block = new FlowCounterBlock(I_LaneMsgs + 2 * _inboundLanes, I_LaneMsgs + _inboundLanes, lane: -1);
            lock (_inboundGate)
                _inboundBlocks.Add(block);
            return block;
        }

        /// <summary>Registers the counters of one inbound lane consumer.</summary>
        public FlowCounterBlock RegisterConsumer(int lane)
        {
            var block = new FlowCounterBlock(C_Size, C_Size, lane);
            lock (_inboundGate)
                _consumerBlocks.Add(block);
            return block;
        }

        public static int BytesBucket(long bytes) =>
            bytes < 256 ? 0 : bytes < 1024 ? 1 : bytes < 4096 ? 2 : bytes < 16384 ? 3 : 4;

        public static int FramesPerWriteBucket(int frames) =>
            frames <= 1 ? 0 : frames < 4 ? 1 : frames < 8 ? 2 : frames < 16 ? 3 : frames < 32 ? 4 : frames < 64 ? 5 : 6;

        public static int FramesPerChunkBucket(int frames) =>
            frames <= 0 ? 0 : frames == 1 ? 1 : frames < 4 ? 2 : frames < 8 ? 3 : frames < 16 ? 4 : 5;

        public static int ItemsPerWakeBucket(int items) =>
            items <= 1 ? 0 : items < 4 ? 1 : items < 8 ? 2 : items < 16 ? 3 : 4;

        /// <summary>
        /// Records one write of <paramref name="frames"/> frames / <paramref name="bytes"/> bytes handed to the
        /// connection stage. Single-writer: call only from the stage that owns <paramref name="block"/>.
        /// </summary>
        public static void RecordWrite(FlowCounterBlock block, int frames, long bytes)
        {
            block.Inc(W_Writes);
            block.Add(W_Frames, frames);
            block.Add(W_Bytes, bytes);
            block.Inc(W_FramesHist + FramesPerWriteBucket(frames));
            block.Inc(W_BytesHist + BytesBucket(bytes));
        }

        /// <summary>
        /// Number of frames in one element of the ordinary outbound stream. Every frame is exactly one
        /// segment of the element's chain (an element is either a single encoded frame, or several
        /// frames chained by the bulk-drain encode stage or the lane write batch), so this walks the
        /// chain from start to end. Only called when diagnostics are on.
        /// </summary>
        public static int CountFrames(System.Buffers.ReadOnlySequence<byte> element)
        {
            if (element.Start.GetObject() is not System.Buffers.ReadOnlySequenceSegment<byte> segment)
                return element.IsEmpty ? 0 : 1;

            var end = element.End.GetObject();
            var frames = 1;
            while (!ReferenceEquals(segment, end) && segment.Next is { } next)
            {
                frames++;
                segment = next;
            }

            return frames;
        }

        /// <summary>Logs the counters accumulated since the previous call and starts a new window.</summary>
        public void EmitSnapshot()
        {
            try
            {
                var elapsedMs = _window.ElapsedMilliseconds;
                _window.Restart();

                foreach (var pair in _outbound)
                {
                    var line = pair.Value.TryFormatAndReset(elapsedMs);
                    if (line is not null)
                        _log.Info("{0}", line);
                }

                var inbound = FormatInbound(elapsedMs);
                if (inbound is not null)
                    _log.Info("{0}", inbound);
            }
            catch (Exception ex)
            {
                // A diagnostics tick must never throw into the scheduler.
                _log.Debug(ex, "ARTERY-FLOW snapshot failed.");
            }
        }

        private string? FormatInbound(long elapsedMs)
        {
            var lanes = _inboundLanes;
            var stage = new long[I_LaneMsgs + 2 * lanes];
            var consumers = new long[lanes][];
            for (var i = 0; i < lanes; i++)
                consumers[i] = new long[C_Size];

            DrainRegistered(_inboundBlocks, b => b.DrainInto(stage));
            DrainRegistered(_consumerBlocks, b =>
            {
                if (b.Lane >= 0 && b.Lane < lanes)
                    b.DrainInto(consumers[b.Lane]);
            });

            var any = false;
            foreach (var v in stage)
                any |= v != 0;
            foreach (var c in consumers)
                foreach (var v in c)
                    any |= v != 0;
            if (!any)
                return null;

            var sb = new StringBuilder(512);
            sb.Append("ARTERY-FLOW dir=in interval_ms=").Append(elapsedMs).Append(" lanes=").Append(lanes);
            sb.Append(" in.chunks=").Append(stage[I_Chunks]);
            sb.Append(" in.chunk_bytes=").Append(stage[I_ChunkBytes]);
            sb.Append(" in.frames=").Append(stage[I_Frames]);
            sb.Append(" in.msgs=").Append(stage[I_Msgs]);
            AppendHistogram(sb, "in.chunk_bytes_hist.", BytesLabels, stage, I_ChunkBytesHist);
            AppendHistogram(sb, "in.frames_per_chunk.", FramesPerChunkLabels, stage, I_FramesHist);

            if (lanes > 1)
            {
                for (var i = 0; i < lanes; i++)
                    sb.Append(" in.lane").Append(i).Append('=').Append(stage[I_LaneMsgs + i]);
                for (var i = 0; i < lanes; i++)
                    sb.Append(" in.depth_hw.lane").Append(i).Append('=').Append(stage[I_LaneMsgs + lanes + i]);
                for (var i = 0; i < lanes; i++)
                {
                    var c = consumers[i];
                    sb.Append(" in.consumer").Append(i).Append(".items=").Append(c[C_Items]);
                    sb.Append(" in.consumer").Append(i).Append(".wakes=").Append(c[C_Wakes]);
                    sb.Append(" in.consumer").Append(i).Append(".suspends=").Append(c[C_Suspends]);
                    AppendHistogram(sb, "in.consumer" + i + ".items_per_wake.", ItemsPerWakeLabels, c, C_WakeHist);
                }
            }

            return sb.ToString();
        }

        private void DrainRegistered(List<FlowCounterBlock> blocks, Action<FlowCounterBlock> drain)
        {
            lock (_inboundGate)
            {
                for (var i = blocks.Count - 1; i >= 0; i--)
                {
                    var block = blocks[i];
                    var retired = block.Retired; // read BEFORE draining so the final values are included
                    drain(block);
                    if (retired)
                        blocks.RemoveAt(i);
                }
            }
        }

        internal static void AppendHistogram(StringBuilder sb, string prefix, string[] labels, long[] values, int start)
        {
            for (var i = 0; i < labels.Length; i++)
                sb.Append(' ').Append(prefix).Append(labels[i]).Append('=').Append(values[start + i]);
        }
    }

    /// <summary>
    /// A flat array of counters with exactly one writing thread (plain, non-atomic updates) and one
    /// reading thread (the snapshot tick). Slots below <c>maxStart</c> are monotonically increasing
    /// counters, drained as deltas against <c>_last</c>; slots from <c>maxStart</c> on are high-water
    /// marks, drained by reading and zeroing them.
    /// </summary>
    internal sealed class FlowCounterBlock
    {
        private readonly long[] _values;
        private readonly long[] _last;
        private readonly int _maxStart;

        public FlowCounterBlock(int size, int maxStart, int lane)
        {
            _values = new long[size];
            _last = new long[size];
            _maxStart = maxStart;
            Lane = lane;
        }

        public int Lane { get; }

        /// <summary>Set by the writer once it will not write again; the snapshot then drops the block.</summary>
        public volatile bool Retired;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Inc(int slot) => _values[slot]++;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int slot, long amount) => _values[slot] += amount;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Max(int slot, long value)
        {
            if (value > _values[slot])
                _values[slot] = value;
        }

        /// <summary>Snapshot thread only. Adds this window's counter deltas, and max-merges the high-water marks, into <paramref name="sum"/>.</summary>
        public void DrainInto(long[] sum)
        {
            for (var i = 0; i < _maxStart; i++)
            {
                var current = Volatile.Read(ref _values[i]);
                sum[i] += current - _last[i];
                _last[i] = current;
            }

            for (var i = _maxStart; i < _values.Length; i++)
            {
                var current = Volatile.Read(ref _values[i]);
                _values[i] = 0;
                if (current > sum[i])
                    sum[i] = current;
            }
        }
    }

    /// <summary>
    /// Outbound counters of one association (remote address): enqueue-side counters are
    /// <see cref="Interlocked"/> because any sender thread enqueues; the write-side counters live in
    /// <see cref="Write"/>, written by the one write stage at a time.
    /// </summary>
    internal sealed class ArteryOutboundFlow
    {
        private readonly Address _remote;
        private readonly long[] _enqueued;
        private readonly long[] _depthHighWater;
        private readonly long[] _writeSum = new long[ArteryFlowDiagnostics.W_Size];

        public ArteryOutboundFlow(Address remote, int lanes)
        {
            _remote = remote;
            _enqueued = new long[lanes];
            _depthHighWater = new long[lanes];
            Write = new FlowCounterBlock(ArteryFlowDiagnostics.W_Size, ArteryFlowDiagnostics.W_Size, lane: -1);
        }

        public FlowCounterBlock Write { get; }

        /// <summary>Called by any sender thread after a successful enqueue; <paramref name="depth"/> is the lane channel depth observed right after it.</summary>
        public void OnEnqueued(int lane, int depth)
        {
            Interlocked.Increment(ref _enqueued[lane]);

            long seen;
            while (depth > (seen = Volatile.Read(ref _depthHighWater[lane])))
            {
                if (Interlocked.CompareExchange(ref _depthHighWater[lane], depth, seen) == seen)
                    break;
            }
        }

        /// <summary>Snapshot thread only. Returns the line for this window, or null when nothing happened.</summary>
        public string? TryFormatAndReset(long elapsedMs)
        {
            var lanes = _enqueued.Length;
            var enq = new long[lanes];
            var hw = new long[lanes];
            var any = false;
            for (var i = 0; i < lanes; i++)
            {
                enq[i] = Interlocked.Exchange(ref _enqueued[i], 0);
                hw[i] = Interlocked.Exchange(ref _depthHighWater[i], 0);
                any |= enq[i] != 0;
            }

            Array.Clear(_writeSum, 0, _writeSum.Length);
            Write.DrainInto(_writeSum);
            any |= _writeSum[ArteryFlowDiagnostics.W_Writes] != 0;
            if (!any)
                return null;

            var sb = new StringBuilder(512);
            sb.Append("ARTERY-FLOW dir=out interval_ms=").Append(elapsedMs).Append(" remote=").Append(_remote).Append(" lanes=").Append(lanes);
            for (var i = 0; i < lanes; i++)
                sb.Append(" out.lane").Append(i).Append('=').Append(enq[i]);
            for (var i = 0; i < lanes; i++)
                sb.Append(" out.depth_hw.lane").Append(i).Append('=').Append(hw[i]);
            sb.Append(" out.writes=").Append(_writeSum[ArteryFlowDiagnostics.W_Writes]);
            sb.Append(" out.frames=").Append(_writeSum[ArteryFlowDiagnostics.W_Frames]);
            sb.Append(" out.bytes=").Append(_writeSum[ArteryFlowDiagnostics.W_Bytes]);
            ArteryFlowDiagnostics.AppendHistogram(sb, "out.frames_per_write.", ArteryFlowDiagnostics.FramesPerWriteLabels, _writeSum, ArteryFlowDiagnostics.W_FramesHist);
            ArteryFlowDiagnostics.AppendHistogram(sb, "out.bytes_per_write.", ArteryFlowDiagnostics.BytesLabels, _writeSum, ArteryFlowDiagnostics.W_BytesHist);
            sb.Append(" out.batch_wait_demand=").Append(_writeSum[ArteryFlowDiagnostics.W_WaitDemand]);
            sb.Append(" out.batch_partial_push=").Append(_writeSum[ArteryFlowDiagnostics.W_PartialPush]);
            return sb.ToString();
        }
    }
}
