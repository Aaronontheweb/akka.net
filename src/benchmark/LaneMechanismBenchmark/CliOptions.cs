//-----------------------------------------------------------------------
// <copyright file="CliOptions.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Globalization;

namespace LaneMechanismBenchmark
{
    internal enum Mechanism
    {
        Baseline,
        Channels,
        PartitionHub
    }

    internal sealed class CliOptions
    {
        public Mechanism Mechanism { get; private init; }
        public int Lanes { get; private init; } = 4;
        public int Messages { get; private init; } = 100_000;
        public int Iterations { get; private init; } = 5;
        public int WarmupIterations { get; private init; } = 1;
        public int Recipients { get; private init; } = 32;
        public int ChannelCapacity { get; private init; } = 4096;
        public int HubBuffer { get; private init; } = 4096;
        public int Seed { get; private init; } = 42;

        public static CliOptions? Parse(string[] args)
        {
            Mechanism? mechanism = null;
            var lanes = 4;
            var messages = 100_000;
            var iterations = 5;
            var warmup = 1;
            var recipients = 32;
            var channelCapacity = 4096;
            var hubBuffer = 4096;
            var seed = 42;

            try
            {
                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--mechanism":
                        {
                            var value = RequireValue(args, ref i, "--mechanism");
                            mechanism = ParseMechanism(value);
                            if (mechanism is null)
                            {
                                Console.Error.WriteLine($"Unrecognized --mechanism value: {value} (expected baseline|channels|partitionhub).");
                                return null;
                            }
                            break;
                        }
                        case "--lanes":
                            lanes = ParseInt(args, ref i, "--lanes");
                            break;
                        case "--messages":
                            messages = ParseInt(args, ref i, "--messages");
                            break;
                        case "--iterations":
                            iterations = ParseInt(args, ref i, "--iterations");
                            break;
                        case "--warmup":
                            warmup = ParseInt(args, ref i, "--warmup");
                            break;
                        case "--recipients":
                            recipients = ParseInt(args, ref i, "--recipients");
                            break;
                        case "--channel-capacity":
                            channelCapacity = ParseInt(args, ref i, "--channel-capacity");
                            break;
                        case "--hub-buffer":
                            hubBuffer = ParseInt(args, ref i, "--hub-buffer");
                            break;
                        case "--seed":
                            seed = ParseInt(args, ref i, "--seed");
                            break;
                        case "--help":
                        case "-h":
                            return null;
                        default:
                            Console.Error.WriteLine($"Unrecognized argument: {args[i]}");
                            return null;
                    }
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return null;
            }

            if (mechanism is null)
            {
                Console.Error.WriteLine("--mechanism is required (baseline|channels|partitionhub).");
                return null;
            }

            if (lanes <= 0 || messages <= 0 || iterations <= 0 || warmup < 0 || recipients <= 0
                || channelCapacity <= 0 || hubBuffer <= 0)
            {
                Console.Error.WriteLine("--lanes/--messages/--iterations/--recipients/--channel-capacity/--hub-buffer must be positive (--warmup may be 0).");
                return null;
            }

            return new CliOptions
            {
                Mechanism = mechanism.Value,
                Lanes = lanes,
                Messages = messages,
                Iterations = iterations,
                WarmupIterations = warmup,
                Recipients = recipients,
                ChannelCapacity = channelCapacity,
                HubBuffer = hubBuffer,
                Seed = seed
            };
        }

        private static string RequireValue(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"{flag} requires a value.");

            i++;
            return args[i];
        }

        private static int ParseInt(string[] args, ref int i, string flag)
        {
            var value = RequireValue(args, ref i, flag);
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException($"{flag} expects an integer, got '{value}'.");

            return parsed;
        }

        private static Mechanism? ParseMechanism(string value) => value.ToLowerInvariant() switch
        {
            "baseline" => Mechanism.Baseline,
            "channels" => Mechanism.Channels,
            "partitionhub" => Mechanism.PartitionHub,
            _ => null
        };

        public static void PrintUsage()
        {
            Console.WriteLine("""
                LaneMechanismBenchmark --mechanism <baseline|channels|partitionhub> [options]

                  Compares three inbound-lane fan-out mechanisms on the REAL Artery envelope codec +
                  real Akka serialization (G5-entry re-baseline; see
                  openspec/changes/artery-tcp-remoting/design.md).

                  --mechanism <baseline|channels|partitionhub>  (required)
                  --lanes <N>              lane count (default 4)
                  --messages <M>           corpus size (default 100000)
                  --iterations <K>         measured iterations (default 5)
                  --warmup <W>             warmup iterations, run and printed but excluded from the summary (default 1)
                  --recipients <R>         distinct recipient paths in the corpus (default 32)
                  --channel-capacity <N>   bounded Channel capacity per lane; channels mechanism only (default 4096)
                  --hub-buffer <N>         PartitionHub buffer size; partitionhub mechanism only (default 4096)
                  --seed <S>               corpus RNG seed, reserved for future non-uniform corpora (default 42)
                """);
        }
    }
}
