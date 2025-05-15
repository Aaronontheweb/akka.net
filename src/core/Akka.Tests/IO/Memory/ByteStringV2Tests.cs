//-----------------------------------------------------------------------
// <copyright file="ByteStringV2Tests.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Buffers;
using System.Linq;
using System.Text;
using Akka.IO.Memory;
using FluentAssertions;
using Xunit;

namespace Akka.Tests.IO.Memory
{
    public class ByteStringV2Tests
    {
        [Fact]
        public void ByteStringV2_Empty_should_have_zero_length()
        {
            ByteStringV2.Empty.Count.Should().Be(0);
            ByteStringV2.Empty.IsEmpty.Should().BeTrue();
            ByteStringV2.Empty.IsCompact.Should().BeTrue();
        }

        [Fact]
        public void ByteStringV2_CopyFrom_should_create_copy_of_array()
        {
            var original = new byte[] { 1, 2, 3, 4, 5 };
            var bs = ByteStringV2.CopyFrom(original);

            bs.Count.Should().Be(5);
            bs.IsEmpty.Should().BeFalse();
            bs.IsCompact.Should().BeTrue();

            // Verify it's a copy
            original[0] = 99;
            bs[0].Should().Be(1); // Original array modification shouldn't affect ByteStringV2
        }

        [Fact]
        public void ByteStringV2_FromBytes_should_reference_same_array()
        {
            var original = new byte[] { 1, 2, 3, 4, 5 };
            var bs = ByteStringV2.FromBytes(original);

            bs.Count.Should().Be(5);
            bs.IsEmpty.Should().BeFalse();
            bs.IsCompact.Should().BeTrue();

            // Verify it's the same instance
            original[0] = 99;
            bs[0].Should().Be(99); // Original array modification should affect ByteStringV2
        }

        [Fact]
        public void ByteStringV2_FromMemory_should_reference_same_memory()
        {
            var original = new byte[] { 1, 2, 3, 4, 5 };
            var memory = new Memory<byte>(original);
            var bs = ByteStringV2.FromMemory(memory);

            bs.Count.Should().Be(5);
            bs.IsEmpty.Should().BeFalse();
            bs.IsCompact.Should().BeTrue();

            // Verify it's the same memory
            original[0] = 99;
            bs[0].Should().Be(99); // Original memory modification should affect ByteStringV2
        }

        [Fact]
        public void ByteStringV2_Concat_should_combine_bytestrings_without_copying()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var bs3 = ByteStringV2.FromBytes(new byte[] { 7, 8, 9 });

            var combined = bs1.Concat(bs2).Concat(bs3);

            combined.Count.Should().Be(9);
            combined.IsCompact.Should().BeFalse(); // Multiple segments

            // Verify content
            var array = combined.ToArray();
            array.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });

            // Use indexer
            for (int i = 0; i < 9; i++)
            {
                combined[i].Should().Be((byte)(i + 1));
            }
        }

        [Fact]
        public void ByteStringV2_Slice_should_create_view_without_copying()
        {
            var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
            var bs = ByteStringV2.FromBytes(original);

            var slice = bs.Slice(3, 4); // Elements 4, 5, 6, 7

            slice.Count.Should().Be(4);
            slice.IsCompact.Should().BeTrue();

            // Verify slice content
            slice[0].Should().Be(4);
            slice[1].Should().Be(5);
            slice[2].Should().Be(6);
            slice[3].Should().Be(7);

            // Verify original array modification affects slice
            original[3] = 99; // Change 4 to 99
            slice[0].Should().Be(99);
        }

        [Fact]
        public void ByteStringV2_Slice_should_work_with_multipart_strings()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var bs3 = ByteStringV2.FromBytes(new byte[] { 7, 8, 9 });

            var combined = bs1.Concat(bs2).Concat(bs3);
            var slice = combined.Slice(2, 5); // Elements 3, 4, 5, 6, 7

            slice.Count.Should().Be(5);
            slice.IsCompact.Should().BeFalse(); // Should still be multi-segment

            // Verify slice content
            slice[0].Should().Be(3);
            slice[1].Should().Be(4);
            slice[2].Should().Be(5);
            slice[3].Should().Be(6);
            slice[4].Should().Be(7);
        }

        [Fact]
        public void ByteStringV2_Compact_should_create_single_segment_copy()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var multipart = bs1.Concat(bs2);

            multipart.IsCompact.Should().BeFalse();

            var compacted = multipart.Compact();

            compacted.IsCompact.Should().BeTrue();
            compacted.Count.Should().Be(6);

            // Verify content
            compacted.ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4, 5, 6 });
        }

        [Fact]
        public void ByteStringV2_AsReadOnlySequence_should_expose_segments()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var multipart = bs1.Concat(bs2);

            var sequence = multipart.AsReadOnlySequence();

            sequence.Length.Should().Be(6);
            
            // Check we can read correct values from the sequence
            var position = sequence.Start;
            for (int i = 1; i <= 6; i++)
            {
                sequence.TryGet(ref position, out var memory).Should().BeTrue();
                if (i <= 3)
                    memory.Span[0].Should().Be((byte)i);
                else
                    memory.Span[i - 4].Should().Be((byte)i);
            }
        }

        [Fact]
        public void ByteStringV2_TryGetSingleMemory_should_work_for_compact_strings()
        {
            var bs = ByteStringV2.FromBytes(new byte[] { 1, 2, 3, 4, 5 });

            bs.TryGetSingleMemory(out var memory).Should().BeTrue();
            memory.Length.Should().Be(5);
            memory.Span[0].Should().Be(1);
            memory.Span[4].Should().Be(5);
        }

        [Fact]
        public void ByteStringV2_TryGetSingleMemory_should_return_false_for_multipart_strings()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var multipart = bs1.Concat(bs2);

            multipart.TryGetSingleMemory(out var memory).Should().BeFalse();
        }
        
        [Fact]
        public void ByteStringV2_TryGetReadOnlySpan_should_work_for_compact_strings()
        {
            var bs = ByteStringV2.FromBytes(new byte[] { 1, 2, 3, 4, 5 });

            bs.TryGetReadOnlySpan(out var span).Should().BeTrue();
            span.Length.Should().Be(5);
            span[0].Should().Be(1);
            span[4].Should().Be(5);
        }

        [Fact]
        public void ByteStringV2_TryGetReadOnlySpan_should_return_false_for_multipart_strings()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var multipart = bs1.Concat(bs2);

            multipart.TryGetReadOnlySpan(out var span).Should().BeFalse();
        }
        
        [Fact]
        public void ByteStringV2_FromMemory_should_not_copy_data()
        {
            var original = new byte[] { 1, 2, 3, 4, 5 };
            var memory = new Memory<byte>(original);
            var bs = ByteStringV2.FromMemory(memory);

            bs.Count.Should().Be(5);
            bs.IsEmpty.Should().BeFalse();
            bs.IsCompact.Should().BeTrue();
            
            // Check that it truly shares memory
            original[0] = 99;
            bs[0].Should().Be(99);
            
            // Check we can efficiently get back the memory without copying
            bs.TryGetReadOnlySpan(out var span).Should().BeTrue();
            span[0].Should().Be(99);
        }
        
        [Fact]
        public void ByteStringV2_FromSpan_should_copy_data()
        {
            var original = new byte[] { 1, 2, 3, 4, 5 };
            var span = new Span<byte>(original);
            var bs = ByteStringV2.FromSpan(span);

            bs.Count.Should().Be(5);
            bs.IsEmpty.Should().BeFalse();
            bs.IsCompact.Should().BeTrue();
            
            // Verify it's a copy
            original[0] = 99;
            bs[0].Should().Be(1); // Original array modification shouldn't affect ByteStringV2
        }
        
        [Fact]
        public void ByteStringV2_AsReadOnlySequence_should_enable_enumeration_of_segments()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3 });
            var bs2 = ByteStringV2.FromBytes(new byte[] { 4, 5, 6 });
            var multipart = bs1.Concat(bs2);
            
            var sequence = multipart.AsReadOnlySequence();
            sequence.Length.Should().Be(6);
            
            // Can enumerate all the segments
            var position = sequence.Start;
            int count = 0;
            while (sequence.TryGet(ref position, out var memory))
            {
                count++;
                if (!sequence.End.Equals(position))
                    sequence.GetPosition(0, position);
            }
            
            // There should be exactly 2 segments
            count.Should().Be(2);
        }

        [Fact]
        public void ByteStringV2_FromString_should_create_utf8_bytesting()
        {
            var text = "Hello, world!";
            var expected = Encoding.UTF8.GetBytes(text);

            var bs = ByteStringV2.FromString(text);

            bs.Count.Should().Be(expected.Length);
            bs.ToArray().Should().BeEquivalentTo(expected);
            bs.ToString().Should().Be(text);
        }

        [Fact]
        public void ByteStringV2_can_handle_empty_operations()
        {
            var empty = ByteStringV2.Empty;
            
            // Test empty operations
            empty.Slice(0, 0).Should().BeSameAs(ByteStringV2.Empty);
            empty.Compact().Should().BeSameAs(ByteStringV2.Empty);
            empty.Concat(ByteStringV2.Empty).Should().BeSameAs(ByteStringV2.Empty);
            empty.ToArray().Should().BeEmpty();
            empty.ToString().Should().Be(string.Empty);
            empty.TryGetSingleMemory(out var memory).Should().BeTrue();
            memory.IsEmpty.Should().BeTrue();
        }

        [Fact]
        public void ByteStringV2_Equals_should_compare_content_not_structure()
        {
            var bs1 = ByteStringV2.FromBytes(new byte[] { 1, 2, 3, 4, 5 });
            var bs2 = ByteStringV2.CopyFrom(new byte[] { 1, 2, 3, 4, 5 });
            var bs3 = ByteStringV2.FromBytes(new byte[] { 1, 2 }).Concat(ByteStringV2.FromBytes(new byte[] { 3, 4, 5 }));
            var bs4 = ByteStringV2.FromBytes(new byte[] { 9, 8, 7, 6, 5 });

            // Same content, different instances
            bs1.Equals(bs2).Should().BeTrue();
            (bs1 == bs2).Should().BeTrue();

            // Same content, different structure
            bs1.Equals(bs3).Should().BeTrue();
            (bs1 == bs3).Should().BeTrue();

            // Different content
            bs1.Equals(bs4).Should().BeFalse();
            (bs1 == bs4).Should().BeFalse();
            (bs1 != bs4).Should().BeTrue();

            // Compare with null
            bs1.Equals(null).Should().BeFalse();
            (bs1 == null).Should().BeFalse();
            (null == bs1).Should().BeFalse();
            (bs1 != null).Should().BeTrue();
            (null != bs1).Should().BeTrue();
        }
    }
}