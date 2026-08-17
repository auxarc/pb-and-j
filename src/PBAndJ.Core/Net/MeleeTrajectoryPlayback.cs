namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Where a playback cursor sits inside a recorded melee swing.
    /// </summary>
    /// <remarks>
    /// The game's replay drives melee shockwaves at
    /// <c>CombatReplayHelper.cs:1311-1329</c>: for each recorded swing whose
    /// window contains the requested time, evaluate the normalised progress and
    /// hand it to <c>MeleeUtility.CheckOverlapsWithShockwave</c>; if none is
    /// active, clear the trail instead.
    /// <para>
    /// Only the arithmetic lives here. The drive itself is one call into game
    /// code, and the "none active, so clear" decision is the caller's
    /// accumulation over its own loop — a loop that must preserve record order,
    /// because co-active swings share one trail object and the last call wins.
    /// </para>
    /// </remarks>
    public static class MeleeTrajectoryPlayback
    {
        /// <summary>
        /// Whether this swing is live at <paramref name="cursor"/>, and how far
        /// through it the cursor stands.
        /// </summary>
        /// <remarks>
        /// The activity test composes
        /// <see cref="ReplayAssetPlayback.IsActiveAt(float, float, float)"/>
        /// rather than restating <c>start &lt;= t &amp;&amp; end &gt;= t</c>.
        /// That is deliberate: the comparison is written the way it is so that
        /// hostile floats off the wire fall <i>inactive</i>, and a fresh
        /// <c>t &lt; start || t &gt; end</c> would invert exactly that — a NaN
        /// stamp would read active at every cursor and pin a shockwave on screen
        /// for the rest of the fight.
        /// <para>
        /// A zero-duration record would divide 0 by 0. It is degenerate but
        /// reachable, and NaN handed to <c>AnimationCurve.Evaluate</c> is
        /// undefined in the game's own code, so it resolves to the well-defined
        /// start of the animation instead: the swing shows its first frame for
        /// the one instant it exists.
        /// </para>
        /// </remarks>
        public static bool TryNormalise(MeleeTrajectory melee, float cursor, out float normalised)
        {
            normalised = 0f;
            if (!ReplayAssetPlayback.IsActiveAt(melee.TimeStart, melee.TimeEnd, cursor))
            {
                return false;
            }

            var duration = melee.TimeEnd - melee.TimeStart;
            if (duration > 0f)
            {
                normalised = (cursor - melee.TimeStart) / duration;
            }
            return true;
        }
    }
}
