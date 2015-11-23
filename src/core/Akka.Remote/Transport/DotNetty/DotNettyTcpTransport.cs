using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Event;
using Akka.Remote.Transport.Helios;
using Akka.Util;
using DotNetty.Transport.Channels;
using Google.ProtocolBuffers;
using Helios.Net;
using Helios.Ops.Executors;

namespace Akka.Remote.Transport.DotNetty
{

    class TcpAssociationHandle : AssociationHandle
    { 

        public TcpAssociationHandle(Address localAddress, Address remoteAddress) : base(localAddress, remoteAddress)
        {
        }

        public override bool Write(ByteString payload)
        {
            throw new NotImplementedException();
        }

        public override void Disassociate()
        {
            throw new NotImplementedException();
        }
    }

    class TcpServerHandler : ChannelHandlerAdapter
    {
        protected DotNettyTcpTransport Transport;
        private readonly Task<IAssociationEventListener> _associationListenerFuture;

        public TcpServerHandler(DotNettyTcpTransport transport, Task<IAssociationEventListener> associationListenerFuture)
        {
            Transport = transport;
            _associationListenerFuture = associationListenerFuture;
        }

        protected void InitInbound(IChannel channel, EndPoint remoteSocketAddress)
        {
            
        }
    }

    class DotNettyTcpTransport : Transport
    {
        protected ILoggingAdapter Log;

        private volatile Address LocalAddress;
        private volatile Address BoundTo;
        private volatile IChannel ServerChannel;

        protected DotNettyTcpTransport(ActorSystem system, Config config)
        {
            Config = config;
            System = system;
            Settings = new DotNettyTransportSettings(config);
            Log = Logging.GetLogger(System, GetType());
        }

        public DotNettyTransportSettings Settings { get; private set; }

        public override Task<Tuple<Address, TaskCompletionSource<IAssociationEventListener>>> Listen()
        {
            throw new NotImplementedException();
        }

        public override bool IsResponsibleFor(Address remote)
        {
            throw new NotImplementedException();
        }

        public override Task<AssociationHandle> Associate(Address remoteAddress)
        {
            throw new NotImplementedException();
        }

        public override Task<bool> Shutdown()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    static class ChannelLocalActor
    {
        private static readonly ConcurrentDictionary<IChannel, IHandleEventListener> ChannelActors = new ConcurrentDictionary<IChannel, IHandleEventListener>();

        public static void Set(IChannel channel, IHandleEventListener listener = null)
        {
            ChannelActors.AddOrUpdate(channel, listener, (connection, eventListener) => listener);
        }

        public static void Remove(IChannel channel)
        {
            IHandleEventListener listener;
            ChannelActors.TryRemove(channel, out listener);
        }

        public static void Notify(IChannel channel, IHandleEvent msg)
        {
            IHandleEventListener listener;

            if (ChannelActors.TryGetValue(channel, out listener))
            {
                listener.Notify(msg);
            }
        }
    }
}
