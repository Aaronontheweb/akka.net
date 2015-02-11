using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Transport;
using Akka.TestKit;
using Akka.TestKit.TestActors;
using Akka.Util.Internal;
using Xunit;

namespace Akka.Remote.Tests.Transport
{
    public class ThrottlerTransportAdapterSpec : AkkaSpec
    {
        #region Setup / Config

        public static Config ThrottlerTransportAdapterSpecConfig
        {
            get
            {
                return ConfigurationFactory.ParseString(@"
                akka {
                  actor.provider = ""Akka.Remote.RemoteActorRefProvider, Akka.Remote""
                  remote.helios.tcp.hostname = ""localhost""
                  remote.log-remote-lifecycle-events = off
                  remote.retry-gate-closed-for = 1 s
                  remote.transport-failure-detector.heartbeat-interval = 1 s
                  remote.transport-failure-detector.acceptable-heartbeat-pause = 300 s
                  remote.helios.tcp.applied-adapters = [""trttl""]
                  remote.helios.tcp.port = 0
                }");
            }
        }

        private const int PingPacketSize = 350;
        private const int MessageCount = 15;
        private const int BytesPerSecond = 700;
        private static readonly long TotalTime = (MessageCount * PingPacketSize) / BytesPerSecond;

        public class ThrottlingTester : ReceiveActor
        {
            private ActorRef _remoteRef;
            private ActorRef _controller;

            private int _received = 0;
            private int _messageCount = MessageCount;
            private long _startTime = 0L;

            public ThrottlingTester(ActorRef remoteRef, ActorRef controller)
            {
                _remoteRef = remoteRef;
                _controller = controller;

                Receive<string>(s => s.Equals("start"), s =>
                {
                    Self.Tell("sendNext");
                    _startTime = SystemNanoTime.GetNanos();
                });

                Receive<string>(s => s.Equals("sendNext") && _messageCount > 0, s =>
                {
                    _remoteRef.Tell("ping");
                    Self.Tell("sendNext");
                    _messageCount--;
                });

                Receive<string>(s => s.Equals("pong"), s =>
                {
                    _received++;
                    if (_received >= MessageCount)
                        _controller.Tell(SystemNanoTime.GetNanos() - _startTime);
                });
            }

            public sealed class Lost
            {
                public Lost(string msg)
                {
                    Msg = msg;
                }

                public string Msg { get; private set; }
            }
        }

        public class Echo : UntypedActor
        {
            protected override void OnReceive(object message)
            {
                var str = message as string;
                if(!string.IsNullOrEmpty(str) && string.Equals(str, "ping"))
                    Sender.Tell("pong");
                else
                    Sender.Tell(message);
            }
        }

        private ActorSystem systemB;
        private ActorRef remote;

        private readonly RootActorPath rootB;

        private ActorRef Here
        {
            get
            {
                Sys.ActorSelection(rootB / "user" / "echo").Tell(new Identify(null), TestActor);
                return ExpectMsg<ActorIdentity>(TimeSpan.FromSeconds(3)).Subject;
            }
        }

        private bool Throttle(ThrottleTransportAdapter.Direction direction, ThrottleMode mode)
        {
            var rootBAddress = new Address("akka", "systemB", "localhost", rootB.Address.Port.Value);
            var transport =
                Sys.AsInstanceOf<ExtendedActorSystem>().Provider.AsInstanceOf<RemoteActorRefProvider>().Transport;
            var task = transport.ManagementCommand(new SetThrottle(rootBAddress, direction, mode));
            task.Wait(TimeSpan.FromSeconds(3));
            return task.Result;
        }

        private bool Disassociate()
        {
            var rootBAddress = new Address("akka", "systemB", "localhost", rootB.Address.Port.Value);
            var transport =
                Sys.AsInstanceOf<ExtendedActorSystem>().Provider.AsInstanceOf<RemoteActorRefProvider>().Transport;
            var task = transport.ManagementCommand(new ForceDisassociate(rootBAddress));
            task.Wait(TimeSpan.FromSeconds(3));
            return task.Result;
        }

        #endregion

        public ThrottlerTransportAdapterSpec()
            : base(ThrottlerTransportAdapterSpecConfig)
        {
            systemB = ActorSystem.Create("systemB", Sys.Settings.Config);
            remote = systemB.ActorOf(Props.Create<Echo>(), "echo");
            rootB = new RootActorPath(systemB.AsInstanceOf<ExtendedActorSystem>().Provider.DefaultAddress);
        }

        #region Tests

        [Fact]
        public void ThrottlerTransportAdapter_must_maintain_average_message_rate()
        {
            Throttle(ThrottleTransportAdapter.Direction.Send, new TokenBucket(PingPacketSize*4, BytesPerSecond, 0, 0)).ShouldBeTrue();
            var tester = Sys.ActorOf(Props.Create(() => new ThrottlingTester(Here, TestActor)));
            tester.Tell("start");

            var time = TimeSpan.FromTicks(ExpectMsg<long>(TimeSpan.FromSeconds(TotalTime + 12))).TotalSeconds;
            Log.Warning("Total time of transmission: {0}", time);
            Assert.True(time > TotalTime - 12);
            Throttle(ThrottleTransportAdapter.Direction.Send, Unthrottled.Instance).ShouldBeTrue();
        }

        #endregion
    }
}
