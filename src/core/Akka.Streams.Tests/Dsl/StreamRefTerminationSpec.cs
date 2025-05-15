//-----------------------------------------------------------------------
// <copyright file="StreamRefTerminationSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Akka.Streams.Dsl;
using Akka.Streams.TestKit;
using Akka.TestKit.Xunit2;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Akka.Streams.Tests.Dsl
{
    public class StreamRefTerminationSpec : AkkaSpec
    {
        private readonly ActorMaterializer _materializer;
        
        public StreamRefTerminationSpec(ITestOutputHelper output) 
            : base(ConfigurationFactory.ParseString(@"
                akka.loglevel = DEBUG
                akka.actor.provider = remote
                akka.remote.dot-netty.tcp.port = 0
                akka.stream.materializer.debug.fuzzing-mode = on
                akka.stream.stream-ref {
                    final-termination-signal-deadline = 30s
                }
            "), output: output)
        {
            _materializer = Sys.Materializer();
        }
        
        [Fact(DisplayName = "SourceRef should detect termination quickly when remote system is terminated")]
        public void SourceRef_Should_Detect_Termination_Quickly()
        {
            // This test simulates the normal termination case, which works well
            RunSourceRefTerminationTest(shouldBackpressure: false);
        }
        
        [Fact(DisplayName = "SourceRef should detect termination when remote system is terminated under backpressure")]
        public void SourceRef_Should_Detect_Termination_Under_Backpressure()
        {
            // This test simulates the problematic case where no elements are flowing due to backpressure
            RunSourceRefTerminationTest(shouldBackpressure: true);
        }
        
        private void RunSourceRefTerminationTest(bool shouldBackpressure)
        {
            // Create another actor system to represent the remote system
            var remoteConfig = ConfigurationFactory.ParseString(@"
                akka.loglevel = DEBUG
                akka.actor.provider = remote
                akka.remote.dot-netty.tcp.port = 0
            ");
            
            var remoteSystem = ActorSystem.Create("RemoteSystem", remoteConfig);
            var remoteMaterializer = remoteSystem.Materializer();
            
            try
            {
                // Create a SourceRef in remote system
                var sourceRefTask = Source.Repeat(1)
                    .Delay(TimeSpan.FromMilliseconds(100), DelayOverflowStrategy.Backpressure)
                    .RunWith(StreamRefs.SourceRef<int>(), remoteMaterializer);
                
                // Wait for SourceRef to complete
                var sourceRef = sourceRefTask.Result;
                
                // Now use it in the local system
                var probe = this.CreateManualSubscriberProbe<int>();
                sourceRef.Source.RunWith(Sink.FromSubscriber(probe), _materializer);
                
                // Pull some elements to establish the connection
                var subscription = probe.ExpectSubscription();
                
                if (!shouldBackpressure)
                {
                    // Normal case - request and receive elements to establish active communication
                    subscription.Request(1);
                    probe.ExpectNext(1);
                    subscription.Request(1);
                    probe.ExpectNext(1);
                    Output.WriteLine("Active communication established");
                }
                else
                {
                    // Backpressure case - don't request any elements
                    Output.WriteLine("No elements requested - simulating backpressure");
                }
                
                // Add some logging to understand what's happening
                Output.WriteLine($"About to terminate remote system at {DateTime.UtcNow:HH:mm:ss.fff}");
                
                // Kill the remote system abruptly
                var terminationTask = remoteSystem.Terminate();
                
                // Measure how long it takes to detect the termination
                var startTime = DateTime.UtcNow;
                
                // The stream should fail with RemoteStreamRefActorTerminatedException
                var error = probe.ExpectError();
                var endTime = DateTime.UtcNow;
                error.Should().BeOfType<RemoteStreamRefActorTerminatedException>();
                
                var detectionTime = endTime - startTime;
                Output.WriteLine($"Error received at {endTime:HH:mm:ss.fff}, detection time: {detectionTime.TotalMilliseconds}ms");
                Output.WriteLine($"Exception message: {error.Message}");
                
                // Validate termination detection timing based on test scenario
                if (!shouldBackpressure)
                {
                    // For active communication, we expect quick detection (<5s)
                    detectionTime.Should().BeLessThan(TimeSpan.FromSeconds(5), 
                        "Termination should be detected quickly with active transfers");
                }
                else
                {
                    // For backpressure case, the test should still work but might take longer
                    // We'd prefer faster detection (<10s) rather than waiting for the full 30s timeout
                    detectionTime.Should().BeLessThan(TimeSpan.FromSeconds(10), 
                        "Termination should be detected in reasonable time even under backpressure");
                }
                
                // Wait for remote system to fully terminate
                terminationTask.Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                remoteSystem.Terminate().Wait(TimeSpan.FromSeconds(10));
            }
        }
    }
}