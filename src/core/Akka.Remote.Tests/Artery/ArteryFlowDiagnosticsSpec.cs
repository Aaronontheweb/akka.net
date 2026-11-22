//-----------------------------------------------------------------------
// <copyright file="ArteryFlowDiagnosticsSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.TestKit;
using Akka.TestKit.Extensions;
using FluentAssertions;
using FluentAssertions.Extensions;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    /// <summary>
    /// Covers <c>akka.remote.artery.advanced.flow-diagnostics</c>: with the setting on, both systems log
    /// <c>ARTERY-FLOW</c> lines in each direction carrying non-zero message, write and chunk counts.
    /// </summary>
    public class ArteryFlowDiagnosticsSpec : AkkaSpec
    {
        private readonly ITestOutputHelper _output;

        public ArteryFlowDiagnosticsSpec(ITestOutputHelper output) : base(output)
        {
            _output = output;
        }

        private static readonly Config FlowConfig = ConfigurationFactory.ParseString("""
            akka.loglevel = INFO
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.remote.artery.advanced.outbound-lanes = 2
            akka.remote.artery.advanced.inbound-lanes = 2
            akka.remote.artery.advanced.flow-diagnostics = on
            akka.remote.artery.advanced.flow-diagnostics-interval = 500ms
            """);

        private static readonly Regex KeyValue = new(@"(\S+?)=(\S+)", RegexOptions.Compiled);

        private sealed class Sink : ReceiveActor
        {
            public Sink() => ReceiveAny(_ => { });
        }

        private static string RemotePath(ActorSystem system, string name) =>
            $"akka://{system.Name}@127.0.0.1:{RARP.For(system).Provider.DefaultAddress.Port!.Value}/user/{name}";

        // Sums every numeric key=value of the lines for one direction; returns true once the predicate holds.
        private async Task AwaitFlowAsync(ActorSystem system, TestProbe probe, Func<Dictionary<string, long>, Dictionary<string, long>, bool> done)
        {
            var outbound = new Dictionary<string, long>();
            var inbound = new Dictionary<string, long>();

            await probe.FishForMessageAsync(e =>
            {
                if (e is not Info info || info.Message.ToString() is not { } text || !text.StartsWith("ARTERY-FLOW "))
                    return false;

                _output.WriteLine($"[{system.Name}] {text}");
                var target = text.StartsWith("ARTERY-FLOW dir=out ") ? outbound : inbound;
                foreach (Match m in KeyValue.Matches(text))
                {
                    if (long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                        target[m.Groups[1].Value] = target.GetValueOrDefault(m.Groups[1].Value) + v;
                }

                return done(outbound, inbound);
            }, 20.Seconds());
        }

        private static long Get(Dictionary<string, long> d, string key) => d.GetValueOrDefault(key);

        [Fact(DisplayName = "Flow diagnostics should log non-zero ARTERY-FLOW message, write and chunk counts in both directions")]
        public async Task Should_LogFlowCounters_When_Enabled()
        {
            var systemA = ActorSystem.Create("ArteryFlowA", FlowConfig);
            var systemB = ActorSystem.Create("ArteryFlowB", FlowConfig);
            try
            {
                var probeA = CreateTestProbe(systemA);
                var probeB = CreateTestProbe(systemB);
                systemA.EventStream.Subscribe(probeA.Ref, typeof(Info));
                systemB.EventStream.Subscribe(probeB.Ref, typeof(Info));

                const int recipients = 8;
                const int burst = 2000;
                var toB = new IActorRef[recipients];
                var toA = new IActorRef[recipients];
                for (var i = 0; i < recipients; i++)
                {
                    systemB.ActorOf(Props.Create(() => new Sink()), $"sink-{i}");
                    systemA.ActorOf(Props.Create(() => new Sink()), $"sink-{i}");
                }

                for (var i = 0; i < recipients; i++)
                {
                    toB[i] = await systemA.ActorSelection(RemotePath(systemB, $"sink-{i}")).ResolveOne(10.Seconds());
                    toA[i] = await systemB.ActorSelection(RemotePath(systemA, $"sink-{i}")).ResolveOne(10.Seconds());
                }

                for (var n = 0; n < burst; n++)
                {
                    for (var i = 0; i < recipients; i++)
                    {
                        toB[i].Tell("payload-" + n);
                        toA[i].Tell("payload-" + n);
                    }
                }

                // A sent to B and B sent to A, so each system must report an outbound AND an inbound line.
                foreach (var (system, probe) in new[] { (systemA, probeA), (systemB, probeB) })
                {
                    await AwaitFlowAsync(system, probe, (o, i) =>
                        Get(o, "out.lane0") + Get(o, "out.lane1") > 0 && Get(o, "out.writes") > 0 && Get(o, "out.frames") > 0 &&
                        Get(i, "in.chunks") > 0 && Get(i, "in.frames") > 0 &&
                        Get(i, "in.lane0") + Get(i, "in.lane1") > 0 &&
                        Get(i, "in.consumer0.items") + Get(i, "in.consumer1.items") > 0);
                }
            }
            finally
            {
                await systemA.Terminate().AwaitWithTimeout(15.Seconds());
                await systemB.Terminate().AwaitWithTimeout(15.Seconds());
            }
        }
    }
}
