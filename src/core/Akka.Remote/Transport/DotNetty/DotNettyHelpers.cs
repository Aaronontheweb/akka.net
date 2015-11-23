using System;
using System.Net;
using System.Threading.Tasks;
using Akka.Actor;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;

namespace Akka.Remote.Transport.DotNetty
{
    /// <summary>
    ///     INTERNAL API
    /// </summary>
    internal class DotNettyTransportException : AkkaException
    {
        public DotNettyTransportException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    ///     INTERNAL API
    /// </summary>
    internal abstract class CommonHandlers : ChannelHandlerAdapter
    {
        protected readonly DotNettyTransport Transport;

        protected CommonHandlers(DotNettyTransport transport)
        {
            Transport = transport;
        }

        protected abstract void OnConnect(IChannelHandlerContext ctx);
        protected abstract void OnDisconnect(IChannelHandlerContext ctx);
        protected abstract void OnMessage(IChannelHandlerContext ctx, object message);
        protected abstract void OnException(IChannelHandlerContext ctx, Exception ex);

        protected void TransformException(IChannelHandlerContext ctx, Exception ex)
        {
            var cause = ex ?? new DotNettyTransportException("Unknown cause");
            if (cause is ClosedChannelException)
            {
                //ignore
            }
            else
            {
                OnException(ctx, cause);
            }
        }

        protected abstract AssociationHandle CreateHandle(IChannel channel, Address localAddress, Address remoteAddress);

        protected abstract void RegisterListener(IChannel channel, IHandleEventListener listener, IByteBuffer message,
            EndPoint remoteSocketAddress);

        protected void Init(IChannel channel, EndPoint remoteSocketAddress, Address remoteAddress, IByteBuffer msg,
            Action<AssociationHandle> op)
        {
            var address = DotNettyTransport.AddressFromSocketAddress(channel.LocalAddress, Transport.SchemeIdentifier,
                Transport.System.Name, Transport.Settings.Hostname, null);
            if (address != null)
            {
                var handle = CreateHandle(channel, address, remoteAddress);
                handle.ReadHandlerSource.Task.ContinueWith(tr =>
                {
                    var listener = tr.Result;
                    RegisterListener(channel, listener, msg, remoteSocketAddress);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
                op(handle);
            }
            else
            {
                DotNettyTransport.GracefulClose(channel);
            }
        }

        public override void ChannelRead(IChannelHandlerContext context, object message)
        {
            base.ChannelRead(context, message);
            OnMessage(context, message);
        }

        public override void ChannelActive(IChannelHandlerContext context)
        {
            base.ChannelActive(context);
            OnConnect(context);
        }

        public override void ChannelInactive(IChannelHandlerContext context)
        {
            base.ChannelInactive(context);
            OnDisconnect(context);
        }

        public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
        {
            TransformException(context, exception);
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal abstract class ServerHandler : CommonHandlers
    {
        private readonly Task<IAssociationEventListener> _associationListenerFuture;

        protected ServerHandler(DotNettyTransport transport, Task<IAssociationEventListener> associationListenerFuture)
            : base(transport)
        {
            _associationListenerFuture = associationListenerFuture;
        }

        protected void InitInbound(IChannel channel, EndPoint remoteSocketAddress, IByteBuffer msg)
        {
            _associationListenerFuture.ContinueWith(tr =>
            {
                var listener = tr.Result;
                var remoteAddress = DotNettyTransport.AddressFromSocketAddress(remoteSocketAddress,
                    Transport.SchemeIdentifier, Transport.System.Name);
                if (remoteAddress == null)
                    throw new DotNettyTransportException($"Unknown inbound remote address type [{remoteSocketAddress}]");
                Init(channel, remoteSocketAddress, remoteAddress, msg,
                    handle => { listener.Notify(new InboundAssociation(handle)); });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal abstract class ClientHandler : CommonHandlers
    {
        protected readonly Address RemoteAddress;

        protected readonly TaskCompletionSource<AssociationHandle> StatusPromise =
            new TaskCompletionSource<AssociationHandle>();

        protected ClientHandler(DotNettyTransport transport, Address remoteAddress) : base(transport)
        {
            RemoteAddress = remoteAddress;
        }

        protected void InitOutbound(IChannel channel, EndPoint remoteSocketAddress, IByteBuffer msg)
        {
            Init(channel, remoteSocketAddress, RemoteAddress, msg, handle => StatusPromise.SetResult(handle));
        }
    }
}