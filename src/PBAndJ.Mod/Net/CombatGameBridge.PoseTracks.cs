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
    // Building one unit's UnitPoseTrack: its joint poses, and the two things that
    // ride the pose track rather than travelling on their own -- the reaction pings
    // and the melee swings.
    //
    // CaptureReactions and CaptureMelees are here rather than beside the other
    // capture helpers because CapturePoses is their only caller and it folds both
    // results straight into the UnitPoseTrack it returns.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        private static readonly List<float> NoReactionPings = new List<float>();

        private static readonly List<MeleeTrajectory> NoMelees = new List<MeleeTrajectory>();

        /// <summary>
        /// One unit's skeletal track for this turn, or null when the host has
        /// no skeleton to describe.
        /// </summary>
        /// <remarks>
        /// The joint <i>names</i> come from the same <c>GetRecordedBones()</c>
        /// list the recorder walked to fill every key, so name <c>i</c> and
        /// value <c>i</c> are the same bone by construction rather than by
        /// agreement. The client rebuilds each key into its own bone order from
        /// those names, which is the only reason this travels at all — the
        /// game's playback loop indexes the joint array to the <i>receiving</i>
        /// machine's bone count with no length guard whatsoever.
        /// <para>
        /// No closing key is appended, unlike the transform track above. The
        /// recorder's own <c>OnExecutionEnd</c> has already added a pose at the
        /// window's end by the time this runs — it fires from
        /// <c>CombatUILinkSimulationEnd</c>, which sits ahead of the system this
        /// capture hangs off — and unlike a transform, a pose cannot be
        /// reconstructed from the ECS if it were missing.
        /// </para>
        /// </remarks>
        private static UnitPoseTrack? CapturePoses(
            CombatEntity unit,
            string? name,
            ReplayUnit track,
            float windowStart,
            float windowEnd,
            ref int strandedKeys)
        {
            var recorded = track.keyframesPoses;
            // Explicit null comparisons rather than ?., because a Unity object
            // that has been destroyed is only null through its own operator.
            var view = unit.hasCombatView ? unit.combatView.view : null;
            var visualManager = view != null ? view.visualManager : null;
            var bones = visualManager != null ? visualManager.GetRecordedBones() : null;
            if (bones == null || bones.Count == 0)
            {
                return null;
            }

            var joints = new string[bones.Count];
            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                joints[i] = bone != null ? bone.name : string.Empty;
            }

            // Sliced exactly as the transform track is, and for the same
            // reason: experimentalMode accumulates across the whole combat, so
            // an unsliced track grows without bound.
            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);

            var keys = new List<PoseKey>(recorded.Count - first);
            for (var i = first; i < recorded.Count; i++)
            {
                var key = recorded[i];

                // RecordUnitPose leaves joints null when a unit had no visual
                // manager at that instant, and a shorter or longer array is a
                // skeleton that was rebuilt part-way through the turn. Either
                // way the values no longer answer to the names above, so the
                // key is dropped rather than mismatched into place.
                if (key.joints == null || key.joints.Length != joints.Length)
                {
                    strandedKeys++;
                    continue;
                }

                var posed = new JointPose[joints.Length];
                for (var j = 0; j < joints.Length; j++)
                {
                    posed[j] = new JointPose(
                        ToVec3(key.joints[j].position), ToVec4(key.joints[j].rotation));
                }
                keys.Add(new PoseKey(
                    key.time, key.syncLeftEquipment, key.syncRightEquipment, posed));
            }

            // The flashes this unit fired, resolved at fire time by
            // WeaponLightPatches and merely collected here. They ride the pose
            // track because a light is meaningless without the unit whose
            // UnitLightManager owns the Light it drives — the same join key
            // serves both — and because the game hangs keyframesLightsWeapons
            // off ReplayUnit for that reason too.
            //
            // Note this returns before here on a boneless unit, which is exactly
            // the orphan case CaptureKeyframes reports: those flashes have no
            // ride and would otherwise vanish without a word.
            var lights = WeaponLightPatches.For(unit.id.id);

            return new UnitPoseTrack(
                name,
                joints,
                keys,
                lights,
                CaptureReactions(track, windowStart),
                CaptureMelees(track, windowStart, windowEnd));
        }

        /// <summary>
        /// This unit's reaction-glow pings inside the turn. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Bare stamps — the recorder's reaction keyframe carries a time and
        /// nothing else — sliced from the window start exactly as the pose keys
        /// above are, and for the identical reason: <c>units</c> MAY not be
        /// cleared between turns, so this list may hold the whole combat's
        /// pings. The slice is written to be correct either way rather than to
        /// branch on the setting, per note 2 on <c>CaptureKeyframes</c>.
        /// <para>
        /// The cap drops the <i>oldest</i>. Only the newest ping at or before
        /// the cursor can ever animate, so trimming the front is invisible while
        /// trimming the back would throw away the live one.
        /// </para>
        /// </remarks>
        private static List<float> CaptureReactions(ReplayUnit track, float windowStart)
        {
            var recorded = track.keyframesLightsReactions;
            if (recorded == null)
            {
                return NoReactionPings;
            }

            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);
            if (recorded.Count - first > PbjMessageCodec.MaxReactionPingsPerUnit)
            {
                first = recorded.Count - PbjMessageCodec.MaxReactionPingsPerUnit;
            }

            var pings = new List<float>(recorded.Count - first);
            for (var i = first; i < recorded.Count; i++)
            {
                pings.Add(recorded[i].time);
            }
            AssetReactionsSent += pings.Count;
            return pings;
        }

        /// <summary>
        /// This unit's melee swings whose windows touch the turn. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Sliced by interval <em>overlap</em>, not by a start-time point test:
        /// a swing straddling the boundary has to arrive in both windows, or the
        /// client shows the second half of a shockwave with no first half and
        /// then never clears it.
        /// <para>
        /// ⚠️ The slice is what makes the cap safe. With the recorder retaining
        /// tracks between turns, <c>entitiesMelee</c> holds every swing of the
        /// fight, so capping the raw list would drop this unit's whole track a
        /// few turns in — silently, and reading exactly like a turn with no
        /// melee in it.
        /// </para>
        /// </remarks>
        private static List<MeleeTrajectory> CaptureMelees(
            ReplayUnit track, float windowStart, float windowEnd)
        {
            var recorded = track.entitiesMelee;
            if (recorded == null)
            {
                return NoMelees;
            }

            var melees = new List<MeleeTrajectory>();
            for (var i = 0; i < recorded.Count; i++)
            {
                var swing = recorded[i];
                if (!ReplayAssetPlayback.OverlapsWindow(
                        swing.timeStart, swing.timeEnd, windowStart, windowEnd))
                {
                    continue;
                }

                melees.Add(new MeleeTrajectory(
                    swing.timeStart,
                    swing.timeEnd,
                    swing.partUsed,
                    swing.shockwaveKey,
                    ToVec3(swing.posStart),
                    ToVec3(swing.posEnd)));
            }

            // Oldest first out, as with the pings: a swing that started earlier
            // is the one nearer to being over.
            while (melees.Count > PbjMessageCodec.MaxMeleesPerUnit)
            {
                melees.RemoveAt(0);
                AssetMeleesDropped++;
            }

            AssetMeleesSent += melees.Count;
            return melees;
        }
    }
}
