// Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
#nullable enable
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Akka.Actor;
using Akka.Actor.Setup;
using Akka.AOT.Shared;
using Akka.Configuration;
using Akka.Event;
using Akka.Serialization;
using Akka.Remote.Serialization;
using Akka.Streams.Serialization;
using AkkaSerialization = Akka.Serialization.Serialization;

var cases = 0;
var nativeWrites = 0;
var segmentedReads = 0;
var rejected = 0;
try
{
    Require(!RuntimeFeature.IsDynamicCodeSupported, "A native executable is required.");
    Require(AppContext.TryGetSwitch("Akka.DynamicTypeLoading", out var dynamicLoading) && !dynamicLoading,
        "Dynamic type loading must be explicitly disabled.");
    foreach (var mode in new[] { (Name: "off", Id: 30), (Name: "on", Id: 70), (Name: "pinned", Id: 30) })
    {
        var settings = $"akka.actor.serialization-v2 = {(mode.Name == "off" ? "off" : "on")}";
        if (mode.Name == "pinned")
            settings += """

                akka.actor.serialization-bindings {
                  "Akka.Streams.Implementation.StreamRef.IStreamRefsProtocol, Akka.Streams" = akka-stream-ref
                }
                """;
        var watchdog = new LogWatchdogFilter();
        var system = ActorSystem.Create("streams-native-" + mode.Name,
            ActorSystemSetup.Create(BootstrapSetup.Create().WithConfig(ConfigurationFactory.ParseString(settings)), new LogFilterSetup([watchdog])));
        try
        {
            // The public legacy codec bootstraps private protocol shapes from frozen bytes.
            // The executable uses its ordinary assembly identity and no reflective type access.
            var legacy = new StreamRefSerializer((ExtendedActorSystem)system);
            Require(legacy.Identifier == 30, "Legacy id changed.");
            Require(new PrimitiveSerializers((ExtendedActorSystem)system, ConfigurationFactory.ParseString("use-legacy-behavior = on")).Identifier == 17, "Primitive payload codec missing.");
            watchdog.ThrowIfAnyProblems(mode.Name, "startup");
            foreach (var fixture in FrozenFixtures.Cases)
            {
                var oldBytes = Convert.FromHexString(fixture.LegacyHex);
                var newBytes = Convert.FromHexString(fixture.V2Hex);
                var expected = system.Serialization.Deserialize(oldBytes, 30, fixture.Manifest);
                var actual = system.Serialization.Deserialize(newBytes, 70, fixture.Manifest);
                Compare(expected, actual, fixture.Manifest, legacy);
                var writer = system.Serialization.FindSerializerFor(expected);
                Require(writer.Identifier == mode.Id, "Incorrect selected writer: " + fixture.Name);
                Require(AkkaSerialization.ManifestFor(writer, expected) == fixture.Manifest, "Manifest changed.");
                var bytes = writer.ToBinary(expected);
                Compare(expected, system.Serialization.Deserialize(bytes, writer.Identifier, fixture.Manifest), fixture.Manifest, legacy);
                // Nobody canonically resolves to this system's deadLetters. All remaining
                // records preserve the exact reviewed bytes, including nested primitive manifests.
                if (fixture.Manifest != "G")
                    Require(bytes.AsSpan().SequenceEqual(mode.Id == 30 ? oldBytes : newBytes), "Frozen writer bytes differ.");
                if (mode.Id == 70)
                {
                    var modern = writer as SerializerV2 ?? throw new InvalidOperationException("Native writer expected.");
                    var buffer = new ArrayBufferWriter<byte>();
                    Require(modern.Serialize(expected, buffer) == bytes.Length && buffer.WrittenCount == bytes.Length, "Native size differs.");
                    Require(buffer.WrittenSpan.SequenceEqual(bytes), "Native bytes differ.");
                    Compare(expected, modern.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), fixture.Manifest), fixture.Manifest, legacy);
                    nativeWrites++;
                }
                for (var split = 0; split <= newBytes.Length; split++)
                    Compare(expected, system.Serialization.Deserialize(Split(newBytes, split), 70, fixture.Manifest), fixture.Manifest, legacy);
                segmentedReads++;
                cases++;
            }
            // The wire format of bare refs is unchanged; native reads still reject their
            // runtime generic reconstruction. Neither E nor F belongs to the new codec.
            var typeName = Encoding.UTF8.GetBytes("System.Int32, System.Private.CoreLib");
            var eventType = new byte[] { 0x0a, checked((byte)typeName.Length) }.Concat(typeName).ToArray();
            var bare = new byte[] { 0x0a, 0x00, 0x12, checked((byte)eventType.Length) }.Concat(eventType).ToArray();
            foreach (var manifest in new[] { "E", "F" })
            {
                Reject(() => system.Serialization.Deserialize(bare, 30, manifest), "Akka.DynamicTypeLoading");
                Reject(() => system.Serialization.Deserialize(new byte[] { 0x80 }, 70, manifest), null);
                rejected += 2;
            }
            watchdog.ThrowIfAnyProblems(mode.Name, "serialization");
            Console.WriteLine($"[streams-native] mode={mode.Name} writer={mode.Id} cases=12 rejected=4");
        }
        finally
        {
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
        watchdog.ThrowIfAnyProblems(mode.Name, "shutdown");
    }
    Require(cases == 36 && nativeWrites == 12 && segmentedReads == 36 && rejected == 12, "Incomplete corpus.");
    Console.WriteLine("[streams-native] OK cases=36 native-writes=12 segmented-reads=36 rejected=12");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("[streams-native] FAILED: " + error);
    return 1;
}

static void Compare(object expected, object actual, string manifest, StreamRefSerializer legacy)
{
    Require(expected.GetType() == actual.GetType(), "Protocol runtime class differs.");
    // Re-encode every member through the unchanged protobuf writer. No message Equals
    // implementation or internal-type reflection can hide a changed sequence/payload/ref.
    Require(legacy.Manifest(expected) == manifest && legacy.Manifest(actual) == manifest, "Runtime manifest differs.");
    Require(legacy.ToBinary(expected).AsSpan().SequenceEqual(legacy.ToBinary(actual)), "Protocol members differ.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action action, string? expectedMessage)
{
    try { action(); }
    catch (SerializationException error)
    {
        Require(expectedMessage is null || error.Message.Contains(expectedMessage, StringComparison.Ordinal), "Incorrect failure boundary: " + error.Message);
        return;
    }
    throw new InvalidOperationException("Expected serialization rejection.");
}

static ReadOnlySequence<byte> Split(byte[] bytes, int split)
{
    var first = new Segment(bytes.AsMemory(0, split));
    var last = first.Append(bytes.AsMemory(split));
    return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
}

sealed class Segment : ReadOnlySequenceSegment<byte>
{
    public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

    public Segment Append(ReadOnlyMemory<byte> nextMemory)
    {
        var next = new Segment(nextMemory) { RunningIndex = RunningIndex + Memory.Length };
        Next = next;
        return next;
    }
}
