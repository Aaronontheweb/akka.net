//-----------------------------------------------------------------------
// <copyright file="IMemorySegment.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Akka.IO.Memory
{
    /// <summary>
    /// Represents a segment of memory that can be accessed in a read-only manner.
    /// This interface abstracts away the underlying memory source, which could be
    /// an array, Memory<byte>, ReadOnlyMemory<byte>, etc.
    /// </summary>
    internal interface IMemorySegment
    {
        /// <summary>
        /// Gets the length of the memory segment in bytes.
        /// </summary>
        int Length { get; }
        
        /// <summary>
        /// Gets the byte at the specified index in the memory segment.
        /// </summary>
        /// <param name="index">The zero-based index of the byte to get.</param>
        /// <returns>The byte at the specified index.</returns>
        /// <exception cref="IndexOutOfRangeException">Thrown when the index is out of range.</exception>
        byte this[int index] { get; }
        
        /// <summary>
        /// Returns the memory segment as a read-only span.
        /// </summary>
        /// <returns>A read-only span representing the memory segment.</returns>
        ReadOnlySpan<byte> AsSpan();
        
        /// <summary>
        /// Copies the contents of the memory segment to the specified destination span.
        /// </summary>
        /// <param name="destination">The destination span to copy to.</param>
        /// <exception cref="ArgumentException">Thrown when the destination span is too small.</exception>
        void CopyTo(Span<byte> destination);
        
        /// <summary>
        /// Creates a new memory segment that represents a slice of this memory segment.
        /// </summary>
        /// <param name="start">The index at which to begin the slice.</param>
        /// <param name="length">The length of the slice.</param>
        /// <returns>A new memory segment that represents a slice of this memory segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the start or length is invalid.</exception>
        IMemorySegment Slice(int start, int length);
        
        /// <summary>
        /// Attempts to get the underlying memory as a ReadOnlyMemory<byte>.
        /// </summary>
        /// <param name="memory">When this method returns, contains the ReadOnlyMemory<byte> if successful; otherwise, default.</param>
        /// <returns>true if the memory segment can be represented as a ReadOnlyMemory<byte>; otherwise, false.</returns>
        bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory);
    }
}