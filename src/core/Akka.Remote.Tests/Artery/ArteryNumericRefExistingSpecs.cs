//-----------------------------------------------------------------------
// <copyright file="ArteryNumericRefExistingSpecs.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using Akka.Configuration;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    // Existing two-system Artery specs re-run, unchanged, with numeric refs written by every node.

    public sealed class ArteryTransportNumericRefsSpec : ArteryTransportSpec
    {
        public ArteryTransportNumericRefsSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig() =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.numeric-refs = on").WithFallback(base.ArteryConfig());
    }

    public sealed class ArteryOutboundLanesNumericRefsSpec : ArteryOutboundLanesSpec
    {
        public ArteryOutboundLanesNumericRefsSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig(int outboundLanes) =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.numeric-refs = on").WithFallback(base.ArteryConfig(outboundLanes));
    }
}
