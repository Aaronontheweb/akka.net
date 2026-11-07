// Copyright (C) 2013-2026 .NET Foundation <https://github.com/akkadotnet/akka.net>
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Akka.Actor;
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
                actual.TargetRef.Should().BeSameAs(expected.TargetRef);
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
}
