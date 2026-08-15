using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Dropping interior keys until a track fits its cap, keeping both
    /// endpoints.
    /// </summary>
    /// <remarks>
    /// One implementation for every kind of key, because the rule is about the
    /// shape of a sampled track and not about what was sampled.
    /// <see cref="PoseTracks.Thin"/> forwards here rather than keeping its own
    /// copy: poses, projectile transforms and beam samples all come off the
    /// same recorder at the same player-configurable interval, so a divergence
    /// between three copies of this arithmetic would be a difference nothing
    /// intended.
    /// <para>
    /// Not a theoretical guard. Replay sampling frequency is a player-facing
    /// graphics setting whose floor is 0.016 s, so a five-second turn can
    /// record well past any cap on a host that has done nothing but move a
    /// slider. Left un-thinned, the receiving peer rejects the frame as
    /// malformed and drops the host — every turn, and silently from the
    /// sender's side.
    /// </para>
    /// </remarks>
    public static class TrackThinning
    {
        /// <summary>
        /// At most <paramref name="cap"/> keys, first and last among them.
        /// </summary>
        /// <remarks>
        /// The tail is the endpoint that matters most: the last key is where
        /// the snapshot has already corrected everyone to, so a track truncated
        /// at the end would finish somewhere its subject no longer is.
        /// </remarks>
        public static IReadOnlyList<T> Thin<T>(IReadOnlyList<T> keys, int cap)
        {
            if (keys.Count <= cap)
            {
                return keys;
            }

            var kept = new T[cap];
            kept[0] = keys[0];
            // Deliberately a double division. A cap of two leaves no interior,
            // and integer arithmetic here would throw on that input rather than
            // producing the infinity a loop that never runs never looks at.
            var interior = cap - 2;
            var step = (keys.Count - 2) / (double)interior;
            for (var i = 0; i < interior; i++)
            {
                kept[1 + i] = keys[1 + (int)(i * step)];
            }
            kept[cap - 1] = keys[keys.Count - 1];
            return kept;
        }
    }
}
