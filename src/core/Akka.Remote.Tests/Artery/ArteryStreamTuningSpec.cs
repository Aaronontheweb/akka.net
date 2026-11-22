//-----------------------------------------------------------------------
// <copyright file="ArteryStreamTuningSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Artery;
using Akka.Remote.Configuration;
using Akka.Streams;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Covers <c>akka.remote.artery.advanced.outbound-hub-buffer-size</c>,
    /// <c>advanced.stream-input-buffer-size</c> and <c>advanced.stream-sync-processing-limit</c>:
    /// defaults preserve the previous values, overrides reach Artery's materializer, and per-recipient
    /// ordering plus clean shutdown hold at a small and a large setting.
    /// </summary>
    public sealed class ArteryStreamTuningSpec : AkkaSpec
    {
        public ArteryStreamTuningSpec(ITestOutputHelper output) : base(output)
        {
        }

        private static ArterySettings SettingsFrom(string hocon) =>
            new(ConfigurationFactory.ParseString(hocon)
                .WithFallback(RemoteConfigFactory.Default())
                .GetConfig("akka.remote.artery"));

        [Fact(DisplayName = "Should_KeepPreviousValues_When_StreamTuningKeysNotOverridden")]
        public void Should_KeepPreviousValues_When_StreamTuningKeysNotOverridden()
        {
            var settings = SettingsFrom(string.Empty);
            settings.OutboundHubBufferSize.Should().Be(16);
            settings.StreamInputBufferSize.Should().Be(0);
            settings.StreamSyncProcessingLimit.Should().Be(0);

            var baseSettings = ActorMaterializerSettings.Create(Sys);
            ArteryRemoting.ApplyStreamOverrides(baseSettings, settings).Should().BeSameAs(baseSettings);
        }

        [Fact(DisplayName = "Should_ApplyStreamOverrides_When_StreamTuningKeysSet")]
        public void Should_ApplyStreamOverrides_When_StreamTuningKeysSet()
        {
            var settings = SettingsFrom("""
                akka.remote.artery.advanced.outbound-hub-buffer-size = 128
                akka.remote.artery.advanced.stream-input-buffer-size = 64
                akka.remote.artery.advanced.stream-sync-processing-limit = 5000
                """);
            settings.OutboundHubBufferSize.Should().Be(128);

            var applied = ArteryRemoting.ApplyStreamOverrides(ActorMaterializerSettings.Create(Sys), settings);
            applied.MaxInputBufferSize.Should().Be(64);
            applied.InitialInputBufferSize.Should().Be(4);
            applied.SyncProcessingLimit.Should().Be(5000);

            var small = ArteryRemoting.ApplyStreamOverrides(ActorMaterializerSettings.Create(Sys),
                SettingsFrom("akka.remote.artery.advanced.stream-input-buffer-size = 1"));
            small.MaxInputBufferSize.Should().Be(1);
            small.InitialInputBufferSize.Should().Be(1);
        }

        [Theory(DisplayName = "Should_ThrowConfigurationException_When_StreamTuningKeyIsInvalid")]
        [InlineData("akka.remote.artery.advanced.outbound-hub-buffer-size = 0")]
        [InlineData("akka.remote.artery.advanced.stream-input-buffer-size = 3")]
        [InlineData("akka.remote.artery.advanced.stream-input-buffer-size = -1")]
        [InlineData("akka.remote.artery.advanced.stream-sync-processing-limit = -1")]
        public void Should_ThrowConfigurationException_When_StreamTuningKeyIsInvalid(string hocon)
        {
            Assert.Throws<ConfigurationException>(() => SettingsFrom(hocon));
        }

        [Theory(DisplayName = "Should_PreserveOrderAndShutDownCleanly_When_StreamTuningApplied")]
        [InlineData(1, 1, 1)]
        [InlineData(256, 128, 10000)]
        public async Task Should_PreserveOrderAndShutDownCleanly_When_StreamTuningApplied(
            int hubBuffer, int inputBuffer, int syncLimit)
        {
            const int recipientCount = 8;
            const int perRecipientCount = 250;

            var config = ConfigurationFactory.ParseString($"""
                akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
                akka.remote.artery.enabled = on
                akka.remote.artery.canonical.hostname = "127.0.0.1"
                akka.remote.artery.canonical.port = 0
                akka.remote.artery.advanced.outbound-lanes = 4
                akka.remote.artery.advanced.outbound-hub-buffer-size = {hubBuffer}
                akka.remote.artery.advanced.stream-input-buffer-size = {inputBuffer}
                akka.remote.artery.advanced.stream-sync-processing-limit = {syncLimit}
                """);

            var systemA = ActorSystem.Create("ArteryStreamTuningA", config);
            var systemB = ActorSystem.Create("ArteryStreamTuningB", config);
            var terminated = false;
            try
            {
                foreach (var system in new[] { systemA, systemB })
                {
                    var transport = (ArteryRemoting)RARP.For(system).Provider.Transport;
                    var materializer = MaterializerOf(transport);
                    materializer.Settings.MaxInputBufferSize.Should().Be(inputBuffer);
                    materializer.Settings.SyncProcessingLimit.Should().Be(syncLimit);
                    materializer.EffectiveSettings(Attributes.None).MaxInputBufferSize.Should().Be(inputBuffer);
                }

                var portB = RARP.For(systemB).Provider.DefaultAddress.Port!.Value;
                var refs = new IActorRef[recipientCount];
                for (var i = 0; i < recipientCount; i++)
                {
                    systemB.ActorOf(Props.Create(() => new OrderRecorder()), $"recorder-{i}");
                    refs[i] = await systemA
                        .ActorSelection($"akka://{systemB.Name}@127.0.0.1:{portB}/user/recorder-{i}")
                        .ResolveOne(TimeSpan.FromSeconds(10));
                }

                var senders = refs.Select(recipient => Task.Run(() =>
                {
                    for (var m = 0; m < perRecipientCount; m++)
                        recipient.Tell(m.ToString(), ActorRefs.NoSender);
                })).ToArray();
                await Task.WhenAll(senders);

                foreach (var recipient in refs)
                {
                    var probe = CreateTestProbe(systemA);
                    recipient.Tell(GetReceived.Instance, probe.Ref);
                    var received = await probe.ExpectMsgAsync<string[]>(TimeSpan.FromSeconds(20));
                    received.Should().Equal(Enumerable.Range(0, perRecipientCount).Select(m => m.ToString()));
                }

                // Clean shutdown: both systems terminate within the bound with the tuned streams live.
                var stopA = systemA.Terminate();
                var stopB = systemB.Terminate();
                (await stopA.AwaitWithTimeout(15.Seconds())).Should().BeTrue();
                (await stopB.AwaitWithTimeout(15.Seconds())).Should().BeTrue();
                terminated = true;
            }
            finally
            {
                if (!terminated)
                {
                    await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                    await systemB.Terminate().AwaitWithTimeout(15.Seconds());
                }
            }
        }

        private static ActorMaterializer MaterializerOf(ArteryRemoting transport)
        {
            var field = typeof(ArteryRemoting).GetField("_materializer", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("ArteryRemoting._materializer not found via reflection");
            return (ActorMaterializer)field.GetValue(transport)!;
        }

        private sealed class GetReceived
        {
            public static readonly GetReceived Instance = new();
            private GetReceived() { }
        }

        private sealed class OrderRecorder : ReceiveActor
        {
            private readonly List<string> _received = new();

            public OrderRecorder()
            {
                Receive<string>(msg => _received.Add(msg));
                Receive<GetReceived>(_ => Sender.Tell(_received.ToArray()));
            }
        }
    }
}
