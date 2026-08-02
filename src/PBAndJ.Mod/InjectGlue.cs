using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Area;
using PBAndJ.Core;
using PhantomBrigade;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod
{
    // M3b: inject one move order for the currently selected friendly unit via
    // the game's own action-creation API — the "external order source" primitive.
    [ExcludeFromCodeCoverage]
    internal static class InjectGlue
    {
        private const string MoveActionKey = "move_run";
        private const float MoveDistance = 12f;

        public static string InjectMove()
        {
            var unit = IDUtility.GetSelectedCombatEntity();
            if (unit == null)
            {
                return "[pb-and-j] no unit selected — select a friendly unit first";
            }
            if (!CombatUIUtility.IsUnitFriendly(unit))
            {
                return "[pb-and-j] selected unit is not friendly";
            }
            if (!unit.hasPosition)
            {
                return "[pb-and-j] selected unit has no position";
            }

            var persistent = IDUtility.GetLinkedPersistentEntity(unit);
            var unitName = persistent != null && persistent.hasNameInternal ? persistent.nameInternal.s : "?";

            // Same construction the game uses for its own move_run extrapolation
            // in CombatUILinkSimulationStart: pathing origin + one horizontal link.
            var start = PathUtility.GetPathingOrigin(unit);
            var forward = unit.hasRotation ? unit.rotation.q * Vector3.forward : Vector3.forward;
            var end = start + forward.normalized * MoveDistance;
            var points = new List<Vector3> { start, end };
            var links = new List<AreaNavLink> { new AreaNavLink(AreaNavLinkType.Horizontal, 0) };

            var action = ActionUtility.CreatePathAction(unit, MoveActionKey, points, links);
            var valid = action != null;
            var report = InjectionReport.Compose(
                unitName,
                valid,
                valid ? action!.id.id : -1,
                valid && action!.hasStartTime ? action.startTime.f : 0f,
                valid && action!.hasDuration ? action.duration.f : 0f);
            Debug.Log(report);
            Debug.Log(ActionDumpGlue.BuildDump());
            return report;
        }

        internal static void RegisterConsoleCommand()
        {
            var method = typeof(InjectGlue).GetMethod(nameof(InjectMove), BindingFlags.Static | BindingFlags.Public);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.inject-move"));
        }
    }
}
