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
        /// Clears the campaign bit when the player leaves the campaign.
        /// </summary>
        /// <remarks>
        /// <c>GameEventType.CampaignExit</c> fires at exactly three sites and all
        /// three are genuine exits: exit-to-menu, exit-to-desktop (<em>after</em>
        /// its autosave, so that save is still caught), and
        /// <c>OnLoadingExternal</c>, which every route out of the in-campaign load
        /// screen goes through. A crash needs nothing — the bit is in memory and a
        /// fresh process starts clean.
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
                if (type == GameEventType.CampaignExit)
                {
                    MultiplayerCampaign.Leave();
                }
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
