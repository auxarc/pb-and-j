using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using PBAndJ.Core;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod
{
    // Humble-object glue: marshals ActionEntity components into ActionSnapshots
    // and logs the Core-formatted dump. No decisions are made here.
    [ExcludeFromCodeCoverage]
    internal static class ActionDumpGlue
    {
        internal static string BuildDump()
        {
            var contexts = Contexts.sharedInstance;
            var turn = contexts.combat.hasCurrentTurn ? contexts.combat.currentTurn.i : -1;
            return ActionDumpFormatter.Format(turn, BuildSnapshots());
        }

        internal static List<ActionSnapshot> BuildSnapshots()
        {
            var contexts = Contexts.sharedInstance;
            var snapshots = new List<ActionSnapshot>();
            var group = contexts.action.GetGroup(ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed));

            foreach (var a in group.GetEntities())
            {
                if (a.CompletedAction || a.isDisposed || !a.hasDataKeyAction)
                {
                    continue;
                }
                var ownerCombat = IDUtility.GetCombatEntity(a.actionOwner.combatID);
                var ownerPersistent = ownerCombat != null ? IDUtility.GetLinkedPersistentEntity(ownerCombat) : null;
                var ownerName = ownerPersistent != null && ownerPersistent.hasNameInternal
                    ? ownerPersistent.nameInternal.s
                    : null;
                snapshots.Add(new ActionSnapshot(
                    a.actionOwner.combatID, ownerName, a.dataKeyAction.s, a.startTime.f, a.duration.f, a.isLocked));
            }
            return snapshots;
        }

        // Console command target, registered manually in PBAndJModLink.OnLoadEnd —
        // [Command] attributes in mod assemblies are invisible to QC's scanner.
        public static string DumpActions()
        {
            var dump = BuildDump();
            Debug.Log(dump);
            return dump;
        }

        internal static void RegisterConsoleCommand()
        {
            var method = typeof(ActionDumpGlue).GetMethod(nameof(DumpActions), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.dump-actions"));
        }
    }

    // Fires at the planning→execution commit point, before the turn increments:
    // the committed plan for the turn about to execute.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatUtilities), nameof(CombatUtilities.ConfirmExecution))]
    internal static class Patch_CombatUtilities_ConfirmExecution
    {
        private static void Prefix()
        {
            Debug.Log(ActionDumpGlue.BuildDump());
        }
    }
}
