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
    // The replay ASSET tracks -- beams, trails and the rest -- sliced to this turn's
    // window. CaptureAssets is the entry point; Head, InWindow, SliceTrail and
    // WindowRange are called from nowhere else.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        /// <summary>
        /// One turn's projectiles, beams and one-shot effects. M14.
        /// </summary>
        /// <remarks>
        /// Sliced by window overlap and never sent whole, and the slice must not
        /// branch on <c>experimentalMode</c>. That player setting gates the
        /// game's own prune (<c>CombatReplayHelper.cs:241</c>), so on a default
        /// machine <b>nothing is ever pruned</b> and these collections still hold
        /// every effect of the fight at turn twenty — one measured fight grew
        /// <c>assetsStandalone</c> from 51 to 727 across five turns. The slice
        /// has to be correct whichever way the setting sits, so it reads the
        /// times rather than trusting the collection.
        /// <para>
        /// No <c>IsRecordingAllowed</c> gate and no unit lookup, for the reason
        /// <see cref="CaptureKeyframes"/> gives: the flag is already false by the
        /// time this runs. Unlike the unit tracks, nothing here is keyed to a
        /// living entity — which is why a turn that kills everyone can still
        /// record a great deal, and why the host says so rather than discarding
        /// it silently.
        /// </para>
        /// </remarks>
        private static AssetCapture CaptureAssets(float windowStart, float windowEnd)
        {
            var standalone = new List<StandaloneAssetTrack>();
            var projectiles = new List<ProjectileAssetTrack>();
            var beams = new List<BeamAssetTrack>();
            var trailed = 0;
            var trailPoints = 0;
            var trailOverCap = 0;

            var recorded = CombatReplayHelper.assetsStandalone;
            if (recorded != null)
            {
                for (var i = 0; i < recorded.Count; i++)
                {
                    var entry = recorded[i];
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }

                    // The id is this list's position in THIS capture, not the
                    // game's — these have no identity of their own, and the
                    // recorder's own list is pruned by index so its positions
                    // shift between turns. A within-turn label, nothing more.
                    standalone.Add(new StandaloneAssetTrack(
                        standalone.Count,
                        Head(entry),
                        ToVec3(entry.position),
                        ToVec4(entry.rotation),
                        // Load-bearing: AssignAsset writes this straight to
                        // localScale, so a lost scale is an invisible effect.
                        ToVec3(entry.scale),
                        ToVec4(entry.velocityAndDecay),
                        ToVec3(entry.positionLocal)));
                }
            }

            if (CombatReplayHelper.assetsProjectiles != null)
            {
                foreach (var pair in CombatReplayHelper.assetsProjectiles)
                {
                    var entry = pair.Value;
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }
                    var keys = new List<TransformKey>();
                    var recordedKeys = entry.keyframesTransform;
                    WindowRange(
                        recordedKeys.Count, i => recordedKeys[i].time, windowStart, windowEnd,
                        out var first, out var last);
                    for (var i = first; i <= last; i++)
                    {
                        keys.Add(new TransformKey(
                            recordedKeys[i].time,
                            ToVec3(recordedKeys[i].position),
                            ToVec4(recordedKeys[i].rotation)));
                    }

                    var trail = SliceTrail(entry.keyframesTrail, windowStart, windowEnd);
                    if (trail.Count > 0)
                    {
                        trailed++;
                        trailPoints += trail.Count;

                        // Counted here rather than where the thinning happens,
                        // because this is the last place the pre-cap length
                        // exists. TryPrepare only ever sees the result.
                        if (trail.Count > PbjMessageCodec.MaxTrailPointsPerTrack)
                        {
                            trailOverCap++;
                        }
                    }

                    projectiles.Add(new ProjectileAssetTrack(
                        pair.Key, Head(entry), ToVec3(entry.scale), keys, trail));
                }
            }

            if (CombatReplayHelper.assetsBeams != null)
            {
                foreach (var pair in CombatReplayHelper.assetsBeams)
                {
                    var entry = pair.Value;
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }

                    var keys = new List<BeamKey>();
                    var recordedKeys = entry.keyframes;
                    WindowRange(
                        recordedKeys.Count, i => recordedKeys[i].time, windowStart, windowEnd,
                        out var first, out var last);
                    for (var i = first; i <= last; i++)
                    {
                        keys.Add(new BeamKey(
                            recordedKeys[i].time,
                            ToVec3(recordedKeys[i].position),
                            ToVec4(recordedKeys[i].rotation),
                            ToVec3(recordedKeys[i].parameters)));
                    }

                    beams.Add(new BeamAssetTrack(pair.Key, Head(entry), keys));
                }
            }

            if (trailed > 0)
            {
                // Points per turn is the number MaxTrailPointsPerTrack is sized
                // against, and the only one that would show a weapon far heavier
                // than the ~32-points-per-trail this was measured on.
                Debug.Log(NetLog.AssetTrailsSent(trailed, trailPoints, trailOverCap));
            }

            return new AssetCapture(standalone, projectiles, beams);
        }

        /// <summary>
        /// The trail points alive at any moment of the window, in emission order.
        /// </summary>
        /// <remarks>
        /// <b>Interval overlap, never the point test the transform keys use.</b>
        /// A trail point's <c>timeStart</c>/<c>timeEnd</c> are on an absolute
        /// clock of their own, and a point emitted before the window opens is
        /// still part of the visible ribbon until its life runs out. Slicing on
        /// <c>timeStart</c> with <see cref="WindowRange"/> would drop exactly
        /// those, and the trail would pop in bald at every turn boundary on any
        /// projectile that spans two turns — a loss no count and no log would
        /// show.
        /// <para>
        /// <see cref="ReplayAssetPlayback.OverlapsWindow"/> is the predicate,
        /// reused rather than restated: stage A wrote it under the coverage gate
        /// for track activation and it generalises here unchanged.
        /// </para>
        /// <para>
        /// No bracketing key on either side, unlike the transform slice. Trail
        /// points are not interpolated between — the game rebuilds each one into
        /// an <c>AraTrail.Point</c> and hands the list over as a polyline — so a
        /// neighbour outside the window is simply a point that should not be
        /// drawn.
        /// </para>
        /// </remarks>
        private static List<TrailKey> SliceTrail(
            List<ReplayKeyframeTrailPoint>? recorded, float windowStart, float windowEnd)
        {
            var trail = new List<TrailKey>();
            if (recorded == null)
            {
                return trail;
            }

            for (var i = 0; i < recorded.Count; i++)
            {
                var point = recorded[i];
                if (!ReplayAssetPlayback.OverlapsWindow(
                        point.timeStart, point.timeEnd, windowStart, windowEnd))
                {
                    continue;
                }

                trail.Add(new TrailKey(
                    point.timeStart,
                    point.timeEnd,
                    ToVec3(point.position),
                    ToVec3(point.velocity),
                    ToVec3(point.perlinDirection),
                    ToVec3(point.tangent),
                    ToVec3(point.normal),
                    ToVec4(point.color),
                    point.thickness,
                    point.texcoord));
            }

            return trail;
        }

        private static bool InWindow(ReplayEntityAsset entry, float windowStart, float windowEnd) =>
            ReplayAssetPlayback.OverlapsWindow(
                entry.timeStart, entry.timeEnd, windowStart, windowEnd);

        /// <summary>
        /// The head every asset track shares, both optional blocks included.
        /// </summary>
        /// <remarks>
        /// <c>assetKeyHash</c> is deliberately not read. It is
        /// <c>string.GetHashCode</c>, which carries no cross-process stability
        /// guarantee, so a hash minted here would match nothing on a client —
        /// silently, since a failed lookup simply shows no effect. The key
        /// travels as the string it is.
        /// </remarks>
        private static AssetTrackHead Head(ReplayEntityAsset entry)
        {
            // Null is a real value in both, and not the same as zero: an absent
            // hue means the effect keeps its prefab's own, where a hue of zero
            // is an instruction to flatten it.
            var hue = entry.assetHueOffset != null ? entry.assetHueOffset.f : (float?)null;
            var colour = entry.assetColorOverride != null
                ? new AssetColour(
                    ToVec4(entry.assetColorOverride.colorFrom),
                    ToVec4(entry.assetColorOverride.colorTo))
                : (AssetColour?)null;

            return new AssetTrackHead(
                entry.assetKey, entry.timeStart, entry.timeEnd, hue, colour);
        }

        /// <summary>
        /// The keys of one asset track that this turn's window needs, with the
        /// bracketing key on each side.
        /// </summary>
        /// <remarks>
        /// The brackets are not padding. <c>ReplayEntityAssetProjectile.ApplyTime</c>
        /// interpolates between the pair of keys straddling the requested time
        /// on an <b>absolute</b> clock, so a slice cut flush to the window leaves
        /// the cursor's first frames with nothing before them and the projectile
        /// snaps to its first in-window key instead of arriving at it. Same for
        /// the far end.
        /// <para>
        /// An empty range is returned as <c>first = 0, last = -1</c>, so the
        /// caller's loop adds nothing and the track goes out with no keys — where
        /// <c>ReplayAssetParts.TryPrepare</c> drops it as
        /// <see cref="AssetTrackFault.TooFewKeys"/>. That is the right outcome
        /// and the right place for it: a track with nothing in this window has
        /// nothing to draw, and sending it would put a frozen instance at
        /// <c>keyframes[0]</c> or at the world origin.
        /// </para>
        /// </remarks>
        private static void WindowRange(
            int count,
            Func<int, float> timeAt,
            float windowStart,
            float windowEnd,
            out int first,
            out int last)
        {
            first = count;
            last = -1;
            for (var i = 0; i < count; i++)
            {
                var time = timeAt(i);
                if (time < windowStart || time > windowEnd)
                {
                    continue;
                }
                if (i < first)
                {
                    first = i;
                }
                last = i;
            }

            if (last < 0)
            {
                first = 0;
                return;
            }
            if (first > 0)
            {
                first--;
            }
            if (last < count - 1)
            {
                last++;
            }
        }
    }
}
