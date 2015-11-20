using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Akka.Actor;
using DotNetty.Transport.Channels;

namespace Akka.Remote.Transport.DotNetty
{

    internal class DotNettyTcpTransport : Transport
    {
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
