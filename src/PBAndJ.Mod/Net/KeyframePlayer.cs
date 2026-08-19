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
    internal static partial class KeyframePlayer
    {

        private static readonly List<Target> targets = new List<Target>();
        private static readonly List<Sleeper> sleepers = new List<Sleeper>();

        /// <summary>
        /// Units the host has wrecked, left asleep on purpose. M17 stage 1.
        /// </summary>
        /// <remarks>
        /// <b>A map keyed by unit name, never a list, and the difference is not
        /// bookkeeping.</b> The host keeps recording bones for a wrecked unit, so
        /// a corpse is re-dressed and re-slept every subsequent window and would
        /// be appended here once per turn for the rest of the fight — a leak that
        /// also makes the feature's own counter climb without anything being
        /// wrong. Re-freezing replaces, and the newest handle is the right one to
        /// keep because the view can be rebuilt underneath us.
        /// </remarks>
        private static readonly Dictionary<string, Sleeper> frozen =
            new Dictionary<string, Sleeper>();
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
        /// <summary>Repositioned per key; see <c>ApplyWeaponLights</c>.</summary>
        private static Transform? lightAnchor;

        /// <summary>
        /// The light counter's own previous cursor, one notch below the window.
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>cursorPrevious</c>, which starts exactly AT
        /// <c>WindowStart</c>. A flash stamped at the window's first instant
        /// then fails a strict <c>previous &lt; time</c> crossing test and is
        /// never counted — while still being armed, because the arming path
        /// only asks that elapsed time be non-negative. That undercounts by one
        /// per turn, which is precisely the sort of standing discrepancy that
        /// sends someone hunting a loss that is not there. Measured: host sent
        /// 142, client counted 141.
        /// </remarks>
        private static float lightsCursorPrevious;

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


        private static Vector3 ToVector3(Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        private static Vector4 ToVector4(Vec4 v) => new Vector4(v.X, v.Y, v.Z, v.W);

        private static Quaternion ToQuaternion(Vec4 v) => new Quaternion(v.X, v.Y, v.Z, v.W);

        private static Color ToColor(Vec4 v) => new Color(v.X, v.Y, v.Z, v.W);

        /// <summary>
        /// Digests the skeleton this machine is drawing right now. M18.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>The measurement this file never had.</b> Thirty-eight counters
        /// reach <c>pbj.drive-state</c> from here and not one of them can tell a
        /// correct pose from a wrong one — <see cref="PosedUnits"/> counts units
        /// bones were written for, never whether the bones were right. So the
        /// whole playback path could be refactored, every number could match, and
        /// every mech could hold the wrong pose.
        /// <para>
        /// Read from the same <see cref="Target.Bones"/> the driver writes, in
        /// local space because that is what <see cref="ApplyPose"/> sets, and by
        /// bone index because two joints that swapped are the defect worth
        /// catching.
        /// </para>
        /// <para>
        /// ⚠️ Meaningful only at a pinned cursor — see <c>pbj.fx-hold</c> — and
        /// only beside a non-zero count. A machine posing nothing digests to the
        /// empty basis and matches another machine posing nothing.
        /// </para>
        /// </remarks>
        internal static (int Count, string Digest) DigestPose()
        {
            var entries = new List<PoseBoneEntry>();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var bones = target.Bones;
                if (bones == null || string.IsNullOrEmpty(target.Name))
                {
                    continue;
                }

                for (var b = 0; b < bones.Count; b++)
                {
                    var bone = bones[b];
                    if (bone == null)
                    {
                        continue;
                    }

                    var p = bone.localPosition;
                    var r = bone.localRotation;
                    entries.Add(new PoseBoneEntry(
                        target.Name, b,
                        new Vec3(p.x, p.y, p.z),
                        new Vec4(r.x, r.y, r.z, r.w)));
                }
            }

            return PoseDigest.Compute(entries);
        }

        /// <summary>Only for the log line — the window this playback covers.</summary>
        internal static float Duration => windowEnd - windowStart;
    }
}
