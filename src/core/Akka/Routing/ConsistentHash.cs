using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Util;

namespace Akka.Routing
{
    /// <summary>
    /// Marks a given class as consistently hashable, for use with <see cref="ConsistentHashingGroup"/>
    /// or <see cref="ConsistentHashingPool"/> routers.
    /// </summary>
    public interface IConsistentHashable
    {
        object ConsistentHashKey { get; }
    }

    public class ConsistentHash<T>
    {
        private readonly SortedDictionary<int, T> _nodes;
        private readonly int _virtualNodesFactor;

        public ConsistentHash(SortedDictionary<int, T> nodes, int virtualNodesFactor)
        {
            _nodes = nodes;

            Guard.Assert(virtualNodesFactor >= 1, "virtualNodesFactor must be >= 1");

            _virtualNodesFactor = virtualNodesFactor;
        }

        private Tuple<int[], T[]> _ring = null;
        private Tuple<int[], T[]> NodeRing
        {
            get { return _ring ?? (_ring = Tuple.Create<int[], T[]>(_nodes.Keys.ToArray(), _nodes.Values.ToArray())); }
        }

        public ConsistentHash<T> Add(T node)
        {
            var nodeHash = HashFor(node.ToString());
            
        }

        #region Hashing methods

        private int ConcatenateNodeHash(int nodeHash, int vnode)
        {
            
        }

        private int HashFor(object hashKey)
        {
            return Murmur3.Hash(hashKey);
        }

        #endregion
    }
}