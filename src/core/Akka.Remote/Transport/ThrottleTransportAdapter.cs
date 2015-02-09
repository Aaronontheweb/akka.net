using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Util;
using Akka.Util.Internal;
using Google.ProtocolBuffers;

namespace Akka.Remote.Transport
{
    public class ThrottlerProvider : ITransportAdapterProvider
    {
        public Transport Create(Transport wrappedTransport, ActorSystem system)
        {
            throw new NotImplementedException();
        }
    }

    internal class ThrottleTransportAdapter : ActorTransportAdapter
    {
        #region Static methods and self-contained data types

        public const string Scheme = "trttl";
        public static readonly AtomicCounter UniqueId = new AtomicCounter(0);

        public enum Direction
        {
            Send,
            Receive,
            Both
        }

        #endregion

        public ThrottleTransportAdapter(Transport wrappedTransport, ActorSystem system) : base(wrappedTransport, system)
        {
        }

// ReSharper disable once InconsistentNaming
        private static readonly SchemeAugmenter _schemeAugmenter = new SchemeAugmenter(Scheme);
        protected override SchemeAugmenter SchemeAugmenter
        {
            get { return _schemeAugmenter; }
        }

        protected override string ManagerName
        {
            get
            {
                return string.Format("throttlermanager.${0}${1}", WrappedTransport.SchemeIdentifier, UniqueId.GetAndIncrement());
            }
        }

        protected override Props ManagerProps
        {
            get { throw new NotImplementedException(); }
        }
    }

    /// <summary>
    /// Management command to force disassociation of an address
    /// </summary>
    public sealed class ForceDisassociate
    {
        public ForceDisassociate(Address address)
        {
            Address = address;
        }

        public Address Address { get; private set; }
    }

    /// <summary>
    /// Management command to force disassociation of an address with an explicit error.
    /// </summary>
    public sealed class ForceDisassociateExplicitly
    {
        public ForceDisassociateExplicitly(Address address, DisassociateInfo reason)
        {
            Reason = reason;
            Address = address;
        }

        public Address Address { get; private set; }

        public DisassociateInfo Reason { get; private set; }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    public sealed class ForceDisassociateAck
    {
        private ForceDisassociateAck() { }
// ReSharper disable once InconsistentNaming
        private static readonly ForceDisassociateAck _instance = new ForceDisassociateAck();

        public static ForceDisassociateAck Instance
        {
            get
            {
                return _instance;
            }
        }
    }

    internal class ThrottlerManager : ActorTransportAdapterManager
    {
        protected readonly Transport WrappedTransport;
        private Dictionary<Address, Tuple<ThrottleMode, ThrottleTransportAdapter.Direction>> _throttlingModes 
            = new Dictionary<Address, Tuple<ThrottleMode, ThrottleTransportAdapter.Direction>>();
        

        public ThrottlerManager(Transport wrappedTransport)
        {
            WrappedTransport = wrappedTransport;
        }

        protected override void Ready(object message)
        {
            throw new NotImplementedException();
        }
    }

    public abstract class ThrottleMode : NoSerializationVerificationNeeded
    {
        public abstract Tuple<ThrottleMode, bool> TryConsumeTokens(long nanoTimeOfSend, int tokens);
        public abstract TimeSpan TimeToAvailable(long currentNanoTime, int tokens);
    }

    public class Blackhole : ThrottleMode
    {
        private Blackhole() { }
// ReSharper disable once InconsistentNaming
        private static readonly Blackhole _instance = new Blackhole();

        public static Blackhole Instance
        {
            get
            {
                return _instance;
            }
        }

        public override Tuple<ThrottleMode, bool> TryConsumeTokens(long nanoTimeOfSend, int tokens)
        {
            return Tuple.Create<ThrottleMode, bool>(this, true);
        }

        public override TimeSpan TimeToAvailable(long currentNanoTime, int tokens)
        {
            return TimeSpan.Zero;
        }
    }

    public class Unthrottled : ThrottleMode
    {
        private Unthrottled() { }
        private static readonly Unthrottled _instance = new Unthrottled();

        public static Unthrottled Instance
        {
            get
            {
                return _instance;
            }
        }

        public override Tuple<ThrottleMode, bool> TryConsumeTokens(long nanoTimeOfSend, int tokens)
        {
            return Tuple.Create<ThrottleMode, bool>(this, false);
        }

        public override TimeSpan TimeToAvailable(long currentNanoTime, int tokens)
        {
            return TimeSpan.Zero;
        }
    }

    sealed class TokenBucket : ThrottleMode
    {
        readonly int _capacity;
        readonly double _tokensPerSecond;
        readonly long _nanoTimeOfLastSend;
        readonly int _availableTokens;

        public TokenBucket(int capacity, double tokensPerSecond, long nanoTimeOfLastSend, int availableTokens)
        {
            _capacity = capacity;
            _tokensPerSecond = tokensPerSecond;
            _nanoTimeOfLastSend = nanoTimeOfLastSend;
            _availableTokens = availableTokens;
        }

        bool IsAvailable(long nanoTimeOfSend, int tokens)
        {
            if (tokens > _capacity && _availableTokens > 0)
                return true; // Allow messages larger than capacity through, it will be recorded as negative tokens
            return Math.Min(_availableTokens + TokensGenerated(nanoTimeOfSend), _capacity) >= tokens;
        }

        public override Tuple<ThrottleMode, bool> TryConsumeTokens(long nanoTimeOfSend, int tokens)
        {
            if (IsAvailable(nanoTimeOfSend, tokens))
            {
                return Tuple.Create<ThrottleMode, bool>(Copy(
                    nanoTimeOfLastSend: nanoTimeOfSend,
                    availableTokens: Math.Min(_availableTokens - tokens + TokensGenerated(nanoTimeOfSend), _capacity))
                    , true);
            }
            return Tuple.Create<ThrottleMode, bool>(this, false);
        }

        public override TimeSpan TimeToAvailable(long currentNanoTime, int tokens)
        {
            var needed = (tokens > _capacity ? 1 : tokens) - TokensGenerated(currentNanoTime);
            return TimeSpan.FromSeconds(needed / _tokensPerSecond);
        }

        int TokensGenerated(long nanoTimeOfSend)
        {
            return Convert.ToInt32((nanoTimeOfSend - nanoTimeOfSend) * 1000000 * _tokensPerSecond / 1000);
        }

        TokenBucket Copy(int? capacity = null, double? tokensPerSecond = null, long? nanoTimeOfLastSend = null, int? availableTokens = null)
        {
            return new TokenBucket(
                capacity ?? _capacity,
                tokensPerSecond ?? _tokensPerSecond,
                nanoTimeOfLastSend ?? _nanoTimeOfLastSend,
                availableTokens ?? _availableTokens);
        }

        private bool Equals(TokenBucket other)
        {
            return _capacity == other._capacity
                && _tokensPerSecond.Equals(other._tokensPerSecond)
                && _nanoTimeOfLastSend == other._nanoTimeOfLastSend
                && _availableTokens == other._availableTokens;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is TokenBucket && Equals((TokenBucket)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = _capacity;
                hashCode = (hashCode * 397) ^ _tokensPerSecond.GetHashCode();
                hashCode = (hashCode * 397) ^ _nanoTimeOfLastSend.GetHashCode();
                hashCode = (hashCode * 397) ^ _availableTokens;
                return hashCode;
            }
        }

        public static bool operator ==(TokenBucket left, TokenBucket right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(TokenBucket left, TokenBucket right)
        {
            return !Equals(left, right);
        }
    }

    internal sealed class SetThrottle
    {
        readonly Address _address;
        public Address Address { get { return _address; } }
        readonly ThrottleTransportAdapter.Direction _direction;
        public ThrottleTransportAdapter.Direction Direction { get { return _direction; } }
        readonly ThrottleMode _mode;
        public ThrottleMode Mode { get { return _mode; } }

        public SetThrottle(Address address, ThrottleTransportAdapter.Direction direction, ThrottleMode mode)
        {
            _address = address;
            _direction = direction;
            _mode = mode;
        }

        private bool Equals(SetThrottle other)
        {
            return Equals(_address, other._address) && _direction == other._direction && Equals(_mode, other._mode);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            return obj is SetThrottle && Equals((SetThrottle)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = (_address != null ? _address.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ (int)_direction;
                hashCode = (hashCode * 397) ^ (_mode != null ? _mode.GetHashCode() : 0);
                return hashCode;
            }
        }

        public static bool operator ==(SetThrottle left, SetThrottle right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(SetThrottle left, SetThrottle right)
        {
            return !Equals(left, right);
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal sealed class ThrottlerHandle : AbstractTransportAdapterHandle
    {
        private readonly ActorRef _throttlerActor;
        private AtomicReference<ThrottleMode> _outboundThrottleMode = new AtomicReference<ThrottleMode>(Unthrottled.Instance);

        /// <summary>
        /// Need to have time that is much more precise than <see cref="DateTime.Now"/> when throttling sends
        /// </summary>
        private readonly Stopwatch _stopWatch = new Stopwatch();

        public ThrottlerHandle(AssociationHandle wrappedHandle, ActorRef throttlerActor) : base(wrappedHandle, ThrottleTransportAdapter.Scheme)
        {
            _throttlerActor = throttlerActor;
            ReadHandlerSource = new TaskCompletionSource<IHandleEventListener>();
            _stopWatch.Start();
        }

        public override bool Write(ByteString payload)
        {
            var tokens = payload.Length;
            //need to declare recursive delegates first before they can self-reference
            //might want to consider making this consumer function strongly typed: http://blogs.msdn.com/b/wesdyer/archive/2007/02/02/anonymous-recursion-in-c.aspx
            Func<ThrottleMode, bool> tryConsume = null; 
            tryConsume = currentBucket =>
            {
                var timeOfSend = _stopWatch.ElapsedTicks;
                var res = currentBucket.TryConsumeTokens(timeOfSend, tokens);
                var newBucket = res.Item1;
                var allow = res.Item2;
                if (allow)
                {
                    return _outboundThrottleMode.CompareAndSet(currentBucket, newBucket) || tryConsume(_outboundThrottleMode.Value);
                }
                return false;
            };

            var throttleMode = _outboundThrottleMode.Value;
            if (throttleMode is Blackhole) return true;
            
            var sucess = tryConsume(_outboundThrottleMode.Value);
            return sucess && WrappedHandle.Write(payload);
        }

        public override void Disassociate()
        {
            _throttlerActor.Tell(PoisonPill.Instance);
        }

        public void DisassociateWithFailure(DisassociateInfo reason)
        {
            _throttlerActor.Tell(new ThrottledAssociation.FailWith(reason));
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal class ThrottledAssociation : FSM<ThrottledAssociation.ThrottlerState, ThrottledAssociation.IThrottlerData>
    {
        #region ThrottledAssociation FSM state and data classes

        
        public enum ThrottlerState
        {
            /*
             * STATES FOR INBOUND ASSOCIATIONS
             */

            /// <summary>
            /// Waiting for the <see cref="ThrottlerHandle"/> coupled with the throttler actor.
            /// </summary>
            WaitExposedHandle,
            /// <summary>
            /// Waiting for the ASSOCIATE message that contains the origin address of the remote endpoint
            /// </summary>
            WaitOrigin,
            /// <summary>
            /// After origin is known and a Checkin message is sent to the manager, we must wait for the <see cref="ThrottleMode"/>
            /// for the address
            /// </summary>
            WaitMode,
            /// <summary>
            /// After all information is known, the throttler must wait for the upstream listener to be able to forward messages
            /// </summary>
            WaitUpstreamListener,

            /*
             * STATES FOR OUTBOUND ASSOCIATIONS
             */
            /// <summary>
            /// Waiting for the tuple containing the upstream listener and the <see cref="ThrottleMode"/>
            /// </summary>
            WaitModeAndUpstreamListener,
            /// <summary>
            /// Fully initialized state
            /// </summary>
            Throttling
        }

        internal interface IThrottlerData { }

        internal class Uninitialized : IThrottlerData
        {
            private Uninitialized() { }
// ReSharper disable once InconsistentNaming
            private static readonly Uninitialized _instance = new Uninitialized();
            public Uninitialized Instance { get { return _instance; } }
        }

        internal sealed class ExposedHandle : IThrottlerData
        {
            public ExposedHandle(ThrottlerHandle handle)
            {
                Handle = handle;
            }

            public ThrottlerHandle Handle { get; private set; }
        }

        internal sealed class FailWith
        {
            public FailWith(DisassociateInfo failReason)
            {
                FailReason = failReason;
            }

            public DisassociateInfo FailReason { get; private set; }
        }

        #endregion
    }
}
