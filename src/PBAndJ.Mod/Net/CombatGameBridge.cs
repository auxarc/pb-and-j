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

        /// <summary>
        /// True while WE are inside ConfirmExecution. The external-advance
        /// detector must not fire on our own barrier-driven commit.
        /// </summary>
        internal static bool CommitInProgress { get; private set; }

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

        // The digest is a projection of the snapshot, never an independent walk.
        // If the two were allowed to disagree about which units exist, a client
        // would fail its post-correction check for reasons that have nothing to
        // do with correction.
        public string ComputeStateDigest()
        {
            var snapshot = CaptureSnapshot();
            var units = new UnitState[snapshot.Count];
            for (var i = 0; i < snapshot.Count; i++)
            {
                units[i] = snapshot[i].ToUnitState();
            }
            return StateDigest.Compute(units);
        }

        public IReadOnlyList<UnitSnapshot> CaptureSnapshot()
        {
            var units = new List<UnitSnapshot>();
            if (!InCombat)
            {
                return units;
            }

            // Every unit with a resolvable name — hostiles included, not just the
            // assignable ones. A client must be corrected about the whole fight.
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }

                var position = unit.hasPosition ? unit.position.v : Vector3.zero;
                var rotation = unit.hasRotation ? unit.rotation.q : Quaternion.identity;
                var facing = unit.hasFacing ? unit.facing.v : Vector3.forward;
                var integrity = persistent.hasUnitFrameIntegrity ? persistent.unitFrameIntegrity.f : 0f;
                var dead = persistent.hasDeathStatus;

                units.Add(new UnitSnapshot(
                    persistent.nameInternal.s,
                    new Vec3(position.x, position.y, position.z),
                    new Vec4(rotation.x, rotation.y, rotation.z, rotation.w),
                    new Vec3(facing.x, facing.y, facing.z),
                    integrity,
                    dead,
                    dead ? persistent.deathStatus.time : 0f));

                if (units.Count == PbjMessageCodec.MaxUnitsPerSnapshot)
                {
                    // Clamp at capture rather than letting the encoder produce a
                    // frame the far side would reject outright. Loud, because a
                    // silently truncated snapshot reads as a correct one.
                    Debug.LogWarning(NetLog.SnapshotClamped(
                        units.Count, PbjMessageCodec.MaxUnitsPerSnapshot));
                    break;
                }
            }
            return units;
        }

        // Safe only because a client never sets combat.Simulating, so no playback
        // system is driving these transforms and nothing overwrites the write on
        // the next tick. The same call on a simulating host would lose.
        public void ApplySnapshot(IReadOnlyList<UnitSnapshot> snapshot)
        {
            if (!InCombat || snapshot.Count == 0)
            {
                return;
            }

            var byName = new Dictionary<string, UnitSnapshot>(snapshot.Count);
            for (var i = 0; i < snapshot.Count; i++)
            {
                var name = snapshot[i].Name;
                if (!string.IsNullOrEmpty(name))
                {
                    byName[name!] = snapshot[i];
                }
            }

            var localOnly = 0;
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                if (!byName.TryGetValue(persistent.nameInternal.s, out var state))
                {
                    localOnly++;
                    continue;
                }

                unit.ReplacePosition(new Vector3(state.Position.X, state.Position.Y, state.Position.Z));
                unit.ReplaceRotation(new Quaternion(
                    state.Rotation.X, state.Rotation.Y, state.Rotation.Z, state.Rotation.W));
                unit.ReplaceFacing(new Vector3(state.Facing.X, state.Facing.Y, state.Facing.Z));
                persistent.ReplaceUnitFrameIntegrity(state.Integrity);

                if (state.IsDead && !persistent.hasDeathStatus)
                {
                    persistent.ReplaceDeathStatus(state.DeathTime, "remote");
                }
                byName.Remove(persistent.nameInternal.s);
            }

            // Entities are never created from a snapshot. A roster difference is
            // a structural mismatch that hard-setting positions cannot fix, so
            // it is reported rather than papered over.
            if (byName.Count > 0 || localOnly > 0)
            {
                Debug.Log(NetLog.SnapshotUnitsSkipped(byName.Count, localOnly));
            }
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
