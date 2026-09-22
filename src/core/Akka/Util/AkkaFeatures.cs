//-----------------------------------------------------------------------
// <copyright file="AkkaFeatures.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Akka.Util
{
    /// <summary>
    /// Trim / Native AOT feature switches for Akka.NET.
    /// </summary>
    internal static class AkkaFeatures
    {
        /// <summary>
        /// Controls whether Akka.NET may resolve types named in HOCON through
        /// <see cref="Type.GetType(string)"/> and friends.
        ///
        /// Defaults to <c>true</c>. An application turns it off with
        /// <c>&lt;RuntimeHostConfigurationOption Include="Akka.DynamicTypeLoading" Value="false" Trim="true" /&gt;</c>,
        /// which lets the trimmer replace this property with the constant <c>false</c> and drop every
        /// reflection fallback behind it. With the switch off, only the types Akka.NET knows about at
        /// compile time are available, and anything else raises a <c>ConfigurationException</c> naming
        /// the offending setting.
        /// </summary>
        [FeatureSwitchDefinition("Akka.DynamicTypeLoading")]
        [FeatureGuard(typeof(RequiresUnreferencedCodeAttribute))]
        [FeatureGuard(typeof(RequiresDynamicCodeAttribute))]
        internal static bool IsDynamicTypeLoadingSupported =>
            !AppContext.TryGetSwitch("Akka.DynamicTypeLoading", out var isSupported) || isSupported;
    }
}
