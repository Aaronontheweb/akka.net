using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote;
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

        private ActorRef _remoteWatcher;

        protected ActorRef RemoteWatcher
        {
            get
            {
                if (_remoteWatcher == null)
                {
                    Sys.ActorSelection("/system/remote-watcher").Tell(new Identify(null), TestActor);
                    _remoteWatcher = ExpectMsg<ActorIdentity>().Subject;
                }
                return _remoteWatcher;
            }
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
            AnActorWatchingARemoteActorInTheClusterMustReceiveTerminatedWhenWatchedNodeBecomesDownRemoved();
            //AnActorWatchingARemoteActorInTheClusterMustReceiveTerminatedWhenWatchedPathDoesNotExist();
            AnActorWatchingARemoteActorInTheClusterMustBeAbleToWatchActorBeforeNodeJoinsClusterAndClusterRemoteWatcherTakesOverFromRemoteWatcher();
        }

        public void AnActorWatchingARemoteActorInTheClusterMustReceiveTerminatedWhenWatchedNodeBecomesDownRemoved()
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

        /* 
         * NOTE: it's not possible to watch a path that doesn't exist in Akka.NET
         * REASON: in order to do this, you fist need an ActorRef. Can't get one for
         * a path that doesn't exist at the time of creation. In Scala Akka they have to use
         * System.ActorFor for this, which has been deprecated for a long time and has never been
         * supported in Akka.NET.
        */
        //public void AnActorWatchingARemoteActorInTheClusterMustReceiveTerminatedWhenWatchedPathDoesNotExist()
        //{
        //    Thread.Sleep(5000);
        //    RunOn(() =>
        //    {
        //        var path2 = new RootActorPath(GetAddress(_config.Second)) / "user" / "non-existing";
        //        Sys.ActorOf(Props.Create(() => new DumbObserver(path2, TestActor)).WithDeploy(Deploy.Local), "observer3");
        //        ExpectMsg(path2);
        //    }, _config.First);

        //    EnterBarrier("after-2");
        //}

        public void AnActorWatchingARemoteActorInTheClusterMustBeAbleToWatchActorBeforeNodeJoinsClusterAndClusterRemoteWatcherTakesOverFromRemoteWatcher()
        {
            Within(TimeSpan.FromSeconds(20), () =>
            {
                RunOn(() => Sys.ActorOf(BlackHoleActor.Props.WithDeploy(Deploy.Local), "subject5"), _config.Fifth);
                EnterBarrier("subjected-started");

                RunOn(() =>
                {
                    Sys.ActorSelection(new RootActorPath(GetAddress(_config.Fifth)) / "user" / "subject5").Tell(new Identify("subject5"), TestActor);
                    var subject5 = ExpectMsg<ActorIdentity>().Subject;
                    Watch(subject5);

                    //fifth is not a cluster member, so the watch is handled by the RemoteWatcher
                    AwaitAssert(() =>
                    {
                        RemoteWatcher.Tell(Remote.RemoteWatcher.Stats.Empty);
                        ExpectMsg<Remote.RemoteWatcher.Stats>().WatchingRefs.Contains(new Tuple<ActorRef, ActorRef>(subject5, TestActor)).ShouldBeTrue();
                    });
                }, _config.First);
                EnterBarrier("remote-watch");

                // second and third are already removed
                AwaitClusterUp(_config.First, _config.Fourth, _config.Fifth);

                RunOn(() =>
                {
                    // fifth is member, so the watch is handled by the ClusterRemoteWatcher,
                    // and cleaned up from RemoteWatcher
                    AwaitAssert(() =>
                    {
                        RemoteWatcher.Tell(Remote.RemoteWatcher.Stats.Empty);
                        ExpectMsg<Remote.RemoteWatcher.Stats>().WatchingRefs.Select(x => x.Item1.Path.Name).Contains("subject5").ShouldBeFalse();
                    });
                }, _config.First);

                EnterBarrier("cluster-watch");

                RunOn(() =>
                {
                    MarkNodeAsUnavailable(GetAddress(_config.Fifth));
                    AwaitAssert(() => ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Fifth)).ShouldBeTrue());
                    Cluster.Down(GetAddress(_config.Fifth));
                    // removed
                    AwaitAssert(() => Assert.False(ClusterView.UnreachableMembers.Select(x => x.Address).Contains(GetAddress(_config.Fifth))));
                    AwaitAssert(() => Assert.False(ClusterView.Members.Select(x => x.Address).Contains(GetAddress(_config.Fifth))));
                }, _config.Fourth);

                EnterBarrier("fifth-terminated");
                RunOn(() =>
                {
                    ExpectMsg<Terminated>().ActorRef.Path.Name.ShouldBe("subject5");
                }, _config.First);

                EnterBarrier("after-3");
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
        }

        class DumbObserver : ReceiveActor
        {
            private readonly ActorRef _testActorRef;

            public DumbObserver(ActorPath path2, ActorRef testActorRef)
            {
                _testActorRef = testActorRef;

                Receive<ActorIdentity>(identity =>
                {
                    Context.Watch(identity.Subject);
                });

                Receive<Terminated>(terminated =>
                {
                    _testActorRef.Tell(terminated.ActorRef.Path);
                });

                Context.ActorSelection(path2).Tell(new Identify(path2));
            }
        }
    }
}
