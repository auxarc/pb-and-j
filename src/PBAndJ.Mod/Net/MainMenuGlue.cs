using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core;
using PBAndJ.Core.Net;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Puts a Multiplayer entry on the title menu.
    //
    // The title menu is CIViewPauseRoot in mainMode — NOT CIViewMainMenu, which
    // is a 50-line class referenced nowhere in the entire game assembly and
    // renders nothing. An earlier reading of this had us cloning buttons into
    // CIViewMainMenu.holderMain, which would have shipped a button that appears
    // nowhere and clicks never; two greps refuted it. See docs/notes/ngui-surface.md.
    //
    // CIViewPauseRoot.Start builds the menu in code from a serialized list of
    // ButtonLink, and everything a menu entry needs is driven from that list:
    // creation from a prefab, naming, click wiring, vertical layout, the intro
    // animation, and localization. So the mod adds a link and lets the game do
    // the rest, rather than cloning a GameObject and re-deriving all of it.
    [ExcludeFromCodeCoverage]
    internal static class MainMenuGlue
    {
        /// <summary>Our entry's key, which is also the GameObject's name.</summary>
        internal const string ButtonKey = "pbj_multiplayer";

        internal static void OnMenuStarting(CIViewPauseRoot view)
        {
            try
            {
                if (view == null || view.buttonLinks == null)
                {
                    return;
                }

                // Idempotent by key. Start runs once per process today — exit to
                // menu is a state change, not a scene load — but the cost of
                // being wrong about that is a second button every time somebody
                // leaves a campaign, and the check is one comparison.
                foreach (var existing in view.buttonLinks)
                {
                    if (existing != null && existing.key == ButtonKey)
                    {
                        return;
                    }
                }

                var link = new CIViewPauseRoot.ButtonLink
                {
                    key = ButtonKey,
                    displayInMain = true,

                    // Not in the in-game pause menu. A session is set up before
                    // a scenario is loaded, and offering it mid-combat would
                    // suggest you can join from there, which you cannot.
                    displayInPause = false,
                    labelBoldMain = true,
                };

                // textKey and icon are borrowed from an entry the game already
                // ships rather than invented. An unknown text key would make
                // DataManagerText log a "Text not found" warning on every
                // localization refresh — a mod has no business putting warnings
                // in the game's log — and an unknown sprite name would render a
                // missing icon. The label itself is overwritten below, so the
                // borrowed text is never seen.
                var donor = FindDonor(view);
                if (donor != null)
                {
                    link.textKey = donor.textKey;
                    link.icon = donor.icon;
                }

                // Placed directly after Load rather than appended. The menu is
                // laid out by walking this list in order, so list position IS
                // screen position — and the bottom of the list is where Quit
                // lives, which is not where a thing you might actually pick
                // belongs.
                view.buttonLinks.Insert(IndexAfter(view, "load"), link);
                Debug.Log(LoadBanner.PatchFired("CIViewPauseRoot.Start"));
            }
            catch (Exception e)
            {
                // A mod must not be able to take the main menu down.
                Debug.LogWarning("[pb-and-j] could not add the multiplayer button: " + e);
            }
        }

        internal static void OnMenuStarted(CIViewPauseRoot view)
        {
            try
            {
                var link = view?.GetMenuButtonLink(ButtonKey);
                if (link == null)
                {
                    return;
                }

                // OnSpecialButton calls this when the entry is clicked; the game
                // has already wired the button itself to that dispatcher.
                link.action = OpenConnectScreen;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not wire the multiplayer button: " + e);
            }
        }

        /// <summary>
        /// Sets our label after the game has set everyone else's.
        /// </summary>
        /// <remarks>
        /// CIViewPauseRoot.RefreshLocalization writes every button's label from
        /// the localization table and re-runs on a language change, so this is
        /// the only place a label of ours survives. It also wraps in [b][/b] and
        /// fills the tooltip, which is matched here so our entry does not look
        /// subtly different from its neighbours.
        /// </remarks>
        internal static void OnLocalizationRefreshed(CIViewPauseRoot view)
        {
            try
            {
                var link = view?.GetMenuButtonLink(ButtonKey);
                if (link?.btMain == null)
                {
                    return;
                }

                var text = ConnectText.Title();

                if (link.btMain.label != null)
                {
                    link.btMain.label.text = "[b]" + text + "[/b]";
                }
                if (link.btMain.labelSecondary != null)
                {
                    link.btMain.labelSecondary.text = "[b]" + text + "[/b]";
                }
                if (link.btMain.button != null)
                {
                    link.btMain.button.tooltipHeader = text;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not label the multiplayer button: " + e);
            }
        }

        /// <summary>
        /// The slot just after the entry with <paramref name="key"/>, or the end
        /// of the list if the game no longer has one by that name.
        /// </summary>
        private static int IndexAfter(CIViewPauseRoot view, string key)
        {
            for (var i = 0; i < view.buttonLinks.Count; i++)
            {
                var link = view.buttonLinks[i];
                if (link != null && link.key == key)
                {
                    return i + 1;
                }
            }

            // Appending is a worse position, not a broken one, so a renamed menu
            // entry in some future patch costs us placement and nothing else.
            return view.buttonLinks.Count;
        }

        private static CIViewPauseRoot.ButtonLink? FindDonor(CIViewPauseRoot view)
        {
            foreach (var link in view.buttonLinks)
            {
                if (link != null && link.displayInMain && !string.IsNullOrEmpty(link.textKey))
                {
                    return link;
                }
            }

            return null;
        }

        private static void OpenConnectScreen()
        {
            // The mirror of Patch_CIViewPauseRoot_OnSubviewChange, and just as
            // necessary: the game's pages close each other through
            // TryExitSubordinates, which our screen cannot join, so without this
            // opening Multiplayer over an open Load screen stacks the two.
            // Toggling ours closed is a no-op here — opening it already cleared
            // them.
            try
            {
                CIViewPauseRoot.ins?.TryExitSubordinates();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not close the game's menu pages: " + e.GetType().Name);
            }

            ConnectScreenGlue.Open();
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CIViewPauseRoot), "Start")]
    internal static class Patch_CIViewPauseRoot_Start
    {
        // Prefix: the link has to be in the list before Start walks it, because
        // that walk is what creates, names and positions the button.
        private static void Prefix(CIViewPauseRoot __instance)
        {
            MainMenuGlue.OnMenuStarting(__instance);
        }

        // Postfix: the link's btMain does not exist until the walk has run, and
        // the game's own TryAddActionToButtonKey calls happen in between.
        private static void Postfix(CIViewPauseRoot __instance)
        {
            MainMenuGlue.OnMenuStarted(__instance);
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CIViewPauseRoot), nameof(CIViewPauseRoot.RefreshLocalization))]
    internal static class Patch_CIViewPauseRoot_RefreshLocalization
    {
        private static void Postfix(CIViewPauseRoot __instance)
        {
            MainMenuGlue.OnLocalizationRefreshed(__instance);
        }
    }

    /// <summary>
    /// Closes our screen when the player opens one of the game's own menu pages.
    /// </summary>
    /// <remarks>
    /// Found in-game 2026-08-04: choosing Load, Options, Mods or any other
    /// top-level entry left the multiplayer screen sitting on top of it.
    /// <para>
    /// The cause is that <b>there is no view stack</b>. `CIView.subordinates` is a
    /// flat list on `CIViewPauseRoot` holding the game's own six pages, and each
    /// page's `TryEntry` calls `TryExitSubordinates` to close its siblings — a
    /// mechanism our screen cannot join, because it is a cloned GameObject and not
    /// a `CIView`. So it was never asked to leave.
    /// </para>
    /// <para>
    /// `OnSubviewChange` is the right hook rather than each page's `TryEntry`:
    /// every one of the seven pages calls it, so this stays correct for a page we
    /// have never heard of.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CIViewPauseRoot), nameof(CIViewPauseRoot.OnSubviewChange))]
    internal static class Patch_CIViewPauseRoot_OnSubviewChange
    {
        private static void Postfix()
        {
            // Except when we are the one who opened it. M11c borrows the load
            // screen as its save picker, which routes through here too, and a
            // picker that closed the lobby behind it would be picking for nobody.
            if (SaveVisibilityPatches.PickMode)
            {
                return;
            }
            ConnectScreenGlue.Close();
            LobbyScreenGlue.Close();
        }
    }

    /// <summary>
    /// Closes our screen when anything starts loading a save.
    /// </summary>
    /// <remarks>
    /// The other half of the same finding, and the half that is easy to miss:
    /// Continue, Resume to Briefing and Skip Tutorial are top-level menu entries
    /// too, but they load directly and never open a page, so
    /// <see cref="Patch_CIViewPauseRoot_OnSubviewChange"/> never fires for them —
    /// the screen would hang over the loading screen instead.
    /// <para>
    /// Hooked at <c>TryLoading</c>, the single funnel every load and every
    /// new-game path goes through, so this covers routes that do not exist yet.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(DataHelperLoading), nameof(DataHelperLoading.TryLoading))]
    internal static class Patch_DataHelperLoading_TryLoading
    {
        private static void Postfix()
        {
            ConnectScreenGlue.Close();
            LobbyScreenGlue.Close();
        }
    }
}
