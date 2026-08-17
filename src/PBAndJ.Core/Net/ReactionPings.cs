using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Which reaction-light ping a playback cursor is standing on.
    /// </summary>
    /// <remarks>
    /// The game's replay does this at <c>CombatReplayHelper.cs:1247-1254</c>:
    /// scan the unit's ping list backwards, take the first stamp at or before
    /// the requested time, hand it to <c>UnitLightManager.OnReactionPing</c>,
    /// stop. <c>OnReactionPing</c> only writes a field, so re-stamping the same
    /// ping every frame is idempotent and the drive is safe to run per frame.
    /// <para>
    /// ⚠️ This is <b>not</b> the sampling shape
    /// <see cref="KeyframePlayback.TrySample"/> uses. That clamps to the first
    /// key even before its time, because a unit has to be somewhere at every
    /// instant. A ping that has not happened yet has not happened, and clamping
    /// it forward would flash a glow at the top of every turn.
    /// </para>
    /// </remarks>
    public static class ReactionPings
    {
        /// <summary>
        /// The newest ping at or before <paramref name="cursor"/>, or null if
        /// none has landed yet.
        /// </summary>
        /// <remarks>
        /// Null rather than a sentinel time: zero is a real stamp on the host's
        /// clock, so a caller cannot be asked to tell "no ping" from "a ping at
        /// the origin". The caller's contract is to skip the game call entirely
        /// on null, which is what the game's own loop does by never reaching
        /// <c>OnReactionPing</c>.
        /// <para>
        /// "Newest" means "last in list order", which holds only because the
        /// recorder appends in nondecreasing simulation time
        /// (<c>CombatReplayHelper.cs:1971-1975</c>). Anything that reorders or
        /// thins this list must preserve ascending order or this diverges from
        /// the game silently.
        /// </para>
        /// <para>
        /// The NaN guard is not decoration. The game writes its test as
        /// <c>!(time &gt; requested)</c>, which is <b>true</b> for every key when
        /// the cursor is NaN — a naive transcription hands back the last ping in
        /// the list and arms a glow off a hostile float. Written as
        /// <c>time &lt;= cursor</c> instead, NaN on either side fails and the
        /// scan simply finds nothing.
        /// </para>
        /// </remarks>
        public static float? LatestAtOrBefore(IReadOnlyList<float>? times, float cursor)
        {
            if (times == null)
            {
                return null;
            }

            for (var i = times.Count - 1; i >= 0; i--)
            {
                var time = times[i];
                if (time <= cursor)
                {
                    return time;
                }
            }
            return null;
        }
    }
}
