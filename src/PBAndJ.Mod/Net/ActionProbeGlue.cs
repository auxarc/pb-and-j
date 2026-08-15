using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Combat;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// THROWAWAY. Every planned action on this machine, and the state of the AI
    /// planner that would produce them.
    /// </summary>
    /// <remarks>
    /// Answers <c>docs/notes/enemy-previews-recon.md</c>, which is a decompile
    /// read with five compounding mechanisms and no measurement behind any of
    /// them. Run it on <b>both</b> machines, on the same turn, and diff — the
    /// question is not what either machine holds but whether they hold the same
    /// thing, and one dump alone cannot answer that.
    /// <para>
    /// The two fields that carry the most are <c>ai=</c> per action and
    /// <c>plan=</c> in the header. The recon claims a client's actions arrive
    /// from the <i>save</i> untagged (so <c>ai=False</c> on units the host
    /// reports <c>ai=True</c>), and that a client's planner runs exactly once
    /// and then stops (so <c>plan=</c> sticks at <c>Finished</c> while the
    /// host's cycles). Both are single-word reads and both are the kind of claim
    /// four review passes have failed to settle before.
    /// </para>
    /// <para>
    /// Deliberately dumps disposed actions too. <c>ClearLocalOrders</c> disposes
    /// rather than destroying, so an action it has taken out still exists — and
    /// "the enemy's plan was disposed" and "the enemy never had a plan" are the
    /// two outcomes this probe exists to tell apart.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class ActionProbeGlue
    {
        public static string ActionProbe()
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }

            var combat = Contexts.sharedInstance.combat;
            var ai = Contexts.sharedInstance.aI;

            var sb = new StringBuilder();
            sb.Append("[pb-and-j] action-probe | turn=")
                .Append(combat.hasCurrentTurn ? combat.currentTurn.i : -1)
                .Append(" simulating=").Append(combat.Simulating)
                .Append(" simTime=").Append(Num(combat.hasSimulationTime ? combat.simulationTime.f : -1f))
                .Append(" | plan=")
                .Append(ai.hasAIPlanningRequest ? ai.aIPlanningRequest.phase.ToString() : "NONE")
                .Append(" fullReplan=").Append(ai.isFullReplanningRequest);

            var group = Contexts.sharedInstance.action.GetGroup(ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed));

            var total = 0;
            var tagged = 0;
            var friendly = 0;
            var disposed = 0;
            var lines = new StringBuilder();

            foreach (var action in group.GetEntities())
            {
                total++;
                if (action.AIAction)
                {
                    tagged++;
                }
                if (action.isDisposed)
                {
                    disposed++;
                }

                var owner = IDUtility.GetCombatEntity(action.actionOwner.combatID);
                var ownerPersistent = owner != null ? IDUtility.GetLinkedPersistentEntity(owner) : null;
                var ownerName = ownerPersistent != null && ownerPersistent.hasNameInternal
                    ? ownerPersistent.nameInternal.s
                    : "(unnamed)";

                // Friendliness is read through the game's own helper rather than
                // inferred from the assignment roster: a client's roster is what
                // the host told it, and the question here is what the client's
                // OWN world model believes.
                var isFriendly = owner != null && CombatUIUtility.IsUnitFriendly(owner);
                if (isFriendly)
                {
                    friendly++;
                }

                lines.Append("\n[pb-and-j] action-probe   ")
                    .Append(ownerName)
                    .Append(' ')
                    .Append(action.hasDataKeyAction ? action.dataKeyAction.s : "(no key)")
                    .Append(" ai=").Append(action.AIAction)
                    .Append(" friendly=").Append(isFriendly)
                    .Append(" t=").Append(Num(action.startTime.f))
                    .Append(" d=").Append(Num(action.duration.f))
                    .Append(" path=")
                    .Append(action.hasMovementPath && action.movementPath.points != null
                        ? action.movementPath.points.Count
                        : 0)
                    .Append(" disp=").Append(action.isDisposed);
            }

            sb.Append(" | actions=").Append(total)
                .Append(" aiTagged=").Append(tagged)
                .Append(" friendly=").Append(friendly)
                .Append(" disposed=").Append(disposed)
                .Append(lines);

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        private static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        internal static void RegisterConsoleCommands()
        {
            var method = typeof(ActionProbeGlue).GetMethod(
                nameof(ActionProbe), BindingFlags.Static | BindingFlags.Public,
                null, new System.Type[0], null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, "pbj.action-probe"));
        }
    }
}
