// -----------------------------------------------------------------------
// <copyright file="ArteryQuicWriteProgress.cs" company="Akka.NET Project">
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;

namespace Akka.Remote.Artery
{
    /// <summary>Opt-in observations of public writes, interpreter handoff and coalescing rentals.</summary>
    internal sealed class ArteryQuicWriteProgress
    {
        internal const int DefaultSamplingInterval = 64;
        private readonly int _sampleMask;
        private long _calls, _bytes, _ready, _completed, _canceled, _faulted;
        private long _samples, _invocationTicks, _completionTicks;
        private long _batches, _callbackSamples, _callbackTicks;
        private long _rentedCapacity, _peakRentedCapacity;
        private long _rentOperations, _copiedBytes;

        internal ArteryQuicWriteProgress(int sampleEvery = DefaultSamplingInterval)
        {
            if (sampleEvery < 1 || (sampleEvery & (sampleEvery - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(sampleEvery), "Sampling interval must be a positive power of two.");
            _sampleMask = sampleEvery - 1;
        }

        internal readonly record struct Snapshot(long Calls, long Bytes, long ReadyOnInspection, long Completed,
            long Canceled, long Faulted, long Samples, long InvocationTicks, long CompletionTicks,
            long Batches, long CallbackSamples, long CallbackTicks, long CurrentRentedCapacityBytes, long LifetimePeakRentedCapacityBytes,
            long RentOperations, long CopiedBytes)
        {
            internal Snapshot Since(Snapshot before) => new(Calls - before.Calls, Bytes - before.Bytes,
                ReadyOnInspection - before.ReadyOnInspection, Completed - before.Completed, Canceled - before.Canceled,
                Faulted - before.Faulted, Samples - before.Samples, InvocationTicks - before.InvocationTicks,
                CompletionTicks - before.CompletionTicks, Batches - before.Batches, CallbackSamples - before.CallbackSamples,
                CallbackTicks - before.CallbackTicks, CurrentRentedCapacityBytes, LifetimePeakRentedCapacityBytes,
                RentOperations - before.RentOperations, CopiedBytes - before.CopiedBytes);
            public double MeanSampledInvocationUs => Samples == 0 ? 0 : InvocationTicks * 1_000_000.0 / Stopwatch.Frequency / Samples;
            public double MeanSampledCompletionUs => Samples == 0 ? 0 : CompletionTicks * 1_000_000.0 / Stopwatch.Frequency / Samples;
            public double MeanSampledCallbackUs => CallbackSamples == 0 ? 0 : CallbackTicks * 1_000_000.0 / Stopwatch.Frequency / CallbackSamples;
        }

        internal Snapshot Read() => new(Volatile.Read(ref _calls), Volatile.Read(ref _bytes), Volatile.Read(ref _ready),
            Volatile.Read(ref _completed), Volatile.Read(ref _canceled), Volatile.Read(ref _faulted), Volatile.Read(ref _samples),
            Volatile.Read(ref _invocationTicks), Volatile.Read(ref _completionTicks), Volatile.Read(ref _batches),
            Volatile.Read(ref _callbackSamples), Volatile.Read(ref _callbackTicks), Volatile.Read(ref _rentedCapacity), Volatile.Read(ref _peakRentedCapacity),
            Volatile.Read(ref _rentOperations), Volatile.Read(ref _copiedBytes));

        /// <summary>One preallocated probe per writer; that writer never overlaps public writes.</summary>
        internal sealed class Writer(ArteryQuicWriteProgress counters)
        {
            private bool _active;
            private bool _sampleCallback;
            private long _started, _returned, _posted;
            private long _calls, _batches;

            internal void BeginBatch()
            {
                Interlocked.Increment(ref counters._batches);
                _sampleCallback = (++_batches & counters._sampleMask) == 0;
            }

            internal void BeforeInvocation(int bytes)
            {
                if (_active)
                    throw new InvalidOperationException("Write progress probe observed overlapping public writes.");
                _active = bytes > 0;
                if (!_active)
                    return;
                Interlocked.Increment(ref counters._calls);
                Interlocked.Add(ref counters._bytes, bytes);
                _started = (++_calls & counters._sampleMask) == 0 ? Stopwatch.GetTimestamp() : 0;
                _returned = 0;
            }

            internal void InvocationReturned(bool successfullyReady)
            {
                if (!_active)
                    return;
                if (_started != 0)
                    _returned = Stopwatch.GetTimestamp();
                if (successfullyReady)
                    Interlocked.Increment(ref counters._ready);
            }

            internal void Completed()
            {
                if (!_active)
                    return;
                if (_started != 0)
                {
                    Interlocked.Add(ref counters._invocationTicks, _returned - _started);
                    Interlocked.Add(ref counters._completionTicks, Stopwatch.GetTimestamp() - _started);
                    Interlocked.Increment(ref counters._samples);
                }
                Interlocked.Increment(ref counters._completed);
                _active = false;
            }

            internal void Failed(Exception error)
            {
                if (!_active)
                    return;
                if (error is OperationCanceledException)
                    Interlocked.Increment(ref counters._canceled);
                else
                    Interlocked.Increment(ref counters._faulted);
                _active = false;
            }

            internal void CallbackPosted()
            {
                if (_sampleCallback)
                    Volatile.Write(ref _posted, Stopwatch.GetTimestamp());
            }

            internal void CallbackHandled()
            {
                var posted = Interlocked.Exchange(ref _posted, 0);
                if (posted == 0)
                    return;
                Interlocked.Add(ref counters._callbackTicks, Stopwatch.GetTimestamp() - posted);
                Interlocked.Increment(ref counters._callbackSamples);
            }

            internal void Rented(int capacity)
            {
                Interlocked.Increment(ref counters._rentOperations);
                var current = Interlocked.Add(ref counters._rentedCapacity, capacity);
                var peak = Volatile.Read(ref counters._peakRentedCapacity);
                while (current > peak)
                {
                    var observed = Interlocked.CompareExchange(ref counters._peakRentedCapacity, current, peak);
                    if (observed == peak)
                        break;
                    peak = observed;
                }
            }

            internal void Returned(int capacity) => Interlocked.Add(ref counters._rentedCapacity, -capacity);
            internal void Copied(int bytes) => Interlocked.Add(ref counters._copiedBytes, bytes);
        }
    }
}
