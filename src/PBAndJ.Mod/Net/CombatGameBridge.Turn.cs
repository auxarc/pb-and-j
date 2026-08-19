using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // The turn barrier as this machine sees it: the execute-button lock, the orders
    // we send up, the order we are told to place, and the commit itself.
    //
    // ClearLocalOrders lives here rather than with the snapshot because it is the
    // counterpart of CaptureLocalOrders -- a client's planned orders never execute,
    // and left alone CaptureLocalOrders starts re-submitting what the host already ran.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
        /// <summary>Read by the execute-button patches.</summary>
        internal static bool ExecutionLocked { get; private set; }

        /// <summary>
        /// True while WE are inside ConfirmExecution. The external-advance
        /// detector must not fire on our own barrier-driven commit.
        /// </summary>
        internal static bool CommitInProgress { get; private set; }

        internal static void ResetLock()
        {
            ExecutionLocked = false;
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
            CommitInProgress = true;
            try
            {
                CombatUtilities.ConfirmExecution(1);
            }
            finally
            {
                CommitInProgress = false;
            }

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

        public void ClearLocalOrders()
        {
            if (!InCombat)
            {
                return;
            }

            // A client's planned orders never execute, because it never
            // simulates. Left alone they accumulate and CaptureLocalOrders starts
            // re-submitting orders the host already ran.
            var matcher = ActionMatcher
                .AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration, ActionMatcher.StartTime)
                .NoneOf(ActionMatcher.Destroyed);
            foreach (var action in Contexts.sharedInstance.action.GetGroup(matcher).GetEntities())
            {
                if (action.CompletedAction || action.isDisposed || action.AIAction)
                {
                    continue;
                }
                action.isDisposed = true;
            }
        }
    }
}
