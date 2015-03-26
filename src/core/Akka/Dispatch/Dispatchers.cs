/**
 * Copyright (C) 2009-2015 Typesafe Inc. <http://www.typesafe.com>
 * Original C# code written by Akka.NET project <http://getakka.net/>
 */
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;

namespace Akka.Dispatch
{
    /// <summary>
    ///     Class ThreadPoolDispatcher.
    /// </summary>
    public class ThreadPoolDispatcher : MessageDispatcher
    {
        /// <summary>
        /// Takes a <see cref="MessageDispatcherConfigurator"/>
        /// </summary>
        public ThreadPoolDispatcher(MessageDispatcherConfigurator configurator) : base(configurator)
        {
        }

        /// <summary>
        ///     Schedules the specified run.
        /// </summary>
        /// <param name="run">The run.</param>
        public override void Schedule(Action run)
        {
            var wc = new WaitCallback(_ => run());
            ThreadPool.UnsafeQueueUserWorkItem(wc, null);
            //ThreadPool.QueueUserWorkItem(wc, null);
        }
    }

    /// <summary>
    ///     Dispatcher that dispatches messages on the current synchronization context, e.g. WinForms or WPF GUI thread
    /// </summary>
    public class CurrentSynchronizationContextDispatcher : MessageDispatcher
    {
        /// <summary>
        ///     The scheduler
        /// </summary>
        private readonly TaskScheduler scheduler;

        /// <summary>
        ///     Initializes a new instance of the <see cref="CurrentSynchronizationContextDispatcher" /> class.
        /// </summary>
        public CurrentSynchronizationContextDispatcher()
        {
            scheduler = TaskScheduler.FromCurrentSynchronizationContext();
        }

        /// <summary>
        ///     Schedules the specified run.
        /// </summary>
        /// <param name="run">The run.</param>
        public override void Schedule(Action run)
        {
            var t = new Task(run);
            t.Start(scheduler);
        }
    }

    /// <summary>
    ///     Class SingleThreadDispatcher.
    /// </summary>
    public class SingleThreadDispatcher : MessageDispatcher
    {
        /// <summary>
        ///     The queue
        /// </summary>
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();

        /// <summary>
        ///     The running
        /// </summary>
        private volatile bool running = true;

        /// <summary>
        ///     Initializes a new instance of the <see cref="SingleThreadDispatcher" /> class.
        /// </summary>
        public SingleThreadDispatcher()
        {
            var thread = new Thread(_ =>
            {
                foreach (var next in queue.GetConsumingEnumerable())
                {
                    next();
                    if (!running) return;
                }
            });
            thread.Start(); //thread won't start automatically without this
        }

        /// <summary>
        ///     Schedules the specified run.
        /// </summary>
        /// <param name="run">The run.</param>
        public override void Schedule(Action run)
        {
            queue.Add(run);
        }
    }

    /// <summary>
    /// The registry of all <see cref="MessageDispatcher"/> instances available to this <see cref="ActorSystem"/>.
    /// </summary>
    public class Dispatchers
    {
        /// <summary>
        ///     The default dispatcher identifier, also the full key of the configuration of the default dispatcher.
        /// </summary>
        public readonly static string DefaultDispatcherId = "akka.actor.default-dispatcher";
        public readonly static string SynchronizedDispatcherId = "akka.actor.synchronized-dispatcher";

        private readonly ActorSystem _system;
        private readonly CachingConfig _cachingConfig;
        private readonly MessageDispatcher _defaultGlobalDispatcher;

        /// <summary>
        /// The list of all configurators used to create <see cref="MessageDispatcher"/> instances.
        /// 
        /// Has to be thread-safe, as this collection can be accessed concurrently by many actors.
        /// </summary>
        private ConcurrentDictionary<string, MessageDispatcherConfigurator> _dispatcherConfigurators = new ConcurrentDictionary<string, MessageDispatcherConfigurator>();

        /// <summary>Initializes a new instance of the <see cref="Dispatchers" /> class.</summary>
        /// <param name="system">The system.</param>
        /// <param name="prerequisites">The prerequisites required for some <see cref="MessageDispatcherConfigurator"/> instances.</param>
        public Dispatchers(ActorSystem system, IDispatcherPrerequisites prerequisites)
        {
            _system = system;
            Prerequisites = prerequisites;
            _cachingConfig = new CachingConfig(prerequisites.Settings.Config);
            _defaultGlobalDispatcher = FromConfig(DefaultDispatcherId);
        }

        /// <summary>Gets the one and only default dispatcher.</summary>
        public MessageDispatcher DefaultGlobalDispatcher
        {
            get { return _defaultGlobalDispatcher; }
        }

        /// <summary>
        /// The prerequisites required for some <see cref="MessageDispatcherConfigurator"/> instances.
        /// </summary>
        public IDispatcherPrerequisites Prerequisites { get; private set; }

        /// <summary>
        ///     Gets the MessageDispatcher for the current SynchronizationContext.
        ///     Use this when scheduling actors in a UI thread.
        /// </summary>
        /// <returns>MessageDispatcher.</returns>
        public static MessageDispatcher FromCurrentSynchronizationContext()
        {
            return new CurrentSynchronizationContextDispatcher();
        }


        public MessageDispatcher Lookup(string dispatcherName)
        {
            return FromConfig(dispatcherName);
        }

        private MessageDispatcherConfigurator LookupConfigurator(string id)
        {
            MessageDispatcherConfigurator configurator;
            if (_dispatcherConfigurators.TryGetValue(id, out configurator))
            {
                // It doesn't matter if we create a dispatcher configurator that isn't used due to concurrent lookup.
                // That shouldn't happen often and in case it does the actual ExecutorService isn't
                // created until used, i.e. cheap.
                
            }
        }

        /// <summary>
        /// Register a <see cref="MessageDispatcherConfigurator"/> that will be used by <see cref="Lookup"/>
        /// and <see cref="HasDispatcher"/> instead of looking up the configurator from the system
        /// configuration.
        /// 
        /// This enables dynamic addtition of dispatchers.
        /// 
        /// <remarks>
        /// A <see cref="MessageDispatcherConfigurator"/> for a certain id can only be registered once,
        /// i.e. it can not be replaced. It is safe to call this method multiple times, but only the
        /// first registration will be used.
        /// </remarks>
        /// </summary>
        /// <returns>This method returns <c>true</c> if the specified configurator was successfully regisetered.</returns>
        private bool RegisterConfigurator(string id, MessageDispatcherConfigurator configurator)
        {
            return _dispatcherConfigurators.TryAdd(id, configurator);
        }

        private MessageDispatcherConfigurator ConfiguratorFrom(Config cfg)
        {
            if(!cfg.HasPath("id")) throw new ConfigurationException(string.Format("Missing dispatcher `id` property in config: {0}", cfg.Root));

            var type = cfg.GetString("type");
            var throughput = cfg.GetInt("throughput");
            var throughputDeadlineTime = cfg.GetTimeSpan("throughput-deadline-time").Ticks;


            MessageDispatcherConfigurator dispatcher;
            switch (type)
            {
                case "Dispatcher":
                    dispatcher = new ThreadPoolDispatcherConfigurator(cfg, Prerequisites);
                    break;
                case "TaskDispatcher":
                    dispatcher = new TaskDispatcherConfigurator(cfg, Prerequisites);
                    break;
                case "PinnedDispatcher":
                    dispatcher = new PinnedDispatcherConfigurator(cfg, Prerequisites);
                    break;
                case "SynchronizedDispatcher":
                    dispatcher = new CurrentSynchronizationContextDispatcherConfigurator(cfg, Prerequisites);
                    break;
                case null:
                    throw new NotSupportedException("Could not resolve dispatcher for path " + path + ". type is null");
                default:
                    Type dispatcherType = Type.GetType(type);
                    if (dispatcherType == null)
                    {
                        throw new NotSupportedException("Could not resolve dispatcher type " + type + " for path " + path);
                    }
                    dispatcher = (MessageDispatcherConfigurator)Activator.CreateInstance(dispatcherType, cfg, Prerequisites);
                    break;
            }

            return new DispatcherConfigurator(dispatcher, cfg.GetString("id"), throughput, throughputDeadlineTime);
        }

        /// <summary>
        ///     Froms the configuration.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>MessageDispatcher.</returns>
        public MessageDispatcher FromConfig(string path)
        {
            //TODO: this should not exist, it is only here because we dont serialize dispathcer when doing remote deploy..
            if (string.IsNullOrEmpty(path))
            {
                var disp = new ThreadPoolDispatcher
                {
                    Throughput = 100
                };
                return disp;
            }

            Config config = _system.Settings.Config.GetConfig(path);
            string type = config.GetString("type");
            int throughput = config.GetInt("throughput");
            long throughputDeadlineTime = config.GetTimeSpan("throughput-deadline-time").Ticks;
            //shutdown-timeout
            //throughput-deadline-time
            //attempt-teamwork
            //mailbox-requirement

            MessageDispatcher dispatcher;
            switch (type)
            {
                case "Dispatcher":
                    dispatcher = new ThreadPoolDispatcher();
                    break;
                case "TaskDispatcher":
                    dispatcher = new TaskDispatcher();
                    break;
                case "PinnedDispatcher":
                    dispatcher = new SingleThreadDispatcher();
                    break;
                case "SynchronizedDispatcher":
                    dispatcher = new CurrentSynchronizationContextDispatcher();
                    break;
                case null:
                    throw new NotSupportedException("Could not resolve dispatcher for path " + path + ". type is null");
                default:
                    Type dispatcherType = Type.GetType(type);
                    if (dispatcherType == null)
                    {
                        throw new NotSupportedException("Could not resolve dispatcher type " + type + " for path " + path);
                    }
                    dispatcher = (MessageDispatcher)Activator.CreateInstance(dispatcherType);
                    break;
            }

            dispatcher.Throughput = throughput;
            if (throughputDeadlineTime > 0)
            {
                dispatcher.ThroughputDeadlineTime = throughputDeadlineTime;
            }
            else
            {
                dispatcher.ThroughputDeadlineTime = null;
            }

            return dispatcher;
        }
    }

    /// <summary>
    /// The cached <see cref="MessageDispatcher"/> factory that gets looked up via configuration
    /// inside <see cref="Dispatchers"/>
    /// </summary>
    class DispatcherConfigurator : MessageDispatcherConfigurator
    {
        public string Id { get; private set; }

        private readonly MessageDispatcherConfigurator _configurator;

        public DispatcherConfigurator(MessageDispatcherConfigurator configurator, string id, int throughput, long? throughputDeadlineTime)
            : base(configurator.Config, configurator.Prerequisites)
        {
            _configurator = configurator;
            ThroughputDeadlineTime = throughputDeadlineTime;
            Id = id;
            Throughput = throughput;
        }

        public int Throughput { get; private set; }

        public long? ThroughputDeadlineTime { get; private set; }
        public override MessageDispatcher Dispatcher()
        {
            var dispatcher = _configurator.Dispatcher();
            dispatcher.Throughput = Throughput;
            dispatcher.ThroughputDeadlineTime = ThroughputDeadlineTime > 0 ? ThroughputDeadlineTime : null;
            return dispatcher;
        }
    }
}