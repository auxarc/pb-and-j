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
        private static List<ActionSnapshot>? beforeSave;

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
            beforeSave = ActionDumpGlue.BuildSnapshots();
            Debug.Log(ActionDumpFormatter.Format(CurrentTurn(), beforeSave));
            DataManagerSave.DoSave(SaveName, SaveLocation.Normal, null, -1, false);
            return $"[pb-and-j] combat save '{SaveName}' written | {beforeSave.Count} actions captured for diff";
        }

        public static string CombatLoad()
        {
            DataHelperLoading.OnLoadingExternal(SaveName, SaveLocation.Normal);
            return $"[pb-and-j] loading '{SaveName}'...";
        }

        internal static void OnCombatRestored()
        {
            var after = ActionDumpGlue.BuildSnapshots();
            Debug.Log(ActionDumpFormatter.Format(CurrentTurn(), after));
            if (beforeSave != null)
            {
                Debug.Log(SnapshotDiff.Compare(beforeSave, after));
            }
            else
            {
                Debug.Log("[pb-and-j] combat restored (no pre-save capture this session — diff skipped)");
            }
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
