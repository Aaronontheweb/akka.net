using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.Routing;
using Akka.Configuration;
using Akka.Remote.TestKit;
using Akka.Routing;
using Akka.TestKit;

namespace Akka.Cluster.Tests.MultiNode.Routing
{
    public class ConsistentHashingRouterMultiNodeConfig : MultiNodeConfig
    {
        public class Echo : UntypedActor
        {
            protected override void OnReceive(object message)
            {
                Sender.Tell(Self);
            }
        }

        private readonly RoleName _first;
        public RoleName First { get { return _first; } }

        private readonly RoleName _second;
        public RoleName Second { get { return _second; } }

        private readonly RoleName _third;

        public RoleName Third { get { return _third; } }

        public ConsistentHashingRouterMultiNodeConfig()
        {
            _first = Role("first");
            _second = Role("second");
            _third = Role("third");

            CommonConfig = MultiNodeLoggingConfig.LoggingConfig.WithFallback(DebugConfig(true))
                .WithFallback(ConfigurationFactory.ParseString(@"
                    common-router-settings = {
                        router = consistent-hashing-pool
                        nr-of-instances = 10
                        cluster {
                            enabled = on
                            max-nr-of-instances-per-node = 2
                        }
                    }
                    akka.actor.deployment {
                    /router1 = ${common-router-settings}
                    /router3 = ${common-router-settings}
                    /router4 = ${common-router-settings}
                    }
                    akka.cluster.publish-stats-interval = 5s
                "))
                .WithFallback(MultiNodeClusterSpec.ClusterConfig());
        }
    }

    public class ClusterConsistentHashingRouterMultiNode1 : ClusterConsistentHashingRouterSpec { }
    public class ClusterConsistentHashingRouterMultiNode2 : ClusterConsistentHashingRouterSpec { }
    public class ClusterConsistentHashingRouterMultiNode3 : ClusterConsistentHashingRouterSpec { }

    public abstract class ClusterConsistentHashingRouterSpec : MultiNodeClusterSpec
    {
        private readonly ConsistentHashingRouterMultiNodeConfig _config;

        protected ClusterConsistentHashingRouterSpec() : this(new ConsistentHashingRouterMultiNodeConfig()) { }

        protected ClusterConsistentHashingRouterSpec(ConsistentHashingRouterMultiNodeConfig config) : base(config)
        {
            _config = config;
        }

        private ActorRef _router1 = null;

        protected ActorRef Router1
        {
            get { return _router1 ?? (_router1 = CreateRouter1()); }
        }

        private ActorRef CreateRouter1()
        {
            return
                Sys.ActorOf(
                    Props.Create<ConsistentHashingRouterMultiNodeConfig.Echo>().WithRouter(FromConfig.Instance),
                    "router1");
        }

        protected Routees CurrentRoutees(ActorRef router)
        {
            var routerAsk = router.Ask<Routees>(new GetRoutees(), GetTimeoutOrDefault(null));
            routerAsk.Wait();
            return routerAsk.Result;
        }

        /// <summary>
        /// Fills in the self address for local ActorRef
        /// </summary>
        protected Address FullAddress(ActorRef actorRef)
        {
            if (string.IsNullOrEmpty(actorRef.Path.Address.Host) || !actorRef.Path.Address.Port.HasValue)
                return Cluster.SelfAddress;
            return actorRef.Path.Address;
        }

        [MultiNodeFact]
        public void ClusterConsistentHashingRouterSpecs()
        {
            AClusterRouterWithConsistentHashingPoolMustStartClusterWith2Nodes();
            AClusterRouterWithConsistentHashingPoolMustCreateRouteesFromConfiguration();
            AClusterRouterWithConsistentHashingPoolMustSelectDestinationBasedOnHashKey();
            AClusterRouterWithConsistentHashingPoolMustDeployRouteesToNewMemberNodesInTheCluster();
            AClusterRouterWithConsistentHashingPoolMustDeployProgramaticallyDefinedRouteesToTheMemberNodesInTheCluster();
        }

        protected void AClusterRouterWithConsistentHashingPoolMustStartClusterWith2Nodes()
        {
            AwaitClusterUp(_config.First, _config.Second);
            EnterBarrier("after-1");
        }

        protected void AClusterRouterWithConsistentHashingPoolMustCreateRouteesFromConfiguration()
        {
            RunOn(() =>
            {
                // it may take some timeuntil router receives cluster member events
                AwaitAssert(() =>
                {
                    CurrentRoutees(Router1).Members.Count().ShouldBe(4);
                });
                var routees = CurrentRoutees(Router1);
                var routerMembers = routees.Members.Select(x => FullAddress(((ActorRefRoutee)x).Actor)).Distinct().ToList();
                routerMembers.ShouldBe(new List<Address>(){ GetAddress(_config.First), GetAddress(_config.Second) });
            }, _config.First);

            EnterBarrier("after-2");
        }

        protected void AClusterRouterWithConsistentHashingPoolMustSelectDestinationBasedOnHashKey()
        {
            RunOn(() =>
            {
                Router1.Tell(new ConsistentHashableEnvelope("A", "a"));
                var destinationA = ExpectMsg<ActorRef>();
                Router1.Tell(new ConsistentHashableEnvelope("AA", "a"));
                ExpectMsg(destinationA);
            }, _config.First);

            EnterBarrier("after-3");
        }

        protected void AClusterRouterWithConsistentHashingPoolMustDeployRouteesToNewMemberNodesInTheCluster()
        {
            AwaitClusterUp(_config.First, _config.Second, _config.Third);

            RunOn(() =>
            {
                //it may take some time until router receives cluster member events
                AwaitAssert(() =>
                {
                    CurrentRoutees(Router1).Members.Count().ShouldBe(6);
                });
                var routees = CurrentRoutees(Router1);
                var routerMembers = routees.Members.Select(x => FullAddress(((ActorRefRoutee)x).Actor)).Distinct().ToList();
                routerMembers.ShouldBe(Roles.Select(GetAddress).ToList());
            }, _config.First);

            EnterBarrier("after-4");
        }

        protected void
            AClusterRouterWithConsistentHashingPoolMustDeployProgramaticallyDefinedRouteesToTheMemberNodesInTheCluster()
        {
            RunOn(() =>
            {
                var router2 =
                    Sys.ActorOf(
                        new ClusterRouterPool(local: new ConsistentHashingPool(0),
                            settings: new ClusterRouterPoolSettings(totalInstances: 10, maxInstancesPerNode: 2,
                                allowLocalRoutees: true, useRole: null)).Props(Props.Create<ConsistentHashingRouterMultiNodeConfig.Echo>()), "router2");

                //it may take some time until router receives cluster member events
                //it may take some time until router receives cluster member events
                AwaitAssert(() =>
                {
                    CurrentRoutees(router2).Members.Count().ShouldBe(6);
                });
                var routees = CurrentRoutees(router2);
                var routerMembers = routees.Members.Select(x => FullAddress(((ActorRefRoutee)x).Actor)).Distinct().ToList();
                routerMembers.ShouldBe(Roles.Select(GetAddress).ToList());
            }, _config.First);

            EnterBarrier("after-5");
        }
    }
}
