//-----------------------------------------------------------------------
// <copyright file="Deployer.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Akka.Configuration;
using Akka.Routing;
using Akka.Util;
using Akka.Util.Internal;

namespace Akka.Actor
{
    /// <summary>
    /// Used to configure and deploy actors.
    /// </summary>
    public class Deployer
    {
        /// <summary>
        /// TBD
        /// </summary>
        protected readonly Config Default;
        private readonly Settings _settings;
        private readonly AtomicReference<WildcardIndex<Deploy>> _deployments = new(new WildcardIndex<Deploy>());

        /// <summary>
        /// Initializes a new instance of the <see cref="Deployer"/> class.
        /// </summary>
        /// <param name="settings">The settings used to configure the deployer.</param>
        public Deployer(Settings settings)
        {
            _settings = settings;
            var config = _settings.Config.GetConfig("akka.actor.deployment");
            Default = config.GetConfig("default");

            var rootObj = config.Root.GetObject();
            if (rootObj == null) return;
            var deploys = rootObj.Items
                .Where(d => !d.Key.Equals("default"))
                .Select(kvp => ParseConfig(kvp.Key, kvp.Value.ToConfig()));
            foreach (var d in deploys)
            {
                SetDeploy(d);
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="path">TBD</param>
        /// <returns>TBD</returns>
        public Deploy Lookup(ActorPath path)
        {
            var rawElements = path.Elements;
            if (rawElements[0] != "user" || rawElements.Count < 2)
            {
                return Deploy.None;
            }

            var elements = rawElements.Drop(1);
            return Lookup(elements);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="path">TBD</param>
        /// <returns>TBD</returns>
        public Deploy Lookup(IEnumerable<string> path)
        {
            return _deployments.Value.Find(path);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="deploy">TBD</param>
        /// <exception cref="IllegalActorNameException">
        /// This exception is thrown if the actor name in the deployment path is empty or contains invalid ASCII.
        /// Valid ASCII includes letters and anything from <see cref="ActorPath.ValidSymbols"/>. Note that paths
        /// cannot start with the <c>$</c>.
        /// </exception>
        public void SetDeploy(Deploy deploy)
        {
            void Add(IList<string> path, Deploy d)
            {
                var w = _deployments.Value;
                foreach (var t in path)
                {
                    if (string.IsNullOrEmpty(t))
                        throw new IllegalActorNameException($"Actor name in deployment [{d.Path}] must not be empty");
                    if (!ActorPath.IsValidPathElement(t))
                    {
                        throw new IllegalActorNameException(
                            $"Illegal actor name [{t}] in deployment [${d.Path}]. {ActorPath.ValidActorNameDescription}");
                    }
                }
                if (!_deployments.CompareAndSet(w, w.Insert(path, d))) Add(path, d);
            }

            var elements = deploy.Path.Split('/').Drop(1).ToList();
            Add(elements, deploy);
        }

        /// <summary>
        /// Creates an actor deployment to the supplied path, <paramref name="key"/>, using the supplied configuration, <paramref name="config"/>.
        /// </summary>
        /// <param name="key">The path used to deploy the actor.</param>
        /// <param name="config">The configuration used to configure the deployed actor.</param>
        /// <returns>A configured actor deployment to the given path.</returns>
        public virtual Deploy ParseConfig(string key, Config config)
        {
            var deployment = config.WithFallback(Default);
            var routerType = deployment.GetString("router", "from-code");
            // var router = CreateRouterConfig(routerType, key, config, deployment);
            var router = CreateRouterConfig(routerType, deployment);
            var dispatcher = deployment.GetString("dispatcher", "");
            var mailbox = deployment.GetString("mailbox", "");
            var stashCapacity = deployment.GetInt("stash-capacity", Deploy.NoStashSize);
            var deploy = new Deploy(key, deployment, router, Deploy.NoScopeGiven, dispatcher, mailbox, stashCapacity);
            return deploy;
        }

        private RouterConfig CreateRouterConfig(string routerTypeAlias, Config deployment)
        {
            if (routerTypeAlias == "from-code")
                return NoRouter.Instance;

            if (deployment.IsNullOrEmpty())
                throw ConfigurationException.NullOrEmptyConfig<RouterConfig>();

            var path = string.Format("akka.actor.router.type-mapping.{0}", routerTypeAlias);
            var routerTypeName = _settings.Config.GetString(path, null);

            if(routerTypeName == null)
            {
                var message = $"Could not find type mapping for router alias [{routerTypeAlias}].";
                if (routerTypeAlias is
                    "cluster-metrics-adaptive-group" or
                    "cluster-metrics-adaptive-pool")
                    message += " Please install Akka.Cluster.Metrics extension nuget package.";
                else
                    message += " Did you forgot to install a specific router extension?";

                throw new ConfigurationException(message);
            }

            if (TryCreateBuiltInRouterConfig(routerTypeName.Trim(), deployment, out var builtInRouterConfig))
                return builtInRouterConfig;

            if (!AkkaFeatures.IsDynamicTypeLoadingSupported)
                throw new ConfigurationException(
                    $"Router type [{routerTypeName}] mapped from alias [{routerTypeAlias}] is not built in and dynamic type loading is disabled. " +
                    "Use one of the built-in routers or enable the [Akka.DynamicTypeLoading] feature switch.");

            return CreateRouterConfigFromTypeName(routerTypeName, routerTypeAlias, deployment);
        }

        /// <summary>
        /// The routers Akka.NET's own <c>akka.conf</c> maps under <c>akka.actor.router.type-mapping</c>,
        /// constructed directly so the trimmer and Native AOT compiler keep them without a
        /// <see cref="Type.GetType(string)"/> call. Matches both the bare and assembly-qualified spellings.
        /// </summary>
        private static bool TryCreateBuiltInRouterConfig(string routerTypeName, Config deployment, out RouterConfig routerConfig)
        {
            routerConfig = StripAkkaAssembly(routerTypeName) switch
            {
                "Akka.Routing.NoRouter" => NoRouter.Instance,
                "Akka.Routing.RoundRobinPool" => new RoundRobinPool(deployment),
                "Akka.Routing.RoundRobinGroup" => new RoundRobinGroup(deployment),
                "Akka.Routing.RandomPool" => new RandomPool(deployment),
                "Akka.Routing.RandomGroup" => new RandomGroup(deployment),
                "Akka.Routing.SmallestMailboxPool" => new SmallestMailboxPool(deployment),
                "Akka.Routing.BroadcastPool" => new BroadcastPool(deployment),
                "Akka.Routing.BroadcastGroup" => new BroadcastGroup(deployment),
                "Akka.Routing.ScatterGatherFirstCompletedPool" => new ScatterGatherFirstCompletedPool(deployment),
                "Akka.Routing.ScatterGatherFirstCompletedGroup" => new ScatterGatherFirstCompletedGroup(deployment),
                "Akka.Routing.ConsistentHashingPool" => new ConsistentHashingPool(deployment),
                "Akka.Routing.ConsistentHashingGroup" => new ConsistentHashingGroup(deployment),
                "Akka.Routing.TailChoppingPool" => new TailChoppingPool(deployment),
                "Akka.Routing.TailChoppingGroup" => new TailChoppingGroup(deployment),
                _ => null
            };

            return routerConfig != null;
        }

        private static string StripAkkaAssembly(string routerTypeName)
            => routerTypeName.EndsWith(", Akka", StringComparison.Ordinal)
                ? routerTypeName.Substring(0, routerTypeName.Length - ", Akka".Length)
                : routerTypeName;

        [RequiresUnreferencedCode("Resolves a router type mapped in HOCON by name. The trimmer cannot tell which type that is, so it may have been removed.")]
        private static RouterConfig CreateRouterConfigFromTypeName(string routerTypeName, string routerTypeAlias, Config deployment)
        {
            Type routerType;
            try
            {
                routerType = Type.GetType(routerTypeName);
            }
            catch (ArgumentNullException e)
            {
                var message = $"Could not find extension Type [{routerTypeAlias}] for router alias [{routerTypeAlias}].";
                if (routerTypeAlias is "cluster-metrics-adaptive-group" or "cluster-metrics-adaptive-pool")
                    message += " Please install Akka.Cluster.Metrics extension nuget package.";
                else
                    message += " Did you forgot to install a specific router extension?";

                throw new ConfigurationException(message, e);
            }

            Debug.Assert(routerType != null, "routerType != null");
            return (RouterConfig)Activator.CreateInstance(routerType, deployment);
        }
    }
}
