/**
 * Copyright (C) 2009-2015 Typesafe Inc. <http://www.typesafe.com>
 * Original C# code written by Akka.NET project <http://getakka.net/>
 */

using Akka.Configuration;
using Akka.TestKit;

namespace Akka.Tests.Dispatch
{
    public class DispatchersSpec : AkkaSpec
    {
        public static Config DispatcherConfiguration
        {
            get { return ConfigurationFactory.ParseString(@"
                myapp{
                    mydispatcher {
                        throughput = 17
                    }
                    my-pinned-dispatcher {
                        type = PinnedDispatcher
                    }
                    my-fork-join-dispatcher{
                        type = ForkJoinDispatcher
                        throughput = 60
                        dedicated-thread-pool.thread-count = 4
                    }
                    my-synchronized-dispather{
                        type = SynchronizedDispatcher
		                throughput = 10
                    }
                }
            "); }
        }
    }
}
