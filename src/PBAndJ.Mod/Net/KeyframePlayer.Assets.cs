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
    // Building and driving a turn's effect tracks, M14.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
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
    }
}
