using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Borrows the game's own load screen as the lobby's save picker.
    /// </summary>
    /// <remarks>
    /// Reusing <c>CIViewPauseLoad</c> gets its grid, its screenshots, its sorting
    /// and its gamepad navigation for free — none of which we could build from the
    /// widgets in <c>docs/notes/ngui-surface.md</c>, which documents labels, inputs
    /// and buttons and no list of any kind.
    /// <para>
    /// The cost is that the screen is hard-wired to load. There is <b>no picker
    /// mode</b> and no "return the key" API, so four small patches turn one into
    /// the other, and a fifth undoes the fact that <b>this UI has no view stack</b>
    /// — nothing will restore the lobby when the load screen closes.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class LobbyPicker
    {
        private static string? selected;

        internal static void Open()
        {
            var view = CIViewPauseLoad.ins;
            var root = CIViewPauseRoot.ins;
            if (view == null || root == null || !root.IsEntered())
            {
                Debug.LogWarning("[pb-and-j] the load screen is not reachable from here");
                return;
            }

            // TryEntry no-ops when the view is ALREADY entered, and a no-op does
            // not rebuild the grid — so the "picker" would present whatever list
            // was built last time, singleplayer saves included, and Confirm would
            // hand one of those keys to the lobby as its campaign.
            if (view.IsEntered())
            {
                Debug.LogWarning("[pb-and-j] the load screen is already open — close it first");
                return;
            }

            selected = null;

            // Set BEFORE TryEntry, because TryEntry rebuilds the grid on the way
            // in and that rebuild is what the filter inverts.
            SaveVisibilityPatches.EnterPickMode(LobbyScreenGlue.IsPickingPlausible);

            // Hide, NOT Close. Close ends the lobby flow and clears pick mode,
            // which would switch the filter off one line after switching it on —
            // the picker would then open on the singleplayer catalogue with a live
            // Confirm behind it.
            LobbyScreenGlue.Hide();

            view.TryEntry();

            // TryEntry declines in silence if the root is not entered or the view
            // already is. Without this rollback the player is left at the title
            // with the lobby hidden, the picker closed, and pick mode stuck on —
            // and a stuck pick mode turns Title -> Load into a list of nothing but
            // multiplayer saves.
            if (!view.IsEntered())
            {
                Debug.LogWarning("[pb-and-j] the load screen refused to open — staying in the lobby");
                SaveVisibilityPatches.ExitPickMode();
                LobbyScreenGlue.Reopen();
            }
        }

        /// <summary>
        /// Takes the chosen save instead of loading it.
        /// </summary>
        /// <remarks>
        /// A Prefix rather than swapping <c>sidebarHelper.buttonConfirm.callbackOnClick</c>,
        /// which is assigned once in the view's <c>Awake</c> and would work: a
        /// prefix leaves no field to restore and so cannot strand a delegate
        /// pointing at us after the lobby is gone. It also skips the "load this
        /// save?" confirmation dialog, whose wording would be wrong for picking.
        /// <para>
        /// One prefix covers mouse and gamepad both, because each arrives through
        /// <c>callbackOnClick</c> and therefore through this method.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseLoad), nameof(CIViewPauseLoad.OnConfirmButton))]
        internal static class Confirm
        {
            private static bool Prefix(CIViewPauseLoad __instance)
            {
                if (!SaveVisibilityPatches.PickMode)
                {
                    return true;
                }

                if (string.IsNullOrEmpty(selected))
                {
                    Debug.Log("[pb-and-j] no save selected yet");
                    return false;
                }

                var key = selected!;
                __instance.TryExit();
                LobbyScreenGlue.OnSavePicked(key);
                return false;
            }
        }

        /// <summary>
        /// Records the highlighted save. <c>null</c> means deselected.
        /// </summary>
        /// <remarks>
        /// A postfix on the public <c>OnSaveSelected</c> rather than reflecting the
        /// private <c>saveNameSelectedLast</c>: every selection route — mouse
        /// click, gamepad node select, gamepad forward — funnels through it.
        /// <para>
        /// The null case is not defensive. <c>OnGridChange</c> calls
        /// <c>OnSaveSelected(null)</c> on a page flip, so treating null as a chosen
        /// save would mean paging the grid selects nothing-in-particular.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseLoad), nameof(CIViewPauseLoad.OnSaveSelected))]
        internal static class Selected
        {
            private static void Postfix(string saveName)
            {
                if (SaveVisibilityPatches.PickMode)
                {
                    selected = string.IsNullOrEmpty(saveName) ? null : saveName;
                }
            }
        }

        /// <summary>
        /// Disarms Delete while picking.
        /// </summary>
        /// <remarks>
        /// The load screen's delete works on whatever is highlighted, behind
        /// singleplayer dialog wording. In pick mode that is a multiplayer save,
        /// possibly the one the lobby has selected and every peer has readied on.
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseLoad), nameof(CIViewPauseLoad.OnDeleteButton))]
        internal static class Delete
        {
            private static bool Prefix()
            {
                if (!SaveVisibilityPatches.PickMode)
                {
                    return true;
                }
                Debug.Log("[pb-and-j] not deleting saves from the lobby's picker");
                return false;
            }
        }

        /// <summary>
        /// Puts the lobby back when the load screen closes.
        /// </summary>
        /// <remarks>
        /// <b>A Postfix alone cannot do this, and that is the whole reason for the
        /// Prefix.</b> <c>CIView.TryExit</c> sets <c>entered = false</c> before
        /// returning, so by the time any Postfix runs the flag reads false whether
        /// the call really exited the view or was one of the no-op calls that
        /// arrive constantly from <c>TryExitSubordinates</c>, Continue and
        /// exit-to-menu. Gate on the post-call value and the lobby is never
        /// restored <em>and pick mode never clears</em>; do not gate at all and the
        /// lobby is thrown back up over the title screen.
        /// <para>
        /// So the Prefix snapshots the pre-call value into <c>__state</c> and the
        /// Postfix acts only on a real exit. The attribute names the one-argument
        /// overload explicitly: the class exposes both it and the inherited no-arg
        /// <c>TryExit</c>, and an unqualified patch throws
        /// <c>AmbiguousMatchException</c> at startup.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseLoad), nameof(CIViewPauseLoad.TryExit), new[] { typeof(Action) })]
        internal static class Restore
        {
            private static void Prefix(bool ___entered, out bool __state)
            {
                __state = ___entered;
            }

            private static void Postfix(bool __state)
            {
                if (!__state || !SaveVisibilityPatches.PickMode)
                {
                    return;
                }

                SaveVisibilityPatches.ExitPickMode();
                LobbyScreenGlue.Reopen();
            }
        }
    }
}
