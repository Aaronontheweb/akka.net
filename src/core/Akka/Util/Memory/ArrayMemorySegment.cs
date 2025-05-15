//-----------------------------------------------------------------------
// <copyright file="ArrayMemorySegment.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Akka.IO.Memory
{
    /// <summary>
    /// An implementation of <see cref="IMemorySegment"/> that wraps an <see cref="ArraySegment{T}"/> of bytes.
    /// This is primarily used for backward compatibility with existing ByteString implementations.
    /// </summary>
    internal sealed class ArrayMemorySegment : IMemorySegment
    {
        private readonly ArraySegment<byte> _segment;

        /// <summary>
        /// Initializes a new instance of the <see cref="ArrayMemorySegment"/> class.
        /// </summary>
        /// <param name="segment">The array segment to wrap.</param>
        public ArrayMemorySegment(ArraySegment<byte> segment)
        {
            _segment = segment;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ArrayMemorySegment"/> class.
        /// </summary>
        /// <param name="array">The array to wrap.</param>
        /// <param name="offset">The offset into the array.</param>
        /// <param name="count">The number of bytes to include.</param>
        public ArrayMemorySegment(byte[] array, int offset, int count)
        {
            _segment = new ArraySegment<byte>(array, offset, count);
        }

        /// <inheritdoc />
        public int Length => _segment.Count;

        /// <inheritdoc />
        public byte this[int index]
        {
            get
            {
                if (index < 0 || index >= _segment.Count)
                    throw new IndexOutOfRangeException($"Index {index} is outside the bounds of the segment with length {_segment.Count}");

                return _segment.Array[_segment.Offset + index];
            }
        }

        /// <inheritdoc />
        public ReadOnlySpan<byte> AsSpan() => _segment.AsSpan();

        /// <inheritdoc />
        public void CopyTo(Span<byte> destination)
        {
            if (destination.Length < _segment.Count)
                throw new ArgumentException($"Destination span is too small. Required {_segment.Count}, but got {destination.Length}", nameof(destination));

            _segment.AsSpan().CopyTo(destination);
        }

        /// <inheritdoc />
        public IMemorySegment Slice(int start, int length)
        {
            if (start < 0 || start > _segment.Count)
                throw new ArgumentOutOfRangeException(nameof(start), $"Start index {start} is outside the bounds of the segment with length {_segment.Count}");

            if (length < 0 || start + length > _segment.Count)
                throw new ArgumentOutOfRangeException(nameof(length), $"Length {length} is invalid for start index {start} in segment with length {_segment.Count}");

            // If we're slicing the entire segment, return this instance
            if (start == 0 && length == _segment.Count)
                return this;

            // Create a new segment with adjusted offset and count
            return new ArrayMemorySegment(_segment.Array, _segment.Offset + start, length);
        }

        /// <inheritdoc />
        public bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory)
        {
            memory = _segment;
            return true;
        }
    }
}