using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Akka.Cluster
{
    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal sealed class MembershipState
    {
        private static readonly ISet<MemberStatus> LeaderMemberStatus = new HashSet<MemberStatus>(new[] { MemberStatus.Up, MemberStatus.Leaving });
        private static readonly ISet<MemberStatus> ConvergenceMemberStatus = new HashSet<MemberStatus>(new[] { MemberStatus.Up, MemberStatus.Leaving });

        public static readonly ISet<MemberStatus> ConvergenceSkipUnreachableWithMemberStatus = new HashSet<MemberStatus>(new[] { MemberStatus.Down, MemberStatus.Exiting });
        public static readonly ISet<MemberStatus> RemoveUnreachableWithMemberStatus = new HashSet<MemberStatus>(new[] { MemberStatus.Down, MemberStatus.Exiting });

        private readonly Gossip _lastestGossip;
        private readonly Lazy<Member> _selfMember;


        public MembershipState(Gossip lastestGossip, UniqueAddress selfUniqueAddress)
        {
            _lastestGossip = lastestGossip;
            SelfUniqueAddress = selfUniqueAddress;
            _selfMember = new Lazy<Member>(() => _lastestGossip.GetMember(SelfUniqueAddress));
        }

        public UniqueAddress SelfUniqueAddress { get; }

        /// <summary>
        /// The current cluster members.
        /// </summary>
        public ImmutableSortedSet<Member> Members => _lastestGossip.Members;

        /// <summary>
        /// The current cluster overview.
        /// </summary>
        public GossipOverview Overview => _lastestGossip.Overview;

        /// <summary>
        /// The reachability of the current cluster.
        /// </summary>
        public Reachability Reachability => Overview.Reachability;

        private Reachability _reachabilityExcludingDownedObservers;

        /// <summary>
        /// Reachability for the current cluster filtering out any members that are current down.
        /// </summary>
        public Reachability ReachabilityExcludingDownedObservers
        {
            get
            {
                if (_reachabilityExcludingDownedObservers == null)
                {
                    var membersToExclude = Members.Where(x => x.Status == MemberStatus.Down).Select(x => x.UniqueAddress).ToImmutableHashSet();
                    _reachabilityExcludingDownedObservers = Overview.Reachability.RemoveObservers(membersToExclude);
                }

                return _reachabilityExcludingDownedObservers;
            }
        }

        /// <summary>
        /// The current leader of this cluster.
        /// </summary>
        public UniqueAddress Leader => LeaderOf(Members);

        /// <summary>
        /// Determines if the current address is the leader.
        /// </summary>
        /// <param name="address">The address to check.</param>
        /// <returns><c>true</c> if the current address is the leader; <c>false</c> otherwise.</returns>
        public bool IsLeader(UniqueAddress address)
        {
            return Leader?.Equals(address) ?? false;
        }

        /// <summary>
        /// Determine the leader of the current cluster, if any.
        /// </summary>
        /// <param name="members">The members of the current cluster.</param>
        /// <returns><c>null</c> if there are no reachable members. Otherwise, the unique address of the node that is currently the leader.</returns>
        public UniqueAddress LeaderOf(ImmutableSortedSet<Member> members)
        {
            var reachability = Reachability;

            var reachableMembers = (reachability.IsAllReachable
                ? members.Where(x => x.Status != MemberStatus.Down)
                : members.Where(y =>
                    y.Status != MemberStatus.Down && (reachability.IsReachable(y.UniqueAddress) ||
                                                      y.UniqueAddress.Equals(SelfUniqueAddress))))
                                                      .ToImmutableSortedSet();

            if (reachableMembers.IsEmpty) return null;
            return reachableMembers.FirstOrDefault(x => LeaderMemberStatus.Contains(x.Status))?.UniqueAddress ??
                   reachableMembers.Min(Member.LeaderStatusOrdering).UniqueAddress;
        }

        /// <summary>
        /// Determines if a node is a valid target for gossip.
        /// </summary>
        /// <param name="node">The address of the target node.</param>
        /// <returns><c>true</c> if valid; <c>false</c> otherwise.</returns>
        public bool ValidNodeForGossip(UniqueAddress node)
        {
            return node != SelfUniqueAddress && Overview.Reachability.IsReachable(node);
        }

        /// <summary>
        /// The youngest member of the cluster.
        /// </summary>
        public Member YoungestMember
        {
            get
            {
                if(Members.IsEmpty)
                    throw new ArgumentException("No youngest when no cluster members");
                return Members.MaxBy(m => m.UpNumber == Int32.MaxValue ? 0 : m.UpNumber);
            }
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal class GossipTargetSelector
    {
        public GossipTargetSelector(double reduceGossipDifferentViewProbability)
        {
            ReduceGossipDifferentViewProbability = reduceGossipDifferentViewProbability;
        }

        public double ReduceGossipDifferentViewProbability { get; }
    }
}
