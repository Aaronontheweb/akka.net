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
    /// an array, Memory&lt;byte&gt;, ReadOnlyMemory&lt;byte&gt;, etc.
    /// </summary>
    public interface IMemorySegment
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
        /// Returns the memory segment content as a new array.
        /// </summary>
        /// <returns>A new array containing the segment data.</returns>
        byte[] ToArray();
        
        /// <summary>
        /// Copies the contents of the memory segment to the specified destination byte array.
        /// </summary>
        /// <param name="destination">The destination byte array to copy to.</param>
        /// <param name="destinationIndex">The index in the destination array at which to start copying.</param>
        /// <exception cref="ArgumentException">Thrown when the destination array is too small.</exception>
        void CopyTo(byte[] destination, int destinationIndex);
        
        /// <summary>
        /// Creates a new memory segment that represents a slice of this memory segment.
        /// </summary>
        /// <param name="start">The index at which to begin the slice.</param>
        /// <param name="length">The length of the slice.</param>
        /// <returns>A new memory segment that represents a slice of this memory segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the start or length is invalid.</exception>
        IMemorySegment Slice(int start, int length);
        
        /// <summary>
        /// Attempts to get the underlying memory as a ReadOnlyMemory&lt;byte&gt;.
        /// </summary>
        /// <param name="memory">When this method returns, contains the ReadOnlyMemory&lt;byte&gt; if successful; otherwise, default.</param>
        /// <returns>true if the memory segment can be represented as a ReadOnlyMemory&lt;byte&gt;; otherwise, false.</returns>
        bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory);
    }
}