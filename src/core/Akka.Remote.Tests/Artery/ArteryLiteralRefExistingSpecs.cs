//-----------------------------------------------------------------------
// <copyright file="ArteryLiteralRefExistingSpecs.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

using Akka.Configuration;
using Xunit;

namespace Akka.Remote.Tests.Artery
{
    // Existing two-system Artery specs re-run, unchanged, with numeric refs forced OFF on every node, so the literal
    // path keeps full coverage now that numeric refs are on by default.

    public sealed class ArteryTransportLiteralRefsSpec : ArteryTransportSpec
    {
        public ArteryTransportLiteralRefsSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig() =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.numeric-refs = off").WithFallback(base.ArteryConfig());
    }

    public sealed class ArteryOutboundLanesLiteralRefsSpec : ArteryOutboundLanesSpec
    {
        public ArteryOutboundLanesLiteralRefsSpec(ITestOutputHelper output) : base(output)
        {
        }

        protected override Config ArteryConfig(int outboundLanes) =>
            ConfigurationFactory.ParseString("akka.remote.artery.advanced.numeric-refs = off").WithFallback(base.ArteryConfig(outboundLanes));
    }
}
