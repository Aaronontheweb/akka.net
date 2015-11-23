using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Remote.Transport.Helios;
using Akka.Util.Internal;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Groups;

namespace Akka.Remote.Transport.DotNetty
{
    abstract class TransportMode { }

    class Tcp : TransportMode
    {
        public override string ToString()
        {
            return "tcp";
        }
    }

    class Udp : TransportMode
    {
        public override string ToString()
        {
            return "udp";
        }
    }

    class TcpTransportException : RemoteTransportException
    {
        public TcpTransportException(string message, Exception cause = null) : base(message, cause)
        {
        }
    }

    /// <summary>
    /// Configuration options for the DotNetty transport
    /// </summary>
    class DotNettyTransportSettings
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
            else throw new ConfigurationException(string.Format("Unknown transport transport-protocol='{0}'", protocolString));
            EnableSsl = Config.GetBoolean("enable-ssl");
            ConnectTimeout = Config.GetTimeSpan("connection-timeout");
            WriteBufferHighWaterMark = OptionSize("write-buffer-high-water-mark");
            WriteBufferLowWaterMark = OptionSize("write-buffer-low-water-mark");
            SendBufferSize = OptionSize("send-buffer-size");
            ReceiveBufferSize = OptionSize("receive-buffer-size");
            var size = OptionSize("maximum-frame-size");
            if (size == null || size < 32000) throw new ConfigurationException("Setting 'maximum-frame-size' must be at least 32000 bytes");
            MaxFrameSize = (long)size;
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
    class DotNettyTransport : Transport
    {
        #region Static Members

        /// <summary>
        /// We use 4 bytes to represent the frame length. Used by the <see cref="LengthFieldPrepender"/>
        /// and <see cref="LengthFieldBasedFrameDecoder"/>.
        /// </summary>
        public const int FrameLengthFieldLength = 4;

        public static readonly AtomicCounter UniqueIdCounter = new AtomicCounter(0);

        public static Task GracefulClose(IChannel channel)
        {
            return channel.WriteAndFlushAsync(Unpooled.Empty)
                .ContinueWith(tr => channel.DisconnectAsync())
                .Unwrap()
                .ContinueWith(tc => channel.CloseAsync())
                .Unwrap();
        }

        public static Address AddressFromSocketAddress(EndPoint socketAddress, string schemeIdentifier,
            string systemName, string hostName = null, int? port = null)
        {
            var ipAddress = socketAddress as IPEndPoint;
            if (ipAddress == null) return null;
            return new Address(schemeIdentifier, systemName, hostName ?? ipAddress.Address.ToString(), port ?? ipAddress.Port);
        }

        #endregion

        public readonly IChannelGroup ChannelGroup;
        public DotNettyTransportSettings Settings { get; }

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
        Task<IPEndPoint> AddressToSocketAddress(Address address)
        {
            if(string.IsNullOrEmpty(address.Host) || address.Port == null)
                throw new ArgumentException($"Address {address} does not contain host or port information.");
            return Dns.GetHostEntryAsync(address.Host).ContinueWith(tr =>
            {
                var ipHostEntry = tr.Result;
                var ip = ipHostEntry.AddressList[0];
                return new IPEndPoint(ip, address.Port.Value);
            });
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



}
