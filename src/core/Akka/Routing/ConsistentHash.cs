﻿using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Routing
{
    /// <summary>
    /// Consistent Hashing node ring implementaiton.
    /// 
    ///  A good explanation of Consistent Hashing:
    /// http://weblogs.java.net/blog/tomwhite/archive/2007/11/consistent_hash.html
    /// 
    /// Note that toString of the ring nodes are used for the node
    /// hash, i.e. make sure it is different for different nodes.
    /// </summary>
    public class ConsistentHash<T>
    {
        private readonly SortedDictionary<int, T> _nodes;
        private readonly int _virtualNodesFactor;

        internal ConsistentHash(SortedDictionary<int, T> nodes, int virtualNodesFactor)
        {
            _nodes = nodes;

            Guard.Assert(virtualNodesFactor >= 1, "virtualNodesFactor must be >= 1");

            _virtualNodesFactor = virtualNodesFactor;
        }

        /// <summary>
        /// arrays for fast binary search access
        /// </summary>
        private Tuple<int[], T[]> _ring = null;
        private Tuple<int[], T[]> RingTuple
        {
            get { return _ring ?? (_ring = Tuple.Create(_nodes.Keys.ToArray(), _nodes.Values.ToArray())); }
        }

        /// <summary>
        /// Sorted hash values of the nodes
        /// </summary>
        private int[] NodeHashRing
        {
            get { return RingTuple.Item1; }
        }

        /// <summary>
        /// NodeRing is the nodes sorted in the same order as <see cref="NodeHashRing"/>, i.e. same index
        /// </summary>
        private T[] NodeRing
        {
            get { return RingTuple.Item2; }
        }

        /// <summary>
        /// Add a node to the hash ring.
        /// 
        /// Note that <see cref="ConsistentHash{T}"/> is immutable and
        /// this operation returns a new instance.
        /// </summary>
        public ConsistentHash<T> Add(T node)
        {
            return this + node;
        }

        /// <summary>
        /// Removes a node from the hash ring.
        /// 
        /// Note that <see cref="ConsistentHash{T}"/> is immutable and
        /// this operation returns a new instance.
        /// </summary>
        public ConsistentHash<T> Remove(T node)
        {
            return this - node;
        }

        /// <summary>
        /// Converts the result of <see cref="Array.BinarySearch(T[], T)"/> into an index in the 
        /// <see cref="RingTuple"/> array.
        /// </summary>
        /// <param name="i"></param>
        /// <returns></returns>
        private int Idx(int i)
        {
            if (i >= 0) return i; //exact match
            else
            {
                var j = Math.Abs(i + 1);
                if (j >= NodeHashRing.Length) return 0; //after last, use first
                else return j; //next node clockwise
            }
        }

        /// <summary>
        /// Get the node responsible for the data key.
        /// Can only be used if nodes exist in the node ring.
        /// Otherwise throws <see cref="ArgumentException"/>.
        /// </summary>
        public T NodeFor(byte[] key)
        {
            Guard.Assert(!IsEmpty, string.Format("Can't get node for [{0}] from an empty node ring", key));

            return NodeRing[Idx(Array.BinarySearch(NodeHashRing, ConsistentHash.HashFor(key)))];
        }

        /// <summary>
        /// Get the node responsible for the data key.
        /// Can only be used if nodes exist in the node ring.
        /// Otherwise throws <see cref="ArgumentException"/>.
        /// </summary>
        public T NodeFor(string key)
        {
            Guard.Assert(!IsEmpty, string.Format("Can't get node for [{0}] from an empty node ring", key));

            return NodeRing[Idx(Array.BinarySearch(NodeHashRing, ConsistentHash.HashFor(key)))];
        }

        /// <summary>
        /// Is the node ring empty? i.e. no nodes added or all removed
        /// </summary>
        public bool IsEmpty
        {
            get { return !_nodes.Any(); }
        }

        #region Operator overloads

        /// <summary>
        /// Add a node to the hash ring.
        /// 
        /// Note that <see cref="ConsistentHash{T}"/> is immutable and
        /// this operation returns a new instance.
        /// </summary>s
        public static ConsistentHash<T> operator +(ConsistentHash<T> hash, T node)
        {
            var nodeHash = ConsistentHash.HashFor(node.ToString());
            return new ConsistentHash<T>(hash._nodes.CopyAndAdd(Enumerable.Range(1, hash._virtualNodesFactor).Select(r => new KeyValuePair<int, T>(ConsistentHash.ConcatenateNodeHash(nodeHash, r), node))),
                hash._virtualNodesFactor);
        }

        /// <summary>
        /// Removes a node from the hash ring.
        /// 
        /// Note that <see cref="ConsistentHash{T}"/> is immutable and
        /// this operation returns a new instance.
        /// </summary>
        public static ConsistentHash<T> operator -(ConsistentHash<T> hash, T node)
        {
            var nodeHash = ConsistentHash.HashFor(node.ToString());
            return new ConsistentHash<T>(hash._nodes.CopyAndRemove(Enumerable.Range(1, hash._virtualNodesFactor).Select(r => new KeyValuePair<int, T>(ConsistentHash.ConcatenateNodeHash(nodeHash, r), node))),
                hash._virtualNodesFactor);
        }

        #endregion
    }

    /// <summary>
    /// Static helper class for creating <see cref="ConsistentHash{T}"/> instances.
    /// </summary>
    public static class ConsistentHash
    {
        /// <summary>
        /// Factory method to create a <see cref="ConsistentHash{T}"/> instance.
        /// </summary>
        public static ConsistentHash<T> Create<T>(IEnumerable<T> nodes, int virtualNodesFactor)
        {
            var sortedDict = new SortedDictionary<int, T>();
            foreach (var node in nodes)
            {
                var nodeHash = HashFor(node.ToString());
                var vnodes = Enumerable.Range(1, virtualNodesFactor)
                    .Select(x => ConcatenateNodeHash(nodeHash, x)).ToList();
                foreach(var vnode in vnodes)
                    sortedDict.Add(vnode, node);
            }

            return new ConsistentHash<T>(sortedDict, virtualNodesFactor);
        }

        #region Hashing methods

        internal static int ConcatenateNodeHash(int nodeHash, int vnode)
        {
            unchecked
            {
                var h = MurmurHash.StartHash((uint)nodeHash);
                h = MurmurHash.ExtendHash(h, (uint)vnode, MurmurHash.StartMagicA, MurmurHash.StartMagicB);
                return (int)MurmurHash.FinalizeHash(h);
            }
        }

        internal static int HashFor(byte[] bytes)
        {
            return MurmurHash.ByteHash(bytes);
        }

        internal static int HashFor(string hashKey)
        {
            return MurmurHash.StringHash(hashKey);
        }

        #endregion
    }
}