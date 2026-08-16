using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using PhantomBrigade;
using PhantomBrigade.Data;
using QFSW.QC;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    /// <summary>
    /// Puts a real beam into a turn so M14's measurement 2 has one to measure.
    /// </summary>
    /// <remarks>
    /// Measurement 2 is the <c>_TimeSimulation</c> A/B against a <b>beam</b>, and
    /// it is a named merge gate. It cannot be run at all without a beam in the
    /// recorded turn, and no mech in this campaign carries a beam weapon.
    /// <para>
    /// <b>This spawns a beam entity rather than re-equipping a mech</b>, and the
    /// reason is in <c>BeamVizSystem.cs:31</c>: the subsystem lookup that turns a
    /// weapon into a beam asset is guarded by <c>!item.hasAssetLink</c>. Attach
    /// the asset first and that entire branch is skipped — no equipment entity,
    /// no part graph, no action, and nothing written to the save. Everything
    /// downstream is then the game's own: <c>:74</c> calls
    /// <c>CombatReplayHelper.OnBeamTransform</c> exactly as it does for a fired
    /// beam, so the recorded track, the wire bytes and the client's replay are
    /// indistinguishable from the real thing. Only the host-side question of how
    /// the beam came to exist differs, and that is not what is being measured.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>EnergyBeamEmission</c> is deliberately NOT added.</b> That is the
    /// component <c>BeamProjectionSystem</c> matches on
    /// (<c>BeamProjectionSystem.cs:78</c>), and it is the half that raycasts,
    /// applies damage, spawns impacts and builds reflection children. We want the
    /// visual and the recording, not a weapon — an injected beam must not be able
    /// to kill anything, or the measurement changes the fight it is measuring.
    /// </para>
    /// <para>
    /// Teardown is the game's, not ours: setting <c>isDestroyed</c> is the whole
    /// trigger for <c>BeamDestroySystem</c> (<c>:13</c>), which calls
    /// <c>fxHelperBeam.OnBeamEnd()</c> and <c>CombatReplayHelper.OnBeamEnd</c> —
    /// and the latter is what stamps <c>timeEnd</c> and records the closing
    /// keyframe. A beam torn down any other way would leave a track that never
    /// ends.
    /// </para>
    /// </remarks>
    [ExcludeFromCodeCoverage]
    internal static class BeamInjectGlue
    {
        // Vanilla's own default, from BeamProjectionSystem.cs:79-85. Copied
        // rather than invented so the extension maths at BeamVizSystem.cs:69-71
        // behaves exactly as it does for a fired beam — that line divides by
        // weaponProjectileSpeed, so a zero here is a NaN in the game's system.
        private const float RangeMax = 60f;
        private const float DamageRadius = 1f;
        private const float DamageBuildup = 0.5f;
        private const float ProjectileSpeed = 500f;
        private const float ReflectionAngleLimit = 0f;

        // How far the beam sweeps across its life, in degrees either side of the
        // firing unit's facing. A beam that never moves records two identical
        // keyframes and tells us nothing about motion under a frozen shader
        // clock, which is half of what the A/B is looking at.
        private const float SweepDegrees = 25f;

        private static int beamEntityId = -1;
        private static float beamStartedAt;
        private static float beamLifetime;
        private static Vector3 beamOrigin;
        private static Vector3 beamForward;

        /// <summary>Which asset pools are actually beams.</summary>
        /// <remarks>
        /// Found by inspecting the prefab for an <c>fxHelperBeam</c> rather than
        /// by guessing at names, and that matters more than it looks:
        /// <c>BeamVizSystem.cs:68</c> dereferences <c>fxHelperBeam</c> with no
        /// null check at all, so injecting a non-beam pool would NRE inside the
        /// game's own system every frame, not inside ours.
        /// </remarks>
        public static string FxBeamKeys()
        {
            var pools = DataMultiLinker<DataContainerAssetPool>.data;
            if (pools == null)
            {
                return "[pb-and-j] fx-beam-keys | no asset pools are loaded";
            }

            var found = new List<string>();
            foreach (var pair in pools)
            {
                var pool = pair.Value;
                // Unity's overloaded ==: a prefab whose Resources.Load failed is
                // kept on the container as a fake-null.
                if (pool != null && pool.prefab != null && pool.prefab.fxHelperBeam != null)
                {
                    found.Add(pair.Key);
                }
            }

            found.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("[pb-and-j] fx-beam-keys | ").Append(found.Count).Append(" beam pool(s)");
            for (var i = 0; i < found.Count; i++)
            {
                sb.Append(i == 0 ? " | " : ", ").Append(found[i]);
            }

            var line = sb.ToString();
            Debug.Log(line);
            return line;
        }

        /// <summary>
        /// Spawns one beam from the selected unit, lasting <paramref name="seconds"/>
        /// of simulation time.
        /// </summary>
        /// <remarks>
        /// Run it in the planning phase, <b>before</b> Execute. Simulation time is
        /// frozen until the turn runs, so the beam sits visible and unrecorded
        /// until then — which is deliberate: you can see it exists before
        /// committing a turn to it, and <c>recordingAllowed</c> is false until
        /// execution anyway. <c>CombatReplayHelper</c> clears <c>assetsBeams</c>
        /// when recording starts (<c>:216</c>), so the track opens at execution
        /// start rather than carrying planning-phase junk.
        /// </remarks>
        public static string FxBeamInject(string assetKey, float seconds)
        {
            if (!IDUtility.IsGameState("combat"))
            {
                return "[pb-and-j] not in combat";
            }
            if (beamEntityId >= 0)
            {
                return "[pb-and-j] a beam is already injected — pbj.fx-beam-clear first";
            }

            var pool = DataMultiLinker<DataContainerAssetPool>.GetEntry(assetKey, false);
            if (pool == null)
            {
                return $"[pb-and-j] no asset pool '{assetKey}' — try pbj.fx-beam-keys";
            }
            // Checked here as well as in fx-beam-keys, because this is the one
            // that would fault inside the game rather than inside us.
            if (pool.prefab == null || pool.prefab.fxHelperBeam == null)
            {
                return $"[pb-and-j] pool '{assetKey}' is not a beam (its prefab has no fxHelperBeam)"
                    + " — BeamVizSystem would NRE on it every frame";
            }

            var unit = SelectedUnit();
            if (unit == null)
            {
                return "[pb-and-j] no unit to fire from — pbj.select-unit 0 first";
            }

            var combat = Contexts.sharedInstance.combat;
            var simTime = combat.hasSimulationTime ? combat.simulationTime.f : 0f;

            // Out of the unit's centre rather than its feet, so the beam reads as
            // a shot rather than as a line drawn on the floor.
            var origin = unit.GetCenterPoint();
            var forward = unit.hasRotation ? unit.rotation.q * Vector3.forward : Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

            var entity = combat.CreateEntity();

            // Position BEFORE the attach: AssetPoolUtility.AttachInstance refuses
            // outright on an entity without one (:166-170) and only warns, so a
            // wrong order here looks like the pool being missing.
            entity.AddPosition(origin);
            entity.AddEnergyBeamProjection(origin, origin + (forward * RangeMax));
            entity.AddBeamStats(
                RangeMax, DamageRadius, DamageBuildup, ProjectileSpeed, ReflectionAngleLimit);
            entity.AddCreationTime(simTime);

            // Required, and required BEFORE BeamVizSystem next runs:
            // BeamVizSystem.cs:29 reads item.beamEmitter.combatID unconditionally,
            // above every other guard in that loop. Without it the game's own
            // system throws on our entity.
            entity.AddBeamEmitter(unit.id.id);

            AssetPoolUtility.AttachInstance(assetKey, entity, play: false);
            if (!entity.hasAssetLink || entity.assetLink.instance == null)
            {
                // isDestroyed rather than a raw Destroy(), which is the game's own
                // idiom: DestroyDestroyedCombatSystem is a cleanup system over
                // every Destroyed entity (:12-20), so it is swept at the end of
                // this frame without us tearing an entity out from under a group
                // that may still be iterating it.
                entity.isDestroyed = true;
                return $"[pb-and-j] pool '{assetKey}' would not give up an instance"
                    + " — it may be at its limit; check the log for a warning";
            }

            // What BeamVizSystem does for a real beam on the frame it attaches
            // one (:50). Without it the beam renders in whatever state the pooled
            // instance was last left in.
            entity.assetLink.instance.fxHelperBeam.OnBeamStart();

            beamEntityId = entity.id.id;
            beamStartedAt = simTime;
            beamLifetime = seconds > 0f ? seconds : 4f;
            beamOrigin = origin;
            beamForward = forward;

            var line = string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] fx-beam-inject | '{0}' from unit {1} | entity {2} | {3:0.00}s of sim time"
                    + " | sweeping {4:0.} degrees | execute the turn to record it",
                assetKey, unit.id.id, beamEntityId, beamLifetime, SweepDegrees * 2f);
            Debug.Log(line);
            return line;
        }

        /// <summary>Ends an injected beam early, through the game's own teardown.</summary>
        public static string FxBeamClear()
        {
            if (beamEntityId < 0)
            {
                return "[pb-and-j] no beam is injected";
            }

            var id = beamEntityId;
            Retire();
            return "[pb-and-j] fx-beam-clear | ended beam entity " + id;
        }

        /// <summary>
        /// Sweeps the live beam and ends it when its time is up.
        /// </summary>
        /// <remarks>
        /// Pumped from the same <c>Heartbeat.Update</c> postfix everything else
        /// here is. Measured in <b>simulation</b> time rather than real time, so
        /// the beam lasts a fixed slice of the turn: simulation time is frozen
        /// during planning, which is exactly what lets the beam be injected
        /// before Execute and still cover the turn once it runs.
        /// </remarks>
        internal static void Tick()
        {
            if (beamEntityId < 0)
            {
                return;
            }

            if (!IDUtility.IsGameState("combat"))
            {
                // Combat teardown takes the entity with it; keeping the id would
                // make the next mission's fx-beam-inject refuse for a beam that
                // no longer exists.
                beamEntityId = -1;
                return;
            }

            var entity = IDUtility.GetCombatEntity(beamEntityId);
            if (entity == null || entity.isDestroyed || !entity.hasEnergyBeamProjection)
            {
                beamEntityId = -1;
                return;
            }

            var combat = Contexts.sharedInstance.combat;
            var simTime = combat.hasSimulationTime ? combat.simulationTime.f : beamStartedAt;
            var elapsed = simTime - beamStartedAt;

            if (elapsed >= beamLifetime)
            {
                Retire();
                return;
            }

            // A sweep rather than a fixed line. Two identical keyframes would
            // record a beam that never moves, and half of what the A/B is looking
            // at is whether motion survives a frozen shader clock.
            var phase = beamLifetime > 0f ? Mathf.Clamp01(elapsed / beamLifetime) : 0f;
            var angle = Mathf.Lerp(-SweepDegrees, SweepDegrees, phase);
            var direction = Quaternion.Euler(0f, angle, 0f) * beamForward;
            entity.ReplaceEnergyBeamProjection(beamOrigin, beamOrigin + (direction * RangeMax));
        }

        private static void Retire()
        {
            var entity = IDUtility.GetCombatEntity(beamEntityId);
            beamEntityId = -1;
            if (entity == null)
            {
                return;
            }

            // isDestroyed and nothing else. BeamDestroySystem triggers on exactly
            // (EnergyBeamProjection, Destroyed) and is what calls OnBeamEnd, which
            // stamps timeEnd and records the closing keyframe. Tearing the entity
            // down by hand would leave a track that never ends.
            //
            // The entity itself then goes at this frame's cleanup, via
            // DestroyDestroyedCombatSystem — a cleanup system over every Destroyed
            // entity. Reactive systems run before cleanup, so BeamDestroySystem is
            // guaranteed to have seen it first.
            entity.isDestroyed = true;
        }

        private static CombatEntity? SelectedUnit()
        {
            var selected = IDUtility.GetSelectedCombatEntity();
            if (selected != null && selected.isUnitTag)
            {
                return selected;
            }

            foreach (var unit in Contexts.sharedInstance.combat
                .GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent != null && CombatUIUtility.IsUnitFriendly(unit))
                {
                    return unit;
                }
            }
            return null;
        }

        internal static void RegisterConsoleCommands()
        {
            Add(nameof(FxBeamKeys), "pbj.fx-beam-keys");
            Add(nameof(FxBeamClear), "pbj.fx-beam-clear");
            Add(nameof(FxBeamInject), "pbj.fx-beam-inject", typeof(string), typeof(float));
        }

        private static void Add(string methodName, string command, params Type[] signature)
        {
            var method = typeof(BeamInjectGlue).GetMethod(
                methodName, BindingFlags.Static | BindingFlags.Public,
                null, signature, null);
            QuantumConsoleProcessor.TryAddCommand(new CommandData(method, command));
        }
    }
}
