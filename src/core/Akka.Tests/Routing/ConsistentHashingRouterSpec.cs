using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Routing;
using Akka.TestKit;
using Xunit;

namespace Akka.Tests.Routing
{
    public class ConsistentHashingRouterSpec : AkkaSpec
    {
        #region Actors & Message Classes

        public class Echo : UntypedActor
        {
            protected override void OnReceive(object message)
            {
                if (message is ConsistentHashableEnvelope)
                {
                    Sender.Tell(string.Format("Unexpected envelope: {0}", message));
                }
                else
                {
                    Sender.Tell(Self);
                }
            }
        }

        public sealed class Msg : IConsistentHashable
        {
            public Msg(object consistentHashKey, string data)
            {
                ConsistentHashKey = consistentHashKey;
                Data = data;
            }

            public string Data { get; private set; }

            public object Key { get { return ConsistentHashKey; } }

            public object ConsistentHashKey { get; private set; }
        }

        public sealed class MsgKey
        {
            public MsgKey(string name)
            {
                Name = name;
            }

            public string Name { get; private set; }
        }

        public sealed class Msg2
        {
            public Msg2(object key, string data)
            {
                Data = data;
                Key = key;
            }

            public string Data { get; private set; }

            public object Key { get; private set; }
        }

        #endregion

        private ActorRef router1;

        public ConsistentHashingRouterSpec()
            : base(@"
            akka.actor.deployment {
              /router1 {
                router = consistent-hashing-pool
                nr-of-instances = 3
                virtual-nodes-factor = 17
              }
              /router2 {
                router = consistent-hashing-pool
                nr-of-instances = 5
              }
        ")
        {
            router1 = Sys.ActorOf(Props.Create<Echo>().WithRouter(FromConfig.Instance), "router1");
        }

        [Fact]
        public async Task ConsistentHashingRouterMustCreateRouteesFromConfiguration()
        {
            var currentRoutees = await router1.Ask<Routees>(new GetRoutees(), GetTimeoutOrDefault(null));
            currentRoutees.Members.Count().ShouldBe(3);
        }

        [Fact]
        public void ConsistentHashingRouterMustSelectDestinationBasedOnConsistentHashKeyOfMessage()
        {
            router1.Tell(new Msg("a", "A"));
            var destinationA = ExpectMsg<ActorRef>();
            router1.Tell(new ConsistentHashableEnvelope("AA", "a"));
            ExpectMsg(destinationA);

            router1.Tell(new Msg(17, "A"));
            var destinationB = ExpectMsg<ActorRef>();
            router1.Tell(new ConsistentHashableEnvelope("BB", 17));
            ExpectMsg(destinationB);

            router1.Tell(new Msg(new MsgKey("c"), "C"));
            var destinationC = ExpectMsg<ActorRef>();
            router1.Tell(new ConsistentHashableEnvelope("CC", new MsgKey("c")));
            ExpectMsg(destinationC);
        }

        [Fact]
        public void ConsistentHashingRouterMustSelectDestinationWithDefinedHashMapping()
        {
            ConsistentHashMapping hashMapping = msg =>
            {
                if (msg is Msg2)
                {
                    var m2 = msg as Msg2;
                    return m2.Key;
                }

                return null;
            };
            var router2 =
                Sys.ActorOf(new ConsistentHashingPool(1, null, null, null, hashMapping: hashMapping).Props(Props.Create<Echo>()), "router2");

            router2.Tell(new Msg2("a", "A"));
            var destinationA = ExpectMsg<ActorRef>();
            router2.Tell(new ConsistentHashableEnvelope("AA", "a"));
            ExpectMsg(destinationA);

            router2.Tell(new Msg2(17, "A"));
            var destinationB = ExpectMsg<ActorRef>();
            router2.Tell(new ConsistentHashableEnvelope("BB", 17));
            ExpectMsg(destinationB);

            router2.Tell(new Msg2(new MsgKey("c"), "C"));
            var destinationC = ExpectMsg<ActorRef>();
            router2.Tell(new ConsistentHashableEnvelope("CC", new MsgKey("c")));
            ExpectMsg(destinationC);
        }
    }
}
