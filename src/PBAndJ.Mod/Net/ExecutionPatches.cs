using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat.Systems;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Intercepts the local Execute button. With a session active, pressing
    // Execute submits a Ready instead of running the turn; the host decides when
    // anyone executes.
    //
    // Gating here (the UI layer) while committing through
    // CombatUtilities.ConfirmExecution (the utility layer) means there is no
    // re-entrancy between the gate and the commit. It also catches the
    // "no orders" confirmation dialog, which routes through this method first.
    //
    // Caveat, deliberate: returning false skips this method's own validation —
    // UI mode, cutscene, tutorial, scenario step, the no-orders check. The real
    // backstop is CombatGameBridge.CommitTurn reporting whether the turn
    // actually advanced.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CIViewCombatExecution), nameof(CIViewCombatExecution.CheckAndAttemptExecution))]
    internal static class Patch_CIViewCombatExecution_CheckAndAttemptExecution
    {
        private static bool Prefix()
        {
            if (!NetGlue.HasSession)
            {
                return true;
            }
            NetGlue.PostLocalReady();
            return false;
        }
    }

    // Keeps the execute button visually locked while we are waiting on peers or
    // the host is simulating.
    //
    // A postfix, not a one-shot write: RecheckExecutionAvailability is called
    // from many places and overwrites the flag each time, so a single write
    // would be clobbered almost immediately.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(ScenarioUtility), nameof(ScenarioUtility.RecheckExecutionAvailability))]
    internal static class Patch_ScenarioUtility_RecheckExecutionAvailability
    {
        private static void Postfix()
        {
            if (!NetGlue.HasSession || !CombatGameBridge.ExecutionLocked)
            {
                return;
            }
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                // Writing the flag outside combat would create a stray flag entity.
                return;
            }
            combat.isScenarioAllowingExecution = false;
            if (CIViewCombatExecution.ins != null)
            {
                CIViewCombatExecution.ins.OnExecutionAvailabilityChange();
            }
        }
    }

    // The authoritative "execution started" signal.
    //
    // The button is not enough: CombatForceExecution is an ICombatFunction that
    // scenario YAML can use to advance the turn directly, and the debug console
    // can too. Both bypass the barrier entirely, so the host learns about them
    // here instead.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatUtilities), nameof(CombatUtilities.ConfirmExecution))]
    internal static class Patch_CombatUtilities_ConfirmExecution
    {
        private static int turnBefore;

        private static void Prefix()
        {
            var combat = Contexts.sharedInstance.combat;
            turnBefore = combat.hasCurrentTurn ? combat.currentTurn.i : -1;
        }

        private static void Postfix()
        {
            // CommitInProgress means this is our own barrier-driven commit, not
            // scenario content sneaking past. Without this the detector would
            // fire on every normal turn and drown out the real signal.
            if (!NetGlue.HasSession || CombatGameBridge.CommitInProgress)
            {
                return;
            }
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn || combat.currentTurn.i == turnBefore)
            {
                return;
            }
            NetGlue.NotifyExternalTurnAdvance(turnBefore, combat.currentTurn.i);
        }
    }

    // Execution finished. Fires on Simulating being removed, which is also
    // reached during combat teardown, hence the in-combat guard.
    //
    // The digest is computed here, but the executed turn number is NOT read
    // from the ECS: ConfirmExecution advanced currentTurn before the sim ran,
    // so it already shows the next turn. HostSession supplies the real one.
    [ExcludeFromCodeCoverage]
    [HarmonyPatch(typeof(CombatExecutionEndLateSystem), "Execute")]
    internal static class Patch_CombatExecutionEndLateSystem_Execute
    {
        private static void Postfix()
        {
            if (!NetGlue.HasSession || !IDUtility.IsGameState("combat"))
            {
                return;
            }
            NetGlue.PostLocalTurnComplete();
        }
    }
}
