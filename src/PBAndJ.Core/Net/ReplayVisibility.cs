namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Whether the host was drawing a unit at one instant of a replayed turn.
    /// </summary>
    /// <remarks>
    /// A transcription of the game's own scrubber, <c>ApplyTimeToUnit</c>
    /// (<c>CombatReplayHelper.cs:1118-1126</c>), and deliberately a transcription
    /// rather than a re-derivation. Four rounds of reasoning about "what the
    /// game must mean" produced four different rules here, three of them wrong;
    /// the shipped shape is the seven lines the game actually runs.
    /// <para>
    /// It is here rather than in the glue for the usual reason — nothing in an
    /// in-game eyeball catches a unit being drawn a second early — and because
    /// the failure it prevents is not subtle. A client that disagrees with the
    /// host about <i>whether</i> a unit was on the field is the same class of
    /// defect as the visibility bug M13 was written for, which went unnoticed
    /// for weeks because the digest never looks at any of this.
    /// </para>
    /// </remarks>
    public static class ReplayVisibility
    {
        /// <summary>
        /// No transition of this kind was recorded in the window.
        /// </summary>
        /// <remarks>
        /// Negative infinity rather than a nullable float: the value travels
        /// beside a presence flag on <see cref="UnitTrack"/>, and a sentinel no
        /// real window can contain lets the comparisons below stay unguarded.
        /// Every test in the rule is <c>stamp &gt; time</c>, and negative
        /// infinity is greater than nothing.
        /// </remarks>
        public const float None = float.NegativeInfinity;

        /// <summary>
        /// True if the unit was on screen at <paramref name="time"/>.
        /// </summary>
        /// <param name="endHidden">
        /// Whether the unit is hidden at the <i>end</i> of the window — the
        /// snapshot's own answer, which the client has already applied.
        /// </param>
        /// <param name="hide">When the unit was hidden, or <see cref="None"/>.</param>
        /// <param name="reveal">When the unit was revealed, or <see cref="None"/>.</param>
        /// <remarks>
        /// The shape is the game's exactly, including one thing that looks like
        /// a bug and is: the <c>else</c> means a window holding <b>both</b> a
        /// reveal and a later hide never consults the reveal, so such a unit is
        /// drawn from the window's first frame even though the host had it
        /// hidden until the reveal. The other double — a hide then a later
        /// reveal — comes out right.
        /// <para>
        /// Reproducing that asymmetry is the point rather than an oversight. The
        /// host's own replay is the reference for what the turn looked like, and
        /// a client that corrected the game here would be visibly out of step
        /// with the machine it is mirroring, for the sake of being right about a
        /// span neither player can check.
        /// </para>
        /// </remarks>
        public static bool IsVisibleAt(bool endHidden, float hide, float reveal, float time)
        {
            var visible = !endHidden;

            // Both stamps are compared against the requested time, never against
            // each other, and the hide is asked first. That ordering is the only
            // thing separating the two double-transition cases above.
            if (hide > time)
            {
                visible = true;
            }
            else if (reveal > time)
            {
                visible = false;
            }

            return visible;
        }
    }
}
