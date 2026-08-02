using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Area;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod
{
    // M4 Step 0b: probe the two game behaviours that determine the HostSession
    // effect sequence, before 4c freezes that sequence into ~32 tests.
    //
    // 1. pbj.inject-two-then-clear — ActionDisposalSystem is reactive on
    //    Disposed.Added(), so it runs on the NEXT systems tick. For a disposed
    //    primary-track action whose end time is not before turn start (true of
    //    every planning-phase action) it disposes all later non-locked
    //    primary-track actions of the same owner. LoadToECSCombat never
    //    exercises this — it runs against a freshly rebuilt, empty context.
    //    So: does clear-then-apply in one frame eat the new orders, and does
    //    isLocked protect them?
    //
    // 2. pbj.commit — CombatUtilities.ConfirmExecution is void with four silent
    //    early-return exits (not in combat / already simulating / no scenario
    //    step / step prohibits execution). Does the turn actually advance?
    [ExcludeFromCodeCoverage]
    internal static class ChoreographySpikeGlue
    {
        private const string MoveActionKey = "move_run";
        private const float FirstMoveDistance = 10f;
        private const float SecondMoveDistance = 20f;

        // --- Probe 1: disposal cascade ---

        public static string InjectTwoThenClear()
        {
            return RunCascadeProbe(lockSurvivor: false);
        }

        public static string InjectTwoThenClearLocked()
        {
            return RunCascadeProbe(lockSurvivor: true);
        }

        private static string RunCascadeProbe(bool lockSurvivor)
        {
            var unit = IDUtility.GetSelectedCombatEntity();
            if (unit == null)
            {
                return "[pb-and-j] spike: no unit selected — select a friendly unit first";
            }
            if (!CombatUIUtility.IsUnitFriendly(unit))
            {
                return "[pb-and-j] spike: selected unit is not friendly";
            }
            if (!unit.hasPosition)
            {
                return "[pb-and-j] spike: selected unit has no position";
            }

            var first = CreateMove(unit, FirstMoveDistance, startTimeOverride: -1f);
            if (first == null)
            {
                return "[pb-and-j] spike: first order rejected by CreatePathAction — cannot probe";
            }

            var secondStart = first.hasStartTime && first.hasDuration
                ? first.startTime.f + first.duration.f
                : -1f;
            var second = CreateMove(unit, SecondMoveDistance, secondStart);
            if (second == null)
            {
                return "[pb-and-j] spike: second order rejected by CreatePathAction — cannot probe";
            }

            if (lockSurvivor)
            {
                second.isLocked = true;
            }

            var firstId = first.id.id;
            var secondId = second.id.id;
            Debug.Log("[pb-and-j] spike: created #" + firstId + " @" + Fmt(first)
                + " and #" + secondId + " @" + Fmt(second)
                + (lockSurvivor ? " (second isLocked=true)" : " (second unlocked)"));
            Debug.Log(ActionDumpGlue.BuildDump());

            // Dispose the EARLIER action — this is what a clear-then-reapply
            // commit sequence would do, and what triggers the cascade.
            first.isDisposed = true;
            Debug.Log("[pb-and-j] spike: disposed #" + firstId
                + " — run pbj.dump-actions on a LATER frame to see whether #"
                + secondId + " survived the ActionDisposalSystem cascade");

            return "[pb-and-j] spike: disposed #" + firstId + ", watch for #" + secondId
                + " in the next pbj.dump-actions";
        }

        private static ActionEntity? CreateMove(CombatEntity unit, float distance, float startTimeOverride)
        {
            var start = PathUtility.GetPathingOrigin(unit);
            var forward = unit.hasRotation ? unit.rotation.q * Vector3.forward : Vector3.forward;
            var end = start + forward.normalized * distance;
            var points = new List<Vector3> { start, end };
            var links = new List<AreaNavLink> { new AreaNavLink(AreaNavLinkType.Horizontal, 0) };
            return ActionUtility.CreatePathAction(unit, MoveActionKey, points, links, false, startTimeOverride);
        }

        private static string Fmt(ActionEntity action)
        {
            var start = action.hasStartTime ? action.startTime.f : 0f;
            var duration = action.hasDuration ? action.duration.f : 0f;
            return start.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + "s +" + duration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }

        // --- Probe 2: does ConfirmExecution actually commit? ---

        public static string Commit()
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                return "[pb-and-j] spike: not in combat (no currentTurn component)";
            }

            var before = combat.currentTurn.i;
            var simulatingBefore = combat.Simulating;

            CombatUtilities.ConfirmExecution(1);

            var after = Contexts.sharedInstance.combat.currentTurn.i;
            var advanced = after != before;
            var result = "[pb-and-j] spike: ConfirmExecution(1) | turn " + before + " -> " + after
                + " | simulating before=" + simulatingBefore
                + " | " + (advanced ? "COMMITTED" : "REFUSED (silent — check the LogWarning above)");
            Debug.Log(result);
            return result;
        }

        internal static void RegisterConsoleCommands()
        {
            var cascade = typeof(ChoreographySpikeGlue).GetMethod(
                nameof(InjectTwoThenClear), BindingFlags.Static | BindingFlags.Public);
            var cascadeLocked = typeof(ChoreographySpikeGlue).GetMethod(
                nameof(InjectTwoThenClearLocked), BindingFlags.Static | BindingFlags.Public);
            var commit = typeof(ChoreographySpikeGlue).GetMethod(
                nameof(Commit), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(cascade, "pbj.inject-two-then-clear"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(cascadeLocked, "pbj.inject-two-then-clear-locked"));
            QuantumConsoleProcessor.TryAddCommand(new CommandData(commit, "pbj.commit"));
        }
    }
}
