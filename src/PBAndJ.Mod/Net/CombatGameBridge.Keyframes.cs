using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The keyframe capture a host ships at the end of a turn: the orchestrator, the
    // transform track it slices and thins (SliceTurn, Decimate), the seam-finder both
    // lean on (TurnStart), Windowed, and the two reports that say what did not make it
    // onto the wire.
    //
    // The three Asset*Sent/Dropped counters live here because CaptureKeyframes owns
    // their lifecycle -- it zeroes them at the top of the capture and reports them at
    // the bottom. CaptureReactions and CaptureMelees, in
    // CombatGameBridge.PoseTracks.cs, only increment them.
    //
    // TurnStart is here against its caller majority -- CapturePoses and
    // CaptureReactions call it from PoseTracks.cs, SliceTurn calls it here, 2:1.
    // It stays because its doc is the SECOND HALF OF NOTE 2 above: note 2 says the
    // slice must be by index because turnStartTime is Mathf.RoundToInt'd, and
    // TurnStart's remarks are the argument for why that rounding needs a second,
    // monotonicity pass. Separating them puts half an argument in each file.
    //
    // Two reasons NOT to believe, both of which earlier drafts of this header shipped
    // and a review refuted: it is not window arithmetic (it never sees a window end,
    // only windowStart), and SliceTurn is not a wrapper that 'applies' its rule --
    // both passes are inside TurnStart, and SliceTurn calls-and-copies exactly as the
    // two majority callers do.
    //
    // TRAP: the three Asset* counters are here, not in CombatGameBridge.Assets.cs.
    // That file is the replay ASSET tracks and holds none of them; what these count
    // is produced in PoseTracks.cs. The name collision is real -- look here first.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        /// <summary>
        /// Reaction pings, melee swings and dropped swings sent this turn.
        /// </summary>
        /// <remarks>
        /// Positive counters, deliberately, and stage B is why: weapon lights
        /// shipped with loss counters only, and all-zero losses read identically
        /// whether every flash travelled or the light code never ran at all. A
        /// number that goes up is the only thing a playtest can falsify.
        /// </remarks>
        internal static int AssetReactionsSent;

        internal static int AssetMeleesSent;

        internal static int AssetMeleesDropped;

        // Walks the game's own replay recorder and re-keys it for the wire.
        //
        // Three things here are not obvious and were each verified against the
        // decompiled 2.2.2-b8339 source:
        //
        // 1. Do NOT gate on CombatReplayHelper.IsRecordingAllowed(). It is
        //    already false by the time we run: OnExecutionEnd clears the flag,
        //    and it is called from CombatUILinkSimulationEnd, which sits in
        //    CombatUISystems (slot 72) — ahead of CombatExecutionEndLateSystem
        //    (slot 93), the system whose postfix brings us here. Both react to
        //    the same Simulating.Removed() collector. Gating on it would return
        //    empty every single turn.
        //
        // 2. The tracks MAY NOT be cleared between turns, so never assume either
        //    way. OnExecutionStart clears `units` only when experimentalMode is
        //    false, and while the field defaults to true in code it is a player
        //    SETTING (Experimental_ReplayExtended) — a probe on a real game read
        //    it as FALSE, so both states ship. With it on, a track accumulates
        //    for the whole combat. We slice from the key OnExecutionStart wrote,
        //    BY INDEX — not by comparing against turnStartTime, which is
        //    Mathf.RoundToInt'd and so can be *later* than the previous turn's
        //    final key, dragging it into our window. That slice is correct
        //    whichever way the setting sits, which is why it is written this way
        //    rather than branching on the flag.
        //
        // 3. The recorder's last key is not the unit's final position.
        //    OnExecutionEnd samples position before CombatExecutionEndLateSystem
        //    force-sets it onto the projected path, and its own OnUnitSnapshot
        //    call is a no-op by (1). So we append a final key ourselves, read
        //    exactly where CaptureSnapshot reads, which is what makes
        //    "last key == snapshot" true rather than merely hoped for.
        public KeyframeCapture CaptureKeyframes()
        {
            if (!InCombat || CombatReplayHelper.units == null || CombatReplayHelper.units.Count == 0)
            {
                Debug.Log(NetLog.KeyframesUnavailable());
                return KeyframeCapture.None;
            }

            var windowStart = CombatReplayHelper.turnStartTime;
            var windowEnd = Contexts.sharedInstance.combat.hasSimulationTime
                ? Contexts.sharedInstance.combat.simulationTime.f
                : windowStart;

            AssetReactionsSent = 0;
            AssetMeleesSent = 0;
            AssetMeleesDropped = 0;

            // M14. Taken from the same window the unit tracks are, before the
            // loop, so the two cannot disagree about where the turn began — the
            // whole reason both live on one KeyframeCapture.
            var assets = CaptureAssets(windowStart, windowEnd);

            var tracks = new List<UnitTrack>();
            var poses = new List<UnitPoseTrack>();
            var clamped = 0;
            var bonelessUnits = 0;
            var strandedKeys = 0;

            foreach (var entry in CombatReplayHelper.units)
            {
                // The recorder keys by combatEntity.id.id, a process-local ECS
                // id that means nothing in another process. Same lookup
                // OnExecutionEnd itself uses.
                var unit = IDUtility.GetCombatEntity(entry.Key);
                if (unit == null || unit.isDestroyed)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }

                var keys = SliceTurn(entry.Value.keyframesTransform, windowStart);

                // The final key, from the same read CaptureSnapshot performs.
                keys.Add(new TransformKey(
                    windowEnd,
                    ToVec3(unit.hasPosition ? unit.position.v : Vector3.zero),
                    ToVec4(unit.hasRotation ? unit.rotation.q : Quaternion.identity)));

                if (keys.Count > PbjMessageCodec.MaxKeysPerTrack)
                {
                    // Drop interior keys and keep the endpoints: a track
                    // truncated at the tail would end playback short of the
                    // state the snapshot already corrected everyone to.
                    clamped++;
                    keys = Decimate(keys, PbjMessageCodec.MaxKeysPerTrack);
                }

                tracks.Add(new UnitTrack(
                    persistent.nameInternal.s,
                    keys,
                    Windowed(entry.Value.keyframeReveal, windowStart, windowEnd),
                    Windowed(entry.Value.keyframeHidden, windowStart, windowEnd)));

                // M8. Beside the transform track, never inside it: a turn whose
                // poses cannot travel still plays as M6 always did.
                var pose = CapturePoses(
                    unit, persistent.nameInternal.s, entry.Value, windowStart, windowEnd,
                    ref strandedKeys);
                if (pose == null)
                {
                    bonelessUnits++;
                }
                else
                {
                    poses.Add(pose);
                }

                if (tracks.Count == PbjMessageCodec.MaxTracksPerKeyframes)
                {
                    Debug.LogWarning(NetLog.KeyframesClamped(
                        CombatReplayHelper.units.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
                    ReportUncaptured(bonelessUnits, strandedKeys);
                    return new KeyframeCapture(windowStart, windowEnd, tracks, poses, assets);
                }
            }

            if (clamped > 0)
            {
                Debug.LogWarning(NetLog.KeyframesClamped(
                    tracks.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
            }
            ReportUncaptured(bonelessUnits, strandedKeys);
            ReportOrphanedLights(poses);

            // Positive first, and only when there is something to say — a quiet
            // turn is not an incomplete one, which is the distinction stage A
            // had to add AssetsNoneSent to make.
            if (AssetReactionsSent > 0 || AssetMeleesSent > 0)
            {
                Debug.Log(NetLog.AssetReactionsAndMeleesSent(
                    AssetReactionsSent, AssetMeleesSent));
            }

            if (AssetMeleesDropped > 0)
            {
                Debug.LogWarning(NetLog.MeleesOverCap(AssetMeleesDropped));
            }
            WeaponLightPatches.Clear();
            return new KeyframeCapture(windowStart, windowEnd, tracks, poses, assets);
        }

        /// <summary>
        /// Names the flashes that fired but found no pose track to ride.
        /// </summary>
        /// <remarks>
        /// The one cost of joining lights to poses, made loud instead of silent.
        /// A unit the recorder gave no bones — or one clamped off the end of the
        /// track list — drops its flashes with it, and a missing muzzle flash
        /// among muzzle flashes is invisible on screen. This project has learned
        /// twice that a loss only a count can see has to be counted.
        /// <para>
        /// Deliberately walks the captured cache rather than the pose list: the
        /// loss is a unit that is <b>not</b> in the collection the harvest
        /// walked, so it cannot be found by looking at what was harvested.
        /// </para>
        /// </remarks>
        private static void ReportOrphanedLights(List<UnitPoseTrack> poses)
        {
            var carried = 0;
            for (var i = 0; i < poses.Count; i++)
            {
                carried += poses[i].Lights.Count;
            }

            var fired = 0;
            var firedUnits = 0;
            foreach (var pair in WeaponLightPatches.All())
            {
                fired += pair.Value.Count;
                firedUnits++;
            }

            // The positive count first, and unconditionally when anything fired.
            // Without it a playtest reading zero losses cannot tell "every flash
            // travelled" from "no light code ran at all".
            if (carried > 0)
            {
                var units = 0;
                for (var i = 0; i < poses.Count; i++)
                {
                    if (poses[i].Lights.Count > 0)
                    {
                        units++;
                    }
                }
                Debug.Log(NetLog.AssetLightsSent(units, carried));
            }

            var orphaned = fired - carried;
            if (orphaned > 0)
            {
                // Units, not flashes, is the honest denominator for the first
                // number: we know how many flashes were stranded but not how
                // many distinct units stranded them, so the unit count is the
                // upper bound and is reported as such.
                Debug.LogWarning(NetLog.LightsWithoutPoseTrack(firedUnits, orphaned));
            }

            if (WeaponLightPatches.SkippedNoTransform > 0)
            {
                Debug.Log(NetLog.LightsUnusable(WeaponLightPatches.SkippedNoTransform));
            }
        }

        /// <summary>
        /// A recorded visibility stamp, if it belongs to this turn.
        /// </summary>
        /// <remarks>
        /// <c>ReplayUnit.keyframeReveal</c> and <c>keyframeHidden</c> are single
        /// slots, not lists, and nothing clears them between turns while
        /// <c>experimentalMode</c> is on — the same accumulation the transform
        /// and pose slices already work around. Sent unclipped, a unit that was
        /// revealed once on turn 2 would be re-revealed on every turn after it,
        /// which on a client looks like the unit blinking out at the start of
        /// every window for the rest of the fight.
        /// <para>
        /// Inclusive at both ends. A stamp exactly at the window start is this
        /// turn's — <c>OnExecutionStart</c> and the reveal it triggers share a
        /// frame — and one exactly at the end is the moment the snapshot was
        /// taken.
        /// </para>
        /// </remarks>
        private static float Windowed(ReplayKeyframe? stamp, float windowStart, float windowEnd)
        {
            if (stamp == null || stamp.time < windowStart || stamp.time > windowEnd)
            {
                return ReplayVisibility.None;
            }
            return stamp.time;
        }

        private static void ReportUncaptured(int bonelessUnits, int strandedKeys)
        {
            if (bonelessUnits > 0 || strandedKeys > 0)
            {
                Debug.LogWarning(NetLog.PosesNotCaptured(bonelessUnits, strandedKeys));
            }
        }

        /// <summary>
        /// Where this turn's keys begin in an accumulated recorder list.
        /// </summary>
        /// <remarks>
        /// Two passes, and the second is not belt-and-braces. The backward scan
        /// is the obvious one: everything at or after <c>turnStartTime</c>
        /// belongs to this turn, and walking back from the end finds it without
        /// touching the whole accumulated combat.
        /// <para>
        /// But <c>turnStartTime</c> is the simulation clock <b>rounded to a
        /// whole second</b>, while the recorder stamps raw simulation time — so
        /// a turn that overruns the rounded boundary leaves the <i>previous</i>
        /// turn's closing keys satisfying that test too. Within a turn the
        /// stamps never decrease, so the last place they do decrease is exactly
        /// the seam between the two turns. Left unfixed, a track arrives
        /// non-monotonic in its middle and the unit jumps backwards mid-window.
        /// </para>
        /// <para>
        /// The remaining case is a boundary that rounds <i>up</i>, which would
        /// put this turn's opening stamp above its own first samples. Not
        /// observed — every measured turn has landed on an exact second — and
        /// not guessed at here.
        /// </para>
        /// </remarks>
        private static int TurnStart(int count, Func<int, float> timeAt, float windowStart)
        {
            var first = count;
            while (first > 0 && timeAt(first - 1) >= windowStart)
            {
                first--;
            }

            for (var i = first + 1; i < count; i++)
            {
                if (timeAt(i) < timeAt(i - 1))
                {
                    first = i;
                }
            }
            return first;
        }

        private static List<TransformKey> SliceTurn(
            List<ReplayKeyframeTransform> recorded, float windowStart)
        {
            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);

            var keys = new List<TransformKey>(recorded.Count - first + 1);
            for (var i = first; i < recorded.Count; i++)
            {
                keys.Add(new TransformKey(
                    recorded[i].time, ToVec3(recorded[i].position), ToVec4(recorded[i].rotation)));
            }
            return keys;
        }

        // Keeps the first and last key and thins what is between them, so a long
        // turn loses temporal resolution rather than its ending.
        private static List<TransformKey> Decimate(List<TransformKey> keys, int cap)
        {
            var kept = new List<TransformKey>(cap) { keys[0] };
            var interior = cap - 2;
            var step = (keys.Count - 2) / (double)interior;
            for (var i = 0; i < interior; i++)
            {
                kept.Add(keys[1 + (int)(i * step)]);
            }
            kept.Add(keys[keys.Count - 1]);
            return kept;
        }
    }
}
