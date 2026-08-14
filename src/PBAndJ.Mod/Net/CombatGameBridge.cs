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
                    dead ? persistent.deathStatus.time : 0f,
                    // M13. A client cannot work any of these out for itself: the
                    // game's detector is line-of-sight fog of war whose only
                    // caller triggers on simulationTime, which a client never
                    // advances. Left un-sent, its copy stays frozen at whatever
                    // the scenario save said on the turn it loaded.
                    unit.isHidden,
                    unit.isHiddenDetectable,
                    persistent.isUnitDeployed,
                    // Presence travels beside the value because the two really
                    // do disagree across the wire. A host's player squad is
                    // deployed with no arrival time at all
                    // (CombatScenarioSetupSystem), while the same units on a
                    // client read has=true, value=-1 — the save writer stamps -1
                    // for an absent component and the loader adds it back to
                    // everything deployed. Sending only a float would leave
                    // those units uncorrectable.
                    unit.hasArrivalTime,
                    unit.hasArrivalTime ? unit.arrivalTime.f : 0f));

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
            var revealed = 0;
            var hidden = 0;
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

                // M13. Visibility goes FIRST, before the position write below,
                // and the order is load-bearing: CombatUILinkInWorldMarkers
                // triggers on Position but skips units that are hidden, so a
                // unit revealed after its position had already been replaced
                // would get no world marker until something else moved it.
                if (ApplyVisibility(unit, persistent, state))
                {
                    if (state.IsHidden)
                    {
                        hidden++;
                    }
                    else
                    {
                        revealed++;
                    }
                }

                // Components only, and that is sufficient to render: PositionLinkSystem
                // and RotationLinkSystem are reactive on CombatMatcher.Position /
                // .Rotation and call CombatView.OnPosition/OnRotation, which set
                // the view transform. Neither is gated on the simulation running,
                // so a correction arriving between turns is visible immediately.
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
            if (revealed > 0 || hidden > 0)
            {
                Debug.Log(NetLog.VisibilityCorrected(revealed, hidden));
            }
        }

        /// <summary>
        /// Puts one unit's visibility where the host's is. True if it moved.
        /// </summary>
        /// <remarks>
        /// <c>VisibilityLinkSystem</c> is reactive on
        /// <c>CombatMatcher.Hidden.AddedOrRemoved()</c> and is not gated on the
        /// simulation running, so the flag alone does redraw the three views —
        /// the same self-healing shape <c>PositionLinkSystem</c> gives the
        /// transform writes above.
        /// <para>
        /// The explicit helper calls are not belt-and-braces. The game never
        /// treats that system as sufficient: every place it writes
        /// <c>isHidden</c> itself it follows with the marker and overlay calls
        /// below. And on the hiding side it must — the in-world marker link
        /// skips hidden units, so nothing would ever take a stale marker down.
        /// </para>
        /// <para>
        /// The Entitas flag setters early-return when the value is unchanged, so
        /// the steady-state cost of this is a comparison per unit per turn and
        /// the collector cannot re-fire on a no-op.
        /// </para>
        /// </remarks>
        private static bool ApplyVisibility(
            CombatEntity unit, PersistentEntity persistent, UnitSnapshot state)
        {
            // Deployment first: the overlay eligibility check rejects on this
            // BEFORE it looks at visibility, so revealing an undeployed unit
            // would show a mesh with no marker, overlay or unit-bar entry.
            persistent.isUnitDeployed = state.IsDeployed;
            unit.isHiddenDetectable = state.IsHiddenDetectable;
            ApplyArrivalTime(unit, state);
            DropLandingData(unit);

            if (unit.isHidden == state.IsHidden)
            {
                return false;
            }

            unit.isHidden = state.IsHidden;
            var visible = !state.IsHidden;
            // Fully qualified rather than pulled in with a using: the
            // PhantomBrigade.Combat namespace carries a lot of short names and
            // this file already reaches into three others.
            PhantomBrigade.Combat.CIHelperWorldMarkers.OnUnitVisibilityChanged(unit.id.id, visible);
            CIHelperOverlays.OnUnitVisibilityChanged(unit.id.id, visible);
            CIHelperOverlays.OnUnitEligibilityChange(persistent);

            // And then hand the freshly-built overlay to the game's own
            // time-dependent refresh, because on a client nothing else ever
            // will. OnTimeChange is what decides the "no data" widget — it is
            // the ONLY writer of it — and that widget is on by default in the
            // prefab, so an overlay created and left alone shows it forever.
            //
            // The host never notices: its timeline and prediction clock move
            // constantly, so OnTimeChange runs and clears the widget within a
            // frame. A client's combat clock is frozen by design, so the call
            // happens during setup and effectively never again — which is fine
            // for every overlay built at setup, and wrong for any built later.
            // Revealing a unit mid-fight builds one later.
            //
            // Measured rather than reasoned: probing the same unit on both
            // machines showed unknown=off on the host and unknown=ON on the
            // client, with a unit visible from turn 0 reading off on both.
            CIHelperOverlays.OnTimeChange();
            return true;
        }

        /// <summary>
        /// Puts one unit's arrival time where the host's is, presence included.
        /// </summary>
        /// <remarks>
        /// Written outside the visibility early-return above, because the value
        /// moves in cases where the flag does not: a unit revealed and then
        /// revealed again by a later wave keeps <c>isHidden == false</c>
        /// throughout while its arrival time is rewritten.
        /// <para>
        /// The removal arm is not an edge case and will fire on the first
        /// snapshot of every fight, for every player unit. A client manufactures
        /// the component for itself on load — <c>DataManagerSave.cs:3047</c> adds
        /// one to everything deployed, taking the <c>-1</c> the save writer
        /// stamps for an absent component — while the host's own player squad
        /// never has it. Correcting that is the point rather than a side effect:
        /// <c>ScenarioUtility.cs:3652</c> branches on presence alone.
        /// </para>
        /// <para>
        /// Guarded on both sides so the steady state costs a comparison rather
        /// than a component write, which matters because this runs for every
        /// unit of every snapshot.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Takes the landing animation away from a unit on a client, which can
        /// never finish playing it.
        /// </summary>
        /// <remarks>
        /// Measured, not reasoned — and it is the one fact this whole change
        /// rested on, argued twice from the decompile and got wrong both times.
        /// A probe on a real client (<c>pbj.vis-probe</c>) reported
        /// <c>landing=True</c> for a hidden scenario unit on <b>both</b>
        /// machines, so the reassuring answer was simply false.
        /// <para>
        /// Why it has to go. <c>CombatLandingSystem</c> is a reactive system on
        /// <c>SimulationTime</c>, and a client's clock is frozen at zero — but
        /// Entitas collectors fire on <i>Replace</i>, not on advancement, and
        /// <c>UnitUtilities.cs:1063</c> replaces the value with itself, so the
        /// system does run here. Its elapsed time is
        /// <c>0 - arrivalTime</c>, i.e. negative, so it takes the
        /// <c>continue</c> and never reaches the branch that completes a landing
        /// and removes the component. A host sheds <c>LandingData</c> seconds
        /// after the unit arrives; a client would hold it for the rest of the
        /// fight.
        /// </para>
        /// <para>
        /// That difference is not cosmetic once the arrival time is replicated.
        /// <c>CIHelperOverlays.OnTimeChange</c> raises the landing countdown on
        /// exactly <c>hasArrivalTime &amp;&amp; hasLandingData</c>, so the client
        /// would pin a "▼ 13.1s" countdown over a unit that landed long ago and
        /// never clear it. The null-clip arm of the landing system is worse: it
        /// <c>ForceUnitTransform</c>s the unit to the landing spot, overriding
        /// the snapshot we just applied.
        /// </para>
        /// <para>
        /// Nothing is lost by dropping it. The landing is presentation, a client
        /// never simulates one, and the host's own replay does not show it
        /// either — the recorder keeps no entry for a unit that was hidden when
        /// the turn began.
        /// </para>
        /// </remarks>
        private static void DropLandingData(CombatEntity unit)
        {
            if (unit.hasLandingData)
            {
                unit.RemoveLandingData();
            }
            if (unit.hasLandingDataCustom)
            {
                unit.RemoveLandingDataCustom();
            }
        }

        private static void ApplyArrivalTime(CombatEntity unit, UnitSnapshot state)
        {
            if (!state.HasArrivalTime)
            {
                if (unit.hasArrivalTime)
                {
                    unit.RemoveArrivalTime();
                }
                return;
            }

            if (!unit.hasArrivalTime)
            {
                unit.AddArrivalTime(state.ArrivalTime);
            }
            else if (unit.arrivalTime.f != state.ArrivalTime)
            {
                unit.ReplaceArrivalTime(state.ArrivalTime);
            }
        }

        // Walks the game's own replay recorder and re-keys it for the wire.
        //
        // Three things here are not obvious and were each verified against the
        // decompiled 2.2.2-b8339 source:
        //
        // 1. Do NOT gate on CombatReplayHelper.IsRecordingAllowed(). It is
        //    already false by the time we run: OnExecutionEnd clears the flag,
        //    and it is called from CombatUILinkSimulationEnd, which sits in
        //    CombatUISystems (slot 72) — ahead of CombatExecutionEndLateSystem
        //    (slot 93), the system whose postfix brings us here. Both react to
        //    the same Simulating.Removed() collector. Gating on it would return
        //    empty every single turn.
        //
        // 2. The tracks MAY NOT be cleared between turns, so never assume either
        //    way. OnExecutionStart clears `units` only when experimentalMode is
        //    false, and while the field defaults to true in code it is a player
        //    SETTING (Experimental_ReplayExtended) — a probe on a real game read
        //    it as FALSE, so both states ship. With it on, a track accumulates
        //    for the whole combat. We slice from the key OnExecutionStart wrote,
        //    BY INDEX — not by comparing against turnStartTime, which is
        //    Mathf.RoundToInt'd and so can be *later* than the previous turn's
        //    final key, dragging it into our window. That slice is correct
        //    whichever way the setting sits, which is why it is written this way
        //    rather than branching on the flag.
        //
        // 3. The recorder's last key is not the unit's final position.
        //    OnExecutionEnd samples position before CombatExecutionEndLateSystem
        //    force-sets it onto the projected path, and its own OnUnitSnapshot
        //    call is a no-op by (1). So we append a final key ourselves, read
        //    exactly where CaptureSnapshot reads, which is what makes
        //    "last key == snapshot" true rather than merely hoped for.
        public KeyframeCapture CaptureKeyframes()
        {
            if (!InCombat || CombatReplayHelper.units == null || CombatReplayHelper.units.Count == 0)
            {
                Debug.Log(NetLog.KeyframesUnavailable());
                return KeyframeCapture.None;
            }

            var windowStart = CombatReplayHelper.turnStartTime;
            var windowEnd = Contexts.sharedInstance.combat.hasSimulationTime
                ? Contexts.sharedInstance.combat.simulationTime.f
                : windowStart;

            var tracks = new List<UnitTrack>();
            var poses = new List<UnitPoseTrack>();
            var clamped = 0;
            var bonelessUnits = 0;
            var strandedKeys = 0;

            foreach (var entry in CombatReplayHelper.units)
            {
                // The recorder keys by combatEntity.id.id, a process-local ECS
                // id that means nothing in another process. Same lookup
                // OnExecutionEnd itself uses.
                var unit = IDUtility.GetCombatEntity(entry.Key);
                if (unit == null || unit.isDestroyed)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }

                var keys = SliceTurn(entry.Value.keyframesTransform, windowStart);

                // The final key, from the same read CaptureSnapshot performs.
                keys.Add(new TransformKey(
                    windowEnd,
                    ToVec3(unit.hasPosition ? unit.position.v : Vector3.zero),
                    ToVec4(unit.hasRotation ? unit.rotation.q : Quaternion.identity)));

                if (keys.Count > PbjMessageCodec.MaxKeysPerTrack)
                {
                    // Drop interior keys and keep the endpoints: a track
                    // truncated at the tail would end playback short of the
                    // state the snapshot already corrected everyone to.
                    clamped++;
                    keys = Decimate(keys, PbjMessageCodec.MaxKeysPerTrack);
                }

                tracks.Add(new UnitTrack(
                    persistent.nameInternal.s,
                    keys,
                    Windowed(entry.Value.keyframeReveal, windowStart, windowEnd),
                    Windowed(entry.Value.keyframeHidden, windowStart, windowEnd)));

                // M8. Beside the transform track, never inside it: a turn whose
                // poses cannot travel still plays as M6 always did.
                var pose = CapturePoses(
                    unit, persistent.nameInternal.s, entry.Value.keyframesPoses, windowStart,
                    ref strandedKeys);
                if (pose == null)
                {
                    bonelessUnits++;
                }
                else
                {
                    poses.Add(pose);
                }

                if (tracks.Count == PbjMessageCodec.MaxTracksPerKeyframes)
                {
                    Debug.LogWarning(NetLog.KeyframesClamped(
                        CombatReplayHelper.units.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
                    ReportUncaptured(bonelessUnits, strandedKeys);
                    return new KeyframeCapture(windowStart, windowEnd, tracks, poses);
                }
            }

            if (clamped > 0)
            {
                Debug.LogWarning(NetLog.KeyframesClamped(
                    tracks.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
            }
            ReportUncaptured(bonelessUnits, strandedKeys);
            return new KeyframeCapture(windowStart, windowEnd, tracks, poses);
        }

        /// <summary>
        /// A recorded visibility stamp, if it belongs to this turn.
        /// </summary>
        /// <remarks>
        /// <c>ReplayUnit.keyframeReveal</c> and <c>keyframeHidden</c> are single
        /// slots, not lists, and nothing clears them between turns while
        /// <c>experimentalMode</c> is on — the same accumulation the transform
        /// and pose slices already work around. Sent unclipped, a unit that was
        /// revealed once on turn 2 would be re-revealed on every turn after it,
        /// which on a client looks like the unit blinking out at the start of
        /// every window for the rest of the fight.
        /// <para>
        /// Inclusive at both ends. A stamp exactly at the window start is this
        /// turn's — <c>OnExecutionStart</c> and the reveal it triggers share a
        /// frame — and one exactly at the end is the moment the snapshot was
        /// taken.
        /// </para>
        /// </remarks>
        private static float Windowed(ReplayKeyframe? stamp, float windowStart, float windowEnd)
        {
            if (stamp == null || stamp.time < windowStart || stamp.time > windowEnd)
            {
                return ReplayVisibility.None;
            }
            return stamp.time;
        }

        private static void ReportUncaptured(int bonelessUnits, int strandedKeys)
        {
            if (bonelessUnits > 0 || strandedKeys > 0)
            {
                Debug.LogWarning(NetLog.PosesNotCaptured(bonelessUnits, strandedKeys));
            }
        }

        /// <summary>
        /// One unit's skeletal track for this turn, or null when the host has
        /// no skeleton to describe.
        /// </summary>
        /// <remarks>
        /// The joint <i>names</i> come from the same <c>GetRecordedBones()</c>
        /// list the recorder walked to fill every key, so name <c>i</c> and
        /// value <c>i</c> are the same bone by construction rather than by
        /// agreement. The client rebuilds each key into its own bone order from
        /// those names, which is the only reason this travels at all — the
        /// game's playback loop indexes the joint array to the <i>receiving</i>
        /// machine's bone count with no length guard whatsoever.
        /// <para>
        /// No closing key is appended, unlike the transform track above. The
        /// recorder's own <c>OnExecutionEnd</c> has already added a pose at the
        /// window's end by the time this runs — it fires from
        /// <c>CombatUILinkSimulationEnd</c>, which sits ahead of the system this
        /// capture hangs off — and unlike a transform, a pose cannot be
        /// reconstructed from the ECS if it were missing.
        /// </para>
        /// </remarks>
        private static UnitPoseTrack? CapturePoses(
            CombatEntity unit,
            string? name,
            List<ReplayKeyframeUnitPose> recorded,
            float windowStart,
            ref int strandedKeys)
        {
            // Explicit null comparisons rather than ?., because a Unity object
            // that has been destroyed is only null through its own operator.
            var view = unit.hasCombatView ? unit.combatView.view : null;
            var visualManager = view != null ? view.visualManager : null;
            var bones = visualManager != null ? visualManager.GetRecordedBones() : null;
            if (bones == null || bones.Count == 0)
            {
                return null;
            }

            var joints = new string[bones.Count];
            for (var i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                joints[i] = bone != null ? bone.name : string.Empty;
            }

            // Sliced exactly as the transform track is, and for the same
            // reason: experimentalMode accumulates across the whole combat, so
            // an unsliced track grows without bound.
            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);

            var keys = new List<PoseKey>(recorded.Count - first);
            for (var i = first; i < recorded.Count; i++)
            {
                var key = recorded[i];

                // RecordUnitPose leaves joints null when a unit had no visual
                // manager at that instant, and a shorter or longer array is a
                // skeleton that was rebuilt part-way through the turn. Either
                // way the values no longer answer to the names above, so the
                // key is dropped rather than mismatched into place.
                if (key.joints == null || key.joints.Length != joints.Length)
                {
                    strandedKeys++;
                    continue;
                }

                var posed = new JointPose[joints.Length];
                for (var j = 0; j < joints.Length; j++)
                {
                    posed[j] = new JointPose(
                        ToVec3(key.joints[j].position), ToVec4(key.joints[j].rotation));
                }
                keys.Add(new PoseKey(
                    key.time, key.syncLeftEquipment, key.syncRightEquipment, posed));
            }

            return new UnitPoseTrack(name, joints, keys);
        }

        /// <summary>
        /// Where this turn's keys begin in an accumulated recorder list.
        /// </summary>
        /// <remarks>
        /// Two passes, and the second is not belt-and-braces. The backward scan
        /// is the obvious one: everything at or after <c>turnStartTime</c>
        /// belongs to this turn, and walking back from the end finds it without
        /// touching the whole accumulated combat.
        /// <para>
        /// But <c>turnStartTime</c> is the simulation clock <b>rounded to a
        /// whole second</b>, while the recorder stamps raw simulation time — so
        /// a turn that overruns the rounded boundary leaves the <i>previous</i>
        /// turn's closing keys satisfying that test too. Within a turn the
        /// stamps never decrease, so the last place they do decrease is exactly
        /// the seam between the two turns. Left unfixed, a track arrives
        /// non-monotonic in its middle and the unit jumps backwards mid-window.
        /// </para>
        /// <para>
        /// The remaining case is a boundary that rounds <i>up</i>, which would
        /// put this turn's opening stamp above its own first samples. Not
        /// observed — every measured turn has landed on an exact second — and
        /// not guessed at here.
        /// </para>
        /// </remarks>
        private static int TurnStart(int count, Func<int, float> timeAt, float windowStart)
        {
            var first = count;
            while (first > 0 && timeAt(first - 1) >= windowStart)
            {
                first--;
            }

            for (var i = first + 1; i < count; i++)
            {
                if (timeAt(i) < timeAt(i - 1))
                {
                    first = i;
                }
            }
            return first;
        }

        private static List<TransformKey> SliceTurn(
            List<ReplayKeyframeTransform> recorded, float windowStart)
        {
            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);

            var keys = new List<TransformKey>(recorded.Count - first + 1);
            for (var i = first; i < recorded.Count; i++)
            {
                keys.Add(new TransformKey(
                    recorded[i].time, ToVec3(recorded[i].position), ToVec4(recorded[i].rotation)));
            }
            return keys;
        }

        // Keeps the first and last key and thins what is between them, so a long
        // turn loses temporal resolution rather than its ending.
        private static List<TransformKey> Decimate(List<TransformKey> keys, int cap)
        {
            var kept = new List<TransformKey>(cap) { keys[0] };
            var interior = cap - 2;
            var step = (keys.Count - 2) / (double)interior;
            for (var i = 0; i < interior; i++)
            {
                kept.Add(keys[1 + (int)(i * step)]);
            }
            kept.Add(keys[keys.Count - 1]);
            return kept;
        }

        private static Vec3 ToVec3(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static Vec4 ToVec4(Quaternion q) => new Vec4(q.x, q.y, q.z, q.w);

        // Host-only bridge: a host never plays back, it simulates.
        public void PlayKeyframes(int turn, KeyframeCapture capture)
        {
            KeyframePlayer.Play(turn, capture);
        }

        public void StopKeyframes()
        {
            KeyframePlayer.Stop();
        }

        /// <summary>
        /// Puts this machine's mobile base where the host's is. M12a.
        /// </summary>
        /// <remarks>
        /// The game's own teleport recipe, cribbed from
        /// <c>ConsoleCommandsOverworld:893-901</c> and proven by <c>pbj.ow-mirror</c>
        /// during the recon rather than invented here. Every step earns its
        /// place, and the recipe is the whole reason this is not two lines:
        /// <list type="bullet">
        ///   <item><c>StopMovement</c> — or the client's own path fights the write.</item>
        ///   <item><c>ReplacePosition</c> — the authoritative value.</item>
        ///   <item><c>ReplacePositionTarget</c> — <b>not optional.</b>
        ///   <c>OverworldMovementSystem</c> drags position back toward a stale
        ///   target whenever the clock runs, so a mirror without it snaps back.</item>
        ///   <item><c>isPositionUnchecked</c> — hands the height to
        ///   <c>OverworldPositionValidationSystem</c>, which snaps to this
        ///   machine's own ground. That is why no Y crosses the wire.</item>
        ///   <item>A <b>same-value</b> <c>ReplaceSimulationTime</c> — Entitas
        ///   raises the replaced event with no value-equality short-circuit, so
        ///   this wakes every <c>SimulationTime</c> collector at a delta of zero.
        ///   <c>OverworldRangeSystem</c> is the one that matters: it copies
        ///   Position into PositionDetectedLast, which is what the renderer
        ///   actually draws.</item>
        /// </list>
        /// <b>Never write the host's time value here.</b> Roughly twenty systems
        /// collect on that component and a real delta would run all of them on a
        /// machine that is not simulating — the overworld cousin of the standing
        /// rule against advancing <c>combat.simulationTime</c> on a client.
        /// <para>
        /// In game state <c>basecrawler</c> the write lands and does not render,
        /// because the feeder above runs only in <c>overworld</c>. That is
        /// measured-correct, not a bug to work around: the position is already
        /// right when the player returns to the map.
        /// </para>
        /// </remarks>
        public void MirrorBase(float x, float z)
        {
            var playerBase = IDUtility.playerBaseOverworld;
            if (playerBase == null || !playerBase.hasPosition)
            {
                return;
            }

            // Keep our own Y. The snap below corrects it against local ground,
            // and starting from the current height means an unremarkable
            // correction rather than a fall from wherever the host stands.
            var target = new Vector3(x, playerBase.position.v.y, z);

            PhantomBrigade.Overworld.OverworldUtility.StopMovement(playerBase);
            playerBase.ReplacePosition(target);
            playerBase.ReplacePositionTarget(target);
            playerBase.isPositionUnchecked = true;

            var overworld = Contexts.sharedInstance.overworld;
            if (overworld.hasSimulationTime)
            {
                overworld.ReplaceSimulationTime(overworld.simulationTime.f);
            }
        }

        /// <summary>
        /// Loads the fight the host shipped. M12b.
        /// </summary>
        /// <remarks>
        /// Routed to <see cref="LoadGlue.BeginCombat"/> rather than
        /// <see cref="LoadGlue.Begin"/>, and the difference is not cosmetic: the
        /// campaign path checks the lobby catalogue, which deliberately excludes
        /// the scenario slot, so a fight sent through it returns Unavailable
        /// every single time and reads as a missing save rather than as wiring.
        /// </remarks>
        public LoadOutcome? BeginCombatLoad(string? saveName, string? digest)
        {
            return LoadGlue.BeginCombat(saveName, digest);
        }

        /// <summary>
        /// Writes the fight we have just entered, so it can be offered. M12b.
        /// </summary>
        /// <remarks>
        /// Only arms the write. The game refuses to save while the scenario intro
        /// runs, and raises that flag in the same tick that makes
        /// <see cref="InCombat"/> true, so <see cref="CombatShipGlue"/> polls from
        /// the next frame on and answers with <c>LocalCombatReadyEvent</c> when it
        /// has a save — or when it has given up on getting one.
        /// </remarks>
        public void ShipCombat()
        {
            CombatShipGlue.Arm();
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

        // --- scenario transfer (M9) ---
        //
        // The save directory the game itself writes: SavedGames/<name>/ holding
        // content.zip and metadata.yaml. Resolved through the game's own
        // DataManagerSave.GetSaveFolderPath rather than a composed path, so this
        // works unchanged on Windows and under Proton, where the same logical
        // folder lives somewhere quite different.

        public ScenarioPayload ReadScenario(string? saveKey)
        {
            try
            {
                var folder = SaveFolder(saveKey);
                if (folder == null || !Directory.Exists(folder))
                {
                    return ScenarioPayload.None;
                }

                // Content is split into parts only when it has to be — M11e. Every
                // save measured is far under one part, so the common case still
                // sends a single content.zip exactly as M9 did. Splitting here
                // rather than at the session keeps the wire-size decision in one
                // place: PbjWriter throws on an oversize blob and PbjRuntime.SendTo
                // does not guard encoding, so nothing above may hand it one.
                var files = new List<ScenarioFile>();
                var contentPath = Path.Combine(folder, ScenarioPayload.ContentFileName);
                if (File.Exists(contentPath))
                {
                    files.AddRange(ScenarioPayload.SplitContent(File.ReadAllBytes(contentPath)));
                }

                var metadataPath = Path.Combine(folder, ScenarioPayload.MetadataFileName);
                if (File.Exists(metadataPath))
                {
                    files.Add(new ScenarioFile(
                        ScenarioPayload.MetadataFileName, File.ReadAllBytes(metadataPath)));
                }

                // A partial directory is handed over as-is rather than patched
                // up here: ScenarioPayload.Inspect is the single place that
                // decides what is sendable, and duplicating that judgement in the
                // glue is how the two drift apart.
                return files.Count == 0
                    ? ScenarioPayload.None
                    : new ScenarioPayload(saveKey, files);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not read the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                return ScenarioPayload.None;
            }
        }

        public bool WriteScenario(ScenarioPayload payload)
        {
            // The destination now travels with the payload — M11e. SaveFolder
            // refuses anything outside the namespace, so a forged key fails here
            // rather than composing a path.
            var folder = SaveFolder(payload.SaveName);
            if (folder == null)
            {
                Debug.LogWarning("[pb-and-j] no writable save folder for '"
                    + payload.SaveName + "' — cannot write the save");
                return false;
            }

            // Staged beside the destination and moved into place, so an
            // interrupted or failed write cannot leave a half-save for
            // pbj.combat-load to find and try to enter.
            var staging = folder + ".pbj-incoming";
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);

                // Split content is reassembled here, never written out as parts:
                // the parts are a wire concern and the game must find the ordinary
                // content.zip it wrote. JoinContent orders by part index rather
                // than by arrival, because the digest is order-independent and
                // nothing promises the wire preserved file order.
                for (var i = 0; i < payload.Files.Count; i++)
                {
                    var file = payload.Files[i];
                    // Belt and braces. The session already refused anything that
                    // is not allowlisted, but this is the statement that actually
                    // composes a path, so it is the one that has to be safe on
                    // its own terms.
                    if (!ScenarioPayload.IsAllowedName(file.Name))
                    {
                        Debug.LogWarning("[pb-and-j] refusing to write scenario file '"
                            + file.Name + "' — not an allowed name");
                        Directory.Delete(staging, true);
                        return false;
                    }
                    if (ScenarioPayload.PartIndex(file.Name) >= 0)
                    {
                        continue;
                    }
                    if (string.Equals(file.Name, ScenarioPayload.ContentFileName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    File.WriteAllBytes(Path.Combine(staging, file.Name), file.Content);
                }

                File.WriteAllBytes(
                    Path.Combine(staging, ScenarioPayload.ContentFileName),
                    ScenarioPayload.JoinContent(payload));

                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
                Directory.Move(staging, folder);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[pb-and-j] could not write the combat save: "
                    + e.GetType().Name + ": " + e.Message);
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }
                catch (Exception cleanup)
                {
                    Debug.LogWarning("[pb-and-j] could not clean up '" + staging + "': "
                        + cleanup.GetType().Name);
                }
                return false;
            }
        }

        /// <summary>
        /// Starts loading a campaign save. M11d.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="LoadGlue"/>, which owns the pre-checks and the
        /// completion callback. Kept out of this class because the bridge is
        /// otherwise all ECS reads and writes, and a load is neither — it tears
        /// the ECS down and builds a new one.
        /// </remarks>
        public LoadOutcome? BeginLoad(string? saveKey, int selectionVersion, string? saveDigest) =>
            LoadGlue.Begin(saveKey, selectionVersion, saveDigest);

        /// <summary>
        /// Where this save lives, from the game's own path resolution. The
        /// directory name is always ours — never the one on the wire.
        /// </summary>
        /// <summary>
        /// Where a save lives, from the game's own path resolution.
        /// </summary>
        /// <remarks>
        /// <b>The one statement in the mod that turns a wire-supplied name into a
        /// path</b>, so the guard is here and not only at the caller. M9 passed a
        /// constant and needed no check; M11e carries the lobby's key, and
        /// <see cref="ScenarioPayload.IsAllowedDestination"/> is what stands between
        /// that and a <c>Path.Combine</c>. Refusing here rather than trusting the
        /// session keeps this safe on its own terms — the session checking first is
        /// defence in depth, not a substitute.
        /// </remarks>
        private static string? SaveFolder(string? saveKey)
        {
            if (!ScenarioPayload.IsAllowedDestination(saveKey))
            {
                Debug.LogWarning("[pb-and-j] refusing to resolve a save folder for '"
                    + saveKey + "' — not an allowed destination");
                return null;
            }

            var root = DataManagerSave.GetSaveFolderPath(SaveLocation.Normal);
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, saveKey);
        }
    }
}
