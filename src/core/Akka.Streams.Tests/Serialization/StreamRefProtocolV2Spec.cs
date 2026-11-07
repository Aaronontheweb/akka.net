// Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
#nullable enable
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System;
using Akka.Actor;
using Akka.Actor.Setup;
using Akka.Configuration;
using Akka.Serialization;
using Akka.Streams.Implementation.StreamRef;
using Akka.Streams.Serialization;
using Akka.TestKit;
using FluentAssertions;
using Xunit;

namespace Akka.Streams.Tests.Serialization;

[Collection(DynamicTypeLoadingCollection.Name)]
public sealed class StreamRefProtocolV2Spec : V2PortSpec
{
    public StreamRefProtocolV2Spec(ITestOutputHelper output) : base(output, ActorMaterializer.DefaultConfig())
    {
        var cases = new List<V2PortCase>
        {
            new(new SequencedOnNext(0, "payload"), "A", "next-string"),
            new(new SequencedOnNext(long.MaxValue, ""), "A", "next-empty-string"),
            new(new SequencedOnNext(3, new byte[] { 0, 1, 127, 255 }), "A", "next-bytes"),
            new(new SequencedOnNext(4, 17), "A", "next-int"),
            new(new CumulativeDemand(1), "B", "demand"),
            new(new CumulativeDemand(long.MaxValue), "B", "demand-max"),
            new(new RemoteStreamFailure("failed \u2603"), "C", "failure"),
            new(new RemoteStreamFailure(""), "C", "failure-empty"),
            new(new RemoteStreamCompleted(0), "D", "completed"),
            new(new RemoteStreamCompleted(long.MaxValue), "D", "completed-max")
        };
        foreach (var (target, name) in new[] { (ActorRefs.Nobody, "handshake"), (ActorRefs.NoSender, "handshake-empty") })
        {
            cases.Add(new V2PortCase(new OnSubscribeHandshake(target), "G", name)
            {
                AssertEquivalent = (expected, actual) =>
                {
                    var legacy = CreateLegacy((ExtendedActorSystem)Sys);
                    var canonical = (OnSubscribeHandshake)legacy.FromBinary(legacy.ToBinary(expected), "G");
                    ((OnSubscribeHandshake)actual).TargetRef.Path.Should().Be(canonical.TargetRef.Path);
                }
            });
        }
        Cases = cases;
    }

    protected override Serializer CreateLegacy(ExtendedActorSystem system) => new Akka.Streams.Serialization.StreamRefSerializer(system);
    protected override SerializerV2 CreateV2(ExtendedActorSystem system) => new StreamRefProtocolMessagePackSerializer(system);
    protected override IReadOnlyList<V2PortCase> Cases { get; }

    [Fact(DisplayName = "Should_ExchangeBothProtocolFormats_When_OnlyOnePeerEnablesV2")]
    public async Task Should_ExchangeBothProtocolFormats_When_OnlyOnePeerEnablesV2()
    {
        await using var pair = MixedBindingPair.Create("stream-protocol-mixed",
            ConfigurationFactory.ParseString("akka.actor.serialization-v2 = on").WithFallback(ActorMaterializer.DefaultConfig()),
            ActorMaterializer.DefaultConfig());
        AssertMixed(pair);
    }

    [Fact(DisplayName = "Should_PreserveTheWholeProtocolPin_When_V2IsEnabled")]
    public async Task Should_PreserveTheWholeProtocolPin_When_V2IsEnabled()
    {
        var enabled = ConfigurationFactory.ParseString("akka.actor.serialization-v2 = on").WithFallback(ActorMaterializer.DefaultConfig());
        await using var pair = MixedBindingPair.Create("stream-protocol-pinned", enabled,
            MixedBindingPair.BindTo("akka-stream-ref", typeof(IStreamRefsProtocol)).WithFallback(enabled));
        AssertMixed(pair);
    }

    private void AssertMixed(MixedBindingPair pair)
    {
        pair.AssertRoundTripsBothWays(Cases.Where(sample => sample.Manifest != "G"), 70, 30);
        foreach (var (writer, reader, id) in new[] { (pair.V2Node, pair.LegacyNode, 70), (pair.LegacyNode, pair.V2Node, 30) })
        {
            // Nobody's legacy path and NoSender canonically return the receiver's
            // deadLetters. Assert that receiving-system identity instead of the spec's system.
            foreach (var target in new[] { ActorRefs.Nobody, ActorRefs.NoSender })
            {
                var message = new OnSubscribeHandshake(target);
                var serializer = writer.Serialization.FindSerializerFor(message);
                serializer.Identifier.Should().Be(id);
                var received = (OnSubscribeHandshake)reader.Serialization.Deserialize(serializer.ToBinary(message), id, "G");
                received.TargetRef.Should().BeSameAs(reader.DeadLetters);
            }
        }
    }

    [Theory(DisplayName = "Should_KeepBareGenericRefsOnLegacy_When_TheGlobalSwitchChanges")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_KeepBareGenericRefsOnLegacy_When_TheGlobalSwitchChanges(bool enabled)
    {
        var config = ConfigurationFactory.ParseString($"akka.actor.serialization-v2 = {(enabled ? "on" : "off")}")
            .WithFallback(ActorMaterializer.DefaultConfig());
        var system = ActorSystem.Create("stream-bare-refs", config);
        InitializeLogger(system);
        try
        {
            foreach (var reference in new object[] { new SourceRefImpl<string>(system.DeadLetters), new SinkRefImpl<string>(system.DeadLetters) })
                system.Serialization.FindSerializerFor(reference).Identifier.Should().Be(30);
            system.Serialization.FindSerializerFor(new CumulativeDemand(1)).Identifier.Should().Be(enabled ? 70 : 30);
        }
        finally
        {
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory(DisplayName = "Should_PreserveProviderSpecificHandshakeRefs_When_TheTargetIsEmptyOrUnresolved")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_PreserveProviderSpecificHandshakeRefs_When_TheTargetIsEmptyOrUnresolved(bool remote)
    {
        var config = remote ? ConfigurationFactory.ParseString("""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.dot-netty.tcp { hostname = "127.0.0.1", port = 0 }
            """) : Config.Empty;
        var system = ActorSystem.Create($"stream-handshake-{remote}", config.WithFallback(ActorMaterializer.DefaultConfig()));
        InitializeLogger(system);
        try
        {
            var actor = system.ActorOf(Props.Empty, "target");
            var legacy = CreateLegacy((ExtendedActorSystem)system);
            var modern = CreateV2((ExtendedActorSystem)system);
            foreach (var target in new[] { ActorRefs.NoSender, ActorRefs.Nobody, system.DeadLetters, actor })
            {
                var message = new OnSubscribeHandshake(target);
                var expected = (OnSubscribeHandshake)legacy.FromBinary(legacy.ToBinary(message), "G");
                var actual = (OnSubscribeHandshake)modern.FromBinary(modern.ToBinary(message), "G");
                if (expected.TargetRef is null)
                {
                    actual.TargetRef.Should().BeNull();
                }
                else
                {
                    actual.TargetRef.Should().NotBeNull();
                    actual.TargetRef.GetType().Should().Be(expected.TargetRef.GetType());
                    actual.TargetRef.Path.ToSerializationFormat().Should().Be(expected.TargetRef.Path.ToSerializationFormat());
                    // Resolving an unknown serialized path can allocate an EmptyLocalActorRef on
                    // each read. Preserve identity only where the legacy reader returns an existing ref.
                    if (ReferenceEquals(expected.TargetRef, actor) || ReferenceEquals(expected.TargetRef, system.DeadLetters))
                        actual.TargetRef.Should().BeSameAs(expected.TargetRef);
                }
            }
        }
        finally
        {
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "Should_UseIndependentUserCodecs_When_TheProtocolWrapsV1AndV2Payloads")]
    public async Task Should_UseIndependentUserCodecs_When_TheProtocolWrapsV1AndV2Payloads()
    {
        var setup = ActorSystemSetup.Create(
            BootstrapSetup.Create().WithConfig(ConfigurationFactory.ParseString("akka.actor.serialization-v2 = on")
                .WithFallback(ActorMaterializer.DefaultConfig())),
            SerializationSetup.Create(owner => ImmutableHashSet.Create(
                SerializerDetails.Create("stream-user-v1", new UserV1Serializer(owner), ImmutableHashSet.Create(typeof(UserV1))),
                SerializerDetails.Create("stream-user-v2", new UserV2Serializer(owner), ImmutableHashSet.Create(typeof(UserV2))))));
        var system = ActorSystem.Create("stream-user-codecs", setup);
        InitializeLogger(system);
        try
        {
            var legacy = CreateLegacy((ExtendedActorSystem)system);
            var modern = CreateV2((ExtendedActorSystem)system);
            foreach (var payload in new object[] { new UserV1("one"), new UserV2("two") })
            {
                var message = new SequencedOnNext(42, payload);
                system.Serialization.FindSerializerFor(message).Identifier.Should().Be(70);
                system.Serialization.FindSerializerFor(payload).Identifier.Should().Be(payload is UserV1 ? 70701 : 70702);
                foreach (var serializer in new[] { legacy, modern })
                {
                    var bytes = serializer.ToBinary(message);
                    var restored = (SequencedOnNext)system.Serialization.Deserialize(bytes, serializer.Identifier, "A");
                    restored.SeqNr.Should().Be(42);
                    restored.Payload.Should().BeOfType(payload.GetType()).And.Be(payload);
                }
                var buffer = new ArrayBufferWriter<byte>();
                modern.Serialize(message, buffer).Should().Be(buffer.WrittenCount);
                var direct = (SequencedOnNext)modern.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), "A");
                direct.Payload.Should().BeOfType(payload.GetType()).And.Be(payload);
                direct.SeqNr.Should().Be(42);
            }
        }
        finally
        {
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact(DisplayName = "Should_RejectBareRefsAndUnknownTokens_When_TheProtocolCodecIsUsedDirectly")]
    public void Should_RejectBareRefsAndUnknownTokens_When_TheProtocolCodecIsUsedDirectly()
    {
        var serializer = CreateV2((ExtendedActorSystem)Sys);
        Action source = () => serializer.ToBinary(new SourceRefImpl<string>(Sys.DeadLetters));
        Action sink = () => serializer.ToBinary(new SinkRefImpl<string>(Sys.DeadLetters));
        Action unknown = () => serializer.FromBinary(new byte[] { 0x80 }, "E");
        source.Should().Throw<ArgumentException>();
        sink.Should().Throw<ArgumentException>();
        unknown.Should().Throw<SerializationException>();
    }

    private sealed record UserV1(string Value);
    private sealed record UserV2(string Value);

    private sealed class UserV1Serializer(ExtendedActorSystem owner) : SerializerWithStringManifest(owner)
    {
        public override int Identifier => 70701;
        public override string Manifest(object obj) => "u1";
        public override byte[] ToBinary(object obj) => Encoding.UTF8.GetBytes(((UserV1)obj).Value);
        public override object FromBinary(byte[] bytes, string manifest) => new UserV1(Encoding.UTF8.GetString(bytes));
    }

    private sealed class UserV2Serializer(ExtendedActorSystem owner) : SerializerV2(owner)
    {
        public override int Identifier => 70702;
        public override string Manifest(object obj) => "u2";
        public override int Serialize(object obj, IBufferWriter<byte> writer)
        {
            var bytes = Encoding.UTF8.GetBytes(((UserV2)obj).Value);
            writer.Write(bytes);
            return bytes.Length;
        }
        public override object Deserialize(ReadOnlySequence<byte> bytes, string manifest) => new UserV2(Encoding.UTF8.GetString(bytes.ToArray()));
    }

}
