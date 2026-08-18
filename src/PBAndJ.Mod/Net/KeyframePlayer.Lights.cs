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
    // Weapon lights, reaction pings and melee trails, M14 stages B and C.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
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

        /// <summary>
        /// Weapon lights this client actually armed, counted once each.
        /// </summary>
        /// <remarks>
        /// The falsifiable half of the weapon-light instrumentation, and it was
        /// added after a playtest proved the other half insufficient: with only
        /// <see cref="LightsRefused"/> and the host's loss lines, a run where
        /// every flash rendered and a run where the light code never executed
        /// both read as all-zero. Counted on the frame the cursor crosses a
        /// key's time, not per call — <c>OnWeaponLight</c> is re-armed every
        /// frame a light is inside its envelope, so counting calls would report
        /// the frame rate.
        /// </remarks>
        internal static int LightsFired { get; private set; }

        /// <summary>
        /// Weapon lights dropped because their unit has no light manager.
        /// </summary>
        /// <remarks>
        /// Its own counter because it is the one weapon-light loss the host
        /// cannot see: the unit HAS a pose track, so
        /// <see cref="NetLog.LightsWithoutPoseTrack"/> stays silent, and nothing
        /// throws, so <see cref="LightsRefused"/> stays silent too. A whole
        /// unit's flashes would simply not happen.
        /// </remarks>
        internal static int LightsNoManager { get; private set; }

        /// <summary>
        /// Reaction glows and melee swings the cursor crossed. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Both counted at the window edge, never per call, for the reason
        /// <see cref="LightsFired"/> gives: the newest ping is re-stamped and a
        /// live swing re-driven on every frame, so counting calls would report
        /// the frame rate rather than the events.
        /// <para>
        /// ⚠️ A 1:1 match against the host still cannot prove either was SEEN.
        /// <c>reactionDuration</c> defaults to 0.1 s
        /// (<c>UnitLightManager.cs:45</c>), so a whole glow can fall between two
        /// playback frames — the same sub-frame class stage B's counter caught,
        /// and the reason the eye test is not optional here.
        /// </para>
        /// </remarks>
        internal static int ReactionsPlayed { get; private set; }

        internal static int MeleesPlayed { get; private set; }

        /// <summary>Swings whose drive threw inside game code.</summary>
        internal static int MeleesRefused { get; private set; }


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
        private static void ApplyUnitLights(Target target)
        {
            var poses = target.Poses;
            if (poses == null || (poses.Lights.Count == 0 && poses.Reactions.Count == 0))
            {
                return;
            }

            var manager = target.Lights;
            if (manager == null)
            {
                // Counted, not merely skipped — see LightsNoManager.
                for (var i = 0; i < poses.Lights.Count; i++)
                {
                    if (lightsCursorPrevious < poses.Lights[i].Time
                        && poses.Lights[i].Time <= cursor)
                    {
                        LightsNoManager++;
                    }
                }
                return;
            }

            for (var i = 0; i < poses.Lights.Count; i++)
            {
                var key = poses.Lights[i];

                // Only flashes the cursor has reached, and only those still
                // within their own envelope — re-arming an expired light every
                // frame would restart its fade and leave it lit forever.
                // COUNTED FIRST, and that order is the whole rule — exactly the
                // one stage A had to move into Core as ActionFor. Written the
                // obvious way, with the envelope test above this, a flash whose
                // whole life falls between two frames is skipped before it is
                // ever counted, so it is neither armed nor reported. Measured:
                // the host sent 58 and the client counted 56.
                if (lightsCursorPrevious < key.Time && key.Time <= cursor)
                {
                    LightsFired++;
                }

                // Interval overlap, not a point test on elapsed time. A frame
                // gap during playback is routinely longer than a weapon light
                // lives, so "is the cursor inside the envelope right now" is
                // false for most of the short ones on every single frame.
                //
                // The durations are floored the way OnWeaponLight itself floors
                // them (UnitLightManager.cs:276-278). Using the raw values makes
                // our envelope narrower than the one the game will actually
                // animate — as little as 0.05s against its 0.10s — so we would
                // decline to arm lights the game would happily have shown.
                var life = Mathf.Max(0.05f, key.DurationStable)
                    + Mathf.Max(0.05f, key.DurationFade);
                if (!ReplayAssetPlayback.OverlapsWindow(
                        key.Time, key.Time + life, lightsCursorPrevious, cursor))
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

            // Ahead of OnTimeChange, exactly as the game's own replay orders
            // them (CombatReplayHelper.cs:1247-1269): the ping stamps the time
            // the glow started, and OnTimeChange is what animates it from there.
            // Skipped entirely when nothing has been pinged yet — vanilla never
            // reaches OnReactionPing in that case either, and a stamp we
            // invented would be a claim rather than a silence.
            var ping = ReactionPings.LatestAtOrBefore(poses.Reactions, cursor);
            if (ping.HasValue)
            {
                for (var i = 0; i < poses.Reactions.Count; i++)
                {
                    if (lightsCursorPrevious < poses.Reactions[i]
                        && poses.Reactions[i] <= cursor)
                    {
                        ReactionsPlayed++;
                    }
                }

                manager.OnReactionPing(ping.Value);
            }

            // Wrapped, and only worth wrapping now that the reaction branch can
            // arm. OnReactionAnimation dereferences reactionAmbient and
            // reactionGlow with no null check while its caller guards only
            // reactionHolder (UnitLightManager.cs:137, :191-192) — the class's
            // own OnFaction null-checks both, which is the tell that a rig may
            // leave them unset. A host would have crashed on the same rig long
            // before we saw it, so this is insurance rather than an expectation.
            try
            {
                manager.OnTimeChange(cursor);
            }
            catch (Exception e)
            {
                LightsRefused++;
                Debug.LogWarning(
                    "[pb-and-j] unit light update was refused: " + e.Message);
            }
        }

        /// <summary>
        /// Drives this unit's melee shockwave trail. M14 stage C.
        /// </summary>
        /// <remarks>
        /// A transcription of <c>CombatReplayHelper.cs:1311-1329</c>, which is a
        /// call rather than a track: the game's replay re-runs
        /// <c>MeleeUtility.CheckOverlapsWithShockwave</c> every frame for each
        /// swing whose window contains the cursor, and clears the trail on any
        /// frame where none does.
        /// <para>
        /// 🔑 <c>registerHits: false</c> with a null action is the whole safety
        /// argument, and the load-bearing line is <c>MeleeUtility.cs:496</c> —
        /// <c>if (!(flag4 &amp;&amp; registerHits) || ...) continue;</c> — which
        /// makes the entire hit-processing block unreachable. That block, not
        /// the tail, is what holds the overlap physics, <c>VerifyMeleeHit</c>,
        /// <b>real level prop destruction</b> and the projectile pop. Vanilla's
        /// own replay passes exactly this pair. Anyone refactoring this must
        /// re-check <c>:496</c>, not merely the impact code further down.
        /// </para>
        /// <para>
        /// Record order is preserved because co-active swings share one trail
        /// object and the last call wins, matching the game's <c>foreach</c>.
        /// </para>
        /// </remarks>
        private static void ApplyMelees(Target target)
        {
            var poses = target.Poses;
            var unit = target.Unit;
            if (poses == null || poses.Melees.Count == 0 || unit == null)
            {
                return;
            }

            var anyActive = false;
            for (var i = 0; i < poses.Melees.Count; i++)
            {
                var melee = poses.Melees[i];
                if (!MeleeTrajectoryPlayback.TryNormalise(melee, cursor, out var normalised))
                {
                    continue;
                }

                anyActive = true;
                if (lightsCursorPrevious < melee.TimeStart && melee.TimeStart <= cursor)
                {
                    MeleesPlayed++;
                }

                try
                {
                    DriveShockwave(unit, melee, normalised);
                }
                catch (Exception e)
                {
                    MeleesRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] melee shockwave was refused: " + e.Message);
                }
            }

            if (!anyActive)
            {
                ClearShockwave(unit);
            }
        }

        private static void DriveShockwave(CombatEntity unit, MeleeTrajectory melee, float normalised)
        {
            var shockwave = DataMultiLinker<DataContainerEquipmentShockwave>
                .GetEntry(melee.ShockwaveKey, printWarning: false);
            var anim = DataShortcuts.anim;
            var curve = melee.PartUsed ? anim.timeRemapMeleeStandard : anim.timeRemapMeleeFallback;

            MeleeUtility.CheckOverlapsWithShockwave(
                unit,
                new Vector3(melee.PosStart.X, melee.PosStart.Y, melee.PosStart.Z),
                new Vector3(melee.PosEnd.X, melee.PosEnd.Y, melee.PosEnd.Z),
                shockwave,
                curve,
                normalised,
                predictionMode: false,
                registerHits: false,
                actionExecuted: null);
        }

        /// <summary>
        /// Puts a shockwave trail away, on the frame it stops being active and
        /// again at teardown.
        /// </summary>
        /// <remarks>
        /// The teardown call is not belt and braces. Vanilla's turn-boundary
        /// clear lives in <c>OnExecutionStart</c>
        /// (<c>CombatReplayHelper.cs:308</c>), which only a simulating host
        /// runs, and our cursor clamps to the window end — so a swing still
        /// active on the final frame is driven and then never cleared, leaving a
        /// trail hanging through a planning phase that lasts minutes.
        /// </remarks>
        private static void ClearShockwave(CombatEntity unit)
        {
            if (!unit.hasCombatView || unit.combatView.view == null)
            {
                return;
            }

            var visualManager = unit.combatView.view.visualManager;
            var vfx = visualManager != null ? visualManager.GetVFXManager() : null;
            if (vfx is UnitVFXManager melee)
            {
                melee.OnMeleeShockwaveClear();
            }
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
    }
}
