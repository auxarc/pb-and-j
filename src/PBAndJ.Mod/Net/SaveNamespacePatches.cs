using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// M11d: keeps a loaded co-op campaign inside the <c>pbj_</c> namespace when
    /// the game writes it back out.
    /// </summary>
    /// <remarks>
    /// M11b's <see cref="SaveVisibilityPatches"/> is the read side and says so in
    /// its own remarks: it hides multiplayer saves from the singleplayer load path
    /// and stops that path overwriting them. This is the write side, and it exists
    /// because M11d is the first milestone that actually loads one of these saves.
    /// From that moment the game writes the campaign back out on its own schedule —
    /// <c>autosave_timed_N</c> from the overworld timer, <c>autosave_game_exit</c>
    /// on quit, the player's typed name from the save screen — and every one of
    /// those would land outside the namespace the lobby selects from.
    /// <para>
    /// <b>This is the one part of M11d that can cost a player data</b>, which is
    /// why the policy lives in <see cref="LobbySaveWrites"/> under the coverage
    /// gate and these patches only supply the two facts it needs: whether a co-op
    /// campaign is loaded, and which save location is being written.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class SaveNamespacePatches
    {
        /// <summary>
        /// Routes every campaign save through the namespace while a co-op campaign
        /// is loaded.
        /// </summary>
        /// <remarks>
        /// One patch covers every save the game makes, because
        /// <c>DataManagerSave.saveName</c> is assigned from this parameter and
        /// <c>GetSavePath</c> is <c>saveFolderPath + saveName + "/"</c> — the
        /// directory name <em>is</em> the argument. The preview screenshot is
        /// captured under the same value, so it stays matched to the save.
        /// <para>
        /// Nine of the game's ten <c>DoSave</c> call sites pass
        /// <c>SaveLocation.Normal</c>; the tenth is the crash reporter's copy under
        /// <c>SaveLocation.Reporter</c>, which must be left alone. That guard also
        /// excludes the dev-only <c>InternalEditable</c> path the save screen takes
        /// when internal saves are in use.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataManagerSave), nameof(DataManagerSave.DoSave))]
        internal static class Naming
        {
            private static void Prefix(ref string saveName, SaveLocation saveLocation)
            {
                var redirected = LobbySaveWrites.NameForWrite(
                    saveName, MultiplayerCampaign.Active, saveLocation == SaveLocation.Normal);
                if (string.Equals(redirected, saveName, StringComparison.Ordinal))
                {
                    return;
                }

                Debug.Log("[pb-and-j] co-op campaign: writing save as '" + redirected
                    + "' rather than '" + saveName + "'");
                // Bang because the game assembly is not nullable-annotated: a null
                // in is returned as a null out, and DoSave rejects that itself.
                saveName = redirected!;
            }
        }

        /// <summary>
        /// Routes a load the <em>game</em> asked for into the namespace, before the
        /// campaign bit that decides it can be cleared.
        /// </summary>
        /// <remarks>
        /// <b>The ordering is the whole reason this patch exists separately.</b>
        /// <c>OnLoadingExternal</c> raises <c>CampaignExit</c> and only then calls
        /// <c>TryLoading</c> (<c>DataHelperLoading.cs:214-218</c>). Our own
        /// <see cref="CampaignExit"/> postfix clears the campaign bit on that event,
        /// so by the time <c>TryLoading</c> runs
        /// <see cref="MultiplayerCampaign.Active"/> is already false and a redirect
        /// hooked there would decline to fire. A prefix here runs ahead of the whole
        /// sequence, while the bit still says what it meant.
        /// <para>
        /// The defect this fixes: combat retry calls
        /// <c>OnLoadingExternal("autosave_before_combat")</c>
        /// (<c>CIViewCombatEnd.cs:333</c>) while the co-op campaign stored its copy
        /// as <c>pbj_autosave_before_combat</c>, so the retry loaded the player's
        /// singleplayer autosave — or nothing, leaving them inside a co-op campaign
        /// with the bit cleared, writing unprefixed saves from then on.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataHelperLoading), nameof(DataHelperLoading.OnLoadingExternal))]
        internal static class ExternalLoadNaming
        {
            private static void Prefix(ref string saveName, SaveLocation location)
            {
                Redirect(ref saveName, location, "external load");

                // Recorded for CampaignExit, which fires from inside this method
                // and cannot otherwise tell a load from a genuine exit. Set after
                // the redirect, so the key is the one really being loaded.
                loadingExternallyInto = saveName;
                loadingExternally = true;
            }

            private static void Postfix()
            {
                // Cleared unconditionally. OnLoadingExternal raises CampaignExit
                // only outside the main menu, so a load started from the menu
                // leaves the flag set and the next genuine exit would read it.
                loadingExternally = false;
                loadingExternallyInto = null;
            }
        }

        /// <summary>
        /// Whether we are inside <c>OnLoadingExternal</c>, and into what.
        /// </summary>
        /// <remarks>
        /// Static rather than threaded through because the two sites are a Harmony
        /// prefix and a different patch's postfix, with the game's own event
        /// dispatch in between. Single-threaded by construction: Unity raises all
        /// of this on the main thread.
        /// </remarks>
        private static bool loadingExternally;

        private static string? loadingExternallyInto;

        /// <summary>
        /// The same redirect for loads that never go through
        /// <c>OnLoadingExternal</c>.
        /// </summary>
        /// <remarks>
        /// <c>TryLoading</c> is the single funnel every load reaches, but most of its
        /// callers arrive with a key already chosen — <c>QuickLoad</c>
        /// (<c>DataManagerSave.cs:3518</c>), the game's own <c>load</c> console
        /// command (<c>ConsoleCommandsShared.cs:74</c>) and M11d's
        /// <see cref="LoadGlue"/>. Quickload in a co-op campaign wants
        /// <c>pbj_autosave_quicksave</c> exactly as combat retry does.
        /// <para>
        /// Safe to sit alongside <see cref="ExternalLoadNaming"/>: that path has
        /// already rewritten the key, and <c>NameForRead</c> leaves an
        /// already-prefixed name alone. M11d's own load passes a <c>pbj_</c> key in
        /// for the same reason.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataHelperLoading), nameof(DataHelperLoading.TryLoading))]
        internal static class DirectLoadNaming
        {
            private static void Prefix(ref string key, SaveLocation saveLocation)
            {
                Redirect(ref key, saveLocation, "load");
            }
        }

        private static void Redirect(ref string saveKey, SaveLocation location, string what)
        {
            var redirected = LobbySaveWrites.NameForRead(
                saveKey, MultiplayerCampaign.Active, location == SaveLocation.Normal);
            if (string.Equals(redirected, saveKey, StringComparison.Ordinal))
            {
                return;
            }

            Debug.Log("[pb-and-j] co-op campaign: " + what + " redirected to '" + redirected
                + "' rather than '" + saveKey + "'");
            saveKey = redirected!;
        }

        /// <summary>
        /// Re-establishes the campaign bit from whatever actually finished loading.
        /// </summary>
        /// <remarks>
        /// Without this the redirect above fixes only half the defect. Combat retry
        /// raises <c>CampaignExit</c>, which clears the bit, and then loads through
        /// the game's own path — which never calls
        /// <see cref="MultiplayerCampaign.Enter"/>, because that only ever ran from
        /// M11d's <see cref="LoadGlue"/> callback. The player would land back in the
        /// co-op campaign with the bit cleared, writing unprefixed saves from then
        /// on: the "campaign looks deleted" failure, reached by a different road.
        /// <para>
        /// <c>LoadingEnd2</c> is the success-only point — every failure path returns
        /// before the progress callback that reaches it
        /// (<c>DataHelperLoading.cs:353</c>) — so setting the bit here honours M11d's
        /// invariant that it is set from a load's success and never before one.
        /// <b>Deriving the bit from <c>DataManagerSave.saveName</c> directly would
        /// not:</b> <c>LoadingStart</c> assigns that at <c>:266</c>, ahead of the
        /// existence check at <c>:269</c>, so a failed load leaves it naming a save
        /// that was never entered.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataHelperLoading), "LoadingEnd2")]
        internal static class CampaignBitFromLoad
        {
            private static void Postfix()
            {
                var loaded = DataManagerSave.saveName;
                if (LobbySaveNames.IsMultiplayerKey(loaded))
                {
                    MultiplayerCampaign.Enter(loaded);
                }
                else
                {
                    MultiplayerCampaign.Leave();
                }
            }
        }

        /// <summary>
        /// Clears the campaign bit when the player leaves the campaign.
        /// </summary>
        /// <remarks>
        /// <c>GameEventType.CampaignExit</c> fires at <b>four</b> sites: exit-to-menu
        /// (<c>CIViewPauseRoot.cs:1429</c>), exit-to-desktop (<c>:1457</c>,
        /// <em>after</em> its autosave, so that save is still caught),
        /// <c>OnApplicationQuit</c> outside the main menu
        /// (<c>Heartbeat.cs:58</c>), and <c>OnLoadingExternal</c>
        /// (<c>DataHelperLoading.cs:216</c>). A crash needs nothing — the bit is in
        /// memory and a fresh process starts clean.
        /// <para>
        /// ⚠️ <b>The fourth is not always an exit.</b> <c>OnLoadingExternal</c> also
        /// carries combat retry (<c>CIViewCombatEnd.cs:333</c>) and load-latest
        /// (<c>CIViewPauseRoot.cs:1369</c>, <c>:1384</c>), which stay in the same
        /// campaign. Clearing the bit is still right for those — the load that
        /// follows re-establishes it through
        /// <see cref="MultiplayerCampaign.Enter"/> on success — but anything heavier
        /// hung on this event must discriminate first. An earlier version of this
        /// comment claimed three sites and called all of them genuine exits.
        /// </para>
        /// <para>
        /// A postfix rather than a prefix, so the bit is still set while the game's
        /// own subscribers run: the campaign is a co-op one right up until the
        /// event finishes being dispatched.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(GameEventUtility), nameof(GameEventUtility.OnEvent))]
        internal static class CampaignExit
        {
            private static void Postfix(GameEventType type)
            {
                if (type != GameEventType.CampaignExit)
                {
                    return;
                }

                MultiplayerCampaign.Leave();

                if (!LeavingTheSessionBehind())
                {
                    return;
                }

                if (NetGlue.HasSession)
                {
                    Debug.Log("[pb-and-j] leaving the campaign — stopping the session");
                    NetGlue.NetStop();
                }
            }

            /// <summary>
            /// Whether this <c>CampaignExit</c> really ends our participation.
            /// </summary>
            /// <remarks>
            /// ⚠️ <b>Three of the four sites are genuine exits and the fourth is
            /// not.</b> <c>OnLoadingExternal</c> raises this event for combat retry
            /// (<c>CIViewCombatEnd.cs:333</c>) and load-latest
            /// (<c>CIViewPauseRoot.cs:1369</c>, <c>:1384</c>), neither of which
            /// leaves the campaign — tearing the session down there would drop
            /// everyone else because one player lost a fight and pressed Retry.
            /// <para>
            /// A "load in flight" flag would not work: M11d's own synchronised load
            /// calls <c>TryLoading</c> directly (<c>LoadGlue.cs</c>) and never
            /// raises <c>CampaignExit</c> at all, so the window such a flag guards
            /// does not exist, while the sites that do fire it have no load of ours
            /// in flight. The load <em>target</em> is what actually separates them:
            /// staying inside the <c>pbj_</c> namespace is staying in the co-op
            /// campaign.
            /// </para>
            /// </remarks>
            private static bool LeavingTheSessionBehind()
            {
                return !loadingExternally || !LobbySaveNames.IsMultiplayerKey(loadingExternallyInto);
            }
        }

        /// <summary>
        /// Makes the save screen's duplicate check see where a name will really be
        /// written.
        /// </summary>
        /// <remarks>
        /// Without this the redirect opens a hole it did not have before. The
        /// screen checks the typed name against the header keys, so in a co-op
        /// campaign typing <c>foo</c> finds nothing — the headers hold
        /// <c>pbj_foo</c> — and the save proceeds with <b>no overwrite
        /// confirmation</b> straight into <c>pbj_foo</c>. That is a silent
        /// overwrite of a save that may belong to an entirely different lobby.
        /// <para>
        /// This one private method is the whole duplicate check: it backs the
        /// confirm button's label, the overwrite warning, the save-limit exemption
        /// for an overwrite, and the confirmation dialog itself. Correcting it here
        /// corrects all four.
        /// </para>
        /// <para>
        /// The location argument mirrors <c>ConfirmSaving</c>'s own choice, so the
        /// check and the write cannot disagree about which name is at stake.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseSave), "DoesSaveAlreadyExist")]
        internal static class Duplicates
        {
            private static void Postfix(
                string saveName,
                Dictionary<string, DataContainerSavedMetadata> saveHeaders,
                ref bool __result)
            {
                if (__result || saveHeaders == null || !MultiplayerCampaign.Active)
                {
                    return;
                }

                var redirected = LobbySaveWrites.NameForWrite(
                    saveName, true, !DataManagerSave.AreInternalSavesUsed());
                if (redirected == null || string.Equals(redirected, saveName, StringComparison.Ordinal))
                {
                    return;
                }

                foreach (var pair in saveHeaders)
                {
                    if (string.Equals(pair.Key, redirected, StringComparison.OrdinalIgnoreCase))
                    {
                        __result = true;
                        return;
                    }
                }
            }
        }
    }
}
