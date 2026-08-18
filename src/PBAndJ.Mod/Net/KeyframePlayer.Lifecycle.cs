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
    // Window lifecycle: open a turn, advance it, close it.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
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

                var target = new Target(track, unit.combatView.view.transform)
                {
                    Unit = unit,
                    Name = persistent.nameInternal.s,
                    Visuals = unit.combatView.view.visualManager,
                };
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
            LightsFired = 0;
            ReactionsPlayed = 0;
            MeleesPlayed = 0;
            MeleesRefused = 0;
            LightsNoManager = 0;
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
            lightsCursorPrevious = capture.WindowStart - 1f;
            Turn = turn;
            playing = true;

            // Applied only now, past both early returns: a unit hidden before
            // playback was committed would stay hidden with nothing left to
            // reveal it.
            ApplyVisibility();
        }

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
            //
            // M17 stage 1 inverts that argument for a frozen unit without
            // breaking it: its unwind is deliberately never run, so there is
            // nothing for an inactive root to swallow. RestoreVisibility may
            // re-activate the CombatPuppetView root of a corpse, and that is
            // harmless only because the puppetMaster and puppetBehaviour holders
            // beneath it stay inactive across a root toggle. Anyone "fixing"
            // this ordering should know it is now carrying two arguments.
            RestoreVisibility();
            Wake();
            // Melee trails on the same argument as the effects below, and for a
            // sharper reason: vanilla's turn-boundary clear lives in
            // OnExecutionStart, which only a simulating host runs. A swing still
            // active on the final frame is driven and then never cleared, so
            // without this the trail hangs there through the whole planning
            // phase.
            for (var i = 0; i < targets.Count; i++)
            {
                var unit = targets[i].Unit;
                if (unit == null || targets[i].Poses == null || targets[i].Poses!.Melees.Count == 0)
                {
                    continue;
                }

                try
                {
                    ClearShockwave(unit);
                }
                catch (Exception e)
                {
                    Debug.LogWarning(
                        "[pb-and-j] could not clear a melee shockwave: " + e.Message);
                }
            }

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
                ApplyUnitLights(target);
                ApplyMelees(target);
                ApplyDestruction(target);
            }

            // After the transform and pose writes, so a unit revealed this frame
            // is shown where the window says it is rather than where it was left
            // standing a frame earlier.
            ApplyVisibility();

            // And the effects after the visibility, so a muzzle flash never
            // appears a frame before the unit firing it does.
            ApplyShows();
            cursorPrevious = cursor;
            lightsCursorPrevious = cursor;

            if (finished)
            {
                // Before Stop, and only on the natural finish. A part wrecked a
                // tenth of a second before the window ends has ramped a fifth of
                // the way there, and without this it would sit half-dissolved
                // through a planning phase that lasts minutes. An aborted window
                // deliberately skips this and converges on the next snapshot
                // instead — there is no reason to believe a fault means the turn
                // is over.
                ApplySettled(destruction.SettleWindow());

                // M16, and after the destruction settle for the same reason the
                // snapshot path orders them that way: M15 writes a synthesised
                // integrity to the visual, and the real value has to land last.
                SettlePartIntegrity();
                Stop();
            }
        }
    }
}
