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
    // Showing, retiring and returning effect instances, M14.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
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
    }
}
