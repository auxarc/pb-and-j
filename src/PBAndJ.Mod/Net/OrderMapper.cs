using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Area;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Humble-object glue: pure field copying between the game's ActionEntity
    // components and Core's OrderPayload. No decisions beyond the guards the
    // game's own code applies.
    //
    // Both directions are deliberate transcriptions of proven game code —
    // DataHelperSaveSerialization.SaveFromECS for capture, and
    // DataManagerSave.LoadToECSCombat for apply. Do not invent a new path:
    // these are the ones the game itself runs on every combat save and load.
    [ExcludeFromCodeCoverage]
    internal static class OrderMapper
    {
        /// <summary>Shortest action the game accepts.</summary>
        private const float MinimumDuration = 0.25f;

        /// <summary>ActionEntity -> OrderPayload. Mirrors SaveFromECS.</summary>
        internal static OrderPayload? Capture(ActionEntity action)
        {
            if (!action.hasActionOwner || !action.hasDataKeyAction || !action.hasStartTime || !action.hasDuration)
            {
                return null;
            }
            var owner = IDUtility.GetCombatEntity(action.actionOwner.combatID);
            var ownerPersistent = owner != null ? IDUtility.GetLinkedPersistentEntity(owner) : null;
            if (ownerPersistent == null || !ownerPersistent.hasNameInternal)
            {
                return null;
            }

            Vec3? targetedPoint = action.hasTargetedPoint ? ToVec3(action.targetedPoint.v) : (Vec3?)null;
            Vec3? targetedDirection = action.hasTargetedDirection ? ToVec3(action.targetedDirection.v) : (Vec3?)null;

            string? targetedEntityName = null;
            if (action.hasTargetedEntity)
            {
                var targetCombat = IDUtility.GetCombatEntity(action.targetedEntity.combatID);
                var targetPersistent = targetCombat != null ? IDUtility.GetLinkedPersistentEntity(targetCombat) : null;
                if (targetPersistent != null && targetPersistent.hasNameInternal)
                {
                    targetedEntityName = targetPersistent.nameInternal.s;
                }
            }

            string? targetedSocketName = null;
            if (action.hasTargetedPart)
            {
                var part = IDUtility.GetEquipmentEntity(action.targetedPart.equipmentID);
                if (part != null && part.hasPartParentUnit)
                {
                    targetedSocketName = part.partParentUnit.socket;
                }
            }

            var targetedHardpointName = action.hasTargetedSystem ? action.targetedSystem.hardpoint : null;

            var points = new List<Vec3>();
            var links = new List<PathLink>();
            if (action.hasMovementPath)
            {
                var path = action.movementPath;
                if (path.points != null)
                {
                    foreach (var point in path.points)
                    {
                        points.Add(ToVec3(point));
                    }
                }
                if (path.links != null)
                {
                    foreach (var link in path.links)
                    {
                        links.Add(new PathLink((int)link.type, link.destinationIndex));
                    }
                }
            }

            float? directionInterpolant = action.hasMovementMeleeDirectionInterpolant
                ? action.movementMeleeDirectionInterpolant.f
                : (float?)null;
            float? offsetInterpolant = action.hasMovementMeleeOffsetInterpolant
                ? action.movementMeleeOffsetInterpolant.f
                : (float?)null;

            MeleePayload? melee = null;
            if (action.hasMovementMeleeAttacker)
            {
                var attacker = action.movementMeleeAttacker;
                melee = new MeleePayload(attacker.isOffsetRight, attacker.shockwaveKey, attacker.partUsed);
            }

            DashPayload? dash = null;
            if (action.hasMovementDash)
            {
                dash = new DashPayload(action.movementDash.durationDashOut, action.movementDash.durationDashAlign);
            }

            DashVerticalPayload? dashVertical = null;
            if (action.hasMovementDashVertical)
            {
                dashVertical = new DashVerticalPayload(
                    ToVec3(action.movementDashVertical.origin), action.movementDashVertical.altitude);
            }

            var bulldoze = new List<DestructionPoint>();
            if (action.hasMovementDashBulldoze && action.movementDashBulldoze.pointsDestroyed != null)
            {
                foreach (var predicted in action.movementDashBulldoze.pointsDestroyed)
                {
                    bulldoze.Add(new DestructionPoint(predicted.time, ToVec3(predicted.position), predicted.index));
                }
            }

            return new OrderPayload(
                action.dataKeyAction.s,
                ownerPersistent.nameInternal.s,
                action.startTime.f,
                action.duration.f,
                targetedPoint,
                targetedDirection,
                targetedEntityName,
                targetedSocketName,
                targetedHardpointName,
                points,
                links,
                directionInterpolant,
                offsetInterpolant,
                melee,
                dash,
                dashVertical,
                bulldoze);
        }

        /// <summary>OrderPayload -> ActionEntity. Mirrors LoadToECSCombat.</summary>
        internal static OrderApplyResult Apply(OrderPayload order)
        {
            var ownerPersistent = Contexts.sharedInstance.persistent.GetEntityWithNameInternal(order.OwnerName);
            if (ownerPersistent == null || !ownerPersistent.hasContextLinkCombat)
            {
                return OrderApplyResult.UnknownUnit;
            }
            var ownerCombat = IDUtility.GetLinkedCombatEntity(ownerPersistent);
            if (ownerCombat == null)
            {
                return OrderApplyResult.UnknownUnit;
            }

            // The load path performs no time validation at all, so an order
            // outside the placement window would apply verbatim and then behave
            // oddly. Check it up front instead.
            var combat = Contexts.sharedInstance.combat;
            var turnStart = combat.currentTurn.i * combat.turnLength.i;
            if (order.StartTime < turnStart - 0.01f
                || order.StartTime > turnStart + DataShortcuts.sim.maxActionTimePlacement)
            {
                return OrderApplyResult.OutOfWindow;
            }

            // Checked before instantiating: an action that fails IsValid later
            // gets disposed at sim start, and disposal cascades to the owner's
            // later orders. Better never to create it.
            if (!DataHelperAction.IsAvailable(order.Blueprint, ownerCombat))
            {
                return OrderApplyResult.Invalid;
            }

            var action = DataHelperAction.InstantiateAction(ownerCombat, order.Blueprint, order.StartTime, out var valid);
            if (!valid || action == null)
            {
                return OrderApplyResult.UnknownBlueprint;
            }

            var duration = order.Duration;

            if (order.TargetedPoint.HasValue)
            {
                action.ReplaceTargetedPoint(ToVector3(order.TargetedPoint.Value));
            }
            if (order.TargetedDirection.HasValue)
            {
                action.ReplaceTargetedDirection(ToVector3(order.TargetedDirection.Value));
            }

            if (!string.IsNullOrEmpty(order.TargetedEntityName))
            {
                var targetPersistent = Contexts.sharedInstance.persistent.GetEntityWithNameInternal(order.TargetedEntityName);
                var targetCombat = targetPersistent != null ? IDUtility.GetLinkedCombatEntity(targetPersistent) : null;
                if (targetCombat != null)
                {
                    action.ReplaceTargetedEntity(targetCombat.id.id);
                    if (!string.IsNullOrEmpty(order.TargetedSocketName))
                    {
                        var part = EquipmentUtility.GetPartInUnit(targetPersistent, order.TargetedSocketName);
                        if (part != null)
                        {
                            action.ReplaceTargetedPart(part.id.id);
                            if (!string.IsNullOrEmpty(order.TargetedHardpointName)
                                && part.partBlueprint.hardpoints.Contains(order.TargetedHardpointName))
                            {
                                action.ReplaceTargetedSystem(order.TargetedHardpointName);
                            }
                        }
                    }
                }
            }

            if (order.PathPoints.Count > 0)
            {
                var points = new List<Vector3>(order.PathPoints.Count);
                for (var i = 0; i < order.PathPoints.Count; i++)
                {
                    points.Add(ToVector3(order.PathPoints[i]));
                }

                // Re-anchor the path to where the unit actually is on THIS
                // machine. For an honest client the delta is ~zero, because its
                // path already started at the same pathing origin. For anything
                // else it prevents the unit teleporting to the path's start.
                var origin = PathUtility.GetPathingOrigin(ownerCombat);
                var delta = origin - points[0];
                for (var i = 0; i < points.Count; i++)
                {
                    points[i] += delta;
                }

                var links = new List<AreaNavLink>(order.PathLinks.Count);
                for (var i = 0; i < order.PathLinks.Count; i++)
                {
                    links.Add(new AreaNavLink((AreaNavLinkType)order.PathLinks[i].Type, order.PathLinks[i].DestinationIndex));
                }
                action.ReplaceMovementPath(points, links);
                action.isMovementPathChanged = true;

                // Movement duration is RECOMPUTED, never trusted. The wire value
                // would otherwise let any peer slide a unit across the map in
                // whatever time it liked; the game's own CreatePathAction always
                // derives it from path length and the unit's speed. For an
                // honest client this reproduces the value it sent, because it is
                // the same unit at the same speed over the same path.
                var speed = ownerCombat.hasMovementSpeedCurrent ? ownerCombat.movementSpeedCurrent.f : 0f;
                var scalar = action.dataLinkAction.data.dataMovement?.movementSpeedScalar ?? 1f;
                if (speed > 0f && scalar > 0f)
                {
                    duration = PathUtility.GetPathLength(points) / (speed * scalar);
                }
            }

            // Same floor the game applies; anything shorter is not a real action.
            if (duration < MinimumDuration)
            {
                return OrderApplyResult.Invalid;
            }
            action.ReplaceDuration(duration);

            if (order.DirectionInterpolant.HasValue)
            {
                action.ReplaceMovementMeleeDirectionInterpolant(order.DirectionInterpolant.Value);
            }
            if (order.OffsetInterpolant.HasValue)
            {
                action.ReplaceMovementMeleeOffsetInterpolant(order.OffsetInterpolant.Value);
            }

            if (order.Melee != null)
            {
                ApplyMelee(action, ownerPersistent, order.Melee);
            }
            if (order.Dash != null)
            {
                action.ReplaceMovementDash(order.Dash.DurationDashOut, order.Dash.DurationDashAlign);
            }
            if (order.DashBulldoze.Count > 0)
            {
                var predicted = new List<PointDestructionPrediction>(order.DashBulldoze.Count);
                for (var i = 0; i < order.DashBulldoze.Count; i++)
                {
                    var point = order.DashBulldoze[i];
                    predicted.Add(new PointDestructionPrediction
                    {
                        time = point.Time,
                        position = ToVector3(point.Position),
                        index = point.Index,
                    });
                }
                action.ReplaceMovementDashBulldoze(predicted);
            }
            if (order.DashVertical != null)
            {
                action.ReplaceMovementDashVertical(
                    ToVector3(order.DashVertical.Origin), order.DashVertical.Altitude);
            }

            return OrderApplyResult.Applied;
        }

        // Transcribed verbatim from LoadToECSCombat: melee needs the attacker's
        // own equipment to resolve the socket side and animation variant.
        private static void ApplyMelee(ActionEntity action, PersistentEntity ownerPersistent, MeleePayload melee)
        {
            var equipmentData = action.dataLinkAction.data.dataEquipment;
            var isSocketRight = equipmentData != null && equipmentData.partSocket == "equipment_right";
            var part = equipmentData != null
                ? EquipmentUtility.GetPartInUnit(ownerPersistent, equipmentData.partSocket)
                : null;
            var subsystem = part != null && part.hasPrimaryActivationSubsystem
                ? IDUtility.GetEquipmentEntity(part.primaryActivationSubsystem.equipmentID)
                : null;

            var variant = 0;
            if (part != null && subsystem != null)
            {
                subsystem.dataLinkSubsystem.data.TryGetInt("melee_animation_variant", out variant);
            }

            action.ReplaceMovementMeleeAttacker(melee.IsOffsetRight, isSocketRight, variant, melee.ShockwaveKey, melee.PartUsed);
            action.ReplaceMovementMeleeContacts(new HashSet<int>());
        }

        private static Vec3 ToVec3(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static Vector3 ToVector3(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
