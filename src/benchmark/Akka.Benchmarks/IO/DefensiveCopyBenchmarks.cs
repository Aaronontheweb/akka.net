//-----------------------------------------------------------------------
// <copyright file="DefensiveCopyBenchmarks.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable
using System;
using System.Buffers;
using Akka.Benchmarks.Configurations;
using BenchmarkDotNet.Attributes;

namespace Akka.Benchmarks.IO
{
    /// <summary>
    /// Isolates the cost of the FULL-ALLOCATION defensive copies that exist elsewhere in the
    /// write path, in contrast to the pipe-layer copy measured in
    /// <see cref="TcpWritePathCopyBenchmarks"/> (which reuses pooled segments and is expected to
    /// allocate ~0 B/op).
    /// <list type="bullet">
    /// <item><description>
    /// <c>ToArray_Span</c> mirrors the Artery encode-buffer -&gt; stream-element copy
    /// (<c>writer.WrittenSpan.ToArray()</c> in the pre-refactor <c>ArteryRemoting.EncodeOutboundElement</c>) --
    /// a flat <see cref="Span{T}"/> over a single contiguous buffer.
    /// </description></item>
    /// <item><description>
    /// <c>ToArray_ReadOnlySequence_SingleSegment</c> mirrors
    /// <c>TcpConnection.BufferSingleWriteBeforeRegister</c>'s <c>write.Data.ToArray()</c> -- the
    /// cold-path pre-registration copy -- via the actual <see cref="ReadOnlySequence{T}"/> type
    /// <c>Write.Data</c> is declared as.
    /// </description></item>
    /// </list>
    /// Both are, mechanically, the same operation (allocate an N-byte array + memcpy the source
    /// into it) so this benchmark also demonstrates whether wrapping the source in a
    /// <see cref="ReadOnlySequence{T}"/> adds measurable overhead over a flat span for the
    /// single-segment case that both real call sites happen to hit in practice.
    /// </summary>
    [Config(typeof(MicroBenchmarkConfig))]
    public class DefensiveCopyBenchmarks
    {
        [Params(256, 4096, 65536)]
        public int PayloadBytes { get; set; }

        private byte[] _sourceArray = null!;
        private ReadOnlySequence<byte> _sourceSequence;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _sourceArray = new byte[PayloadBytes];
            new Random(42).NextBytes(_sourceArray);
            _sourceSequence = new ReadOnlySequence<byte>(_sourceArray);
        }

        /// <summary>Layer-1 shape: <c>Span&lt;byte&gt;.ToArray()</c> (Artery's pre-refactor <c>WrittenSpan.ToArray()</c>).</summary>
        [Benchmark(Baseline = true)]
        public byte[] ToArray_Span() => _sourceArray.AsSpan().ToArray();

        /// <summary>Layer-2 shape: <c>ReadOnlySequence&lt;byte&gt;.ToArray()</c> (<c>BufferSingleWriteBeforeRegister</c>'s <c>write.Data.ToArray()</c>).</summary>
        [Benchmark]
        public byte[] ToArray_ReadOnlySequence_SingleSegment() => _sourceSequence.ToArray();
    }
}
