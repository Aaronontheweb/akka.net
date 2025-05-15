//-----------------------------------------------------------------------
// <copyright file="ByteStringV2.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
 using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Akka.IO.Memory
{
    /// <summary>
    /// A rope-like immutable data structure containing bytes.
    /// The goal of this structure is to reduce copying of arrays
    /// when concatenating and slicing sequences of bytes,
    /// and also providing a thread safe way of working with bytes.
    /// </summary>
    [DebuggerDisplay("(Count = {Count}, Segments = {_segments.Count})")]
    internal sealed class ByteStringV2 : IEquatable<ByteStringV2>, IEnumerable<byte>
    {
        #region Fields and properties

        private readonly List<IMemorySegment> _segments;
        private readonly int _count;

        /// <summary>
        /// Gets a total number of bytes stored inside this <see cref="ByteStringV2"/>.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Determines if current <see cref="ByteStringV2"/> has compact representation.
        /// Compact byte strings represent bytes stored inside single, continuous
        /// block of memory.
        /// </summary>
        public bool IsCompact => _segments.Count <= 1;

        /// <summary>
        /// Determines if current <see cref="ByteStringV2"/> is empty.
        /// </summary>
        public bool IsEmpty => _count == 0;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="ByteStringV2"/> class with a single memory segment.
        /// </summary>
        /// <param name="segment">The memory segment.</param>
        private ByteStringV2(IMemorySegment segment)
        {
            _segments = new List<IMemorySegment>(1) { segment };
            _count = segment.Length;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ByteStringV2"/> class with multiple memory segments.
        /// </summary>
        /// <param name="segments">The memory segments.</param>
        /// <param name="count">The total byte count.</param>
        private ByteStringV2(List<IMemorySegment> segments, int count)
        {
            _segments = segments;
            _count = count;
        }

        #endregion

        #region Creation methods

        /// <summary>
        /// An empty <see cref="ByteStringV2"/>.
        /// </summary>
        public static ByteStringV2 Empty { get; } = new ByteStringV2(EmptySegment.Instance);

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a provided byte array.
        /// </summary>
        /// <param name="array">Array of bytes to copy</param>
        /// <returns>A byte string representation of array of bytes.</returns>
        public static ByteStringV2 CopyFrom(byte[] array) => CopyFrom(array, 0, array?.Length ?? 0);

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a byte array.
        /// </summary>
        /// <param name="array">Array of bytes to copy</param>
        /// <param name="offset">Index in provided <paramref name="array"/>, at which copy should start.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 CopyFrom(byte[] array, int offset, int count)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));

            if (count == 0) return Empty;

            if (offset < 0 || offset >= array.Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Provided offset of [{offset}] is outside bounds of an array [{array.Length}]");
            if (count > array.Length - offset) throw new ArgumentException($"Provided length [{count}] of array to copy doesn't fit array length [{array.Length}] within given offset [{offset}]", nameof(count));

            var copy = new byte[count];
            Array.Copy(array, offset, copy, 0, count);

            return new ByteStringV2(new ArrayMemorySegment(copy, 0, count));
        }

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a <see cref="Memory{T}"/>.
        /// </summary>
        /// <param name="memory">The <see cref="Memory{T}"/> to copy</param>
        /// <returns>The new <see cref="ByteStringV2"/></returns>
        public static ByteStringV2 CopyFrom(Memory<byte> memory)
            => CopyFrom(memory, 0, memory.Length);
        
        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a <see cref="Memory{T}"/>.
        /// </summary>
        /// <param name="memory">The <see cref="Memory{T}"/> to copy</param>
        /// <param name="offset">Index in provided <paramref name="memory"/>, at which copy should start.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <returns>The new <see cref="ByteStringV2"/></returns>
        public static ByteStringV2 CopyFrom(Memory<byte> memory, int offset, int count)
        {
            if (count == 0) return Empty;

            if (offset < 0 || offset >= memory.Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Provided offset of [{offset}] is outside bounds of an array [{memory.Length}]");
            if (count > memory.Length - offset) throw new ArgumentException($"Provided length [{count}] of array to copy doesn't fit array length [{memory.Length}] within given offset [{offset}]", nameof(count));

            var copy = new byte[count];
            memory.Slice(offset, count).CopyTo(copy);

            return new ByteStringV2(new ArrayMemorySegment(copy, 0, count));
        }

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a <see cref="Span{T}"/>.
        /// </summary>
        /// <param name="span">The <see cref="Span{T}"/> to copy</param>
        /// <returns>The new <see cref="ByteStringV2"/></returns>
        public static ByteStringV2 CopyFrom(Span<byte> span)
            => CopyFrom(span, 0, span.Length);
        
        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by copying a <see cref="Span{T}"/>.
        /// </summary>
        /// <param name="span">The <see cref="Span{T}"/> to copy</param>
        /// <param name="offset">Index in provided <paramref name="span"/>, at which copy should start.</param>
        /// <param name="count">Number of bytes to copy.</param>
        /// <returns>The new <see cref="ByteStringV2"/></returns>
        public static ByteStringV2 CopyFrom(Span<byte> span, int offset, int count)
        {
            if (count == 0) return Empty;

            if (offset < 0 || offset >= span.Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Provided offset of [{offset}] is outside bounds of an array [{span.Length}]");
            if (count > span.Length - offset) throw new ArgumentException($"Provided length [{count}] of array to copy doesn't fit array length [{span.Length}] within given offset [{offset}]", nameof(count));

            var copy = new byte[count];
            span.Slice(offset, count).CopyTo(copy);

            return new ByteStringV2(new ArrayMemorySegment(copy, 0, count));
        }

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by wrapping raw array of bytes.
        /// WARNING: this method doesn't copy underlying array, but expects 
        /// that it should not be modified once attached to byte string.
        /// </summary>
        /// <param name="array">The array to wrap.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromBytes(byte[] array) => 
            array == null || array.Length == 0 ? Empty : FromBytes(array, 0, array.Length);

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by wrapping raw range over array of bytes. WARNING: 
        /// this method doesn't copy underlying array, but expects that 
        /// represented range should not be modified once attached to byte string.
        /// </summary>
        /// <param name="array">The array to wrap.</param>
        /// <param name="offset">The offset into the array.</param>
        /// <param name="count">The number of bytes to include.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromBytes(byte[] array, int offset, int count)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (offset < 0 || (offset != 0 && offset >= array.Length)) throw new ArgumentOutOfRangeException(nameof(offset), $"Provided offset [{offset}] is outside bounds of an array");
            if (count > array.Length - offset) throw new ArgumentException($"Provided length of array to copy [{count}] doesn't fit array length [{array.Length}] and offset [{offset}].", nameof(count));

            if (count == 0) return Empty;

            return new ByteStringV2(new ArrayMemorySegment(array, offset, count));
        }

        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by wrapping raw range over memory. WARNING: 
        /// this method doesn't copy underlying memory, but expects that 
        /// represented range should not be modified once attached to byte string.
        /// </summary>
        /// <param name="memory">The memory to wrap.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromMemory(ReadOnlyMemory<byte> memory)
        {
            if (memory.IsEmpty) return Empty;

            return new ByteStringV2(new MemorySegment(memory));
        }
        
        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> by wrapping raw range over memory. WARNING: 
        /// this method doesn't copy underlying memory, but expects that 
        /// represented range should not be modified once attached to byte string.
        /// </summary>
        /// <param name="memory">The memory to wrap.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromMemory(Memory<byte> memory) => FromMemory((ReadOnlyMemory<byte>)memory);
        
        /// <summary>
        /// Creates a new <see cref="ByteStringV2"/> from a span. Note that this will copy the span data 
        /// since Span is a stack-only type and cannot be stored.
        /// </summary>
        /// <param name="span">The span to copy.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromSpan(ReadOnlySpan<byte> span)
        {
            if (span.IsEmpty) return Empty;

            // We must copy the span since it's a stack-only type
            var array = span.ToArray();
            return new ByteStringV2(new ArrayMemorySegment(array, 0, array.Length));
        }

        /// <summary>
        /// Creates a new ByteStringV2 which will contain the UTF-8 representation of the given String
        /// </summary>
        /// <param name="str">The string to convert.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromString(string str) => FromString(str, Encoding.UTF8);

        /// <summary>
        /// Creates a new ByteStringV2 which will contain the representation of 
        /// the given String in the given charset encoding.
        /// </summary>
        /// <param name="str">The string to convert.</param>
        /// <param name="encoding">The encoding to use.</param>
        /// <returns>A new byte string.</returns>
        public static ByteStringV2 FromString(string str, Encoding encoding)
        {
            if (string.IsNullOrEmpty(str)) return Empty;

            var bytes = encoding.GetBytes(str);
            return FromBytes(bytes);
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Gets a byte stored under a provided <paramref name="index"/>.
        /// </summary>
        /// <param name="index">The index of the byte to get.</param>
        /// <returns>The byte at the specified index.</returns>
        public byte this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new IndexOutOfRangeException($"Requested index {index} is outside of the bounds of the ByteString with length {_count}");

                int position = 0;
                foreach (var segment in _segments)
                {
                    if (index < position + segment.Length)
                        return segment[index - position];

                    position += segment.Length;
                }

                throw new IndexOutOfRangeException($"Failed to locate index {index} in ByteString");
            }
        }

        /// <summary>
        /// Compacts current <see cref="ByteStringV2"/>, potentially copying its content underneat
        /// into new byte array.
        /// </summary>
        /// <returns>A compacted byte string.</returns>
        public ByteStringV2 Compact()
        {
            if (IsCompact) return this;
            if (IsEmpty) return Empty;

            // Create a new byte array and copy all segments into it
            var result = new byte[_count];
            int position = 0;

            foreach (var segment in _segments)
            {
                segment.CopyTo(result.AsSpan(position));
                position += segment.Length;
            }

            return new ByteStringV2(new ArrayMemorySegment(result, 0, result.Length));
        }

        /// <summary>
        /// Slices current <see cref="ByteStringV2"/>, creating a new <see cref="ByteStringV2"/>
        /// which contains a specified range of data from the original. This is non-copying
        /// operation.
        /// </summary>
        /// <param name="index">index inside current <see cref="ByteStringV2"/>, from which slicing should start</param>
        /// <returns>A new byte string representing the slice.</returns>
        public ByteStringV2 Slice(int index) => Slice(index, _count - index);

        /// <summary>
        /// Slices current <see cref="ByteStringV2"/>, creating a new <see cref="ByteStringV2"/>
        /// which contains a specified range of data from the original. This is non-copying
        /// operation.
        /// </summary>
        /// <param name="index">index inside current <see cref="ByteStringV2"/>, from which slicing should start</param>
        /// <param name="count">Number of bytes to fit into new <see cref="ByteStringV2"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">If index or count result in an invalid <see cref="ByteStringV2"/>.</exception>
        /// <returns>A new byte string representing the slice.</returns>
        public ByteStringV2 Slice(int index, int count)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Index must be positive number");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive number");
            if (count == 0) 
                return Empty;
            if (index > _count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is outside of the bounds of the ByteString");
            if (index + count > _count)
                throw new ArgumentOutOfRangeException(nameof(count), "Index + count is outside of the bounds of the ByteString");
            
            if (index == 0 && count == _count) 
                return this;

            // If we have a single segment, just slice it directly
            if (IsCompact)
            {
                return new ByteStringV2(_segments[0].Slice(index, count));
            }

            // Otherwise we need to find segments that cover the requested range
            var newSegments = new List<IMemorySegment>();
            int position = 0;
            int remaining = count;

            foreach (var segment in _segments)
            {
                if (position + segment.Length <= index)
                {
                    // Skip segments that are entirely before the start index
                    position += segment.Length;
                    continue;
                }

                if (position >= index + count)
                {
                    // Break once we've collected enough segments
                    break;
                }

                // Calculate the part of this segment to include
                int segmentStart = Math.Max(0, index - position);
                int segmentLength = Math.Min(segment.Length - segmentStart, remaining);

                newSegments.Add(segment.Slice(segmentStart, segmentLength));
                position += segment.Length;
                remaining -= segmentLength;
            }

            return new ByteStringV2(newSegments, count);
        }

        /// <summary>
        /// Appends <paramref name="other"/> <see cref="ByteStringV2"/> at the tail
        /// of a current one, creating a new <see cref="ByteStringV2"/> in result.
        /// Contents of byte strings are NOT copied.
        /// </summary>
        /// <param name="other">The byte string to concatenate with.</param>
        /// <returns>A new concatenated byte string.</returns>
        public ByteStringV2 Concat(ByteStringV2 other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other), "Cannot append null to ByteString.");

            if (other.IsEmpty) return this;
            if (this.IsEmpty) return other;

            // Create a new list with all segments
            var newSegments = new List<IMemorySegment>(_segments.Count + other._segments.Count);
            newSegments.AddRange(_segments);
            newSegments.AddRange(other._segments);

            return new ByteStringV2(newSegments, _count + other._count);
        }

        /// <summary>
        /// Copies content of a current <see cref="ByteStringV2"/> into a single byte array.
        /// </summary>
        /// <returns>A new byte array containing all bytes.</returns>
        public byte[] ToArray()
        {
            if (_count == 0)
                return Array.Empty<byte>();

            // Optimization for single segment
            if (IsCompact && _segments[0] is ArrayMemorySegment segment)
            {
                var span = segment.AsSpan();
                var result = new byte[span.Length];
                span.CopyTo(result);
                return result;
            }

            // Copy all segments to a new array
            var array = new byte[_count];
            int position = 0;

            foreach (var seg in _segments)
            {
                seg.CopyTo(array.AsSpan(position));
                position += seg.Length;
            }

            return array;
        }

        /// <summary>
        /// Attempts to get a ReadOnlySpan over the ByteString contents without copying.
        /// This only works if the ByteString is compact (single segment).
        /// </summary>
        /// <param name="span">The span, if successful.</param>
        /// <returns>True if a span could be created without copying; otherwise, false.</returns>
        public bool TryGetReadOnlySpan(out ReadOnlySpan<byte> span)
        {
            if (_count == 0)
            {
                span = ReadOnlySpan<byte>.Empty;
                return true;
            }
        
            if (IsCompact)
            {
                // If compact, data is in a single segment
                span = _segments[0].AsSpan();
                return true;
            }
           
            span = default;
            return false;
        }
        
        /// <summary>
        /// Returns a ReadOnlySpan over the ByteString contents.
        /// WARNING: This operation creates a copy when the ByteString is not compact.
        /// Use TryGetReadOnlySpan for zero-copy operations.
        /// </summary>
        /// <returns>A ReadOnlySpan over the byte data.</returns>
        public ReadOnlySpan<byte> ToReadOnlySpan()
        {
            if (TryGetReadOnlySpan(out var span))
                return span;
            
            // Fall back to copying
            return new ReadOnlySpan<byte>(ToArray());
        }

        /// <summary>
        /// Attempts to get the ByteString as a single ReadOnlyMemory region.
        /// </summary>
        /// <param name="memory">The memory region, if successful.</param>
        /// <returns>True if the ByteString could be represented as a single memory region; otherwise, false.</returns>
        public bool TryGetSingleMemory(out ReadOnlyMemory<byte> memory)
        {
            if (IsEmpty)
            {
                memory = ReadOnlyMemory<byte>.Empty;
                return true;
            }

            if (IsCompact)
            {
                return _segments[0].TryGetReadOnlyMemory(out memory);
            }

            memory = default;
            return false;
        }

        /// <summary>
        /// Creates a ReadOnlySequence from this ByteString. This is useful for
        /// efficient integration with System.IO.Pipelines. This is a zero-copy operation.
        /// </summary>
        /// <returns>A ReadOnlySequence representing the ByteString contents.</returns>
        public ReadOnlySequence<byte> AsReadOnlySequence()
        {
            if (IsEmpty)
                return ReadOnlySequence<byte>.Empty;

            if (IsCompact && _segments[0].TryGetReadOnlyMemory(out var memory))
                return new ReadOnlySequence<byte>(memory);

            // Create a linked list of segments
            if (_segments.Count == 0)
                return ReadOnlySequence<byte>.Empty;
            
            // Special case for single segment
            if (_segments.Count == 1)
            {
                var segment = _segments[0];
                if (segment.TryGetReadOnlyMemory(out var singleMemory))
                    return new ReadOnlySequence<byte>(singleMemory);
                    
                // If we can't get direct memory and don't want to copy, use a custom segment
                return new ReadOnlySequence<byte>(new SegmentAdapter(segment), 0, new SegmentAdapter(segment), segment.Length);
            }
            
            // Multiple segments case - build a linked list
            var first = new SegmentAdapter(_segments[0], null);
            var current = first;
            long runningIndex = _segments[0].Length;

            for (int i = 1; i < _segments.Count; i++)
            {
                var next = new SegmentAdapter(_segments[i], runningIndex);
                current.SetNext(next);
                current = next;
                runningIndex += _segments[i].Length;
            }

            return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
        }
        
        /// <summary>
        /// Adapter that can wrap any IMemorySegment for use with ReadOnlySequence
        /// </summary>
        private class SegmentAdapter : ReadOnlySequenceSegment<byte>
        {
            private readonly IMemorySegment _segment;
            
            public SegmentAdapter(IMemorySegment segment, long runningIndex = 0)
            {
                _segment = segment;
                RunningIndex = runningIndex;
                
                // Try to get direct memory access
                if (_segment.TryGetReadOnlyMemory(out var memory))
                {
                    Memory = memory;
                }
                else
                {
                    // Use a custom memory that wraps the segment
                    Memory = new SegmentMemory(_segment);
                }
            }
            
            public void SetNext(SegmentAdapter next)
            {
                Next = next;
            }
        }
        
        /// <summary>
        /// Custom ReadOnlyMemory implementation that wraps an IMemorySegment
        /// </summary>
        private readonly struct SegmentMemory : IEquatable<SegmentMemory>
        {
            private readonly IMemorySegment _segment;
            
            public SegmentMemory(IMemorySegment segment)
            {
                _segment = segment;
            }
            
            public int Length => _segment.Length;
            
            // Convert to a ReadOnlyMemory<byte>
            public static implicit operator ReadOnlyMemory<byte>(SegmentMemory memory)
            {
                return memory._segment.AsSpan().ToArray(); // This is only called when we can't avoid copying
            }
            
            public bool Equals(SegmentMemory other) => ReferenceEquals(_segment, other._segment);
            public override bool Equals(object obj) => obj is SegmentMemory other && Equals(other);
            public override int GetHashCode() => _segment?.GetHashCode() ?? 0;
        }



        /// <summary>
        /// Copies content of the current <see cref="ByteStringV2"/> into a provided
        /// <paramref name="buffer"/> starting from <paramref name="index"/> in that
        /// buffer and copying a <paramref name="count"/> number of bytes.
        /// </summary>
        /// <returns>The number of bytes copied.</returns>
        public int CopyTo(byte[] buffer, int index, int count)
        {
            if(buffer?.Length == 0 && count == 0) return 0; // edge case for no-copy
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (index < 0 || index >= buffer.Length) throw new ArgumentOutOfRangeException(nameof(index), "Provided index is outside the bounds of the buffer to copy to.");
            if (count > buffer.Length - index) throw new ArgumentException("Provided number of bytes to copy won't fit into provided buffer", nameof(count));

            return CopyTo(buffer.AsSpan(index, count));
        }

        /// <summary>
        /// Copies content of the current <see cref="ByteStringV2"/> into a provided <see cref="Memory{T}"/>
        /// <paramref name="buffer"/>
        /// </summary>
        /// <returns>The number of bytes copied</returns>
        public int CopyTo(Memory<byte> buffer)
        {
            return CopyTo(buffer.Span);
        }

        /// <summary>
        /// Copies content of the current <see cref="ByteStringV2"/> into a provided <see cref="Span{T}"/>
        /// <paramref name="buffer"/>
        /// </summary>
        /// <returns>The number of bytes copied</returns>
        public int CopyTo(Span<byte> buffer)
        {
            if (buffer.IsEmpty && _count == 0) return 0; // edge case for no-copy
            if (buffer.IsEmpty) throw new ArgumentException("Buffer is empty", nameof(buffer));

            // Fast path for compact ByteStrings
            if (IsCompact && _segments[0].TryGetReadOnlyMemory(out var memory))
            {
                int bytesToCopy = Math.Min(memory.Length, buffer.Length);
                memory.Span.Slice(0, bytesToCopy).CopyTo(buffer);
                return bytesToCopy;
            }

            // Normal path for multi-segment ByteStrings
            int position = 0;
            int bytesRemaining = Math.Min(_count, buffer.Length);

            foreach (var segment in _segments)
            {
                if (bytesRemaining <= 0) break;
                
                int segmentBytesToCopy = Math.Min(segment.Length, bytesRemaining);
                segment.AsSpan().Slice(0, segmentBytesToCopy).CopyTo(buffer.Slice(position, segmentBytesToCopy));
                
                position += segmentBytesToCopy;
                bytesRemaining -= segmentBytesToCopy;
            }

            return position; // Total bytes copied
        }

        /// <summary>
        /// Writes the content of the current <see cref="ByteStringV2"/> to a provided 
        /// writeable <paramref name="stream"/>. This is done with minimal copying.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Use a reasonable buffer size for segments that need copying
            Span<byte> tempBuffer = stackalloc byte[512]; // Try to use stack for small buffers
            byte[] heapBuffer = null;

            try
            {
                foreach (var segment in _segments)
                {
                    if (segment.TryGetReadOnlyMemory(out var memory))
                    {
                        // Direct memory access - no copying needed
                        if (memory.Length > 0)
                            stream.Write(memory.Span);
                    }
                    else
                    {
                        // Need to copy through a buffer
                        var span = segment.AsSpan();
                        if (span.Length <= tempBuffer.Length)
                        {
                            // Use stack-allocated buffer for small segments
                            span.CopyTo(tempBuffer);
                            stream.Write(tempBuffer.Slice(0, span.Length));
                        }
                        else
                        {
                            // For larger segments, use a heap buffer (allocated only once and reused)
                            if (heapBuffer == null || heapBuffer.Length < span.Length)
                                heapBuffer = new byte[Math.Max(span.Length, 4096)]; // Allocate with some room to grow
                            
                            var bufferSpan = heapBuffer.AsSpan(0, span.Length);
                            span.CopyTo(bufferSpan);
                            stream.Write(bufferSpan);
                        }
                    }
                }
            }
            finally
            {
                // Help GC by clearing reference to large buffer
                heapBuffer = null;
            }
        }

        /// <summary>
        /// Asynchronously writes the content of the current <see cref="ByteStringV2"/> 
        /// to a provided writeable <paramref name="stream"/>. This is done with minimal copying.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public async Task WriteToAsync(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // Use a buffer for segments that need copying, but allocate only once
            byte[] buffer = null;
            
            try
            {
                foreach (var segment in _segments)
                {
                    if (segment.TryGetReadOnlyMemory(out var memory))
                    {
                        // Direct memory access - no copying needed
                        if (memory.Length > 0)
                            await stream.WriteAsync(memory);
                    }
                    else
                    {
                        // Need to copy through a buffer
                        var span = segment.AsSpan();
                        
                        // Allocate the buffer only once and reuse it
                        if (buffer == null || buffer.Length < span.Length)
                            buffer = new byte[Math.Max(span.Length, 4096)]; // Allocate with some room to grow
                        
                        span.CopyTo(buffer.AsSpan(0, span.Length));
                        await stream.WriteAsync(buffer.AsMemory(0, span.Length));
                    }
                }
            }
            finally
            {
                // Help GC by clearing reference to large buffer
                buffer = null;
            }
        }

        #endregion

        #region Equality and hash code

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as ByteStringV2);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hashCode = 0;
            foreach (var b in this)
            {
                hashCode = (hashCode * 397) ^ b.GetHashCode();
            }
            return hashCode;
        }

        /// <inheritdoc />
        public bool Equals(ByteStringV2 other)
        {
            if (ReferenceEquals(other, this)) return true;
            if (ReferenceEquals(other, null)) return false;
            if (_count != other._count) return false;

            using (var thisEnum = this.GetEnumerator())
            using (var otherEnum = other.GetEnumerator())
            {
                while (thisEnum.MoveNext() && otherEnum.MoveNext())
                {
                    if (thisEnum.Current != otherEnum.Current) return false;
                }
            }

            return true;
        }

        #endregion

        #region Enumeration and conversion

        /// <inheritdoc />
        public IEnumerator<byte> GetEnumerator()
        {
            foreach (var segment in _segments)
            {
                var span = segment.AsSpan();
                for (int i = 0; i < span.Length; i++)
                {
                    yield return span[i];
                }
            }
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc />
        public override string ToString() => ToString(Encoding.UTF8);

        /// <summary>
        /// Converts the ByteString to a string using the specified encoding.
        /// </summary>
        /// <param name="encoding">The encoding to use.</param>
        /// <returns>The string representation of the ByteString.</returns>
        public string ToString(Encoding encoding)
        {
            if (IsEmpty) return string.Empty;

            // Fast path for compact strings - try to get direct memory access
            if (IsCompact)
            {
                var segment = _segments[0];
                if (segment.TryGetReadOnlyMemory(out var memory))
                    return encoding.GetString(memory.Span);
                
                return encoding.GetString(segment.AsSpan());
            }
            
            // For multi-segment strings, we have a few options
            if (_count <= 1024) // For small strings, just use a temporary array
            {
                return encoding.GetString(ToArray());
            }
            
            // For larger strings, use a StringBuilder to avoid large temporary allocations
            using (var ms = new MemoryStream(_count))
            {
                WriteTo(ms);
                return encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        #endregion

        #region Operators

        /// <summary>
        /// Equality operator.
        /// </summary>
        public static bool operator ==(ByteStringV2 x, ByteStringV2 y) => Equals(x, y);

        /// <summary>
        /// Inequality operator.
        /// </summary>
        public static bool operator !=(ByteStringV2 x, ByteStringV2 y) => !Equals(x, y);

        /// <summary>
        /// Explicit conversion from byte array to ByteString.
        /// </summary>
        public static explicit operator ByteStringV2(byte[] bytes) => ByteStringV2.CopyFrom(bytes);
        
        /// <summary>
        /// Explicit conversion from ByteString to byte array.
        /// </summary>
        public static explicit operator byte[] (ByteStringV2 byteString) => byteString.ToArray();
        
        /// <summary>
        /// Concatenation operator.
        /// </summary>
        public static ByteStringV2 operator +(ByteStringV2 x, ByteStringV2 y) => x.Concat(y);

        #endregion
    }
}