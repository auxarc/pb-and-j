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
    /// M11b: keeps <c>pbj_</c> saves out of the singleplayer load path, and lets
    /// M11c's picker invert that to show only them.
    /// </summary>
    /// <remarks>
    /// The prefix decision (docs/design/campaign-coop.md, "Save storage") buys a
    /// completely unmodified load and save path at the cost of multiplayer saves
    /// appearing in the singleplayer browser. This is that cost, paid.
    /// <para>
    /// <b>What this does NOT do, deliberately.</b> The namespace is enforced on
    /// <em>reads</em> only. Once a <c>pbj_</c> campaign is loaded the game writes it
    /// back out under unprefixed autosave names — <c>autosave_timed_N</c> from the
    /// overworld timer, <c>autosave_game_exit</c> on quit — and Continue will then
    /// offer a co-op campaign as singleplayer through a path no patch here covers.
    /// That is M11d's, because M11d is the first milestone that loads one of these
    /// saves as part of the feature and so the first that has the state to fix it
    /// with.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class SaveVisibilityPatches
    {
        internal enum Filter
        {
            /// <summary>Show the truth. What every call outside a rebuild gets.</summary>
            Off = 0,

            /// <summary>The singleplayer load grid: multiplayer saves are not its business.</summary>
            HideMultiplayer = 1,

            /// <summary>M11c's picker: multiplayer saves are the only ones that are.</summary>
            OnlyMultiplayer = 2,
        }

        /// <summary>
        /// Whether the load screen is currently being borrowed as M11c's save
        /// picker. Persistent, because the grid is rebuilt several times while the
        /// picker is open.
        /// </summary>
        /// <remarks>
        /// <b>This is the most dangerous single bit in M11b.</b> It is
        /// session-global, and if it ever sticks on, Title → Load lists only
        /// multiplayer saves and a player concludes their campaigns are gone. It
        /// has three independent recoveries and needs all of them: the owner check
        /// in <see cref="Rebuilding.Prefix"/> below, a rollback when
        /// <c>TryEntry</c> declines, and the <c>TryExit</c> gate — the last two both
        /// in M11c, which is the only thing that ever sets this true.
        /// </remarks>
        internal static bool PickMode => pickMode;

        private static bool pickMode;
        private static Func<bool>? ownerAlive;

        /// <summary>
        /// Borrows the load screen as a save picker until
        /// <see cref="ExitPickMode"/>. Called by M11c; nothing in M11b turns this
        /// on, so the singleplayer behaviour is all M11b ships.
        /// </summary>
        /// <param name="isOwnerOnScreen">
        /// Whether the screen that borrowed it is <em>currently open</em> — not
        /// whether its object exists. The connect screen closes with
        /// <c>root.SetActive(false)</c> and keeps its instance, so an existence
        /// check written in that shape would make the recovery below permanently
        /// inert after the first open: a dead safety net that reads as a live one.
        /// </param>
        /// <remarks>
        /// The owner is taken as an argument rather than set separately because the
        /// two must never disagree — a pick mode with no owner is precisely the
        /// stuck state this design is built to survive.
        /// </remarks>
        internal static void EnterPickMode(Func<bool> isOwnerOnScreen)
        {
            pickMode = true;
            ownerAlive = isOwnerOnScreen;
        }

        /// <summary>Hands the load screen back to singleplayer.</summary>
        internal static void ExitPickMode()
        {
            pickMode = false;
            ownerAlive = null;
        }

        /// <summary>
        /// What the next <c>GetSaveHeaders</c> should hide. Lives for exactly one
        /// grid rebuild.
        /// </summary>
        private static Filter active = Filter.Off;

        /// <summary>
        /// Scopes the filter to the load screen's own grid rebuild, and nothing
        /// else.
        /// </summary>
        /// <remarks>
        /// The scoping is what makes this safe. <c>GetSaveHeaders</c> has fifteen
        /// callers and most of them must see the truth — the save screen's
        /// duplicate check, the splash screen's first-boot detection, the save
        /// count. Filtering unconditionally would let a player silently overwrite a
        /// multiplayer save.
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseLoad), "RebuildSaveGrid")]
        internal static class Rebuilding
        {
            private static void Prefix()
            {
                // Derive rather than set. The grid rebuilds while the picker is
                // open — the autosave toggle, the internal-save toggle and every
                // delete all call back into it — so a flag that were set here and
                // cleared below would flip an open picker to the wrong catalogue
                // on the player's first toggle.
                if (pickMode && !(ownerAlive?.Invoke() ?? false))
                {
                    Debug.LogWarning("[pb-and-j] pick mode was left on with nobody owning it — clearing");
                    ExitPickMode();
                }

                active = pickMode ? Filter.OnlyMultiplayer : Filter.HideMultiplayer;
            }

            // A Finalizer, not a Postfix: a Postfix is skipped when the method
            // throws, and a filter left switched on would silently blind the game's
            // entire save catalogue for the rest of the session.
            private static void Finalizer() => active = Filter.Off;
        }

        /// <summary>
        /// Hides — or, in pick mode, isolates — multiplayer saves for the duration
        /// of a grid rebuild.
        /// </summary>
        [HarmonyPatch(typeof(DataManagerSave), nameof(DataManagerSave.GetSaveHeaders))]
        internal static class Headers
        {
            private static void Postfix(ref Dictionary<string, DataContainerSavedMetadata> __result)
            {
                if (active == Filter.Off || __result == null)
                {
                    return;
                }

                // A NEW dictionary, never a mutation. GetSaveHeaders returns the
                // live static cache by reference, so filtering in place would
                // corrupt the game's own catalogue rather than the view of it.
                var want = active == Filter.OnlyMultiplayer;
                var filtered = new Dictionary<string, DataContainerSavedMetadata>(__result.Count);
                foreach (var pair in __result)
                {
                    var multiplayer = LobbySaveNames.IsMultiplayerKey(pair.Key);

                    // The scenario slot is inside the prefix but it is not a
                    // campaign: hidden from the singleplayer grid AND from the
                    // picker, so it can be selected in neither.
                    var slot = string.Equals(
                        pair.Key, LobbySaveNames.ScenarioSlot, StringComparison.OrdinalIgnoreCase);

                    if (multiplayer == want && !slot)
                    {
                        filtered[pair.Key] = pair.Value;
                    }
                }
                __result = filtered;
            }
        }

        /// <summary>
        /// Keeps the title screen's Continue button off multiplayer saves.
        /// </summary>
        /// <remarks>
        /// The one leak that is invisible until it misfires. <c>GetSaveHeaderLatest</c>
        /// reads the header cache <em>directly</em> and never routes through
        /// <c>GetSaveHeaders</c>, so the filter above does not reach it — and it is
        /// what backs Continue. Left alone, the newest save wins whatever it is, and
        /// a player pressing Continue drops into a co-op campaign in singleplayer.
        /// <para>
        /// A caller that asked for our prefix explicitly gets what it asked for;
        /// nothing in the game does today, but answering a direct question with a
        /// different answer is its own bug.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataManagerSave), nameof(DataManagerSave.GetSaveHeaderLatest))]
        internal static class Latest
        {
            private static void Postfix(string keyFilter, ref DataContainerSavedMetadata __result)
            {
                if (__result == null
                    || !LobbySaveNames.IsMultiplayerKey(__result.key)
                    || LobbySaveNames.IsMultiplayerKey(keyFilter))
                {
                    return;
                }

                // Re-run the game's own choice over the unfiltered truth, skipping
                // ours. Safe to call GetSaveHeaders here: we are outside a grid
                // rebuild, so the filter above is Off and this sees everything.
                DataContainerSavedMetadata? best = null;
                var headers = DataManagerSave.GetSaveHeaders(false);
                if (headers != null)
                {
                    var filtering = !string.IsNullOrEmpty(keyFilter);
                    foreach (var pair in headers)
                    {
                        var value = pair.Value;
                        if (value == null || value.key == null || LobbySaveNames.IsMultiplayerKey(value.key))
                        {
                            continue;
                        }
                        if (filtering && !value.key.StartsWith(keyFilter, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        if (best == null || value.timeInSystem > best.timeInSystem)
                        {
                            best = value;
                        }
                    }
                }
                // The game's own contract: null means "no save to continue from",
                // and the Continue button already handles it. Bang because the game
                // assembly is not nullable-annotated, not because it cannot be null.
                __result = best!;
            }
        }

        /// <summary>
        /// Makes every <c>pbj_</c> name unwritable from the singleplayer save
        /// screen, using the game's own "restricted" wording.
        /// </summary>
        /// <remarks>
        /// This is why multiplayer saves can stay <em>visible</em> in the save grid:
        /// the screen takes the name from the selected entry when its input is
        /// unfocused, so refusing the name refuses the overwrite whether it was
        /// typed or clicked. Hiding them from that grid instead would have been
        /// worse — the duplicate check, the save count and the save limit all run
        /// off the same call inside the same rebuild, so they would have started
        /// counting a catalogue with holes in it.
        /// <para>
        /// <c>CIViewPauseSave</c> is this method's only caller in the whole game.
        /// </para>
        /// </remarks>
        [HarmonyPatch(typeof(DataPathHelper), nameof(DataPathHelper.IsReservedFilename))]
        internal static class Reserved
        {
            private static void Postfix(string filename, ref bool __result)
            {
                if (LobbySaveNames.IsMultiplayerKey(filename))
                {
                    __result = true;
                }
            }
        }

        /// <summary>
        /// Stops a multiplayer save being deleted from the singleplayer save
        /// screen.
        /// </summary>
        /// <remarks>
        /// Overwrite and delete are separate paths and <see cref="Reserved"/> only
        /// closes the first: <c>ConfirmDelete</c> consults no name rules at all. A
        /// player could otherwise delete — behind singleplayer dialog wording — the
        /// very save a lobby has selected and every peer has readied on.
        /// </remarks>
        [HarmonyPatch(typeof(CIViewPauseSave), nameof(CIViewPauseSave.OnDeleteButton))]
        internal static class SaveScreenDelete
        {
            private static bool Prefix(string ___saveNameSelectedLast)
            {
                if (!LobbySaveNames.IsMultiplayerKey(___saveNameSelectedLast))
                {
                    return true;
                }
                Debug.Log("[pb-and-j] refusing to delete multiplayer save '"
                    + ___saveNameSelectedLast + "' from the singleplayer save screen");
                return false;
            }
        }
    }
}
