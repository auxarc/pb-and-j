using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat.View;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Presents a received turn's motion. Humble-object glue: every decision
    // about *where* a unit or a joint should be at a given moment lives in
    // KeyframePlayback, under the coverage gate; this only resolves entities and
    // writes transforms.
    //
    // It writes the VIEW transform, never the ECS position:
    //
    //   * It keeps playback genuinely presentational. ECS position feeds order
    //     authoring, scenario state volumes and the state digest, so animating
    //     it sixty times a second would let a player author orders from
    //     historical positions and would put a half-played animation into the
    //     correction check. This is the same call the game's own replay scrubber
    //     makes (ApplyTimeToUnit writes view.transform directly).
    //
    //   * It self-heals. PositionLinkSystem is reactive on CombatMatcher.Position
    //     and simply calls CombatView.OnPosition, which sets transform.position —
    //     so the next ReplacePosition on a unit, from execution or from the next
    //     snapshot correction, snaps its view straight back to ECS truth. An
    //     abandoned playback cannot leave anything permanently displaced.
    //
    // The handle is combatView, NOT transformLink: CombatEntity.ReplaceTransformLink
    // is never called anywhere in the game, so no unit has that component and
    // TransformLinkSystem never sees one. Filtering on it silently matched zero
    // units.
    //
    // M8 adds the skeleton. We deliberately do NOT drive CombatReplayHelper's own
    // scrubber, for two reasons that only became clear on a full reading:
    //
    //   * ApplyTime iterates CombatReplayHelper.units, and that dictionary is
    //     filled only by OnExecutionStart — which runs off the ECS Simulating
    //     flag a client never gains. On a client it is empty, so driving the
    //     game's scrubber would mean fabricating its own ReplayUnit graph into a
    //     static two systems are documented to wipe.
    //
    //   * It would not even save us the work below. SleepPuppet is reachable
    //     only through SetReplayActive, which a client cannot call
    //     (activationAllowed is set in OnExecutionEnd and stays false forever
    //     here) — so the game's scrubber would have its bone writes overwritten
    //     by the client's own idle animation exactly as ours would.
    [ExcludeFromCodeCoverage]
    internal static class KeyframePlayer
    {
        private sealed class Target
        {
            public Target(UnitTrack track, Transform transform)
            {
                Track = track;
                Transform = transform;
            }

            public UnitTrack Track { get; }
            public Transform Transform { get; }

            /// <summary>Null when this unit plays transform-only.</summary>
            public UnitPoseTrack? Poses { get; set; }

            /// <summary>This machine's own recorded bones, in its own order.</summary>
            public List<Transform>? Bones { get; set; }

            /// <summary>Client bone index to host joint index, or -1.</summary>
            public int[]? Remap { get; set; }

            /// <summary>Present only on mechs, and only for the palm sync.</summary>
            public CombatMechAnimationView? MechView { get; set; }

            /// <summary>
            /// This unit's light manager, cached at install like every other
            /// handle here.
            /// </summary>
            /// <remarks>
            /// Null is ordinary and not an error: the game itself null-checks
            /// the result of <c>GetLightManager</c> (<c>CombatView.cs:43-47</c>)
            /// even though <c>ActionRecordingSystem:55</c> does not.
            /// </remarks>
            public UnitLightManager? Lights { get; set; }
        }

        /// <summary>
        /// One unit we put to sleep, and what has to be undone.
        /// </summary>
        /// <remarks>
        /// Kept in its own list rather than read back off <see cref="Target"/>,
        /// because the unwind must not need the tracks to know what it slept.
        /// Combat teardown empties the very collections a track-driven unwind
        /// would iterate, and a unit left with a disabled animator is a statue
        /// for the rest of the session.
        /// <para>
        /// It is also why the game's own <c>puppetPhysicsMap</c> is untouched:
        /// <c>DisableRagdollPhysics</c> stores into it only past an early
        /// return that <c>EnableRagdollPhysics</c> does not repeat before
        /// indexing it, so a unit whose crash state changes mid-window throws
        /// <c>KeyNotFoundException</c> on wake. We skip that half entirely and
        /// keep our own record of what we touched.
        /// </para>
        /// </remarks>
        private sealed class Sleeper
        {
            public Sleeper(CombatMechAnimationView view)
            {
                View = view;
            }

            public CombatMechAnimationView View { get; }
            public GameObject? FullBodyIk { get; set; }
            public GameObject? PuppetMaster { get; set; }
            public GameObject? PuppetBehaviour { get; set; }
        }

        /// <summary>
        /// One unit whose visibility changed during the window, and everything
        /// needed to move it without re-reading the ECS.
        /// </summary>
        /// <remarks>
        /// The views and the id are cached at install for the same reason
        /// <see cref="Sleeper"/> caches its handles: the transition fires
        /// mid-window, on entities the snapshot may have just killed and that
        /// combat teardown races. Every handle is Unity-null-checked at use.
        /// <para>
        /// <b>Three views, not one.</b> The game's own scrubber touches only
        /// <c>combatView</c>, but it can afford to because
        /// <c>PrepareUnitForReplay</c> has already parked the projection and
        /// puppet views for every unit. We have no such sandbox — these are live
        /// views — so we do what the game's ECS path does instead
        /// (<c>VisibilityLinkSystem</c>) and what its unwind does
        /// (<c>RestoreUnitForExecution</c>): all three.
        /// </para>
        /// </remarks>
        private sealed class VisibilityWatch
        {
            public VisibilityWatch(int id, bool endHidden, float reveal, float hide)
            {
                Id = id;
                EndHidden = endHidden;
                Reveal = reveal;
                Hide = hide;
            }

            public int Id { get; }
            public bool EndHidden { get; }
            public float Reveal { get; }
            public float Hide { get; }

            public CombatView? View { get; set; }
            public ProjectedCombatView? Projected { get; set; }
            public CombatPuppetView? Puppet { get; set; }

            /// <summary>Null while this unit plays transform-only.</summary>
            public Target? Target { get; set; }

            /// <summary>What we last told the engine, or null before the first frame.</summary>
            public bool? Applied { get; set; }
        }

        /// <summary>
        /// One replayed effect: the game's own track, and the instance it holds.
        /// </summary>
        /// <remarks>
        /// The game's <c>ReplayEntityAsset</c> subclasses are reused verbatim
        /// rather than reimplemented — they are public, their fields are public,
        /// and their <c>ApplyTime</c> is the interpolation we would otherwise be
        /// copying by hand. This is the opposite of M8's finding for units,
        /// where driving the game's own playback was strictly more work; the
        /// asset half has no puppet, no animator and no sleep bookkeeping.
        /// </remarks>
        private sealed class AssetShow
        {
            public AssetShow(ReplayEntityAsset track)
            {
                Track = track;
            }

            public ReplayEntityAsset Track { get; }

            /// <summary>The standalone instance we own, or null.</summary>
            public AssetLinker? Instance { get; set; }

            /// <summary>
            /// Whether this track has had its one turn on screen.
            /// </summary>
            /// <remarks>
            /// Kept apart from <see cref="Instance"/> being non-null, which is
            /// the obvious way to write it and is wrong. The pool is flushed on
            /// combat teardown and nothing promises that lands after we stop, so
            /// an instance can be destroyed from under us mid-window — and a
            /// track whose eligibility was "holds no instance" would then be
            /// re-instantiated on <b>every frame</b> for the rest of its window.
            /// One effect, once.
            /// </remarks>
            public bool Revealed { get; set; }

            /// <summary>
            /// Revealed after its own window had already closed.
            /// </summary>
            /// <remarks>
            /// These are exactly the effects the interval activation test
            /// exists to save — a muzzle flash lives under a tenth of a second
            /// and a frame is a thirtieth, so the cursor lands past the end of
            /// one on the very frame it first sees it. Counted separately
            /// because whether they draw anything at all is the open question
            /// the whole test rests on.
            /// </remarks>
            public bool RevealedLate { get; set; }

            /// <summary>Whether its first sample has been measured yet.</summary>
            public bool Measured { get; set; }

            /// <summary>
            /// Set once this track has been given up on, so it is never retried.
            /// </summary>
            /// <remarks>
            /// Not optional. Vanilla re-attempts activation <b>every frame</b>
            /// for an unassigned active track, and an unresolvable key makes
            /// <c>IsInstanceAvailable</c> log a warning on every one of those —
            /// so a single bad key is a warning per frame for its whole window.
            /// One line, then silence.
            /// </remarks>
            public bool Abandoned { get; set; }
        }

        private static readonly List<Target> targets = new List<Target>();
        private static readonly List<Sleeper> sleepers = new List<Sleeper>();
        private static readonly List<VisibilityWatch> watches = new List<VisibilityWatch>();
        private static readonly List<AssetShow> shows = new List<AssetShow>();
        private static float windowStart;
        private static float windowEnd;
        private static float cursor;

        /// <summary>
        /// Where the cursor was last frame, for the interval activation test.
        /// </summary>
        /// <remarks>
        /// A track whose window closes between two frames would be stepped over
        /// by an activation test sampled only at instants. ⚠️ Measured rare —
        /// three replays of a 389-effect turn saw zero of them, because a
        /// track's window is its pool lifetime (~1 s for muzzle pools) rather
        /// than how briefly the flash looks bright. See
        /// <c>ReplayAssetPlayback.CrossedDuring</c>.
        /// </remarks>
        private static float cursorPrevious;
        private static bool playing;

        // ─── MEASUREMENT 2: the _TimeSimulation mirror ───────────────────────
        //
        // Vanilla replay writes this shader global on every scrub
        // (CombatReplayHelper.cs:970, Shader.SetGlobalFloat(_TimeSimulation,
        // timeRequested)). A client reaches none of the writers that would keep
        // it current — ActionRecordingSystem.cs:42 is gated on combat.Simulating,
        // which a client never sets — so during our playback it holds whatever
        // was last left there, measured once as a stale OVERWORLD value of 49.12.
        //
        // Whether that matters is unknowable from the decompile, because it is a
        // question about compiled shaders. It matters most for BEAMS: revision
        // 4 closed the frozen-clock worry for standalone effects by way of
        // SampleForReplay calling ParticleSystem.Simulate, and
        // ReplayEntityAssetBeam.ApplyTime (decompiled:48-93) never calls
        // SampleForReplay at all. So the one effect class whose immunity was
        // never established is the one this measures.
        //
        // ✅ MEASURED 2026-08-16, and the answer was yes — so this is ON by
        // default and the toggle survives only to run the comparison again.
        //
        // A held replay frame was photographed at two values of the global and
        // the images diffed: between-setting difference was 2.7x the noise
        // floor with no overlap, and the changed pixels lay ONLY along the
        // beam, in the dashed pattern of a scrolling texture sampled at two
        // times. Without the mirror a replayed beam's shader clock is pinned to
        // an arbitrary constant for the whole window — the texture does not
        // scroll at all, where on the host it does.
        //
        // The constant is arbitrary in the literal sense: measured client
        // baselines of 379.69 and 230.87 both tracked how long that instance
        // had been running, which points at CombatIntroStartupSystem.cs:367
        // writing Time.unscaledTime on the combat-intro camera sweep.
        //
        // Full record: docs/notes/timesim-measurement.md.
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

        /// <summary>
        /// Beam tracks built for this window, and how many were put on screen.
        /// </summary>
        /// <remarks>
        /// A run whose turn contained no beams answers nothing, and must not be
        /// mistaken for a clean result — which, without this, it would be
        /// indistinguishable from. <see cref="BeamsRevealed"/> counts past the
        /// missing-<c>fxHelperBeam</c> abandonment, so it is beams that actually
        /// rendered rather than beams that were attempted.
        /// </remarks>
        internal static int BeamsBuilt { get; private set; }

        /// <inheritdoc cref="BeamsBuilt"/>
        internal static int BeamsRevealed { get; private set; }

        /// <summary>
        /// Trails dropped because this client's prefab has no <c>AraTrail</c>.
        /// </summary>
        /// <remarks>
        /// Should be zero between two installs of the same build, and a nonzero
        /// value means something the pool digest cannot see: that digest hashes
        /// pool <i>keys</i>, not which components hang off each prefab, so two
        /// machines can agree on all 176 keys and still disagree here. The
        /// projectile still flies — only its wake is refused — so nothing on
        /// screen would say so.
        /// </remarks>
        internal static int TrailsRefused { get; private set; }

        /// <summary>
        /// Weapon lights the game refused to place, having thrown inside its own
        /// <c>OnWeaponLight</c>.
        /// </summary>
        /// <remarks>
        /// Expected to be zero. The realistic cause is a socket whose
        /// <c>Light</c> died with a blown-off part, which that method
        /// dereferences without checking.
        /// </remarks>
        internal static int LightsRefused { get; private set; }

        /// <summary>Repositioned per key; see <c>ApplyWeaponLights</c>.</summary>
        private static Transform? lightAnchor;

        internal static bool IsPlaying => playing;

        /// <summary>How many replayed effects are on screen right now.</summary>
        internal static int ShownEffects { get; private set; }

        /// <summary>
        /// How many effects this window has put on screen in total.
        /// </summary>
        /// <remarks>
        /// Cumulative, and separate from <see cref="ShownEffects"/> because the
        /// live count is nearly useless to a test: most effects last under a
        /// second, so a poll between two of them reads zero and a window full of
        /// gunfire is indistinguishable from one where nothing fired at all.
        /// This only ever climbs within a window, so any poll after the fact
        /// sees the whole turn.
        /// </remarks>
        internal static int RevealedEffects { get; private set; }

        /// <summary>Effects this window could not show at all.</summary>
        internal static int UnplayableEffects { get; private set; }

        /// <summary>
        /// Effects revealed after their own window had closed, and how many of
        /// those actually drew a particle.
        /// </summary>
        /// <remarks>
        /// <b>The measurement that decides whether the interval activation test
        /// earns its cost</b>, and it exists because the question cannot be
        /// answered by looking. A handful of such activations inside a screen
        /// full of gunfire is not something a person can count, and "it looked
        /// right" is consistent with both answers — so the eye test was
        /// replaced with this one.
        /// <para>
        /// <b>ANSWERED, 2026-08-15.</b> Three replays of a real 389-effect turn:
        /// <c>late=0/0</c> every time, against <c>ontime</c> around 115/389.
        /// There are no late activations to price, so the interval test costs
        /// nothing — and it keeps its place on that rather than on any benefit
        /// this measured. Left in because it still runs, and because a turn that
        /// does produce one would otherwise lose it silently.
        /// </para>
        /// <para>
        /// <see cref="OnTimeReveals"/> and <see cref="OnTimeDrawing"/> are the
        /// control. If late effects draw at roughly the on-time rate, the
        /// interval test is showing the player something real. If they draw at
        /// zero while the control draws at nearly one, then
        /// <c>ParticleSystem.Simulate</c> past an effect's own duration renders
        /// nothing, and we are paying an instantiate, a <c>Setup</c> and a
        /// destroy per flash for no pixels — in which case the honest move is to
        /// fall back to the game's own point test.
        /// </para>
        /// </remarks>
        internal static int LateReveals { get; private set; }

        /// <inheritdoc cref="LateReveals"/>
        internal static int LateDrawing { get; private set; }

        /// <inheritdoc cref="LateReveals"/>
        internal static int OnTimeReveals { get; private set; }

        /// <inheritdoc cref="LateReveals"/>
        internal static int OnTimeDrawing { get; private set; }

        /// <summary>The turn currently being presented, or -1.</summary>
        internal static int Turn { get; private set; } = -1;

        /// <summary>How many units are playing with skeletal animation.</summary>
        internal static int PosedUnits { get; private set; }

        internal static void Play(int turn, KeyframeCapture capture)
        {
            Stop();

            if (capture.Tracks.Count == 0 || !IDUtility.IsGameState("combat"))
            {
                return;
            }

            // Set before the resolve loop, not after it: Watch reads the window
            // to decide whether a unit's arrival time falls inside it, and a
            // stale bound from the previous turn would answer for the wrong one.
            windowStart = capture.WindowStart;
            windowEnd = capture.WindowEnd;

            // Resolve once, not per frame. Unity's null check covers an entity
            // destroyed mid-playback, so a stale Transform simply stops moving.
            var byName = new Dictionary<string, UnitTrack>(capture.Tracks.Count);
            foreach (var track in capture.Tracks)
            {
                if (!string.IsNullOrEmpty(track.Name))
                {
                    byName[track.Name!] = track;
                }
            }

            var posesByName = new Dictionary<string, UnitPoseTrack>(capture.Poses.Count);
            foreach (var pose in capture.Poses)
            {
                if (!string.IsNullOrEmpty(pose.Name))
                {
                    posesByName[pose.Name!] = pose;
                }
            }

            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                if (!unit.hasCombatView || unit.combatView.view == null)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                byName.TryGetValue(persistent.nameInternal.s, out var track);

                // A unit with no track still matters, and is in fact the case
                // this exists for: the recorder never opened an entry for a unit
                // that was hidden when the turn began, so a freshly revealed
                // ambusher arrives with visibility to honour and nothing to
                // animate.
                Watch(unit, track);

                if (track == null)
                {
                    continue;
                }

                var target = new Target(track, unit.combatView.view.transform);
                targets.Add(target);
                if (watches.Count > 0 && watches[watches.Count - 1].Id == unit.id.id)
                {
                    watches[watches.Count - 1].Target = target;
                }

                if (posesByName.TryGetValue(persistent.nameInternal.s, out var pose))
                {
                    Dress(unit, target, pose);
                }
            }

            if (targets.Count == 0)
            {
                // Nothing to play, so nothing to defer within. Discard the
                // watches rather than leaving units hidden with no window to
                // reveal them in — the one failure this ordering exists to
                // prevent.
                watches.Clear();
                return;
            }

            // M14's effects, built past both early returns for the reason the
            // visibility apply below is: a turn that never starts playing must
            // not leave instances checked out with nothing to hand them back.
            BuildShows(capture.Assets);
            RevealedEffects = 0;
            UnplayableEffects = 0;
            LateReveals = 0;
            LateDrawing = 0;
            OnTimeReveals = 0;
            OnTimeDrawing = 0;
            BeamsRevealed = 0;
            TrailsRefused = 0;
            LightsRefused = 0;
            TimeSimOverwrites = 0;

            // Captured here — after the opening Stop(), which has already handed
            // back anything the previous window borrowed, and before playback is
            // armed. Unconditional: the toggle may be flipped mid-window and the
            // unwind still has to have something true to restore.
            timeSimRestore = Shader.GetGlobalFloat(ShaderIdTimeSimulation);
            TimeSimAtStart = timeSimRestore;
            TimeSimAtEnd = timeSimRestore;
            mirrorWroteAny = false;

            cursor = capture.WindowStart;
            cursorPrevious = capture.WindowStart;
            Turn = turn;
            playing = true;

            // Applied only now, past both early returns: a unit hidden before
            // playback was committed would stay hidden with nothing left to
            // reveal it.
            ApplyVisibility();
        }

        /// <summary>
        /// Notes a unit whose visibility moved during the window, if it did.
        /// </summary>
        /// <remarks>
        /// The stamps come from two places because only one of them can carry
        /// each case. A tracked unit's reveal and hide are the recorder's own,
        /// clipped to the window by the host — exact, and absent for a unit
        /// activated during the planning phase, which is right: the recorder
        /// held that unit from the window's first frame, so the host drew it
        /// throughout.
        /// <para>
        /// A unit with <i>no</i> track has no recorder entry to have stamped
        /// anything, so its reveal comes from the snapshot's <c>ArrivalTime</c>,
        /// read off the ECS. That read is safe only because the snapshot is
        /// applied before playback starts — the host sends it first from a
        /// single site and effects run in order — and it is the only carrier
        /// that case has. There is no matching hide: the game has no hide-time
        /// component, and a unit hidden all window has no track to stamp.
        /// </para>
        /// </remarks>
        private static void Watch(CombatEntity unit, UnitTrack? track)
        {
            var reveal = track != null ? track.RevealTime : ArrivalOf(unit);
            var hide = track != null ? track.HideTime : ReplayVisibility.None;

            // Nothing moved, which is almost every unit of almost every turn.
            if (float.IsNegativeInfinity(reveal) && float.IsNegativeInfinity(hide))
            {
                return;
            }

            var watch = new VisibilityWatch(unit.id.id, unit.isHidden, reveal, hide)
            {
                View = unit.hasCombatView ? unit.combatView.view : null,
                Projected = unit.hasProjectionView ? unit.projectionView.view : null,
                Puppet = unit.hasPuppetView ? unit.puppetView.view : null,
            };
            watches.Add(watch);
        }

        // Only meaningful as a reveal instant when it falls inside the window.
        // A client manufactures an arrival time for every deployed unit on load,
        // so an unfiltered read would treat the whole player squad as pending.
        private static float ArrivalOf(CombatEntity unit)
        {
            if (!unit.hasArrivalTime)
            {
                return ReplayVisibility.None;
            }
            var arrival = unit.arrivalTime.f;
            return arrival > windowStart && arrival <= windowEnd
                ? arrival
                : ReplayVisibility.None;
        }

        /// <summary>
        /// Binds one unit's pose track to this machine's own skeleton, and puts
        /// its animation to sleep so the bones stay where we write them.
        /// </summary>
        /// <remarks>
        /// A unit we cannot dress is left transform-only rather than taken as a
        /// reason to demote the whole turn. That is deliberate and it is the
        /// host's own rule arriving here intact: the host already drops a track
        /// too short to animate, on the grounds that its own replay does not
        /// animate it either. Demoting everyone because one unit stood still
        /// would take poses away from the units that have them.
        /// <para>
        /// Sleep is applied here, before the first frame of playback, not
        /// alongside it. A save load schedules forced animation ticks half a
        /// second and two and a half seconds later that bypass the
        /// <c>Time.timeScale</c> gate everything else here relies on, so the
        /// window between resolving a unit and silencing it has to be zero.
        /// </para>
        /// </remarks>
        private static void Dress(CombatEntity unit, Target target, UnitPoseTrack pose)
        {
            var visualManager = unit.combatView.view.visualManager;
            var bones = visualManager != null ? visualManager.GetRecordedBones() : null;
            if (bones == null || bones.Count == 0)
            {
                return;
            }

            target.Poses = pose;
            target.Bones = bones;
            target.Remap = PoseTracks.Remap(pose.Joints, NamesOf(bones));
            PosedUnits++;

            // Cached above the mech/tank fork on purpose. Both visual managers
            // implement GetLightManager (UnitVisualManager:1720 and
            // UnitVisualManagerSimple:516), so putting this after the tank
            // early-return below would quietly give weapon lights to mechs only.
            target.Lights = visualManager!.GetLightManager();

            if (!unit.hasMechAnimationView)
            {
                // Tanks and the like: three recorded bones, driven by nothing
                // that needs silencing. The game does not sleep them either.
                return;
            }

            var mechView = unit.mechAnimationView.view;
            if (mechView == null)
            {
                return;
            }
            target.MechView = mechView;

            var sleeper = new Sleeper(mechView);
            sleepers.Add(sleeper);

            // Order matters on the way down: pauseUpdates last, so nothing
            // reaches a half-silenced unit. It is load-bearing rather than
            // insurance — it is the only switch that closes both of
            // MechAnimationSystem's entry points, including the post-load
            // forced ticks that ignore Time.timeScale.
            if (mechView.animator != null)
            {
                mechView.animator.enabled = false;
            }
            if (mechView.ikFullBodyIK != null)
            {
                sleeper.FullBodyIk = mechView.ikFullBodyIK.gameObject;
                sleeper.FullBodyIk.SetActive(false);
            }

            // PuppetMaster maps muscle transforms onto these same bones from
            // its own LateUpdate, with no timeScale gate at all. A functional
            // mech is kinematic and blends at zero, so it costs nothing there —
            // but a crashed or wrecked one is Active and would overwrite every
            // bone we write, every frame. Deactivating the two holders is what
            // vanilla does, and unlike the ragdoll physics map it stores nothing
            // that a changed unit state can make unrecoverable.
            if (unit.hasPuppetView && unit.puppetView.view != null)
            {
                var puppetView = unit.puppetView.view;
                if (puppetView.puppetMaster != null)
                {
                    sleeper.PuppetMaster = puppetView.puppetMaster.gameObject;
                    sleeper.PuppetMaster.SetActive(false);
                }
                if (puppetView.puppetBehaviour != null)
                {
                    sleeper.PuppetBehaviour = puppetView.puppetBehaviour.gameObject;
                    sleeper.PuppetBehaviour.SetActive(false);
                }
            }

            mechView.pauseUpdates = true;
        }

        private static IReadOnlyList<string> NamesOf(List<Transform> bones)
        {
            var names = new string[bones.Count];
            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                names[i] = bone != null ? bone.name : string.Empty;
            }
            return names;
        }

        /// <summary>
        /// Puts every watched unit where the cursor says the host had it.
        /// </summary>
        /// <remarks>
        /// Runs every frame rather than only on a change, and the reason is a
        /// race rather than laziness. A unit hidden all combat has no world
        /// marker to suppress at install — <c>OnUnitVisibilityChanged</c> is a
        /// filter over the existing marker list and a no-op on an empty one —
        /// and one is then <i>created, visible</i> by
        /// <c>CombatUILinkInWorldMarkers</c>, which collects on Position and
        /// skips only units flagged <c>isHidden</c>. Deferral deliberately never
        /// touches that flag, and the snapshot's own position write is what
        /// fires the collector. So the marker appears after our install call,
        /// floating at the unit's end-of-turn position.
        /// <para>
        /// Re-asserting each frame bounds that leak to a single frame. It does
        /// not eliminate it: whether this pass runs before or after the Entitas
        /// reactive tick within a frame is not pinned down. The call is a
        /// <c>SetActive</c> over a cached list, so the cost of being sure is
        /// negligible and the deferred set is small by construction.
        /// </para>
        /// </remarks>
        private static void ApplyVisibility()
        {
            for (var i = 0; i < watches.Count; i++)
            {
                var watch = watches[i];
                var visible = ReplayVisibility.IsVisibleAt(
                    watch.EndHidden, watch.Hide, watch.Reveal, cursor);
                Show(watch, visible);
            }
        }

        /// <summary>
        /// Applies one unit's visibility, waking it first if it is going away.
        /// </summary>
        /// <remarks>
        /// The wake-before-hide order is load-bearing and is the one sequencing
        /// rule here that no amount of testing on a host would surface.
        /// <c>CombatPuppetView.OnVisibility</c> is a <c>SetActive</c> on the
        /// puppet view's own root, and the puppet master and behaviour objects
        /// that <see cref="Dress"/> deactivates are children of it. Hide first
        /// and the later unwind sets those children active <i>under an inactive
        /// root</i>, so no <c>OnEnable</c> ever fires, the puppet never
        /// re-initialises, and the cascade goes off later in a state no vanilla
        /// path produces.
        /// <para>
        /// So a unit being hidden is unwound first — while its root is still
        /// active and the wake runs in the orderly direction — and only then
        /// hidden. Its remaining keys are no loss: it is invisible for the rest
        /// of the window by definition.
        /// </para>
        /// </remarks>
        private static void Show(VisibilityWatch watch, bool visible)
        {
            var changed = watch.Applied != visible;
            watch.Applied = visible;

            if (changed && !visible)
            {
                WakeOne(watch);
            }

            if (changed)
            {
                if (watch.View != null)
                {
                    watch.View.OnVisibility(visible);
                }
                if (watch.Projected != null)
                {
                    watch.Projected.OnVisibility(visible);
                }
                if (watch.Puppet != null)
                {
                    watch.Puppet.OnVisibility(visible);
                }
                CIHelperOverlays.OnUnitVisibilityChanged(watch.Id, visible);
            }

            // Unconditional, unlike the rest: this is the half that loses a race
            // to a marker created after our install call. Cheap and idempotent.
            if (!visible || changed)
            {
                PhantomBrigade.Combat.CIHelperWorldMarkers.OnUnitVisibilityChanged(watch.Id, visible);
            }
        }

        /// <summary>
        /// Unwinds one unit's sleep ahead of the rest, and stops posing it.
        /// </summary>
        private static void WakeOne(VisibilityWatch watch)
        {
            var target = watch.Target;
            if (target == null)
            {
                return;
            }

            // Bones stop being written the moment the unit is invisible, so the
            // pose driver has nothing left to do for it.
            target.Poses = null;
            target.Bones = null;

            for (var i = sleepers.Count - 1; i >= 0; i--)
            {
                var sleeper = sleepers[i];
                if (sleeper.View != null && target.MechView != null
                    && ReferenceEquals(sleeper.View, target.MechView))
                {
                    WakeSleeper(sleeper);
                    sleepers.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Rebuilds the game's own asset tracks from what the host sent. M14.
        /// </summary>
        /// <remarks>
        /// Every field is set <b>before</b> the track is ever assigned an
        /// instance, because <c>AssignAsset</c> reads them at assignment rather
        /// than at sample time: it writes <c>localScale</c> straight from
        /// <c>scale</c>, and a default-constructed track therefore renders an
        /// effect scaled to nothing — invisible, silent, and indistinguishable
        /// from playback never having run. A probe spent two runs on that.
        /// <para>
        /// <c>assetKeyHash</c> is deliberately left unset. It is
        /// <c>string.GetHashCode</c>, whose only consumer is the same-key steal
        /// in vanilla's pooled path — which the standalone route never takes —
        /// and it has no cross-process stability guarantee anyway.
        /// </para>
        /// <para>
        /// <c>parentPresent</c> is left false. The host's parent is a live
        /// <c>Transform</c> that cannot travel, so the effect falls back to
        /// world space, which is exactly what <c>AssignAsset</c> does for an
        /// unparented track — and only 3 of 51 early-turn effects were parented
        /// when it was measured.
        /// </para>
        /// </remarks>
        private static void BuildShows(AssetCapture assets)
        {
            // Reset here rather than beside the other counters in Play: this runs
            // first, and a reset afterwards would zero what it had just counted.
            BeamsBuilt = 0;

            for (var i = 0; i < assets.Standalone.Count; i++)
            {
                var sent = assets.Standalone[i];
                var track = new ReplayEntityAssetStandalone
                {
                    position = ToVector3(sent.Position),
                    rotation = ToQuaternion(sent.Rotation),
                    scale = ToVector3(sent.Scale),
                    velocityAndDecay = ToVector4(sent.VelocityAndDecay),
                    positionLocal = ToVector3(sent.PositionLocal),
                    parentPresent = false,
                    parent = null,
                };
                if (Dress(track, sent.Head))
                {
                    shows.Add(new AssetShow(track));
                }
            }

            for (var i = 0; i < assets.Projectiles.Count; i++)
            {
                var sent = assets.Projectiles[i];
                var keys = new List<ReplayKeyframeTransform>(sent.Keys.Count);
                for (var k = 0; k < sent.Keys.Count; k++)
                {
                    keys.Add(new ReplayKeyframeTransform
                    {
                        time = sent.Keys[k].Time,
                        position = ToVector3(sent.Keys[k].Position),
                        rotation = ToQuaternion(sent.Keys[k].Rotation),
                    });
                }

                // Null, not an empty list, when nothing came: it is the shape
                // the game's own ApplyTime tests first, and the shape the trail
                // guard in Reveal restores when a client's prefab cannot carry
                // one. An empty list would behave the same today and would make
                // "no trail sent" and "trail refused locally" indistinguishable.
                List<ReplayKeyframeTrailPoint>? trail = null;
                if (sent.Trail.Count > 0)
                {
                    trail = new List<ReplayKeyframeTrailPoint>(sent.Trail.Count);
                    for (var t = 0; t < sent.Trail.Count; t++)
                    {
                        var point = sent.Trail[t];
                        trail.Add(new ReplayKeyframeTrailPoint
                        {
                            timeStart = point.Time,
                            timeEnd = point.TimeEnd,
                            position = ToVector3(point.Position),
                            velocity = ToVector3(point.Velocity),
                            perlinDirection = ToVector3(point.PerlinDirection),
                            tangent = ToVector3(point.Tangent),
                            normal = ToVector3(point.Normal),
                            color = ToColor(point.Colour),
                            thickness = point.Thickness,
                            texcoord = point.Texcoord,
                        });
                    }
                }

                var track = new ReplayEntityAssetProjectile
                {
                    id = sent.Id,
                    scale = ToVector3(sent.Scale),
                    keyframesTransform = keys,
                    keyframesTrail = trail,

                    // Left at their defaults deliberately. All three are read by
                    // ApplyTime and written by nothing in the entire decompile —
                    // trailStartTimeOffset only ever as 0f at
                    // CombatReplayHelper.cs:1584, and the two spline flags never
                    // at all. Carrying them would freeze three dead fields into
                    // a wire layout.
                    trailStartTimeOffset = 0f,
                    trailStartSplineAware = false,
                    trailEndSplineAware = false,
                };
                if (Dress(track, sent.Head))
                {
                    shows.Add(new AssetShow(track));
                }
            }

            for (var i = 0; i < assets.Beams.Count; i++)
            {
                var sent = assets.Beams[i];
                var keys = new List<ReplayKeyframeBeam>(sent.Keys.Count);
                for (var k = 0; k < sent.Keys.Count; k++)
                {
                    keys.Add(new ReplayKeyframeBeam
                    {
                        time = sent.Keys[k].Time,
                        position = ToVector3(sent.Keys[k].Position),
                        rotation = ToQuaternion(sent.Keys[k].Rotation),
                        parameters = ToVector3(sent.Keys[k].Parameters),
                    });
                }

                var track = new ReplayEntityAssetBeam { keyframes = keys };
                if (Dress(track, sent.Head))
                {
                    shows.Add(new AssetShow(track));
                    BeamsBuilt++;
                }
            }
        }

        /// <summary>
        /// Puts the shared head onto a track, or refuses it.
        /// </summary>
        /// <remarks>
        /// Both optional blocks are rebuilt from values rather than referenced,
        /// which is the whole reason effects can travel at all: the game hangs
        /// only a <c>DataBlockFloat01</c> and a <c>DataBlockColorInterpolated</c>
        /// off a track, and both are plain numbers. Absence stays absence —
        /// null is a real instruction meaning "keep the prefab's own", not the
        /// same as a hue of zero, which flattens it.
        /// </remarks>
        private static bool Dress(ReplayEntityAsset track, AssetTrackHead head)
        {
            if (string.IsNullOrEmpty(head.AssetKey))
            {
                return false;
            }

            track.assetKey = head.AssetKey;
            track.timeStart = head.TimeStart;
            track.timeEnd = head.TimeEnd;
            track.assetHueOffset = head.Hue.HasValue
                ? new DataBlockFloat01 { f = head.Hue.Value }
                : null;
            track.assetColorOverride = head.Colour.HasValue
                ? new DataBlockColorInterpolated
                {
                    colorFrom = ToColor(head.Colour.Value.From),
                    colorTo = ToColor(head.Colour.Value.To),
                }
                : null;
            return true;
        }

        /// <summary>
        /// Shows, samples and retires this frame's effects. M14.
        /// </summary>
        /// <remarks>
        /// Activation is an <b>interval</b> test and retirement is a point test,
        /// deliberately and not symmetrically. An effect that begins and ends
        /// between two frames must still be shown — the host's player watched it
        /// fire at its real moment — where retirement only needs to notice that
        /// the cursor is past the end.
        /// <para>
        /// ⚠️ Retirement here can never be the whole story, and building on it
        /// alone is the trap. Every projectile still in flight at execution end
        /// is stamped <c>timeEnd = simTime + 1f</c> and standalone lifetimes run
        /// to ten seconds against a five-second turn, so those tracks are still
        /// active when the cursor reaches the window's end. <see cref="Stop"/>
        /// is what actually guarantees the sweep.
        /// </para>
        /// </remarks>
        private static void ApplyShows()
        {
            var shown = 0;
            for (var i = 0; i < shows.Count; i++)
            {
                var show = shows[i];
                var track = show.Track;

                // The decision itself lives in Core, deliberately. The ORDER of
                // its two tests is the whole rule and getting it backwards
                // fails silently — it throws away exactly the sub-frame effects
                // the interval test exists to save, which is how two of 828
                // went missing on the first two-instance run. A rule that can
                // only be caught by counting effects on a live battle belongs
                // under the gate, not in glue.
                //
                // An abandoned track reads as already-revealed: it must never be
                // offered again, and retiring it is a no-op since it holds
                // nothing.
                var action = ReplayAssetPlayback.ActionFor(
                    track.timeStart, track.timeEnd, cursorPrevious, cursor,
                    show.Revealed || show.Abandoned);

                if (action == AssetShowAction.Reveal)
                {
                    Reveal(show);
                }
                else if (action == AssetShowAction.Retire)
                {
                    Retire(show);
                    continue;
                }

                // Covers never-revealed, abandoned, and destroyed-from-under-us
                // alike: Unity's own == null answers true for a destroyed
                // object, so a pool flush lands here as "nothing to sample"
                // rather than as an exception a frame later.
                if (show.Instance == null)
                {
                    continue;
                }

                track.ApplyTime(cursor);
                shown++;

                // Read once, on the first sample after activation: that is when
                // SampleForReplay has just run Simulate at this effect's own
                // local time, so it is the frame that answers "did anything
                // come out of it".
                if (!show.Measured)
                {
                    show.Measured = true;
                    if (ParticlesOf(show.Instance) > 0)
                    {
                        if (show.RevealedLate)
                        {
                            LateDrawing++;
                        }
                        else
                        {
                            OnTimeDrawing++;
                        }
                    }
                }
            }

            ShownEffects = shown;
        }

        /// <summary>
        /// Checks out one effect's instance and puts it on screen.
        /// </summary>
        /// <remarks>
        /// The four-step sequence below is the one the probe arrived at, and
        /// three of its steps are there because leaving them out rendered
        /// <b>nothing at all</b> — none of them predictable from reading:
        /// <list type="number">
        /// <item><c>GetInstanceStandalone</c> does <b>not</b> activate the
        /// object. <c>GetInstance</c> does; the standalone path does not.</item>
        /// <item><c>SampleForReplay</c> returns immediately unless
        /// <c>linkedToReplay</c> is set, which only <c>SetupForReplay</c> sets,
        /// which is only reachable through a track's <c>AssignAsset</c>. A
        /// driver holding a bare <c>AssetLinker</c> cannot sample at all.</item>
        /// <item><c>AssignAsset</c> writes <c>localScale</c> from the track's
        /// own <c>scale</c>, so the track must be fully built first.</item>
        /// <item><b>And the fourth, which the probe could not have caught:</b>
        /// vanilla applies hue and colour on the last line of
        /// <c>AssetPoolUtility.ActivateInstance</c> —
        /// <c>instance.UpdateColors(...)</c>. The standalone route never goes
        /// through <c>ActivateInstance</c>, so without this call every
        /// hue-shifted and colour-overridden effect renders in prefab defaults.
        /// The probe fired an effect with neither block set.</item>
        /// </list>
        /// <para>
        /// The standalone route is taken rather than the pooled ring for the
        /// reason M8 settled twice: do not participate in a lifecycle we do not
        /// own. It never touches <c>instanceCountUsed</c>, never enters the ring,
        /// and so cannot be dispossessed by a live footfall or field trigger
        /// mid-window, nor poison anyone's budget.
        /// </para>
        /// </remarks>
        private static void Reveal(AssetShow show)
        {
            // Set before anything can fail, so that every route out of here —
            // including the two abandonments below — leaves this track
            // ineligible. Set only on success, a track whose pool is missing
            // would be retried on every frame of its window.
            show.Revealed = true;

            var pool = DataMultiLinker<DataContainerAssetPool>.GetEntry(show.Track.assetKey);
            if (pool == null)
            {
                Abandon(show, "no such asset pool");
                return;
            }

            var instance = pool.GetInstanceStandalone();
            if (instance == null)
            {
                Abandon(show, "the pool would not instantiate it");
                return;
            }

            instance.SetActive(true);
            show.Track.AssignAsset(instance);

            // A beam whose prefab has no beam helper NREs inside the game's own
            // ApplyTime every frame — AssignAsset null-checks it, ApplyTime does
            // not. Our driver's catch would stop the whole turn's playback on
            // the first one, so one mismatched prefab would cost every effect.
            if (show.Track is ReplayEntityAssetBeam && instance.fxHelperBeam == null)
            {
                show.Track.UnlinkAsset(reset: true);
                Destroy(instance);
                Abandon(show, "its prefab carries no beam helper");
                return;
            }

            // The trail's version of the beam hazard above, and it is the same
            // asymmetry in the game's own code: AssignAsset null-checks
            // assetInstance.trail (ReplayEntityAssetProjectile.cs:47) and
            // ApplyTime does not (:103-104), so a projectile carrying trail
            // points onto a prefab with no AraTrail NREs every frame.
            //
            // Unlike the beam case this drops the TRAIL and keeps the effect.
            // A beam with no helper cannot render at all; a bullet with no wake
            // is still a bullet flying the right path, and abandoning it would
            // turn a cosmetic mismatch into a missing projectile.
            if (show.Track is ReplayEntityAssetProjectile projectile
                && projectile.keyframesTrail != null
                && instance.trail == null)
            {
                projectile.keyframesTrail = null;
                TrailsRefused++;
            }

            show.Instance = instance;
            RevealedEffects++;

            // Past the beam-helper abandonment above on purpose: this counts
            // beams that actually rendered, which is what a run claiming to have
            // measured beams has to be able to show.
            if (show.Track is ReplayEntityAssetBeam)
            {
                BeamsRevealed++;
            }

            // Classified at the moment of activation, because that is the only
            // moment the distinction exists: by the next frame every track's
            // phase has moved on and a late reveal is indistinguishable from an
            // ordinary one that is now finishing.
            show.RevealedLate =
                ReplayAssetPlayback.PhaseAt(show.Track.timeStart, show.Track.timeEnd, cursor)
                    == AssetTrackPhase.Expired;
            if (show.RevealedLate)
            {
                LateReveals++;
            }
            else
            {
                OnTimeReveals++;
            }

            instance.UpdateColors(show.Track.assetHueOffset, show.Track.assetColorOverride);
        }

        private static void Abandon(AssetShow show, string why)
        {
            show.Abandoned = true;
            show.Instance = null;
            UnplayableEffects++;
            Debug.LogWarning(NetLog.AssetUnplayable(show.Track.assetKey, why));
        }

        /// <summary>
        /// Hands one effect's instance back, whatever state it is in.
        /// </summary>
        /// <remarks>
        /// Destroyed directly rather than through <c>ReturnInstance</c>, which
        /// takes the standalone branch and logs a warning on every call
        /// (<c>DataContainerAssetPool.cs:205</c>) — at seven hundred effects in
        /// a measured turn that is a log flood, not a diagnostic. The pool's own
        /// <c>ReturnAllInstanceStandalone</c> sweeps its list at combat teardown
        /// and null-guards each entry, and Unity's destroyed objects compare
        /// equal to null, so the entry we leave behind costs nothing.
        /// </remarks>
        private static void Retire(AssetShow show)
        {
            var instance = show.Instance;
            show.Instance = null;
            if (instance == null)
            {
                return;
            }

            show.Track.UnlinkAsset(reset: true);
            Destroy(instance);
        }

        private static void Destroy(AssetLinker instance)
        {
            if (instance != null && instance.gameObject != null)
            {
                UnityEngine.Object.Destroy(instance.gameObject);
            }
        }

        /// <summary>
        /// Retires every effect still holding an instance.
        /// </summary>
        /// <remarks>
        /// Per-instance guarded and never able to abandon the loop, because the
        /// alternative is what the M8 sleeping-statue failure was: this runs
        /// inside <see cref="Stop"/>, <see cref="Stop"/> runs raw in a Harmony
        /// postfix, and a throw here would skip everything after
        /// <c>KeyframePlayer.Advance</c> in that postfix — the connect screen,
        /// the lobby screen, the drive rig — on every frame, forever.
        /// </remarks>
        private static void RetireShows()
        {
            for (var i = 0; i < shows.Count; i++)
            {
                try
                {
                    Retire(shows[i]);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        "[pb-and-j] could not return a replayed effect: "
                            + e.GetType().Name + ": " + e.Message);
                }
            }
            shows.Clear();
            ShownEffects = 0;
        }

        /// <summary>
        /// Live particles across an effect's whole hierarchy.
        /// </summary>
        /// <remarks>
        /// Summed over the children rather than read off
        /// <c>AssetLinker.particleSystem</c> alone. The linker's own root system
        /// is what <c>SampleForReplay</c> simulates, but it simulates it
        /// <c>withChildren: true</c> — and plenty of effects put their actual
        /// emission on a child while the root emits nothing at all, which would
        /// read as "drew nothing" and answer the question backwards.
        /// <para>
        /// Allocating, and deliberately not cached: it runs once per effect per
        /// window, only on the first sample after activation.
        /// </para>
        /// </remarks>
        private static int ParticlesOf(AssetLinker? instance)
        {
            if (instance == null)
            {
                return 0;
            }

            var systems = instance.GetComponentsInChildren<ParticleSystem>();
            var alive = 0;
            for (var i = 0; i < systems.Length; i++)
            {
                if (systems[i] != null)
                {
                    alive += systems[i].particleCount;
                }
            }
            return alive;
        }

        private static Vector3 ToVector3(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        private static Vector4 ToVector4(Vec4 v) => new Vector4(v.X, v.Y, v.Z, v.W);

        private static Quaternion ToQuaternion(Vec4 v) => new Quaternion(v.X, v.Y, v.Z, v.W);

        private static Color ToColor(Vec4 v) => new Color(v.X, v.Y, v.Z, v.W);

        internal static void Stop()
        {
            // FIRST, and in its own try. Stop is called from Advance's catch, so
            // a throw in any of the three unwinds below would otherwise skip this
            // one — and this is the only step here whose blast radius is a
            // process-wide shader global rather than one unit's animator.
            //
            // The residue is real and was measured on the original spike: left
            // alone, the global sits at the playback cursor's last value long
            // after the window ended. Restoring it is a decision, not an
            // accident.
            try
            {
                if (mirrorApplied)
                {
                    mirrorApplied = false;
                    Shader.SetGlobalFloat(ShaderIdTimeSimulation, timeSimRestore);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[pb-and-j] could not restore _TimeSimulation: "
                        + e.GetType().Name + ": " + e.Message);
            }

            // The weapon-light anchor is ours and nothing else refers to it, so
            // it goes back with the window rather than lingering in the scene —
            // and certainly not into the overworld. LightAnchor() rebuilds it on
            // demand, which is also what covers combat teardown destroying it
            // out from under us.
            try
            {
                if (lightAnchor != null)
                {
                    UnityEngine.Object.Destroy(lightAnchor.gameObject);
                }
                lightAnchor = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[pb-and-j] could not retire the weapon-light anchor: " + e.Message);
            }

            // Visibility first, and Wake second. The same ordering argument as
            // Show's: a puppet root left inactive would swallow the sleep
            // unwind's SetActive calls on its own children.
            RestoreVisibility();
            Wake();
            // The effects last, and unconditionally. This is the sweep the whole
            // asset lifecycle rests on: a straddling projectile is still ACTIVE
            // at the window's end by construction, so nothing in the per-frame
            // retirement above will ever have released it.
            RetireShows();
            targets.Clear();
            PosedUnits = 0;
            playing = false;
            Turn = -1;
        }

        /// <summary>
        /// Undoes every sleep, in the reverse of the order it was applied.
        /// </summary>
        /// <remarks>
        /// Every handle is null-checked on the way back up, because the unwind
        /// runs on paths where a unit may have been destroyed since: combat
        /// end, a session fault, a peer saying goodbye, and a turn commit that
        /// lands mid-window.
        /// </remarks>
        private static void Wake()
        {
            for (var i = 0; i < sleepers.Count; i++)
            {
                WakeSleeper(sleepers[i]);
            }
            sleepers.Clear();
        }

        private static void WakeSleeper(Sleeper sleeper)
        {
            if (sleeper.View != null)
            {
                sleeper.View.pauseUpdates = false;
            }
            if (sleeper.PuppetBehaviour != null)
            {
                sleeper.PuppetBehaviour.SetActive(true);
            }
            if (sleeper.PuppetMaster != null)
            {
                sleeper.PuppetMaster.SetActive(true);
            }
            if (sleeper.FullBodyIk != null)
            {
                sleeper.FullBodyIk.SetActive(true);
            }
            if (sleeper.View != null && sleeper.View.animator != null)
            {
                sleeper.View.animator.enabled = true;
            }
        }

        /// <summary>
        /// Hands every watched unit back to the snapshot's own answer.
        /// </summary>
        /// <remarks>
        /// Read live off the entity rather than from anything captured at
        /// install. A new turn's keyframes arrive <i>after</i> that turn's
        /// snapshot has already been applied, and <see cref="Play"/> opens by
        /// stopping the previous window — so restoring a remembered value would
        /// overwrite fresher truth with staler. The entity is the truth; we were
        /// only ever borrowing it for the length of a window.
        /// </remarks>
        private static void RestoreVisibility()
        {
            for (var i = 0; i < watches.Count; i++)
            {
                var watch = watches[i];
                var unit = IDUtility.GetCombatEntity(watch.Id);
                var visible = unit == null || !unit.isHidden;
                if (watch.Applied == visible)
                {
                    continue;
                }
                watch.Applied = null;
                if (watch.View != null)
                {
                    watch.View.OnVisibility(visible);
                }
                if (watch.Projected != null)
                {
                    watch.Projected.OnVisibility(visible);
                }
                if (watch.Puppet != null)
                {
                    watch.Puppet.OnVisibility(visible);
                }
                PhantomBrigade.Combat.CIHelperWorldMarkers.OnUnitVisibilityChanged(watch.Id, visible);
                CIHelperOverlays.OnUnitVisibilityChanged(watch.Id, visible);
            }
            watches.Clear();
        }

        /// <summary>Pumped from the same Heartbeat postfix the runtime is.</summary>
        /// <remarks>
        /// Everything is inside a catch, and the catch stops playback rather
        /// than swallowing and continuing. A driver that throws mid-window with
        /// units asleep would leave them frozen with their animators off for the
        /// rest of the session — the unwind is the thing that must not be
        /// skipped, whatever went wrong above it.
        /// </remarks>
        internal static void Advance(float deltaSeconds)
        {
            if (!playing)
            {
                return;
            }

            try
            {
                Step(deltaSeconds);
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[pb-and-j] replay driver failed, stopping playback: "
                        + e.GetType().Name + ": " + e.Message);
                Stop();
            }
        }

        private static void Step(float deltaSeconds)
        {
            // Leaving combat with units asleep would strand them mid-pose with
            // their animators off for the rest of the session, and the effects
            // that stop playback do not all fire on every exit.
            if (!IDUtility.IsGameState("combat"))
            {
                Stop();
                return;
            }

            // The echo check, before anything this frame writes: if the global no
            // longer holds what we last put there, something else wrote it
            // between the two frames and this run is confounded. Read first,
            // because our own write below would erase the evidence.
            if (mirrorWroteAny
                && !Mathf.Approximately(Shader.GetGlobalFloat(ShaderIdTimeSimulation), mirrorWrote))
            {
                TimeSimOverwrites++;
            }

            // Real time against simulation time one-for-one: the host recorded
            // the turn at the rate it was simulated, so replaying it at any other
            // rate would be a different turn.
            cursor += deltaSeconds;
            var finished = cursor >= windowEnd;
            if (finished)
            {
                cursor = windowEnd;
            }

            // The hold, applied AFTER the natural advance rather than instead of
            // it: the window has to play up to this instant so that every track
            // active there was revealed on the way past, exactly as it would have
            // been. Jumping the cursor straight to the hold point would leave the
            // interval activation test with nothing to cross and reveal a
            // different set of effects than a real playback does.
            if (HoldAt >= 0f && cursor >= HoldAt)
            {
                cursor = Mathf.Clamp(HoldAt, windowStart, windowEnd);
                finished = false;
            }

            // The absolute cursor, not a turn-local elapsed — which is what
            // vanilla writes at CombatReplayHelper.cs:970, on the same clock our
            // window bounds come off (CombatGameBridge sets windowStart from
            // turnStartTime and windowEnd from combat.simulationTime.f). A local
            // 0-based time would feed shaders a clock the game never writes.
            //
            // Placed after the cursor advance and before ApplyShows, matching
            // vanilla's own ordering: it writes the global, then activates and
            // applies its tracks at the same time value.
            if (MirrorTimeSimulation)
            {
                mirrorApplied = true;
                mirrorWrote = cursor;
                mirrorWroteAny = true;
                Shader.SetGlobalFloat(ShaderIdTimeSimulation, cursor);
            }

            TimeSimAtEnd = Shader.GetGlobalFloat(ShaderIdTimeSimulation);

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target.Transform == null)
                {
                    continue;
                }
                if (KeyframePlayback.TrySample(target.Track, cursor, out var position, out var rotation))
                {
                    target.Transform.position = new Vector3(position.X, position.Y, position.Z);
                    target.Transform.rotation = new Quaternion(
                        rotation.X, rotation.Y, rotation.Z, rotation.W);
                }

                ApplyPose(target);
                ApplyWeaponLights(target);
            }

            // After the transform and pose writes, so a unit revealed this frame
            // is shown where the window says it is rather than where it was left
            // standing a frame earlier.
            ApplyVisibility();

            // And the effects after the visibility, so a muzzle flash never
            // appears a frame before the unit firing it does.
            ApplyShows();
            cursorPrevious = cursor;

            if (finished)
            {
                Stop();
            }
        }

        /// <summary>
        /// Fires this unit's weapon lights up to the cursor, then animates them.
        /// </summary>
        /// <remarks>
        /// Two calls into the game's own <c>UnitLightManager</c>, in this order,
        /// and both are needed every frame:
        /// <list type="bullet">
        /// <item><c>OnWeaponLight</c> arms a socket's light — it writes
        /// <c>timeStart</c>, the durations and the position, and does not
        /// animate.</item>
        /// <item><c>OnTimeChange</c> is what actually drives intensity, off
        /// <c>timeCurrent - timeStart</c> against those durations
        /// (<c>UnitLightManager.cs:135-175</c>).</item>
        /// </list>
        /// <para>
        /// <b>Nothing on a client drives <c>OnTimeChange</c> otherwise.</b> Its
        /// two per-unit callers — <c>ActionRecordingSystem.cs:55</c> and
        /// <c>CombatReplayHelper.ApplyTimeToUnit:1269</c> — are both gated
        /// behind state a client never reaches (the <c>Simulating</c> flag, and
        /// a <c>units</c> dictionary only <c>OnExecutionStart</c> fills). So
        /// there is no double-drive to collide with — but that is a property of
        /// those two gates, not an absence, and it would stop being true the
        /// moment anything populated <c>units</c> here.
        /// </para>
        /// <para>
        /// <b>The dummy transform is the whole trick.</b> The game insists on a
        /// <c>Transform</c> it can call <c>TransformPoint(0, 0, positionOffset)</c>
        /// on, and that result is the only use it makes of it. So one shared,
        /// repositioned object plus an offset of zero reproduces the captured
        /// world point exactly — <c>TransformPoint(Vector3.zero)</c> is just the
        /// translation column, under any parenting or scale. One object suffices
        /// for every unit because the value is consumed before the call returns.
        /// </para>
        /// <para>
        /// ⚠️ Wrapped per key because <c>OnWeaponLight</c> dereferences
        /// <c>unitVFXLight.light</c> with no null check (<c>:280-281</c>), unlike
        /// every neighbouring method in that class. A socket whose <c>Light</c>
        /// died with a blown-off part throws inside game code, and an unguarded
        /// throw here would cost the rest of the turn's playback.
        /// </para>
        /// </remarks>
        private static void ApplyWeaponLights(Target target)
        {
            var poses = target.Poses;
            if (poses == null || poses.Lights.Count == 0)
            {
                return;
            }

            var manager = target.Lights;
            if (manager == null)
            {
                return;
            }

            for (var i = 0; i < poses.Lights.Count; i++)
            {
                var key = poses.Lights[i];

                // Only flashes the cursor has reached, and only those still
                // within their own envelope — re-arming an expired light every
                // frame would restart its fade and leave it lit forever.
                var elapsed = cursor - key.Time;
                if (elapsed < 0f || elapsed > key.DurationStable + key.DurationFade)
                {
                    continue;
                }

                var dummy = LightAnchor();
                if (dummy == null)
                {
                    return;
                }
                dummy.position = new Vector3(key.Position.X, key.Position.Y, key.Position.Z);

                try
                {
                    manager.OnWeaponLight(
                        key.Time,
                        dummy,
                        key.Socket,
                        new Color(key.Colour.X, key.Colour.Y, key.Colour.Z, key.Colour.W),
                        key.Intensity,
                        key.DurationBuildup,
                        key.DurationStable,
                        key.DurationFade,
                        // Zero on purpose: the offset is already folded into the
                        // captured point, so applying it twice would push every
                        // flash a metre further down the barrel.
                        0f);
                }
                catch (Exception e)
                {
                    LightsRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] weapon light on socket '" + key.Socket + "' was refused: "
                            + e.Message);
                }
            }

            manager.OnTimeChange(cursor);
        }

        /// <summary>The shared dummy transform, created on first use per window.</summary>
        /// <remarks>
        /// Recreated rather than kept forever: a destroyed one reads
        /// <c>== null</c> through Unity's operator and would silently trip
        /// <c>OnWeaponLight</c>'s own null early-out, taking every weapon light
        /// with it and saying nothing.
        /// </remarks>
        private static Transform? LightAnchor()
        {
            if (lightAnchor == null)
            {
                lightAnchor = new GameObject("pbj_light_anchor").transform;
            }
            return lightAnchor;
        }

        /// <summary>
        /// Writes one unit's skeleton for the current cursor position.
        /// </summary>
        /// <remarks>
        /// Local space throughout, so it is independent of the root transform
        /// written just above — and the palm sync, which is world space, comes
        /// after both for that reason.
        /// <para>
        /// The bone count is re-checked every frame rather than trusted from
        /// install time. <c>UnitVisualManagerSimple.RefreshRecordedBones</c>
        /// clears and rebuilds its list when a view is re-created, and a remap
        /// built against the old one would write the wrong bones or index past
        /// the end of the new one. Mechs never rebuild — theirs is guarded by an
        /// initialised flag — but this driver poses whatever has bones.
        /// </para>
        /// </remarks>
        private static void ApplyPose(Target target)
        {
            var bones = target.Bones;
            var remap = target.Remap;
            if (bones == null || remap == null || bones.Count != remap.Length)
            {
                return;
            }
            if (!KeyframePlayback.TryBracket(target.Poses, cursor, out var span))
            {
                return;
            }

            for (var i = 0; i < remap.Length; i++)
            {
                var source = remap[i];
                if (source == PoseTracks.NoSource)
                {
                    // A bone the host never recorded. Left exactly where it is,
                    // which — the animator being asleep — is the pose it held
                    // when playback started. That is the same filler the game's
                    // own restore path uses, and it costs nothing to obtain.
                    continue;
                }
                var bone = bones[i];
                if (bone == null)
                {
                    continue;
                }

                KeyframePlayback.SampleJoint(span, source, out var position, out var rotation);
                bone.localPosition = new Vector3(position.X, position.Y, position.Z);
                bone.localRotation = new Quaternion(
                    rotation.X, rotation.Y, rotation.Z, rotation.W);
            }

            SyncEquipment(target, span);
        }

        /// <summary>
        /// Pins a weapon to the palm joint it is being held by.
        /// </summary>
        /// <remarks>
        /// The two flags are the reason a pose is not merely a time and an array
        /// of joints. Without this the rifle floats off the hand through every
        /// firing animation — the milestone's showcase, broken in its showcase
        /// moment. World space, and therefore after the bone writes, exactly as
        /// the game orders it.
        /// </remarks>
        private static void SyncEquipment(Target target, PoseSpan span)
        {
            var view = target.MechView;
            if (view == null)
            {
                return;
            }

            if (span.SyncLeftEquipment
                && view.lWeaponTransform != null && view.lWeaponJointPalmLocal != null)
            {
                view.lWeaponTransform.position = view.lWeaponJointPalmLocal.position;
                view.lWeaponTransform.rotation = view.lWeaponJointPalmLocal.rotation;
            }
            if (span.SyncRightEquipment
                && view.rWeaponTransform != null && view.rWeaponJointPalmLocal != null)
            {
                view.rWeaponTransform.position = view.rWeaponJointPalmLocal.position;
                view.rWeaponTransform.rotation = view.rWeaponJointPalmLocal.rotation;
            }
        }

        /// <summary>Only for the log line — the window this playback covers.</summary>
        internal static float Duration => windowEnd - windowStart;
    }
}
