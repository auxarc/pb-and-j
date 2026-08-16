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
    /// What a playback frame should do with one asset track.
    /// </summary>
    public enum AssetShowAction
    {
        /// <summary>Leave it as it is. Sample it if it holds an instance.</summary>
        Nothing = 0,

        /// <summary>Check an instance out and put it on screen.</summary>
        Reveal = 1,

        /// <summary>Hand its instance back.</summary>
        Retire = 2,
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
        /// <remarks>
        /// Defined in terms of <see cref="IsActiveAt"/> rather than by its own
        /// pair of comparisons, and that is not a tidy-up. Written the obvious
        /// way — <c>time &lt; timeStart</c> then <c>time &gt; timeEnd</c> — a
        /// <b>NaN</b> anywhere makes both comparisons false and the track falls
        /// through to <see cref="AssetTrackPhase.Active"/>, permanently, while
        /// <see cref="IsActiveAt"/> and <see cref="CrossedDuring"/> both call
        /// the same track inactive. Two predicates over one track disagreeing
        /// is a glue that activates an effect it will never expire, and expiry
        /// is what hands the pooled instance back.
        /// <para>
        /// NaN is not hypothetical here. Times are raw float bits on the wire
        /// with no decode validation — the codec bounds hostile <i>counts</i>
        /// and not hostile <i>floats</i> — so a malformed or hostile host can
        /// put one in. Falling to <see cref="AssetTrackPhase.Expired"/> is the
        /// safe end: nothing renders, and anything already assigned is
        /// released.
        /// </para>
        /// </remarks>
        public static AssetTrackPhase PhaseAt(float timeStart, float timeEnd, float time)
        {
            if (IsActiveAt(timeStart, timeEnd, time))
            {
                return AssetTrackPhase.Active;
            }
            return time < timeStart ? AssetTrackPhase.Pending : AssetTrackPhase.Expired;
        }

        /// <summary>
        /// Whether a track should be shown for a cursor that moved from
        /// <paramref name="previousTime"/> to <paramref name="currentTime"/>.
        /// </summary>
        /// <remarks>
        /// <b>A deliberate divergence from the game, not a transcription of
        /// it</b> — and the two predicates are here to be chosen between rather
        /// than to be equivalent, so the glue must pick on purpose:
        /// <b>activate on <see cref="CrossedDuring"/>, expire on
        /// <see cref="PhaseAt"/>.</b>
        /// <para>
        /// A track whose whole window falls between two frames would be stepped
        /// over by a cursor sampled only at instants. The game's own replay
        /// (<c>CombatReplayHelper.cs:1469</c>) is that point test and does skip
        /// such tracks, so <see cref="IsActiveAt"/> is exact parity with the
        /// host's <i>replay</i> — but not with what the host's player watched,
        /// since during live simulation the effect fired at its real moment and
        /// was seen.
        /// </para>
        /// <para>
        /// ⚠️ <b>MEASURED, and it fires far less often than this once claimed.</b>
        /// An earlier version of this comment argued the case from "a muzzle
        /// flash lasts under a tenth of a second and a frame is a thirtieth".
        /// <b>That reasoning was wrong.</b> Activation tests against the
        /// <i>track's window</i>, which is <c>timeStart + pool.lifetime</c> —
        /// and the measured muzzle pools carry lifetimes of <b>1.02 s and
        /// 0.95 s</b>, roughly sixty frames, which any point test catches
        /// trivially. How briefly the flash <i>looks</i> bright has nothing to
        /// do with it.
        /// </para>
        /// <para>
        /// Three replays of a real 389-effect turn recorded <b>zero</b>
        /// late activations — every track was reached while still active. So
        /// this <b>keeps its place on cost rather than on benefit</b>: when it
        /// changes nothing it costs nothing, and it still closes a real if rare
        /// hole. The tracks it can save are those whose <c>timeEnd</c> lands
        /// inside the window's first frame delta, which is exactly the shape
        /// that silently lost 2 effects of 828 on a real two-instance turn
        /// before <see cref="ActionFor"/> ordered the two tests correctly.
        /// </para>
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
        /// What this frame should do with a track, reveal before expiry.
        /// </summary>
        /// <remarks>
        /// <b>Here, under the gate, because the ORDER of these two tests is the
        /// whole rule and getting it backwards fails silently.</b> Written the
        /// obvious way — expire first, then activate — a track whose window has
        /// already closed by the first frame that sees it is
        /// <see cref="AssetTrackPhase.Expired"/> on arrival, so it is retired
        /// before <see cref="CrossedDuring"/> is ever consulted. That defeats
        /// the entire reason the interval test exists, and it defeats it for
        /// precisely the tracks it was written to save.
        /// <para>
        /// Not a hypothetical. Measured on two real games, 2026-08-15: 828
        /// effects crossed the wire, 826 reached the screen, and the two that
        /// vanished were neither pool failures nor unplayable keys — they were
        /// sub-frame effects discarded by the wrong order. A count on a live
        /// battle was the only thing that could have caught it; no test asserted
        /// it and no eye could have seen two missing flashes among hundreds.
        /// </para>
        /// <para>
        /// <paramref name="revealed"/> is the track's state <i>before</i> this
        /// frame, so a track this call reveals is never also retired by it — it
        /// gets exactly one frame on screen, which is what the host's player
        /// effectively saw, and is swept on the following pass.
        /// </para>
        /// </remarks>
        public static AssetShowAction ActionFor(
            float timeStart, float timeEnd, float previousTime, float currentTime, bool revealed)
        {
            if (!revealed)
            {
                return CrossedDuring(timeStart, timeEnd, previousTime, currentTime)
                    ? AssetShowAction.Reveal
                    : AssetShowAction.Nothing;
            }

            return PhaseAt(timeStart, timeEnd, currentTime) == AssetTrackPhase.Expired
                ? AssetShowAction.Retire
                : AssetShowAction.Nothing;
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
