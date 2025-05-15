//-----------------------------------------------------------------------
// <copyright file="MemorySegment.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Akka.IO.Memory
{
    /// <summary>
    /// An implementation of <see cref="IMemorySegment"/> that wraps a <see cref="ReadOnlyMemory{T}"/> of bytes.
    /// This provides efficient memory sharing and slicing without copying.
    /// </summary>
    public sealed class MemorySegment : IMemorySegment
    {
        private readonly ReadOnlyMemory<byte> _memory;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemorySegment"/> class.
        /// </summary>
        /// <param name="memory">The memory to wrap.</param>
        public MemorySegment(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MemorySegment"/> class.
        /// </summary>
        /// <param name="memory">The memory to wrap.</param>
        public MemorySegment(Memory<byte> memory)
        {
            _memory = memory;
        }

        /// <inheritdoc />
        public int Length => _memory.Length;

        /// <inheritdoc />
        public byte this[int index]
        {
            get
            {
                if (index < 0 || index >= _memory.Length)
                    throw new IndexOutOfRangeException($"Index {index} is outside the bounds of the segment with length {_memory.Length}");

                return _memory.Span[index];
            }
        }

        /// <inheritdoc />
        public byte[] ToArray() => _memory.ToArray();

        /// <inheritdoc />
        public void CopyTo(byte[] destination, int destinationIndex)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
                
            if (destinationIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(destinationIndex), "Index cannot be negative");
                
            if (destination.Length - destinationIndex < _memory.Length)
                throw new ArgumentException($"Destination array is too small. Required {_memory.Length}, but got {destination.Length - destinationIndex}", nameof(destination));

            var array = _memory.ToArray();
            Array.Copy(array, 0, destination, destinationIndex, _memory.Length);
        }

        /// <inheritdoc />
        public IMemorySegment Slice(int start, int length)
        {
            if (start < 0 || start > _memory.Length)
                throw new ArgumentOutOfRangeException(nameof(start), $"Start index {start} is outside the bounds of the segment with length {_memory.Length}");

            if (length < 0 || start + length > _memory.Length)
                throw new ArgumentOutOfRangeException(nameof(length), $"Length {length} is invalid for start index {start} in segment with length {_memory.Length}");

            // If we're slicing the entire segment, return this instance
            if (start == 0 && length == _memory.Length)
                return this;

            // Create a new segment with sliced memory
            return new MemorySegment(_memory.Slice(start, length));
        }

        /// <inheritdoc />
        public bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory)
        {
            memory = _memory;
            return true;
        }
    }
}