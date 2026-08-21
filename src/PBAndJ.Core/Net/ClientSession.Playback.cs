using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // Being corrected, and being shown how it happened.
    //
    // The snapshot hard-sets the authoritative state; the keyframes that follow only
    // animate the view towards the same place. HandleKeyframes' own remark says why
    // that ordering is what let playback be added without touching the correction
    // path.
    //
    // The pose and asset buffers these read are fields on ClientSession.cs, and
    // HandleMessage is what fills them.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        /// <summary>
        /// Takes the host's authoritative state: drop the stale local plan, then
        /// hard-set.
        /// </summary>
        /// <remarks>
        /// The clear comes first and is not optional. A client's planned orders
        /// are real <c>ActionEntity</c>s that never execute, so left alone they
        /// pile up and <c>CaptureLocalOrders</c> starts re-submitting orders the
        /// host already ran. The disposal cascade does not bite here because
        /// applying a snapshot writes components, not actions.
        /// </remarks>
        private void HandleSnapshot(SnapshotMessage snapshot, List<PbjEffect> effects)
        {
            effects.Add(new ClearLocalOrdersEffect());
            effects.Add(new ApplySnapshotEffect(snapshot.Turn, snapshot.Units, snapshot.Digest));
        }

        /// <summary>
        /// Starts presenting how the units reached the state we were just
        /// corrected to.
        /// </summary>
        /// <remarks>
        /// No session state changes and nothing is deferred. The snapshot that
        /// arrived immediately before this has already hard-set the authoritative
        /// state and verified its digest; playback only animates the view towards
        /// the same place, because the last key of every track is captured from
        /// the same read the snapshot is. That ordering is what lets keyframes be
        /// added without touching the correction path at all.
        /// </remarks>
        private void HandleKeyframes(KeyframesMessage keyframes, List<PbjEffect> effects)
        {
            var keyCount = 0;
            for (var i = 0; i < keyframes.Tracks.Count; i++)
            {
                keyCount += keyframes.Tracks[i].Transforms.Count;
            }

            effects.Add(new LogEffect(NetLog.KeyframesReceived(
                keyframes.Turn, keyframes.Tracks.Count, keyCount,
                keyframes.WindowStart, keyframes.WindowEnd)));

            // The terminator. Whatever poses are held now are all there will
            // ever be for this turn, so the decision can be made here and needs
            // no deadline: an ordered stream and a single send site mean an
            // incomplete set is a host that never finished sending one.
            var held = poses.PartsHeld;
            var expected = poses.PartsExpected;
            var posed = poses.Take(keyframes.Turn);
            effects.Add(new LogEffect(posed.Count > 0
                ? NetLog.PosesReceived(keyframes.Turn, posed.Count)
                : NetLog.PosesIncomplete(keyframes.Turn, held, expected)));

            // The same terminator serves M14's effects. Taking them here rather
            // than on their own schedule is what lets one ordered stream carry
            // two independent sets without either needing a deadline.
            var assetsHeld = assets.PartsHeld;
            var assetsExpected = assets.PartsExpected;
            var effectsCaptured = assets.Take(keyframes.Turn);
            var assetTracks = effectsCaptured.Standalone.Count
                + effectsCaptured.Projectiles.Count
                + effectsCaptured.Beams.Count;
            // Three outcomes rather than two. A turn with no effects is usually
            // an ordinary quiet turn, so calling it incomplete would cry wolf on
            // most of a fight — the difference from the pose pair above, where
            // no poses always does mean the units will slide.
            string assetLine;
            if (assetTracks > 0)
            {
                assetLine = NetLog.AssetsReceived(keyframes.Turn, assetTracks);
            }
            else if (assetsExpected == 0)
            {
                assetLine = NetLog.AssetsNoneSent(keyframes.Turn);
            }
            else
            {
                assetLine = NetLog.AssetsIncomplete(keyframes.Turn, assetsHeld, assetsExpected);
            }
            effects.Add(new LogEffect(assetLine));

            effects.Add(new PlayKeyframesEffect(keyframes.Turn, new KeyframeCapture(
                keyframes.WindowStart, keyframes.WindowEnd, keyframes.Tracks, posed,
                effectsCaptured)));
        }

        private void HandleSnapshotApplied(SnapshotAppliedEvent applied, List<PbjEffect> effects)
        {
            effects.Add(string.Equals(applied.ExpectedDigest, applied.ActualDigest, StringComparison.Ordinal)
                ? new LogEffect(NetLog.SnapshotVerified(applied.Turn, applied.UnitCount, applied.ActualDigest))
                : new LogEffect(NetLog.SnapshotStillDiverged(
                    applied.Turn, applied.ExpectedDigest, applied.ActualDigest)));
        }
    }
}
