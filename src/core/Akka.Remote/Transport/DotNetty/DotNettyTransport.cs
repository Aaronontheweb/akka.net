using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Event;
using Akka.Remote.Transport.Helios;
using Akka.Util.Internal;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Groups;
using DotNetty.Transport.Channels.Sockets;
using Helios.Net.Bootstrap;

namespace Akka.Remote.Transport.DotNetty
{
    internal abstract class TransportMode
    {
    }

    internal class Tcp : TransportMode
    {
        public override string ToString()
        {
            return "tcp";
        }
    }

    internal class Udp : TransportMode
    {
        public override string ToString()
        {
            return "udp";
        }
    }

    internal class TcpTransportException : RemoteTransportException
    {
        public TcpTransportException(string message, Exception cause = null) : base(message, cause)
        {
        }
    }

    /// <summary>
    /// Configuration options for the DotNetty transport
    /// </summary>
    internal class DotNettyTransportSettings
    {
        internal readonly Config Config;

        public DotNettyTransportSettings(Config config)
        {
            Config = config;
            Init();
        }

        private void Init()
        {
            //TransportMode
            var protocolString = Config.GetString("transport-protocol");
            if (protocolString.Equals("tcp")) TransportMode = new Tcp();
            else if (protocolString.Equals("udp")) TransportMode = new Udp();
            else
                throw new ConfigurationException(string.Format("Unknown transport transport-protocol='{0}'",
                    protocolString));
            EnableSsl = Config.GetBoolean("enable-ssl");
            ConnectTimeout = Config.GetTimeSpan("connection-timeout");
            WriteBufferHighWaterMark = OptionSize("write-buffer-high-water-mark");
            WriteBufferLowWaterMark = OptionSize("write-buffer-low-water-mark");
            SendBufferSize = OptionSize("send-buffer-size");
            ReceiveBufferSize = OptionSize("receive-buffer-size");
            var size = OptionSize("maximum-frame-size");
            if (size == null || size < 32000)
                throw new ConfigurationException("Setting 'maximum-frame-size' must be at least 32000 bytes");
            MaxFrameSize = (long) size;
            Backlog = Config.GetInt("backlog");
            TcpNoDelay = Config.GetBoolean("tcp-nodelay");
            TcpKeepAlive = Config.GetBoolean("tcp-keepalive");
            TcpReuseAddr = Config.GetBoolean("tcp-reuse-addr");
            var configHost = Config.GetString("hostname");
            var publicConfigHost = Config.GetString("public-hostname");
            Hostname = string.IsNullOrEmpty(configHost) ? IPAddress.Any.ToString() : configHost;
            PublicHostname = string.IsNullOrEmpty(publicConfigHost) ? configHost : publicConfigHost;
            ServerSocketWorkerPoolSize = ComputeWps(Config.GetConfig("server-socket-worker-pool"));
            ClientSocketWorkerPoolSize = ComputeWps(Config.GetConfig("client-socket-worker-pool"));
            Port = Config.GetInt("port");

            //TODO: SslSettings
        }

        public TransportMode TransportMode { get; private set; }

        public bool EnableSsl { get; private set; }

        public TimeSpan ConnectTimeout { get; private set; }

        public long? WriteBufferHighWaterMark { get; private set; }

        public long? WriteBufferLowWaterMark { get; private set; }

        public long? SendBufferSize { get; private set; }

        public long? ReceiveBufferSize { get; private set; }

        public long MaxFrameSize { get; private set; }

        public int Port { get; private set; }

        public int Backlog { get; private set; }

        public bool TcpNoDelay { get; private set; }

        public bool TcpKeepAlive { get; private set; }

        public bool TcpReuseAddr { get; private set; }

        /// <summary>
        /// The hostname that this server binds to
        /// </summary>
        public string Hostname { get; private set; }

        /// <summary>
        /// If different from <see cref="Hostname"/>, this is the public "address" that is bound to the <see cref="ActorSystem"/>,
        /// whereas <see cref="Hostname"/> becomes the physical address that the low-level socket connects to.
        /// </summary>
        public string PublicHostname { get; private set; }

        public int ServerSocketWorkerPoolSize { get; private set; }

        public int ClientSocketWorkerPoolSize { get; private set; }

        #region Internal methods

        private long? OptionSize(string s)
        {
            var bytes = Config.GetByteSize(s);
            if (bytes == null || bytes == 0) return null;
            if (bytes < 0) throw new ConfigurationException(string.Format("Setting {0} must be 0 or positive", s));
            return bytes;
        }

        private int ComputeWps(Config config)
        {
            return ThreadPoolConfig.ScaledPoolSize(config.GetInt("pool-size-min"), config.GetDouble("pool-size-factor"),
                config.GetInt("pool-size-max"));
        }

        #endregion
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal class DotNettyTransport : Transport
    {
        #region Static Members

        /// <summary>
        /// We use 4 bytes to represent the frame length. Used by the <see cref="LengthFieldPrepender"/>
        /// and <see cref="LengthFieldBasedFrameDecoder"/>.
        /// </summary>
        public const int FrameLengthFieldLength = 4;

        public static readonly AtomicCounter UniqueIdCounter = new AtomicCounter(0);

        public static void GracefulClose(IChannel channel)
        {
            // TODO: check if this needs a configurable timeout setting
            channel.WriteAndFlushAsync(Unpooled.Empty)
                .ContinueWith(tr => channel.DisconnectAsync())
                .Unwrap()
                .ContinueWith(tc => channel.CloseAsync())
                .Unwrap().Wait();
        }

        public static Address AddressFromSocketAddress(EndPoint socketAddress, string schemeIdentifier,
            string systemName, string hostName = null, int? port = null)
        {
            var ipAddress = socketAddress as IPEndPoint;
            if (ipAddress == null) return null;
            return new Address(schemeIdentifier, systemName, hostName ?? ipAddress.Address.ToString(),
                port ?? ipAddress.Port);
        }

        #endregion

        //public readonly IChannelGroup ChannelGroup;

        protected ILoggingAdapter Log;

        private volatile Address LocalAddress;
        private volatile Address BoundTo;
        private volatile IChannel ServerChannel;

        private readonly MultithreadEventLoopGroup _serverBossGroup;
        private readonly MultithreadEventLoopGroup _serverWorkerGroup;

        private readonly MultithreadEventLoopGroup _clientBossGroup;
        private readonly MultithreadEventLoopGroup _clientWorkerGroup;

        private TaskCompletionSource<IAssociationEventListener> _associationEventListenerPromise =
            new TaskCompletionSource<IAssociationEventListener>();

        public DotNettyTransportSettings Settings { get; }

        protected DotNettyTransport(ActorSystem system, Config config)
        {
            Config = config;
            System = system;
            Settings = new DotNettyTransportSettings(config);
            Log = Logging.GetLogger(System, GetType());

            _serverBossGroup = new MultithreadEventLoopGroup(1);
            _serverWorkerGroup = new MultithreadEventLoopGroup(Settings.ServerSocketWorkerPoolSize);

            _clientBossGroup = new MultithreadEventLoopGroup(1);
            _clientWorkerGroup = new MultithreadEventLoopGroup(Settings.ClientSocketWorkerPoolSize);

            //ChannelGroup = new DefaultChannelGroup("akka-netty-transport-driver-channelgroup-" + UniqueIdCounter.GetAndIncrement(), );
        }

        public bool IsDatagram
        {
            get { return Settings.TransportMode is Udp; }
        }

        private ServerBootstrap SetupServerBootstrap(ServerBootstrap b)
        {
            var a = b
                .Group(_serverBossGroup, _clientWorkerGroup)
                .ChildOption(ChannelOption.SoBacklog, Settings.Backlog)
                .ChildOption(ChannelOption.TcpNodelay, Settings.TcpNoDelay)
                .ChildOption(ChannelOption.SoKeepalive, Settings.TcpKeepAlive)
                .ChildOption(ChannelOption.SoReuseaddr, Settings.TcpReuseAddr)
                .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    IChannelPipeline pipeline = channel.Pipeline;
                    BindServerPipeline(pipeline);
                }));
            ;
            if (Settings.ReceiveBufferSize.HasValue)
                a = a.ChildOption(ChannelOption.SoRcvbuf, (int) Settings.ReceiveBufferSize.Value);
            if (Settings.SendBufferSize.HasValue)
                a = a.ChildOption(ChannelOption.SoSndbuf, (int) Settings.SendBufferSize.Value);
            if (Settings.WriteBufferHighWaterMark.HasValue)
                a = a.ChildOption(ChannelOption.WriteBufferHighWaterMark, (int) Settings.WriteBufferHighWaterMark.Value);
            if (Settings.WriteBufferLowWaterMark.HasValue)
                a = a.ChildOption(ChannelOption.WriteBufferLowWaterMark, (int) Settings.WriteBufferLowWaterMark.Value);
            return a;
        }
        
        private ServerBootstrap InboundBootstrap()
        {
            if (IsDatagram) throw new NotImplementedException("UDP not supported");
            return SetupServerBootstrap(new ServerBootstrap().Channel<TcpServerSocketChannel>());
        }

        private Bootstrap OutboundBootstrap(Address remoteAddress)
        {
            if (IsDatagram) throw new NotImplementedException("UDP not supported");
           var b = new Bootstrap().Channel<TcpSocketChannel>()
                .Group(_clientWorkerGroup)
                .Option(ChannelOption.TcpNodelay, Settings.TcpNoDelay)
                .Option(ChannelOption.SoKeepalive, Settings.TcpKeepAlive)
                .Handler(new ActionChannelInitializer<IChannel>(channel =>
                {
                    IChannelPipeline pipeline = channel.Pipeline;
                    BindClientPipeline(pipeline, remoteAddress);
                }));
            
            if (Settings.ReceiveBufferSize.HasValue)
                b = b.Option(ChannelOption.SoRcvbuf, (int)Settings.ReceiveBufferSize.Value);
            if (Settings.SendBufferSize.HasValue)
                b = b.Option(ChannelOption.SoSndbuf, (int)Settings.SendBufferSize.Value);
            if (Settings.WriteBufferHighWaterMark.HasValue)
                b = b.Option(ChannelOption.WriteBufferHighWaterMark, (int)Settings.WriteBufferHighWaterMark.Value);
            if (Settings.WriteBufferLowWaterMark.HasValue)
                b = b.Option(ChannelOption.WriteBufferLowWaterMark, (int)Settings.WriteBufferLowWaterMark.Value);
            return b;
        }


        /// <summary>
        /// Should only be called by <see cref="BindServerPipeline"/>
        /// or <see cref="BindClientPipeline"/>.
        /// </summary>
        /// <param name="pipeline"></param>
        protected void BindDefaultPipeline(IChannelPipeline pipeline)
        {
            if (!IsDatagram)
            {
                pipeline.AddLast(new LengthFieldBasedFrameDecoder(
                    (int) MaximumPayloadBytes,
                    0,
                    FrameLengthFieldLength,
                    0,
                    FrameLengthFieldLength, // Strip the header
                    true));
                pipeline.AddLast(new LengthFieldPrepender(FrameLengthFieldLength));
            }
        }

        protected void BindServerPipeline(IChannelPipeline pipeline)
        {
            BindDefaultPipeline(pipeline);
            if (Settings.EnableSsl)
            {
                // TODO: SSL support
            }
            var handler = CreateServerHandler(this, _associationEventListenerPromise.Task);
            pipeline.AddLast("ServerHandler", handler);
        }

        protected void BindClientPipeline(IChannelPipeline pipeline, Address remoteAddress)
        {
            BindDefaultPipeline(pipeline);
            if (Settings.EnableSsl)
            {
                // TODO: SSL support
            }
            var handler = CreateClientHandler(this, remoteAddress);
            pipeline.AddLast("ClientHandler", handler);
        }

        protected IChannelHandler CreateClientHandler(DotNettyTransport transport, Address remoteAddress)
        {
            if (IsDatagram)
                throw new NotImplementedException("UDP support not enabled at this time");
            return new TcpClientHandler(transport, remoteAddress);
        }

        protected IChannelHandler CreateServerHandler(DotNettyTransport transport,
            Task<IAssociationEventListener> associationListenerFuture)
        {
            if (IsDatagram)
                throw new NotImplementedException("UDP support not enabled at this time");
            return new TcpServerHandler(transport, associationListenerFuture);
        }

        private ClientBootstrap _clientFactory;

        #region Akka.Remote.Transport Members

        public override string SchemeIdentifier
        {
            get
            {
                var sslPrefix = (Settings.EnableSsl ? "ssl." : "");
                return string.Format("{0}{1}", sslPrefix, Settings.TransportMode);
            }
        }

        public override long MaximumPayloadBytes
        {
            get { return Settings.MaxFrameSize; }
        }

        public override Task<Tuple<Address, TaskCompletionSource<IAssociationEventListener>>> Listen()
        {
            throw new NotImplementedException();
        }

        /*
         * TODO: add configurable subnet filtering
         */

        /// <summary>
        /// Determines if this <see cref="DotNettyTransport"/> instance is responsible
        /// for an association with <see cref="remote"/>.
        /// </summary>
        /// <param name="remote">The remote <see cref="Address"/> on the other end of the association.</param>
        /// <returns><c>true</c> if this transport is responsible for an association at this address,<c>false</c> otherwise.
        /// Always returns <c>true</c> for <see cref="DotNettyTransport"/>.
        /// </returns>
        public override bool IsResponsibleFor(Address remote)
        {
            return true;
        }

        public override Task<AssociationHandle> Associate(Address remoteAddress)
        {
            throw new NotImplementedException();
        }

        /*
         * TODO: might need better error handling
         */

        /// <summary>
        /// Create an <see cref="IPEndPoint"/> for DotNetty from an <see cref="Address"/>.
        /// 
        /// Uses <see cref="Dns"/> internally to perform hostname --> IP resolution.
        /// </summary>
        /// <param name="address">The address of the remote system to which we need to connect.</param>
        /// <returns>A <see cref="Task{IPEndPoint}"/> that will resolve to the correct local or remote IP address.</returns>
        private Task<IPEndPoint> AddressToSocketAddress(Address address)
        {
            if (string.IsNullOrEmpty(address.Host) || address.Port == null)
                throw new ArgumentException($"Address {address} does not contain host or port information.");
            return Dns.GetHostEntryAsync(address.Host).ContinueWith(tr =>
            {
                var ipHostEntry = tr.Result;
                var ip = ipHostEntry.AddressList[0];
                return new IPEndPoint(ip, address.Port.Value);
            });
        }

        public override Task<bool> Shutdown()
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}