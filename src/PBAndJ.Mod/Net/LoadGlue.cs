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
        /// Starts the load. Null if it began, or the outcome if it could not.
        /// </summary>
        internal static LoadOutcome? Begin(string? saveKey, int selectionVersion, string? expectedDigest)
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
                var catalogue = SaveCatalogueGlue.List();
                if (!LobbyCatalogue.Contains(catalogue, saveKey))
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
            pending = null;
            pendingVersion = -1;
            if (key == null)
            {
                // A callback for a load we did not start. It cannot normally
                // happen — the game refuses a second TryLoading while one is in
                // flight — but the field is a single static and silence is the
                // failure mode we can least afford to invent.
                return;
            }

            Debug.Log("[pb-and-j] loaded '" + key + "'");
            MultiplayerCampaign.Enter(key);
            NetGlue.PostLoadFinished(version, LoadOutcome.Loaded);
        }
    }
}
