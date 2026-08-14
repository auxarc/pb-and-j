using System;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M11d: starts a campaign load and reports how it went.
    /// </summary>
    /// <remarks>
    /// The game tells you almost nothing about a load that goes wrong.
    /// <c>TryLoading</c> returns <c>void</c>; its completion callback fires
    /// <b>only</b> on success (<c>DataHelperLoading:384</c>); it bails silently
    /// when the game is already loading (<c>:226-230</c>); and a load of a save
    /// that is not there fails at <c>LoadingStart:267</c> without ever reaching a
    /// callback. Every one of those looks identical from here: nothing happens.
    /// <para>
    /// So this checks what it can <em>before</em> calling, and turns each silent
    /// failure into an immediate answer. What is left over — a load that starts
    /// and never finishes — is the host's timeout to notice, and it is the only
    /// case that should cost anyone two minutes.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class LoadGlue
    {
        /// <summary>
        /// What a load is for. The steps differ in two places, and both
        /// differences are load-bearing rather than tidy-ups.
        /// </summary>
        private enum Purpose
        {
            /// <summary>The lobby's campaign save. M11d.</summary>
            Campaign,

            /// <summary>The host's loaded fight, from the scenario slot. M12b.</summary>
            Combat,
        }

        /// <summary>
        /// Starts the load. Null if it began, or the outcome if it could not.
        /// </summary>
        internal static LoadOutcome? Begin(string? saveKey, int selectionVersion, string? expectedDigest) =>
            Start(saveKey, selectionVersion, expectedDigest, Purpose.Campaign);

        /// <summary>
        /// Loads the fight the host just shipped us. M12b.
        /// </summary>
        /// <remarks>
        /// A separate entry point rather than a flag on <see cref="Begin"/>,
        /// because the catalogue check below <em>must</em> be skipped here and
        /// widening the catalogue instead would be the wrong fix:
        /// <c>LobbyCatalogue.IsOffered</c> excludes the scenario slot on purpose,
        /// and that exclusion is what stops a save being rewritten every mission
        /// from also being offered as a campaign somebody could pick — the M11b
        /// trap, which cost a build once already.
        /// <para>
        /// Everything else is kept, and the digest check especially: same name
        /// with different contents is the whole failure this guards, and it
        /// matters more here than for a campaign, since the slot is rewritten on
        /// every mission start and a client may be holding the previous fight.
        /// </para>
        /// </remarks>
        internal static LoadOutcome? BeginCombat(string? saveName, string? expectedDigest) =>
            Start(saveName, CombatVersion, expectedDigest, Purpose.Combat);

        /// <summary>
        /// The version reported for a combat load. The entry barrier is keyed by
        /// the fight rather than by a lobby selection, so there is no selection
        /// version to quote back and inventing one would imply an agreement that
        /// was never made.
        /// </summary>
        private const int CombatVersion = -1;

        private static LoadOutcome? Start(
            string? saveKey, int selectionVersion, string? expectedDigest, Purpose purpose)
        {
            if (string.IsNullOrEmpty(saveKey))
            {
                Debug.LogWarning("[pb-and-j] asked to load nothing");
                return LoadOutcome.Unavailable;
            }

            try
            {
                // 1. Have we got it? A load of a save that is not there never
                //    reaches a callback, so without this check "I do not have it"
                //    is indistinguishable from "I died" — and Unavailable, the
                //    outcome M11e exists to act on, would be unreachable.
                //
                //    The catalogue is the campaign catalogue, and it deliberately
                //    does not contain the scenario slot, so a combat load asks
                //    the question a different way: the digest read below fails
                //    just as loudly when the directory is not there.
                if (purpose == Purpose.Campaign
                    && !LobbyCatalogue.Contains(SaveCatalogueGlue.List(), saveKey))
                {
                    Debug.LogWarning("[pb-and-j] no multiplayer save named '" + saveKey + "' here");
                    return LoadOutcome.Unavailable;
                }

                // 2. Is it the same one? Same name, different contents is a
                //    campaign that would silently diverge from everyone else's.
                //    This used to compute the digest and then never compare it —
                //    the comment claimed a check the code did not make. The
                //    comparison is the whole point: without it "have I got it"
                //    means only "is there a directory with that name".
                var digest = SaveCatalogueGlue.Digest(saveKey);
                if (digest == null)
                {
                    Debug.LogWarning("[pb-and-j] could not read '" + saveKey + "' to check it");
                    return LoadOutcome.Unavailable;
                }
                if (!string.IsNullOrEmpty(expectedDigest)
                    && !string.Equals(digest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                {
                    // Unavailable rather than Refused: as far as the lobby is
                    // concerned we do not have the save it agreed on. M11e's
                    // transfer is what fixes it, and it keys off exactly this.
                    Debug.LogWarning("[pb-and-j] '" + saveKey + "' is " + digest
                        + " here but the lobby agreed on " + expectedDigest);
                    return LoadOutcome.Unavailable;
                }

                // 3. Will the game even start? TryLoading bails on these two with
                //    a warning and registers no callback, so it would go quiet.
                var game = Contexts.sharedInstance.game;
                if (game.isLoadingInProgress || game.isTeardownOfCampaignRequested)
                {
                    Debug.LogWarning("[pb-and-j] the game is already loading something");
                    return LoadOutcome.Refused;
                }

                pending = saveKey;
                pendingVersion = selectionVersion;
                pendingPurpose = purpose;
                DataHelperLoading.TryLoading(saveKey, SaveLocation.Normal, OnLoaded);
                return null;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not start the load: " + e.GetType().Name + ": " + e.Message);
                return LoadOutcome.Refused;
            }
        }

        /// <summary>
        /// Which save the in-flight load is for, so a callback can be matched to
        /// it. Null between loads.
        /// </summary>
        private static string? pending;
        private static int pendingVersion = -1;
        private static Purpose pendingPurpose = Purpose.Campaign;

        /// <summary>
        /// The game says we are in.
        /// </summary>
        /// <remarks>
        /// <c>keepScreenAfterLoading</c> is left false above, so the game dismisses
        /// its own loading screen. Passing true would mean dismissing it by hand,
        /// as <c>QuickLoad</c> does after <c>Co.DelayFrames(10)</c>, and there is
        /// nothing to gain by taking that on.
        /// </remarks>
        private static void OnLoaded()
        {
            var key = pending;
            var version = pendingVersion;
            var purpose = pendingPurpose;
            pending = null;
            pendingVersion = -1;
            pendingPurpose = Purpose.Campaign;
            if (key == null)
            {
                // A callback for a load we did not start. It cannot normally
                // happen — the game refuses a second TryLoading while one is in
                // flight — but the field is a single static and silence is the
                // failure mode we can least afford to invent.
                return;
            }

            Debug.Log("[pb-and-j] loaded '" + key + "'");

            if (purpose == Purpose.Combat)
            {
                // Deliberately NOT MultiplayerCampaign.Enter: the scenario slot
                // is a fight, not the campaign.
                //
                // ⚠️ This abstention is not the whole defence, and an earlier
                // version of this comment implied it was.
                // SaveNamespacePatches.CampaignBitFromLoad postfixes LoadingEnd2
                // and calls Enter(DataManagerSave.saveName) regardless — which on
                // this path IS the slot. What makes that survivable is that the
                // redirect reads MultiplayerCampaign.Active and never SaveKey, so
                // the recorded name is carried by two log lines and nothing else.
                // Anything that ever reads SaveKey for a decision must reckon
                // with this first.
                NetGlue.PostCombatLoadFinished(LoadOutcome.Loaded);
                return;
            }

            MultiplayerCampaign.Enter(key);
            NetGlue.PostLoadFinished(version, LoadOutcome.Loaded);
        }
    }
}
