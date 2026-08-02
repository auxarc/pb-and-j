using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Humble-object glue: the entire ECS surface Core needs, expressed without
    // Core ever seeing a game type. No logic lives here beyond field copying
    // and the guards the game itself requires.
    [ExcludeFromCodeCoverage]
    internal sealed class CombatGameBridge : IPbjGameBridge
    {
        /// <summary>Read by the execute-button patches.</summary>
        internal static bool ExecutionLocked { get; private set; }

        internal static void ResetLock()
        {
            ExecutionLocked = false;
        }

        public int CurrentTurn
        {
            get
            {
                var combat = Contexts.sharedInstance.combat;
                // currentTurn throws when the component is absent, which it is
                // outside combat.
                return combat.hasCurrentTurn ? combat.currentTurn.i : -1;
            }
        }

        public bool InCombat =>
            IDUtility.IsGameState("combat") && Contexts.sharedInstance.combat.hasCurrentTurn;

        public IReadOnlyList<string> AssignableUnitNames
        {
            get
            {
                var names = new List<string>();
                if (!InCombat)
                {
                    return names;
                }
                foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
                {
                    // Player-controllable AND friendly: friendly alone would
                    // include scenario-scripted AI allies, whose orders would
                    // fight the AI planning systems.
                    if (!unit.isPlayerControllable || !CombatUIUtility.IsUnitFriendly(unit))
                    {
                        continue;
                    }
                    var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                    if (persistent != null && persistent.hasNameInternal)
                    {
                        names.Add(persistent.nameInternal.s);
                    }
                }
                return names;
            }
        }

        public IReadOnlyList<OrderPayload> CaptureLocalOrders()
        {
            var orders = new List<OrderPayload>();
            if (!InCombat)
            {
                return orders;
            }

            // Same group query and skip predicate as the M2 action dump.
            var group = Contexts.sharedInstance.action.GetGroup(ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed));

            foreach (var action in group.GetEntities())
            {
                if (action.CompletedAction || action.isDisposed || action.AIAction)
                {
                    continue;
                }
                var order = OrderMapper.Capture(action);
                if (order != null)
                {
                    orders.Add(order);
                }
            }
            return orders;
        }

        public OrderApplyResult ApplyOrder(OrderPayload order)
        {
            return InCombat ? OrderMapper.Apply(order) : OrderApplyResult.UnknownUnit;
        }

        public bool CommitTurn()
        {
            var combat = Contexts.sharedInstance.combat;
            if (!combat.hasCurrentTurn)
            {
                return false;
            }

            var before = combat.currentTurn.i;
            CombatUtilities.ConfirmExecution(1);

            // ConfirmExecution is void and refuses silently in four normal
            // situations, so the only honest test is whether the turn moved.
            var after = Contexts.sharedInstance.combat.currentTurn.i;
            return after != before;
        }

        public void SetExecutionLocked(bool locked)
        {
            ExecutionLocked = locked;
            if (!InCombat)
            {
                return;
            }
            // Never write isScenarioAllowingExecution directly on unlock — let
            // the game recompute the real scenario-derived value.
            ScenarioUtility.RecheckExecutionAvailability(forceUIRefresh: true);
        }

        public string ComputeStateDigest()
        {
            var units = new List<UnitState>();
            if (!InCombat)
            {
                return StateDigest.Compute(units);
            }

            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                var position = unit.hasPosition ? unit.position.v : Vector3.zero;
                var integrity = persistent.hasUnitFrameIntegrity ? persistent.unitFrameIntegrity.f : 0f;
                units.Add(new UnitState(
                    persistent.nameInternal.s,
                    new Vec3(position.x, position.y, position.z),
                    integrity));
            }
            return StateDigest.Compute(units);
        }
    }
}
