//-----------------------------------------------------------------------
// <copyright file="ArteryNumericRefBenchmarks.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.Remote;
using Akka.Remote.Artery;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace Akka.Benchmarks.Remoting.Artery
{
    /// <summary>
    /// The hot paths of numeric ref identity. This file is written to compile and run unchanged on a tree that
    /// does not have numeric refs (every use of a type or setting that only exists with them goes through
    /// reflection and is skipped there), so the same file can produce a before and an after.
    /// </summary>
    internal static class NumericRefSupport
    {
        public static readonly Assembly RemoteAssembly = typeof(RemoteActorRefProvider).Assembly;

        public static readonly Type? NumericRefType = RemoteAssembly.GetType("Akka.Remote.Artery.ArteryNumericRef");
        public static readonly Type? NumericWireRefsType = RemoteAssembly.GetType("Akka.Remote.Artery.NumericWireRefs");

        /// <summary><see langword="true"/> when this tree has numeric refs and their setting.</summary>
        public static bool Available => NumericRefType is not null && NumericWireRefsType is not null;

        public const string SwitchKey = "akka.remote.artery.advanced.numeric-refs";

        public static IEnumerable<string> Switches => Available ? new[] { "off", "on" } : new[] { "off" };

        public static Config SystemConfig(string numericRefs) => ConfigurationFactory.ParseString($$"""
            akka.actor.provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
            akka.remote.artery.enabled = on
            akka.remote.artery.canonical.hostname = "127.0.0.1"
            akka.remote.artery.canonical.port = 0
            akka.log-dead-letters = off
            akka.loglevel = ERROR
            {{(Available ? $"{SwitchKey} = {numericRefs}" : string.Empty)}}
            """);

        /// <summary>A boxed <c>ArteryNumericRef.ForTemporary(owner, counter)</c>.</summary>
        public static object TemporaryRef(long ownerUid, long counter) =>
            NumericRefType!.GetMethod("ForTemporary", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new object[] { ownerUid, counter })!;

        /// <summary>A <c>NumericWireRefs</c> holding only a numeric recipient.</summary>
        public static object WireRefsFor(object recipientRef) =>
            NumericWireRefsType!.GetMethod("Of", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new[] { null, recipientRef })!;

        /// <summary>The inbound envelope for a message to a numeric recipient with a literal sender.</summary>
        public static IInboundEnvelope NumericEnvelope(object message, string? senderPath, long originUid, object wireRefs)
        {
            var type = RemoteAssembly.GetType("Akka.Remote.Artery.InboundEnvelope")!;
            var ctor = type.GetConstructors().Single(c => c.GetParameters().Length == 7);
            return (IInboundEnvelope)ctor.Invoke(new[] { message, senderPath, null, originUid, 1, "S", wireRefs });
        }

        public static IInboundEnvelope LiteralEnvelope(object message, string? senderPath, string recipientPath, long originUid) =>
            new InboundEnvelope(message, senderPath, recipientPath, originUid, 1, "S");
    }

    /// <summary>
    /// Ask round trip between two in-process ActorSystems over Artery TCP loopback, with numeric refs off and on.
    /// </summary>
    [MemoryDiagnoser]
    public class ArteryNumericRefAskBenchmarks
    {
        private const int Concurrent = 64;

        private sealed class Echo : ReceiveActor
        {
            public Echo() => ReceiveAny(message => Sender.Tell(message));
        }

        public IEnumerable<string> Switches => NumericRefSupport.Switches;

        [ParamsSource(nameof(Switches))]
        public string NumericRefs { get; set; } = "off";

        private ActorSystem _client = null!;
        private ActorSystem _server = null!;
        private IActorRef _echo = null!;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        [GlobalSetup]
        public async Task Setup()
        {
            _server = ActorSystem.Create("numeric-ask-server", NumericRefSupport.SystemConfig(NumericRefs));
            _client = ActorSystem.Create("numeric-ask-client", NumericRefSupport.SystemConfig(NumericRefs));
            _server.ActorOf(Props.Create(() => new Echo()), "echo");

            var address = ((RemoteActorRefProvider)((ExtendedActorSystem)_server).Provider).DefaultAddress;
            _echo = await _client.ActorSelection($"{address}/user/echo").ResolveOne(Timeout);

            for (var i = 0; i < 1000; i++)
                await _echo.Ask<string>("warm", Timeout);
        }

        [GlobalCleanup]
        public async Task Cleanup()
        {
            await Task.WhenAll(_client.Terminate(), _server.Terminate());
        }

        /// <summary>One Ask at a time: round-trip latency.</summary>
        [Benchmark]
        public Task<string> Ask() => _echo.Ask<string>("ping", Timeout);

        /// <summary>64 Asks in flight: the per-Ask cost under load.</summary>
        [Benchmark(OperationsPerInvoke = Concurrent)]
        public async Task AskConcurrent()
        {
            var tasks = new Task<string>[Concurrent];
            for (var i = 0; i < Concurrent; i++)
                tasks[i] = _echo.Ask<string>("ping", Timeout);
            await Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// Inbound dispatch of an Ask reply: one call of the real <c>ArteryRemoting.DispatchInbound</c> on an envelope
    /// addressed to a pending Ask future, with the recipient written as a literal temp path or as a numeric record
    /// (the numeric shape exists only where this tree has it). Each invocation uses its own pending future, since
    /// a future completes once; the futures and envelopes are built before each iteration.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 10, invocationCount: ArteryNumericRefInboundDispatchBenchmarks.Invocations, id: "AskReplyDispatch")]
    public class ArteryNumericRefInboundDispatchBenchmarks
    {
        public const int Invocations = 2048;
        private const string SenderPath = "akka://remote-peer@10.1.2.3:4053/user/responder#1234567";

        public IEnumerable<string> Shapes => NumericRefSupport.Available ? new[] { "Literal", "Numeric" } : new[] { "Literal" };

        [ParamsSource(nameof(Shapes))]
        public string RecipientShape { get; set; } = "Literal";

        private ActorSystem _system = null!;
        private RemoteActorRefProvider _provider = null!;
        private Action<IInboundEnvelope> _dispatch = null!;
        private IInboundEnvelope[] _envelopes = new IInboundEnvelope[Invocations];
        private int _next;
        private long _uid;

        [GlobalSetup]
        public void Setup()
        {
            _system = ActorSystem.Create("numeric-inbound-dispatch", NumericRefSupport.SystemConfig("on"));
            var ext = (ExtendedActorSystem)_system;
            _provider = (RemoteActorRefProvider)ext.Provider;
            _uid = AddressUidExtension.Uid(_system);

            var transport = _provider.Transport;
            var method = transport.GetType().GetMethod("DispatchInbound", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                         ?? throw new InvalidOperationException("DispatchInbound not found");
            _dispatch = (Action<IInboundEnvelope>)Delegate.CreateDelegate(typeof(Action<IInboundEnvelope>), transport, method);
        }

        [GlobalCleanup]
        public void Cleanup() => _system.Terminate().Wait(TimeSpan.FromSeconds(10));

        [IterationSetup]
        public void IterationSetup()
        {
            _next = 0;
            for (var i = 0; i < Invocations; i++)
            {
                var future = _provider.CreateFutureRef(new TaskCompletionSource<object>());
                var path = future.Path;
                _provider.RegisterTempActor(future, path);

                if (RecipientShape == "Numeric")
                {
                    Base64Counter(path.Name, out var counter);
                    var wire = NumericRefSupport.WireRefsFor(NumericRefSupport.TemporaryRef(_uid, counter));
                    _envelopes[i] = NumericRefSupport.NumericEnvelope("reply", SenderPath, 42L, wire);
                }
                else
                {
                    var wirePath = path.ToSerializationFormatWithAddress(_provider.DefaultAddress);
                    _envelopes[i] = NumericRefSupport.LiteralEnvelope("reply", SenderPath, wirePath, 42L);
                }
            }
        }

        // The temp name is the canonical Base64 of the counter; decode it with the same alphabet the framework uses.
        private static void Base64Counter(string name, out long counter)
        {
            counter = 0;
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var digit = c >= 'a' && c <= 'z' ? c - 'a'
                    : c >= 'A' && c <= 'Z' ? c - 'A' + 26
                    : c >= '0' && c <= '9' ? c - '0' + 52
                    : c == '+' ? 62 : 63;
                counter |= (long)digit << (6 * i);
            }
        }

        /// <summary>One dispatch of an Ask reply to its pending future.</summary>
        [Benchmark]
        public void DispatchAskReply() => _dispatch(_envelopes[_next++]);
    }

    /// <summary>
    /// Envelope encode and decode of the three ref shapes -- absent, literal, numeric -- for a small message.
    /// Decode reads sender and recipient the way the inbound stage does.
    /// </summary>
    [MemoryDiagnoser]
    public class ArteryNumericRefEnvelopeBenchmarks
    {
        private const long Origin = 0x1234_5678L;
        private const string SenderPath = "akka://remote-peer@10.1.2.3:4053/user/responder#1234567";
        private const string RecipientPath = "akka://client@10.1.2.4:4053/temp/Abc";

        public IEnumerable<string> Shapes => NumericRefSupport.Available ? new[] { "Absent", "Literal", "Numeric" } : new[] { "Absent", "Literal" };

        [ParamsSource(nameof(Shapes))]
        public string Shape { get; set; } = "Literal";

        private ActorSystem _system = null!;
        private Akka.Serialization.Serialization _serialization = null!;
        private ReadOnlySequence<byte> _frame;
        private Func<ReadOnlySequence<byte>, bool> _decode = null!;

        [GlobalSetup]
        public void Setup()
        {
            _system = ActorSystem.Create("numeric-envelope", ConfigurationFactory.ParseString("akka.loglevel = ERROR"));
            _serialization = _system.Serialization;
            var codec = typeof(ArteryEnvelopeCodec);

            Func<Akka.Serialization.PooledPayloadWriter> encode;
            if (Shape == "Numeric")
            {
                var recipientRef = NumericRefSupport.TemporaryRef(Origin + 1, 77);
                var refType = NumericRefSupport.NumericRefType!;
                var nullableRef = typeof(Nullable<>).MakeGenericType(refType);
                var method = codec.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .Single(m => m.Name == "Encode" && m.GetParameters().Length == 8 && m.GetParameters()[3].ParameterType == nullableRef);

                var serialization = Expression.Constant(_serialization);
                var call = Expression.Call(method, serialization, Expression.Constant(Origin), Expression.Constant(null, typeof(string)),
                    Expression.Constant(null, nullableRef), Expression.Constant(null, typeof(string)),
                    Expression.Convert(Expression.Constant(recipientRef, typeof(object)), nullableRef),
                    Expression.Constant("ping", typeof(object)), Expression.Constant(null, typeof(ArrayPool<byte>)));
                encode = Expression.Lambda<Func<Akka.Serialization.PooledPayloadWriter>>(call).Compile();
            }
            else
            {
                var sender = Shape == "Literal" ? SenderPath : null;
                var recipient = Shape == "Literal" ? RecipientPath : null;
                encode = () => ArteryEnvelopeCodec.Encode(_serialization, Origin, sender, recipient, "ping");
            }

            _encodeWriter = encode;
            using (var writer = encode())
                _frame = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray().AsMemory(4));

            _decode = Shape == "Numeric" ? BuildNumericDecode() : seq =>
            {
                var decoded = ArteryEnvelopeCodec.Decode(seq);
                var sender = decoded.TryGetSenderPath(out var s);
                var recipient = decoded.TryGetRecipientPath(out var r);
                return sender && recipient && (s is null || r is null || s.Length + r.Length > 0);
            };
        }

        private Func<Akka.Serialization.PooledPayloadWriter> _encodeWriter = null!;

        private static Func<ReadOnlySequence<byte>, bool> BuildNumericDecode()
        {
            var codec = typeof(ArteryEnvelopeCodec);
            var decode = codec.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static)!;
            var decodedType = decode.ReturnType;
            var refType = NumericRefSupport.NumericRefType!;
            var trySender = decodedType.GetMethod("TryGetSenderNumeric")!;
            var tryRecipient = decodedType.GetMethod("TryGetRecipientNumeric")!;

            var input = Expression.Parameter(typeof(ReadOnlySequence<byte>), "frame");
            var decoded = Expression.Variable(decodedType, "decoded");
            var sender = Expression.Variable(refType, "sender");
            var recipient = Expression.Variable(refType, "recipient");
            var body = Expression.Block(
                new[] { decoded, sender, recipient },
                Expression.Assign(decoded, Expression.Call(decode, input)),
                Expression.OrElse(
                    Expression.Call(decoded, trySender, sender),
                    Expression.Call(decoded, tryRecipient, recipient)));
            return Expression.Lambda<Func<ReadOnlySequence<byte>, bool>>(body, input).Compile();
        }

        [GlobalCleanup]
        public void Cleanup() => _system.Terminate().Wait(TimeSpan.FromSeconds(10));

        /// <summary>Encode one frame (disposed straight away, as a transport would after writing it).</summary>
        [Benchmark]
        public int Encode()
        {
            using var writer = _encodeWriter();
            return writer.WrittenCount;
        }

        /// <summary>Decode the fixed header and read both refs.</summary>
        [Benchmark]
        public bool Decode() => _decode(_frame);
    }
}
