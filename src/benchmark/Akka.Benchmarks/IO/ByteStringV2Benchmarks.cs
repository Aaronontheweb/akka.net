//-----------------------------------------------------------------------
// <copyright file="ByteStringV2Benchmarks.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using Akka.Benchmarks.Configurations;
using Akka.IO;
using Akka.IO.Memory;
using BenchmarkDotNet.Attributes;
using static Akka.Benchmarks.Configurations.BenchmarkCategories;
using System;
using System.Collections.Generic;

namespace Akka.Benchmarks
{
    [Config(typeof(MicroBenchmarkConfig))]
    public class ByteStringV2Benchmarks
    {
        [Params(10, 100, 1000)]
        public int PayloadSize;
        
        private byte[] _bytes;
        private string _str;

        // Original ByteString instances
        private ByteString _original_multipart;
        private ByteString _original_compact;
        
        // New ByteStringV2 instances
        private ByteStringV2 _v2_multipart;
        private ByteStringV2 _v2_compact;

        [GlobalSetup]
        public void Setup()
        {
            _bytes = new byte[PayloadSize];
            _str = new string('x', PayloadSize);

            // Setup original ByteString test data
            _original_multipart = ByteString.Empty;
            byte acc = 1;
            for (int i = 0; i < 10; i++)
            {
                var array = new byte[i];
                for (int j = 0; j < i; j++)
                {
                    array[j] = (byte)(acc++ % byte.MaxValue);
                }
                _original_multipart += ByteString.FromBytes(array);
            }
            _original_compact = _original_multipart.Compact();
            
            // Setup ByteStringV2 test data
            _v2_multipart = ByteStringV2.Empty;
            acc = 1;
            for (int i = 0; i < 10; i++)
            {
                var array = new byte[i];
                for (int j = 0; j < i; j++)
                {
                    array[j] = (byte)(acc++ % byte.MaxValue);
                }
                _v2_multipart += ByteStringV2.FromBytes(array);
            }
            _v2_compact = _v2_multipart.Compact();
        }

        // Original ByteString benchmarks for baseline comparison
        
        [Benchmark(Baseline = true)]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_create_unsafe()
        {
            return ByteString.FromBytes(_bytes);
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_create_copying()
        {
            return ByteString.CopyFrom(_bytes);
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_create_from_string()
        {
            return ByteString.FromString(_str);
        }

        [Benchmark]
        [Arguments(10)]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_concatenation(int times)
        {
            var acc = ByteString.Empty;
            for (int i = 0; i < times; i++)
            {
                acc += ByteString.FromBytes(_bytes);
            }

            return acc;
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_multipart_slice()
        {
            return _original_multipart.Slice(10, 40);
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteString Original_multipart_compact()
        {
            return _original_multipart.Compact();
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public bool Original_multipart_has_substring()
        {
            byte[] array = { 6, 7, 8, 9 };
            return _original_multipart.HasSubstring(ByteString.FromBytes(array), 0);
        }

        // New ByteStringV2 benchmarks for comparison
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_create_unsafe()
        {
            return ByteStringV2.FromBytes(_bytes);
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_create_copying()
        {
            return ByteStringV2.CopyFrom(_bytes);
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_create_from_string()
        {
            return ByteStringV2.FromString(_str);
        }

        [Benchmark]
        [Arguments(10)]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_concatenation(int times)
        {
            var acc = ByteStringV2.Empty;
            for (int i = 0; i < times; i++)
            {
                acc += ByteStringV2.FromBytes(_bytes);
            }

            return acc;
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_multipart_slice()
        {
            return _v2_multipart.Slice(10, 40);
        }

        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_multipart_compact()
        {
            return _v2_multipart.Compact();
        }

        // New ByteStringV2 specific benchmarks
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public bool V2_try_get_single_memory()
        {
            return _v2_compact.TryGetSingleMemory(out _);
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public System.Buffers.ReadOnlySequence<byte> V2_as_readonly_sequence()
        {
            return _v2_multipart.AsReadOnlySequence();
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public bool V2_try_get_readonly_span()
        {
            return _v2_compact.TryGetReadOnlySpan(out _);
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ByteStringV2 V2_from_memory()
        {
            return ByteStringV2.FromMemory(new ReadOnlyMemory<byte>(_bytes));
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public ReadOnlySpan<byte> V2_to_readonly_span()
        {
            return _v2_compact.ToReadOnlySpan();
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public byte[] V2_to_array()
        {
            return _v2_multipart.ToArray();
        }
        
        [Benchmark]
        [BenchmarkCategory(MicroBenchmark, AkkaIOBenchmark)]
        public string V2_to_string()
        {
            return _v2_multipart.ToString();
        }
    }
}