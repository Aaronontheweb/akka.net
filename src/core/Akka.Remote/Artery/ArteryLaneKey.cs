//-----------------------------------------------------------------------
// <copyright file="ArteryLaneKey.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#nullable enable

namespace Akka.Remote.Artery
{
    /// <summary>
    /// INTERNAL API.
    ///
    /// The lane key of a recipient: its 64-bit id -- an actor's UID, or for a temporary ref its counter value
    /// with <see cref="ArteryNumericRef.TemporaryNamespaceBit"/> set -- reduced by <see cref="Association.SelectLane"/>.
    /// A recipient has the same key whether a frame carries it as a numeric record or as a literal path, so the
    /// same actor always lands on the same lane and per-recipient ordering holds whichever shape a frame uses.
    /// </summary>
    internal static class ArteryLaneKey
    {
        /// <summary>The lane key of a numeric ref: its id, namespace bit included.</summary>
        public static long For(in ArteryNumericRef value) => value.Id;

        /// <summary>The lane key of the temporary ref whose name encodes <paramref name="counter"/>.</summary>
        public static long ForTemporary(long counter) => counter | ArteryNumericRef.TemporaryNamespaceBit;

        /// <summary>
        /// For a literal path that is a canonical temporary ref (<c>/temp/&lt;base64 counter&gt;</c>), the lane of the
        /// numeric form of the same ref; <see langword="false"/> for every other path. Allocation-free.
        /// </summary>
        public static bool TrySelectTemporaryLiteral(string path, int lanes, out int lane)
        {
            if (ArteryTempRefs.TryGetTempIdFromLiteral(path, out var counter))
            {
                lane = Association.SelectLane(ForTemporary(counter), lanes);
                return true;
            }

            lane = 0;
            return false;
        }
    }
}
