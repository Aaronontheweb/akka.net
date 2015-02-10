using Akka.Actor;
using Akka.Configuration;
using Akka.Remote.Transport;
using Akka.TestKit;

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
                  remote.transport-failure-detector.acceptable-heartbeat-pause = 3 s
                  remote.helios.tcp.applied-adapters = [""trttl""]
                  remote.helios.tcp.port = 0
                }");
            }
        }

        private const int PingPacketSize = 148;
        private const int MessageCount = 30;
        private const int BytesPerSecond = 500;
        private static readonly long TotalTime = (MessageCount*PingPacketSize)/BytesPerSecond;

        public class ThrottlingTester :  ReceiveActor
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

                Receive<string>(s => s.Equals("sendText") && _messageCount > 0, s =>
                {
                    _remoteRef.Tell("ping");
                    Self.Tell("sendNext");
                    _messageCount--;
                });

                Receive<string>(s => s.Equals("pong"), s =>
                {
                    _received++;
                    if(_received >= MessageCount)
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

        #endregion


    }
}
