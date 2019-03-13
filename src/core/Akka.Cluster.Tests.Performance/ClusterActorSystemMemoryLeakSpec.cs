// //-----------------------------------------------------------------------
// // <copyright file="ClusterActorSystemMemoryLeakSpec.cs" company="Akka.NET Project">
// //     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
// //     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// // </copyright>
// //-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Configuration;
using NBench;

namespace Akka.Cluster.Tests.Performance
{
    /// <summary>
    ///     Test for memory leaks per https://github.com/akkadotnet/akka.net/issues/3735
    /// </summary>
    public class ClusterActorSystemMemoryLeakSpec
    {
        private const int IterationCount = 10;

        private const string ConfigStringCluster = @"
akka {   
    actor {
        provider = cluster
    }
    remote {
        dot-netty.tcp {
            hostname = ""127.0.0.1""
            port = 3000
        }
    }
    cluster {
        seed-nodes = [""akka.tcp://ClusterServer@127.0.0.1:3000""]
    } 
}
";

        [PerfBenchmark(
            Description = "Repeatedly creates and destroys ActorSystems to check to see if Memory is leaked afterwards",
            RunMode = RunMode.Iterations, NumberOfIterations = 3, SkipWarmups = true, TestMode = TestMode.Measurement)]
        [MemoryAssertion(MemoryMetric.TotalBytesAllocated, MustBe.LessThanOrEqualTo, ByteConstants.SixtyFourKb * 512)]
        public void ActorSystem_should_not_leak_memory()
        {
            for(var i = 0; i < IterationCount; i++)
                CreateAndDisposeActorSystem(ConfigStringCluster);
        }

        private static void CreateAndDisposeActorSystem(string configString)
        {
            ActorSystem system;

            if (configString == null)
            {
                system = ActorSystem.Create("ClusterServer");
            }
            else
            {
                var config = ConfigurationFactory.ParseString(configString);
                system = ActorSystem.Create("ClusterServer", config);
            }

            Cluster.Get(system).RegisterOnMemberUp(() =>
            {
                // ensure that a actor system did some work
                var actor = system.ActorOf(Props.Create(() => new MyActor()));
                var result = actor.Ask<ActorIdentity>(new Identify(42)).Result;
                CoordinatedShutdown.Get(system).Run(CoordinatedShutdown.ClrExitReason.Instance);
            });

            system.WhenTerminated.Wait();
            GC.Collect();
        }

        private class MyActor : ReceiveActor
        {
            public MyActor()
            {
                ReceiveAny(_ => Sender.Tell(_));
            }
        }
    }
}