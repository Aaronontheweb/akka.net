//-----------------------------------------------------------------------
// <copyright file="EmptySegment.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;

namespace Akka.IO.Memory
{
    /// <summary>
    /// A singleton implementation of <see cref="IMemorySegment"/> that represents an empty memory segment.
    /// This is used to optimize the case where a ByteString is empty.
    /// </summary>
    internal sealed class EmptySegment : IMemorySegment
    {
        /// <summary>
        /// Gets the singleton instance of the <see cref="EmptySegment"/> class.
        /// </summary>
        public static readonly EmptySegment Instance = new EmptySegment();

        private EmptySegment() { }

        /// <inheritdoc />
        public int Length => 0;

        /// <inheritdoc />
        public byte this[int index] => throw new IndexOutOfRangeException("Cannot access elements in an empty segment.");

        /// <inheritdoc />
        public ReadOnlySpan<byte> AsSpan() => ReadOnlySpan<byte>.Empty;

        /// <inheritdoc />
        public void CopyTo(Span<byte> destination) { /* Nothing to copy */ }

        /// <inheritdoc />
        public IMemorySegment Slice(int start, int length)
        {
            if (start != 0 || length != 0)
                throw new ArgumentOutOfRangeException(nameof(start), "Cannot slice an empty segment with non-zero start or length.");

            return this;
        }

        /// <inheritdoc />
        public bool TryGetReadOnlyMemory(out ReadOnlyMemory<byte> memory)
        {
            memory = ReadOnlyMemory<byte>.Empty;
            return true;
        }
    }
}