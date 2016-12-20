#region copyright
// -----------------------------------------------------------------------
//  <copyright file="TcpListenerHorizontalScaleSpec.cs" company="Akka.NET Team">
//      Copyright (C) 2015-2016 Lightbend <http://www.lightbend.com/>
//      Copyright (C) 2013-2016 Akka.NET Team <https://github.com/akkadotnet>
//  </copyright>
// -----------------------------------------------------------------------
#endregion

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.IO;
using Akka.Remote;
using Akka.Util;
using NBench;

namespace Akka.Tests.Performance.IO
{
    public class TcpListenerHorizontalScaleSpec
    {
        #region internal classes

        private sealed class TestListener : ReceiveActor
        {
            public TestListener(EndPoint endpoint, Counter inboundCounter, Counter errorCounter)
            {
                Context.System.Tcp().Tell(new Tcp.Bind(Self, endpoint));
                Receive<Tcp.Connected>(connected =>
                {
                    var connection = Sender;
                    var connectionHandler = Context.ActorOf(Props.Create(() => new TestHandler(connection, inboundCounter, errorCounter)));
                    connection.Tell(new Tcp.Register(connectionHandler));
                });
            }
        }

        private sealed class TestHandler : ReceiveActor
        {
            public TestHandler(IActorRef connection, Counter inboundCounter, Counter errorCounter)
            {
                Context.Watch(connection);
                Receive<Tcp.Received>(received =>
                {
                    inboundCounter.Increment();
                    // ping back
                    //connection.Tell(Tcp.Write.Create(received.Data));
                });
                Receive<Tcp.ErrorClosed>(err =>
                {
                    errorCounter.Increment();
                });
                Receive<Tcp.ConnectionClosed>(closed => Context.Stop(Self));
                Receive<Terminated>(terminated => Context.Stop(Self));
            }
        }

        #endregion

        private static readonly IPEndPoint TestAddress = new IPEndPoint(IPAddress.Loopback, 9010);
        private static readonly TimeSpan TotalRun = TimeSpan.FromMinutes(4);
        private static readonly TimeSpan SleepInterval = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan SaturationThreshold = TimeSpan.FromSeconds(15);

        private const string ClientConnectCounterName = "connected clients";
        private const string InboundThroughputCounterName = "inbound ops";
        private const string OutboundThroughputCounterName = "outbound ops";
        private const string ErrorCounterName = "exceptions caught";
        private const int IterationCount = 1; // this is long running spec

        private List<TcpClient> _clients;
        private CancellationTokenSource _cancellation;
        private ActorSystem _server;
        private Counter _clientConnectedCounter;
        private Counter _inboundThroughputCounter;
        private Counter _outboundThroughputCounter;
        private Counter _errorCounter;

        [PerfSetup]
        public void Setup(BenchmarkContext context)
        {
            _clients = new List<TcpClient>();
            _cancellation = new CancellationTokenSource();
            _server = ActorSystem.Create("io-tcp-perf-spec");
            _clientConnectedCounter = context.GetCounter(ClientConnectCounterName);
            _inboundThroughputCounter = context.GetCounter(InboundThroughputCounterName);
            _outboundThroughputCounter = context.GetCounter(OutboundThroughputCounterName);
            _errorCounter = context.GetCounter(ErrorCounterName);
            _server.ActorOf(Props.Create(() => new TestListener(TestAddress, _inboundThroughputCounter, _errorCounter)));
        }

        [PerfCleanup]
        public void Cleanup()
        {
            _cancellation.Cancel();
            _server.Dispose();
        }

        [PerfBenchmark(
            Description =
                "Measures how quickly and with how much GC overhead a TcpSocketChannel --> TcpServerSocketChannel connection can decode / encode realistic messages",
            NumberOfIterations = IterationCount, RunMode = RunMode.Iterations, SkipWarmups = true)]
        [CounterMeasurement(InboundThroughputCounterName)]
        [CounterMeasurement(OutboundThroughputCounterName)]
        [CounterMeasurement(ClientConnectCounterName)]
        [CounterMeasurement(ErrorCounterName)]
        [GcMeasurement(GcMetric.TotalCollections, GcGeneration.AllGc)]
        [MemoryMeasurement(MemoryMetric.TotalBytesAllocated)]
        public void TcpRawActorHorizontalScaleStressTest(BenchmarkContext context)
        {
            var due = DateTime.Now + TotalRun;
            var lastMeasure = due;
            var deadline = new Deadline(due);
            var runCount = 1;

            Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var bytes = new byte[50];
                    ThreadLocalRandom.Current.NextBytes(bytes);
                    
                    foreach (var client in _clients)
                    {
                        using (var stream = client.GetStream())
                        {
                            await stream.WriteAsync(bytes, 0, bytes.Length, _cancellation.Token);
                            await stream.FlushAsync(_cancellation.Token);
                        }
                    }

                    await Task.Delay(40);
                }
            });

            while (!deadline.IsOverdue)
            {
                var client = new TcpClient();
                client.Connect(TestAddress);
                _clients.Add(client);
                _clientConnectedCounter.Increment();

                Thread.Sleep(SleepInterval);

                if (++runCount % 10 == 0)
                {
                    Console.WriteLine($"{due - DateTime.Now} minutes remaining [{runCount} connections active].");

                    var saturation = DateTime.Now - lastMeasure;
                    if (saturation > SaturationThreshold)
                    {
                        Console.WriteLine($"Took {saturation}. Threshold exceeded. Ending stress test.");
                        break;
                    }
                    lastMeasure = DateTime.Now;
                }
            }

            _cancellation.Cancel();
        }
    }
}