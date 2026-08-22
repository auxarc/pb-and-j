using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using PBAndJ.Core;
using PBAndJ.Core.Net;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod
{
    // M3a: mid-combat save round-trip fidelity probe.
    // pbj.combat-save captures the planned-action set and saves; the
    // LoadToECSCombat postfix re-captures after restore and logs the diff.
    [ExcludeFromCodeCoverage]
    internal static class SaveLoadGlue
    {
        // The pbj_ namespace has one owner, and it is Core. M11b's catalogue has to
        // exclude this slot by name — WriteScenario deletes and rewrites the
        // directory on every transfer, so a lobby that selected it would have peers
        // ready onto a save the next transfer destroys.
        internal const string SaveName = LobbySaveNames.ScenarioSlot;

        /// <summary>
        /// Pre-save action captures, keyed by the save slot each was taken for.
        /// </summary>
        /// <remarks>
        /// ⭐ <b>Keyed rather than one shared field, because two writers arm it
        /// now.</b> M3a's <see cref="CombatSave"/> arms
        /// <see cref="LobbySaveNames.ScenarioSlot"/> when the player asks for it;
        /// M12c's <c>CheckpointGlue.Write</c> arms
        /// <see cref="LobbySaveNames.CheckpointSlot"/> by itself at every turn
        /// boundary. With a single field the automatic writer silently overwrites
        /// the manual one, so <c>pbj.combat-save</c>, one committed turn, then
        /// <c>pbj.combat-load</c> would compare the scenario slot's restored
        /// actions against the <em>checkpoint's</em> capture and print a DIFF that
        /// means nothing at all.
        /// <para>
        /// The restore side keys on <c>DataManagerSave.saveName</c>, which
        /// <c>LoadingStart</c> assigns from the key being loaded
        /// (<c>decompiled/DataHelperLoading.cs:266</c>) before <c>LoadData</c> at
        /// <c>:267</c> and long before <c>CombatBootstrap</c> reaches
        /// <c>LoadToECSCombat</c>. So every load is diffed against its own capture
        /// or against nothing — never against somebody else's.
        /// </para>
        /// </remarks>
        private static readonly Dictionary<string, PreSaveCapture> PreSaves =
            new Dictionary<string, PreSaveCapture>(StringComparer.OrdinalIgnoreCase);

        internal static void EnableCombatSaves()
        {
            DataShortcuts.debug.allowCombatSaves = true;
            SettingUtility.combatSavesAllowed = true;
        }

        public static string CombatSave()
        {
            if (!DataManagerSave.CanSave(false))
            {
                return "[pb-and-j] combat save blocked (simulating/loading/resolved?)";
            }
            var turn = CurrentTurn();
            var beforeSave = ActionDumpGlue.BuildSnapshots();
            ArmRestoreDiff(SaveName, beforeSave, "pbj.combat-save at turn " + turn);
            Debug.Log(ActionDumpFormatter.Format(turn, beforeSave));
            DataManagerSave.DoSave(SaveName, SaveLocation.Normal, null, -1, false);
            return $"[pb-and-j] combat save '{SaveName}' written | {beforeSave.Count} actions captured for diff";
        }

        public static string CombatLoad()
        {
            DataHelperLoading.OnLoadingExternal(SaveName, SaveLocation.Normal);
            return $"[pb-and-j] loading '{SaveName}'...";
        }

        /// <summary>
        /// Records <paramref name="snapshots"/> as the state of
        /// <paramref name="slot"/> immediately before it was written, to be
        /// diffed if and when that slot is restored.
        /// </summary>
        /// <remarks>
        /// <paramref name="source"/> is printed beside the diff on the restore
        /// side, and it is there so the diff's quiet answer stays readable: a
        /// <c>MATCH</c> over a real capture and the absence of any capture are
        /// completely different findings, and without a source line the second
        /// one is easy to read as the first.
        /// </remarks>
        internal static void ArmRestoreDiff(string slot, List<ActionSnapshot> snapshots, string source)
        {
            PreSaves[slot] = new PreSaveCapture(snapshots, source);
        }

        internal static void OnCombatRestored()
        {
            var after = ActionDumpGlue.BuildSnapshots();
            Debug.Log(ActionDumpFormatter.Format(CurrentTurn(), after));

            // Whatever LoadingStart named before it read the folder. Never the
            // save that DoSave last wrote: DataManagerSave.saveName is a sink on
            // the load path, which is the same property CheckpointGlue's remarks
            // depend on from the other direction.
            var loaded = DataManagerSave.saveName;
            if (loaded == null || !PreSaves.TryGetValue(loaded, out var capture))
            {
                Debug.Log("[pb-and-j] combat restored (no pre-save capture this session — diff skipped)"
                    + " | restored slot '" + (loaded ?? "<null>") + "'"
                    + " | captures armed so far: " + ArmedSlots());
                return;
            }

            Debug.Log("[pb-and-j] restore diff ARMED by " + capture.Source
                + " | slot '" + loaded + "'"
                + " | " + capture.Snapshots.Count + " action(s) captured before the save");

            if (capture.Snapshots.Count == 0 && after.Count == 0)
            {
                // The MATCH below would be the same word for a fight whose plan
                // survived perfectly and for an instrument pointed at nothing.
                Debug.LogWarning("[pb-and-j] ⚠ BOTH SIDES OF THE DIFF ARE EMPTY — the MATCH below is"
                    + " VACUOUS. It is the same output for: no actions were planned when the save"
                    + " was taken; the capture ran before the plan existed; and the restore ran"
                    + " before the ECS was rebuilt. Read the two counts, not the verdict.");
            }

            Debug.Log(SnapshotDiff.Compare(capture.Snapshots, after));
        }

        /// <summary>
        /// Every slot that has a capture, and what armed it — printed when a
        /// restore finds none for the slot it actually loaded.
        /// </summary>
        /// <remarks>
        /// The interesting failure is not "nothing was ever armed" but "something
        /// was armed, for a different slot": arming <c>pbj_combat_test</c> by hand
        /// and then loading <c>pbj_combat_turn</c> produces the same bare
        /// "diff skipped" line as a session that never saved at all. Naming the
        /// armed slots separates them on sight.
        /// </remarks>
        private static string ArmedSlots()
        {
            if (PreSaves.Count == 0)
            {
                return "NONE — nothing has armed a capture in this process"
                    + " (no pbj.combat-save, and no checkpoint written by this machine)";
            }

            var names = new List<string>();
            foreach (var pair in PreSaves)
            {
                names.Add("'" + pair.Key + "' (" + pair.Value.Source + ")");
            }
            return string.Join(", ", names.ToArray());
        }

        /// <summary>One pre-save action capture, and what armed it.</summary>
        private readonly struct PreSaveCapture
        {
            internal PreSaveCapture(List<ActionSnapshot> snapshots, string source)
            {
                Snapshots = snapshots;
                Source = source;
            }

            /// <summary>The planned actions as they stood immediately before the save.</summary>
            internal List<ActionSnapshot> Snapshots { get; }

            /// <summary>Which writer armed it, and when.</summary>
            internal string Source { get; }
        }

        private static int CurrentTurn()
        {
            var combat = Contexts.sharedInstance.combat;
            return combat.hasCurrentTurn ? combat.currentTurn.i : -1;
        }

        internal static void RegisterConsoleCommands()
        {
            var save = typeof(SaveLoadGlue).GetMethod(nameof(CombatSave), BindingFlags.Static | BindingFlags.Public);
            var load = typeof(SaveLoadGlue).GetMethod(nameof(CombatLoad), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(save, "pbj.combat-save"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(load, "pbj.combat-load"));
        }
    }

    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(DataManagerSave), nameof(DataManagerSave.LoadToECSCombat))]
    internal static class Patch_DataManagerSave_LoadToECSCombat
    {
        private static void Postfix()
        {
            SaveLoadGlue.OnCombatRestored();
        }
    }
}
