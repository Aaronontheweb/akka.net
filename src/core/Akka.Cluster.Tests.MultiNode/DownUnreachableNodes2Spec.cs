using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.TestKit;
using Akka.Configuration;
using Akka.Remote.TestKit;
using Akka.TestKit;
using FluentAssertions;

namespace Akka.Cluster.Tests.MultiNode
{
    public class DownUnreachableNodes2SpecConfig : MultiNodeConfig
    {
        public RoleName Observer1 { get; }

        public RoleName Observer2 { get; }

        public RoleName Unreachable1 { get; }

        public RoleName Unreachable2 { get; }

        public DownUnreachableNodes2SpecConfig()
        {
            Observer1 = Role("observer1");
            Observer2 = Role("observer2");
            Unreachable1 = Role("unreachable1");
            Unreachable2 = Role("unreachable2");

            TestTransport = true;

            CommonConfig = DebugConfig(true)
                .WithFallback(ConfigurationFactory.ParseString(@"
                  akka.cluster.retry-unsuccessful-join-after = 3s
                  akka.remote.retry-gate-closed-for = 45s
                  akka.remote.log-remote-lifecycle-events = INFO
                "))
                .WithFallback(MultiNodeClusterSpec.ClusterConfig());
        }
    }

    public class DownUnreachableNodes2Spec : MultiNodeClusterSpec
    {
        private readonly DownUnreachableNodes2SpecConfig _config;

        public DownUnreachableNodes2Spec() : this(new DownUnreachableNodes2SpecConfig())
        {
        }

        protected DownUnreachableNodes2Spec(DownUnreachableNodes2SpecConfig config)
            : base(config, typeof(DownUnreachableNodes2Spec))
        {
            _config = config;
        }

        [MultiNodeFact]
        public void DownUnreachableNodes2Specs()
        {
            Cluster_should_down_single_unreachable_node();
        }

        private void Cluster_should_down_single_unreachable_node()
        {
            AwaitClusterUp(Roles.ToArray());

            var unreachableAddress1 = GetAddress(_config.Unreachable1);
            var unreachableAddress2 = GetAddress(_config.Unreachable2);

            RunOn(() =>
            {
                // make first node unreachable
                TestConductor.Exit(_config.Unreachable1, 0).Wait();

                AwaitCondition(() => Cluster.State.Unreachable.Select(x => x.Address).Contains(unreachableAddress1));
            }, _config.Observer1);

            EnterBarrier("first-unreachable");

            RunOn(() =>
            {
                // make second node unreachable
                TestConductor.Exit(_config.Unreachable2, 0).Wait();

                AwaitCondition(() => Cluster.State.Unreachable.Select(x => x.Address).Contains(unreachableAddress2));
            }, _config.Observer1);

            EnterBarrier("second-unreachable");

            RunOn(() =>
            {
                // mark the first unreachable node as down
                Cluster.Down(unreachableAddress1);
                EnterBarrier("down-first-unreachable");

                // should still have the second unreachable node considered as a member of the cluster
                AwaitMembersUp(3, ImmutableHashSet.Create(unreachableAddress1));
                ClusterView.Members.Any(x => x.Address == unreachableAddress1).Should().BeFalse();

                Cluster.State.Unreachable.Any(x => x.Address.Equals(unreachableAddress1)).Should().BeFalse();
                Cluster.State.Unreachable.Any(x => x.Address.Equals(unreachableAddress2)).Should().BeTrue();
            }, _config.Observer1);

            RunOn(() =>
            {
                EnterBarrier("down-first-unreachable");
            }, _config.Observer2);
        }

        private void Cluster_should_down_second_unreachable_node_separately()
        {
            
        }
    }
}
