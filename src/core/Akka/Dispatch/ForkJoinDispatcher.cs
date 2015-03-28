﻿using System;
using System.Threading;
using Akka.Configuration;
using Helios.Concurrency;

namespace Akka.Dispatch
{
    /// <summary>
    /// <see cref="MessageDispatcherConfigurator"/> for the <see cref="ForkJoinDispatcher"/>.
    /// 
    /// Creates a single <see cref="ForkJoinDispatcher"/> instance and returns the same instace
    /// each time <see cref="Dispatcher"/> is called.
    /// </summary>
    public class ForkJoinDispatcherConfigurator : MessageDispatcherConfigurator
    {
        public ForkJoinDispatcherConfigurator(Config config, IDispatcherPrerequisites prerequisites) : base(config, prerequisites)
        {
            var dtp = config.GetConfig("dedicated-thread-pool");
            if(dtp.IsEmpty) throw new ConfigurationException(string.Format("must define section dedicated-thread-pool for ForkJoinDispatcher {0}", config.GetString("id", "unknown")));

            var settings = new DedicatedThreadPoolSettings(dtp.GetInt("thread-count"),
                ConfigureThreadType(dtp.GetString("threadtype", ThreadType.Background.ToString())),
                dtp.GetTimeSpan("deadlock-timeout"));
            _instance = new ForkJoinDispatcher(this, settings);
        }

        private readonly ForkJoinDispatcher _instance;

        public override MessageDispatcher Dispatcher()
        {
            return _instance;
        }

        private static ThreadType ConfigureThreadType(string threadType)
        {
            return string.Compare(threadType, ThreadType.Foreground.ToString(), StringComparison.InvariantCultureIgnoreCase) == 0 ?
                ThreadType.Foreground : ThreadType.Background;
        }
    }

    /// <summary>
    /// ForkJoinDispatcher - custom multi-threaded dispatcher that runs on top of a 
    /// <see cref="Helios.Concurrency.DedicatedThreadPool"/>, designed to be used for mission-critical actors
    /// that can't afford <see cref="ThreadPool"/> starvation.
    /// 
    /// Relevant configuration options:
    /// <code>
    ///     my-forkjoin-dispatcher{
    ///         type = "ForkJoin" #needed to load this dispatcher
    ///         dedicated-thread-pool{
    ///             type = ForkJoinDispatcher
	///	            throughput = 100
	///	            dedicated-thread-pool{ #settings for Helios.DedicatedThreadPool
	///		            thread-count = 3 #number of threads
	///		            #deadlock-timeout = 3s #optional timeout for deadlock detection
	///		            threadtype = background #values can be "background" or "foreground"
	///	            }
    ///         }
    ///     }
    /// </code>
    /// </summary>
    public class ForkJoinDispatcher : MessageDispatcher
    {
        private readonly DedicatedThreadPool _dedicatedThreadPool;

        internal ForkJoinDispatcher(MessageDispatcherConfigurator configurator, DedicatedThreadPoolSettings settings) : base(configurator)
        {
            _dedicatedThreadPool = new DedicatedThreadPool(settings);
        }

        public override void Schedule(Action run)
        {
            _dedicatedThreadPool.QueueUserWorkItem(run);
        }
    }
}
