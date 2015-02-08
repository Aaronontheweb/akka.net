using System;
using System.Linq;
using System.Runtime.InteropServices;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.TestKit;
using Akka.TestKit;
using Akka.TestKit.TestActors;
using Akka.Util.Internal;
using Xunit;

namespace Akka.Cluster.Tests.MultiNode
{
    public class ClusterDeathWatchSpecConfig : MultiNodeConfig
    {
        readonly RoleName _first;
        public RoleName First { get { return _first; } }
        readonly RoleName _second;
        public RoleName Second { get { return _second; } }
        readonly RoleName _third;
        public RoleName Third { get { return _third; } }
        readonly RoleName _fourth;
        public RoleName Fourth { get { return _fourth; } }
        readonly RoleName _fifth;
        public RoleName Fifth { get { return _fifth; } }

        public ClusterDeathWatchSpecConfig()
        {
            _first = Role("first");
            _second = Role("second");
            _third = Role("third");
            _fourth = Role("fourth");
            _fifth = Role("fifth");

            CommonConfig = ConfigurationFactory.ParseString(@"akka.cluster.publish-stats-interval = 25s")
                .WithFallback(MultiNodeLoggingConfig.LoggingConfig)
                .WithFallback(DebugConfig(true))
                .WithFallback(MultiNodeClusterSpec.ClusterConfigWithFailureDetectorPuppet());
        }
    }

    public class ClusterDeathWatchMultiNode1 : ClusterDeathWatchSpec
    {
    }

    public class ClusterDeathWatchMultiNode2 : ClusterDeathWatchSpec
    {
    }

    public class ClusterDeathWatchMultiNode3 : ClusterDeathWatchSpec
    {
    }

    public class ClusterDeathWatchMultiNode4 : ClusterDeathWatchSpec
    {
    }

    public class ClusterDeathWatchMultiNode5 : ClusterDeathWatchSpec
    {
    }

    public abstract class ClusterDeathWatchSpec : MultiNodeClusterSpec
    {
        readonly ClusterDeathWatchSpecConfig _config;

        protected ClusterDeathWatchSpec()
            : this(new ClusterDeathWatchSpecConfig())
        {
        }

        private ClusterDeathWatchSpec(ClusterDeathWatchSpecConfig config)
            : base(config)
        {
            _config = config;
        }

        protected override void AtStartup()
        {
            if (!Log.IsDebugEnabled)
            {
                MuteMarkingAsUnreachable();
            }
            base.AtStartup();
        }

        [MultiNodeFact]
        public void ClusterDeathWatchSpecTests()
        {
            AnActorWatchingARemoteActorInTheClusterReceiveTerminatedWhenWatchedNodeBecomesDownRemoved();
        }

        public void AnActorWatchingARemoteActorInTheClusterReceiveTerminatedWhenWatchedNodeBecomesDownRemoved()
        {
            Within(TimeSpan.FromSeconds(20), () =>
            {
                AwaitClusterUp(_config.First, _config.Second, _config.Third, _config.Fourth);
                EnterBarrier("cluster-up");

                RunOn(() =>
                {
                    EnterBarrier("subjected-started");

                    var path2 = new RootActorPath(GetAddress(_config.Second)) / "user" / "subject";
                    var path3 = new RootActorPath(GetAddress(_config.Third)) / "user" / "subject";
                    var watchEstablished = new TestLatch(Sys, 2);
                    Sys.ActorOf(Props.Create(() => new Observer(path2, path3, watchEstablished, TestActor))
                        .WithDeploy(Deploy.Local), "observer1");

                    watchEstablished.Ready();
                    EnterBarrier("watch-established");
                    ExpectMsg(path2);
                    ExpectNoMsg(TimeSpan.FromSeconds(2));
                    EnterBarrier("second-terminated");
                    MarkNodeAsUnavailable(GetAddress(_config.Third));
                    AwaitAssert(() => Assert.True(ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Third))));
                    Cluster.Down(GetAddress(_config.Third));
                    //removed
                    AwaitAssert(() => Assert.False(ClusterView.Members.Select(x => x.Address).Contains(GetAddress(_config.Third))));
                    AwaitAssert(() => Assert.False(ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Third))));
                    ExpectMsg(path3);
                    EnterBarrier("third-terminated");
                }, _config.First);

                RunOn(() =>
                {
                    Sys.ActorOf(BlackHoleActor.Props, "subject");
                    EnterBarrier("subjected-started");
                    EnterBarrier("watch-established");
                    RunOn(() =>
                    {
                        MarkNodeAsUnavailable(GetAddress(_config.Second));
                        AwaitAssert(() => Assert.True(ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Second))));
                        Cluster.Down(GetAddress(_config.Second));
                        //removed
                        AwaitAssert(() => Assert.False(ClusterView.Members.Select(x => x.Address).Contains(GetAddress(_config.Second))));
                        AwaitAssert(() => Assert.False(ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Second))));
                    }, _config.Third);
                    EnterBarrier("second-terminated");
                    EnterBarrier("third-terminated");
                }, _config.Second, _config.Third, _config.Fourth);

                RunOn(() =>
                {
                    EnterBarrier("subjected-started");
                    EnterBarrier("watch-established");
                    EnterBarrier("second-terminated");
                    EnterBarrier("third-terminated");
                }, _config.Fifth);

                EnterBarrier("after-1");
            });
        }

        /// <summary>
        /// Used to report <see cref="Terminated"/> events to the <see cref="TestActor"/>
        /// </summary>
        class Observer : ReceiveActor
        {
            private readonly ActorRef _testActorRef;
            readonly TestLatch _watchEstablished;

            public Observer(ActorPath path2, ActorPath path3, TestLatch watchEstablished, ActorRef testActorRef)
            {
                _watchEstablished = watchEstablished;
                _testActorRef = testActorRef;
                
                Receive<ActorIdentity>(identity => identity.MessageId.Equals(path2), identity =>
                {
                    Context.Watch(identity.Subject);
                    _watchEstablished.CountDown();
                });

                Receive<ActorIdentity>(identity => identity.MessageId.Equals(path3), identity =>
                {
                    Context.Watch(identity.Subject);
                    _watchEstablished.CountDown();
                });

                Receive<Terminated>(terminated =>
                {
                    _testActorRef.Tell(terminated.ActorRef.Path);
                });

                Context.ActorSelection(path2).Tell(new Identify(path2));
                Context.ActorSelection(path3).Tell(new Identify(path3));

            }

            protected override void Unhandled(object message)
            {
                Context.GetLogger().Error("Received unhandled message: {0}", message);
            }
        }
    }
}
