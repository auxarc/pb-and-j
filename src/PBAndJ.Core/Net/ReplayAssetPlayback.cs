namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Where a replayed asset track stands relative to a playback cursor.
    /// </summary>
    /// <remarks>
    /// Three states rather than a bool, because the two ends are not symmetric.
    /// <see cref="Pending"/> means "do not show it yet"; <see cref="Expired"/>
    /// means "show it never again, and give its instance back". A bool would
    /// conflate them and leak the instance at the far end, which is the failure
    /// the whole pool analysis was about.
    /// </remarks>
    public enum AssetTrackPhase
    {
        /// <summary>The cursor has not reached this track's start.</summary>
        Pending = 0,

        /// <summary>The cursor is inside the track's window.</summary>
        Active = 1,

        /// <summary>The cursor is past the track's end.</summary>
        Expired = 2,
    }

    /// <summary>
    /// The arithmetic half of the game's <c>CheckAssetTrackActivation</c>.
    /// </summary>
    /// <remarks>
    /// Here rather than in the glue for the reason <see cref="KeyframePlayback"/>
    /// is: every rule below is one no in-game eyeball would catch failing. An
    /// effect shown a tenth of a second early is invisible; an effect never shown
    /// at all, or one whose instance is never handed back, is a defect that only
    /// appears turns later and somewhere else.
    /// <para>
    /// A deliberate transcription of the game's own test
    /// (<c>CombatReplayHelper.cs:1469</c>, <c>timeStart &lt;= t &amp;&amp; timeEnd &gt;= t</c>),
    /// inclusive at both ends exactly as the game has it, so a client and a host
    /// agree about the boundary frames.
    /// </para>
    /// </remarks>
    public static class ReplayAssetPlayback
    {
        /// <summary>
        /// Whether the cursor is inside the track's window.
        /// </summary>
        /// <remarks>
        /// Inclusive at both ends, matching the game. A track whose start and end
        /// are equal is therefore active for exactly the instant it is asked
        /// about, which matters: the game stamps such tracks itself when an
        /// effect begins and ends inside one sample.
        /// </remarks>
        public static bool IsActiveAt(float timeStart, float timeEnd, float time)
        {
            return timeStart <= time && timeEnd >= time;
        }

        /// <summary>Which of the three states the cursor puts this track in.</summary>
        public static AssetTrackPhase PhaseAt(float timeStart, float timeEnd, float time)
        {
            if (time < timeStart)
            {
                return AssetTrackPhase.Pending;
            }
            return time > timeEnd ? AssetTrackPhase.Expired : AssetTrackPhase.Active;
        }

        /// <summary>
        /// Whether a track should be shown for a cursor that moved from
        /// <paramref name="previousTime"/> to <paramref name="currentTime"/>.
        /// </summary>
        /// <remarks>
        /// <b>The rule that <see cref="IsActiveAt"/> alone gets wrong.</b> An
        /// effect can begin and end entirely between two frames — a muzzle flash
        /// is under a tenth of a second and a frame at 30fps is a thirtieth — and
        /// a cursor sampled only at instants would step straight over it and
        /// never show it at all. The host, whose recorder ran at simulation rate,
        /// did show it. So the test is an <i>interval</i> overlap, not a point
        /// test.
        /// <para>
        /// Callers pass the cursor's own previous value, so the very first frame
        /// of a window asks about a zero-length interval and this degrades to
        /// <see cref="IsActiveAt"/> — which is correct, because nothing was
        /// skipped before the window began.
        /// </para>
        /// </remarks>
        public static bool CrossedDuring(
            float timeStart, float timeEnd, float previousTime, float currentTime)
        {
            var from = previousTime <= currentTime ? previousTime : currentTime;
            var to = previousTime <= currentTime ? currentTime : previousTime;
            return timeStart <= to && timeEnd >= from;
        }

        /// <summary>
        /// Whether a track belongs in the slice sent for a turn's window.
        /// </summary>
        /// <remarks>
        /// The capture-side counterpart, and the reason a whole collection is
        /// never sent: the game's prune is gated on <c>experimentalMode</c>, a
        /// player setting, so on a default machine nothing is ever pruned and the
        /// collection still holds every effect of the fight at turn twenty. The
        /// slice must be correct whichever way that setting reads.
        /// </remarks>
        public static bool OverlapsWindow(
            float timeStart, float timeEnd, float windowStart, float windowEnd)
        {
            return timeStart <= windowEnd && timeEnd >= windowStart;
        }

        /// <summary>
        /// How far into its own life the track is, for
        /// <c>AssetLinker.SampleForReplay</c>.
        /// </summary>
        /// <remarks>
        /// Never negative. The game's own <c>ApplyTime</c> subtracts without a
        /// guard (<c>ReplayEntityAssetStandalone.ApplyTime</c>), which is safe for
        /// it because it only ever calls that on an active track — a guarantee we
        /// would have to re-establish rather than inherit, so it is clamped here
        /// instead. A negative sample time reaches
        /// <c>ParticleSystem.Simulate</c>, which is not a thing to find out about
        /// in a firefight.
        /// </remarks>
        public static float LocalTime(float timeStart, float time)
        {
            var local = time - timeStart;
            return local > 0f ? local : 0f;
        }
    }
}
