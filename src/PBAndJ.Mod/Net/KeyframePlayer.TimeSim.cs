using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Combat.View;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The _TimeSimulation mirror, M14.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
        private static readonly int ShaderIdTimeSimulation =
            Shader.PropertyToID("_TimeSimulation");

        /// <summary>Whether to keep <c>_TimeSimulation</c> on the playback cursor.</summary>
        /// <remarks>
        /// On by default because a client reaches none of the writers that keep
        /// this current — vanilla's own replay writes it on every scrub
        /// (<c>CombatReplayHelper.cs:970</c>) and that path is host-only, so
        /// mirroring it is parity rather than embellishment. <c>pbj.fx-mirror 0</c>
        /// turns it off, which is how the A/B is re-run.
        /// </remarks>
        internal static bool MirrorTimeSimulation { get; set; } = true;

        /// <summary>
        /// Freeze playback once the cursor reaches this time. Negative is off.
        /// </summary>
        /// <remarks>
        /// Built because comparing two five-second replays by eye did not work:
        /// one beam among 378 effects, twice, from memory, is more than eyes can
        /// do — the first attempt at measurement 2 returned "could not tell",
        /// which is a real answer about the instrument rather than about the
        /// beam.
        /// <para>
        /// Holding turns the A/B into a still image with exactly one variable.
        /// The window plays normally up to this time and then stops advancing,
        /// so every track that should be active at that instant has been
        /// revealed along the way; then <c>_TimeSimulation</c> can be moved by
        /// hand with nothing else on screen changing. A difference under those
        /// conditions IS the shader sampling it, and it can be photographed and
        /// diffed rather than judged.
        /// </para>
        /// <para>
        /// ⚠️ Deliberately NOT reset by <see cref="Stop"/>: it has to be set
        /// <i>before</i> the replay starts, or the command racing a five-second
        /// window would decide whether the hold took. The cost is that it stays
        /// armed until it is cleared, so the command that sets it says so.
        /// </para>
        /// <para>
        /// ⚠️ A held window never ends, so it never sweeps. Its instances stay
        /// checked out until the hold is released or combat tears down. That is
        /// acceptable for a measurement and would not be for anything shipped.
        /// </para>
        /// </remarks>
        internal static float HoldAt { get; set; } = -1f;

        /// <summary>Whether playback is currently frozen at <see cref="HoldAt"/>.</summary>
        internal static bool Holding => playing && HoldAt >= 0f && cursor >= HoldAt;

        // What the global held before this window, so it can be handed back.
        // Captured UNCONDITIONALLY in Play, not when the mirror is enabled: the
        // toggle can be flipped mid-window, and a mid-window enable with no
        // captured pre-value would restore garbage on unwind.
        private static float timeSimRestore;

        // Set at the FIRST ACTUAL WRITE in Step, never in Play. Play has two
        // early returns past the capture, and Stop() has no `playing` guard and
        // is reachable with playing == false from Play's own opening call, from
        // CombatGameBridge.StopKeyframes, and as a double-stop after a finished
        // window. Without this, an unwind on any of those paths would write a
        // stale value over a live one.
        private static bool mirrorApplied;

        // The cursor value written last frame, for the echo check below.
        private static float mirrorWrote;
        private static bool mirrorWroteAny;

        /// <summary>
        /// What <c>_TimeSimulation</c> held at the window's start and end.
        /// </summary>
        /// <remarks>
        /// Sampled unconditionally, because with the mirror OFF this is the
        /// client's real precondition — the number the whole measurement is
        /// about. The start sample is taken in <see cref="Play"/> before playback
        /// is armed, or a mirror-on run would read back its own first write.
        /// </remarks>
        internal static float TimeSimAtStart { get; private set; }

        /// <inheritdoc cref="TimeSimAtStart"/>
        internal static float TimeSimAtEnd { get; private set; }

        /// <summary>
        /// Frames on which something else overwrote the mirror's value.
        /// </summary>
        /// <remarks>
        /// <b>The detector the A/B is actually trusted on.</b> If another writer
        /// fires during our window, the last write before the frame renders wins
        /// — and our pump is a <c>Heartbeat.Update</c> postfix whose ordering
        /// against the Entitas systems is a script-execution-order question no
        /// decompile answers. A confounded run looks exactly like the result we
        /// hope for, so the confounder has to be visible or the measurement is
        /// worthless.
        /// <para>
        /// Sampling the global at the window's two ends is NOT sufficient and
        /// this is why: a writer that writes the <i>same value every frame</i> is
        /// invisible to start/end sampling and to per-frame min/max alike, while
        /// still winning at render time on every frame. Reading the global back
        /// and comparing it against what we wrote catches any interleaved writer
        /// whose value is not coincidentally our own cursor, on whichever side of
        /// the postfix it runs.
        /// </para>
        /// <para>
        /// A non-zero count <b>voids the run</b>. It is not a defect in the
        /// mirror; it is the finding.
        /// </para>
        /// </remarks>
        internal static int TimeSimOverwrites { get; private set; }
    }
}
