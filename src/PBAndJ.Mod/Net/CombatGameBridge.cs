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

        /// <summary>
        /// Reaction pings, melee swings and dropped swings sent this turn.
        /// </summary>
        /// <remarks>
        /// Positive counters, deliberately, and stage B is why: weapon lights
        /// shipped with loss counters only, and all-zero losses read identically
        /// whether every flash travelled or the light code never ran at all. A
        /// number that goes up is the only thing a playtest can falsify.
        /// </remarks>
        internal static int AssetReactionsSent;

        internal static int AssetMeleesSent;

        internal static int AssetMeleesDropped;

        private static readonly List<float> NoReactionPings = new List<float>();

        private static readonly List<MeleeTrajectory> NoMelees = new List<MeleeTrajectory>();

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
                // M16. Presence travels beside the value, because the two
                // machines take different paths into combat and only one of them
                // strips the component — see FrameIntegrityDrive.Present. Before
                // M16 this captured a bare 0f for the host's whole player squad
                // and the client wrote it as a real value.
                var hasIntegrity = persistent.hasUnitFrameIntegrity;
                var integrity = hasIntegrity ? persistent.unitFrameIntegrity.f : 0f;

                // Walked once and used twice — the set itself travels, and the
                // unit's wreck moment is derived from it below.
                var wrecked = WreckedPartsOf(persistent);

                units.Add(new UnitSnapshot(
                    persistent.nameInternal.s,
                    new Vec3(position.x, position.y, position.z),
                    new Vec4(rotation.x, rotation.y, rotation.z, rotation.w),
                    new Vec3(facing.x, facing.y, facing.z),
                    integrity,
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
                    unit.hasArrivalTime ? unit.arrivalTime.f : 0f,
                    // M15 §3.1. The unit's own wreck, which is a different fact
                    // from every part being wrecked and from integrity reaching
                    // zero — only this one draws the explosion.
                    persistent.isWrecked,
                    WreckMomentOf(wrecked),
                    // M15 §3.2. The live wrecked set, not this turn's additions
                    // — see UnitSnapshot.WreckedParts for why the difference is
                    // the design rather than a convenience.
                    wrecked,
                    // M16. Every part, not only the damaged ones: combat setup
                    // seeds each part's integrity from the unit's pre-combat
                    // frame integrity, so "absent means pristine" would be wrong.
                    PartStatesOf(persistent),
                    hasIntegrity));

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

        /// <summary>
        /// The parts this unit currently has wrecked, and when each went. M15.
        /// </summary>
        /// <remarks>
        /// Walks the live equipment set exactly as the game's own replay does
        /// (<c>CombatReplayHelper.ApplyTimeToUnit:1289-1297</c>), including its
        /// <c>hasDestructionTime ? f : 0f</c> default.
        /// <para>
        /// ⚠️ Deliberately <b>not</b> read from <c>ReplayUnit.keyframesDestructions</c>,
        /// which looks purpose-built and is a trap twice over: it is written at
        /// <c>CombatReplayHelper.cs:1914</c> and read nowhere in the shipped
        /// game, and its recorder attributes a dependency-wrecked part to the
        /// part that triggered it rather than to itself
        /// (<c>EquipmentUtility.cs:3116-3117</c> and <c>:3126-3128</c> both pass
        /// <c>partHit</c>).
        /// </para>
        /// <para>
        /// Unordered, because <c>GetPartsInUnit</c> returns a set and the
        /// receiver joins on socket rather than on index. Sorting would buy
        /// byte-stable frames for identical state and nothing else.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<PartDestruction> WreckedPartsOf(PersistentEntity persistent)
        {
            List<PartDestruction>? wrecked = null;
            foreach (var part in EquipmentUtility.GetPartsInUnit(persistent))
            {
                if (part == null || !part.isWrecked || !part.hasPartParentUnit)
                {
                    continue;
                }

                var socket = part.partParentUnit.socket;
                if (string.IsNullOrEmpty(socket))
                {
                    continue;
                }

                wrecked ??= new List<PartDestruction>(4);
                wrecked.Add(new PartDestruction(
                    socket, part.hasDestructionTime ? part.destructionTime.f : 0f));
            }
            return (IReadOnlyList<PartDestruction>?)wrecked ?? NoWreckedParts;
        }

        private static readonly PartDestruction[] NoWreckedParts = new PartDestruction[0];

        /// <summary>
        /// Every part of this unit and how damaged it is. M16.
        /// </summary>
        /// <remarks>
        /// A second walk of the same set <see cref="WreckedPartsOf"/> takes,
        /// rather than one walk producing both. It runs once per unit per turn
        /// against a blueprint-bounded part count, so the saving is not worth the
        /// coupling — and the two lists answer questions that will diverge, since
        /// the wrecked set is destined to keep its destruction stamps while this
        /// one is a plain state mirror.
        /// <para>
        /// A part with no socket is dropped here rather than sent and skipped on
        /// the far side. The socket is the only join key; without it the record
        /// can address nothing.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<PartState> PartStatesOf(PersistentEntity persistent)
        {
            List<PartState>? states = null;
            foreach (var part in EquipmentUtility.GetPartsInUnit(persistent))
            {
                if (part == null || !part.hasPartParentUnit)
                {
                    continue;
                }

                var socket = part.partParentUnit.socket;
                if (string.IsNullOrEmpty(socket))
                {
                    continue;
                }

                // Both components are read defensively because the game itself
                // does: GetPartIntegrity checks hasIntegrityNormalized before
                // reading it (CombatReplayHelper.cs:1823-1827), and a part
                // created outside the ordinary path can reach here without one.
                states ??= new List<PartState>(8);
                states.Add(new PartState(
                    socket,
                    part.hasIntegrityNormalized ? part.integrityNormalized.f : 1f,
                    part.hasBarrierNormalized ? part.barrierNormalized.f : 1f));
            }
            return (IReadOnlyList<PartState>?)states ?? NoPartStates;
        }

        private static readonly PartState[] NoPartStates = new PartState[0];

        /// <summary>
        /// When this unit was wrecked, derived from its parts. M15 §3.1.
        /// </summary>
        /// <remarks>
        /// The game keeps no unit-level destruction time — <c>crumpleTime</c>
        /// comes closest and is written only for units that have both a mech
        /// animation view and a puppet view, so every tank lacks it. The newest
        /// part stamp is exact rather than approximate, and the reason is in the
        /// damage resolution itself: wrecking a unit wrecks <b>every part it
        /// still has</b>, at one instant, in the same loop that sets the flag
        /// (<c>EquipmentUtility.cs:3247-3255</c>). So the newest stamp is that
        /// instant. A unit that reached the end with everything already gone was
        /// wrecked when the last part went, which is the same number again.
        /// <para>
        /// Falls back to <b>negative</b>, not zero, when there is nothing to
        /// derive from. Zero is a real instant on the host's clock — the very
        /// start of the fight — and would make a client hold the wreck for a
        /// window boundary it had already passed. Negative is the established
        /// "no moment to wait for" convention here, and it makes the client play
        /// the wreck at once, which is the right answer for a unit whose moment
        /// nobody can name.
        /// </para>
        /// </remarks>
        private static float WreckMomentOf(IReadOnlyList<PartDestruction> wrecked)
        {
            var newest = float.NegativeInfinity;
            for (var i = 0; i < wrecked.Count; i++)
            {
                if (wrecked[i].Time > newest)
                {
                    newest = wrecked[i].Time;
                }
            }
            return newest > float.NegativeInfinity ? newest : -100f;
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

                // M16 moved unitFrameIntegrity out of this loop. It is no longer
                // a value to write but a presence to mirror, and the removal case
                // reaches units this loop cannot — so the whole field is owned by
                // ReceivePartIntegrity below, in one place.
                byName.Remove(persistent.nameInternal.s);
            }

            // M15. After the per-unit writes, and outside the loop, because the
            // set it settles spans units the loop above may never have reached —
            // a unit destroyed before this turn has no track and may have no
            // entry here either, and it is exactly the unit whose parts need
            // putting right.
            KeyframePlayer.ReceiveDestruction(snapshot);

            // M16, and AFTER the destruction settle rather than before it. M15's
            // settle writes a synthesised 0f or 1f to the visual for a part it is
            // wrecking or reviving (KeyframePlayer.cs:2346); once real values are
            // on the wire that is a placeholder, so it must be overwritten rather
            // than allowed to overwrite. For a wrecked part the two agree at 0.
            KeyframePlayer.ReceivePartIntegrity(snapshot);

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

            AssetReactionsSent = 0;
            AssetMeleesSent = 0;
            AssetMeleesDropped = 0;

            // M14. Taken from the same window the unit tracks are, before the
            // loop, so the two cannot disagree about where the turn began — the
            // whole reason both live on one KeyframeCapture.
            var assets = CaptureAssets(windowStart, windowEnd);

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
                    unit, persistent.nameInternal.s, entry.Value, windowStart, windowEnd,
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
                    return new KeyframeCapture(windowStart, windowEnd, tracks, poses, assets);
                }
            }

            if (clamped > 0)
            {
                Debug.LogWarning(NetLog.KeyframesClamped(
                    tracks.Count, PbjMessageCodec.MaxTracksPerKeyframes, clamped));
            }
            ReportUncaptured(bonelessUnits, strandedKeys);
            ReportOrphanedLights(poses);

            // Positive first, and only when there is something to say — a quiet
            // turn is not an incomplete one, which is the distinction stage A
            // had to add AssetsNoneSent to make.
            if (AssetReactionsSent > 0 || AssetMeleesSent > 0)
            {
                Debug.Log(NetLog.AssetReactionsAndMeleesSent(
                    AssetReactionsSent, AssetMeleesSent));
            }

            if (AssetMeleesDropped > 0)
            {
                Debug.LogWarning(NetLog.MeleesOverCap(AssetMeleesDropped));
            }
            WeaponLightPatches.Clear();
            return new KeyframeCapture(windowStart, windowEnd, tracks, poses, assets);
        }

        /// <summary>
        /// Names the flashes that fired but found no pose track to ride.
        /// </summary>
        /// <remarks>
        /// The one cost of joining lights to poses, made loud instead of silent.
        /// A unit the recorder gave no bones — or one clamped off the end of the
        /// track list — drops its flashes with it, and a missing muzzle flash
        /// among muzzle flashes is invisible on screen. This project has learned
        /// twice that a loss only a count can see has to be counted.
        /// <para>
        /// Deliberately walks the captured cache rather than the pose list: the
        /// loss is a unit that is <b>not</b> in the collection the harvest
        /// walked, so it cannot be found by looking at what was harvested.
        /// </para>
        /// </remarks>
        private static void ReportOrphanedLights(List<UnitPoseTrack> poses)
        {
            var carried = 0;
            for (var i = 0; i < poses.Count; i++)
            {
                carried += poses[i].Lights.Count;
            }

            var fired = 0;
            var firedUnits = 0;
            foreach (var pair in WeaponLightPatches.All())
            {
                fired += pair.Value.Count;
                firedUnits++;
            }

            // The positive count first, and unconditionally when anything fired.
            // Without it a playtest reading zero losses cannot tell "every flash
            // travelled" from "no light code ran at all".
            if (carried > 0)
            {
                var units = 0;
                for (var i = 0; i < poses.Count; i++)
                {
                    if (poses[i].Lights.Count > 0)
                    {
                        units++;
                    }
                }
                Debug.Log(NetLog.AssetLightsSent(units, carried));
            }

            var orphaned = fired - carried;
            if (orphaned > 0)
            {
                // Units, not flashes, is the honest denominator for the first
                // number: we know how many flashes were stranded but not how
                // many distinct units stranded them, so the unit count is the
                // upper bound and is reported as such.
                Debug.LogWarning(NetLog.LightsWithoutPoseTrack(firedUnits, orphaned));
            }

            if (WeaponLightPatches.SkippedNoTransform > 0)
            {
                Debug.Log(NetLog.LightsUnusable(WeaponLightPatches.SkippedNoTransform));
            }
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
            ReplayUnit track,
            float windowStart,
            float windowEnd,
            ref int strandedKeys)
        {
            var recorded = track.keyframesPoses;
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

            // The flashes this unit fired, resolved at fire time by
            // WeaponLightPatches and merely collected here. They ride the pose
            // track because a light is meaningless without the unit whose
            // UnitLightManager owns the Light it drives — the same join key
            // serves both — and because the game hangs keyframesLightsWeapons
            // off ReplayUnit for that reason too.
            //
            // Note this returns before here on a boneless unit, which is exactly
            // the orphan case CaptureKeyframes reports: those flashes have no
            // ride and would otherwise vanish without a word.
            var lights = WeaponLightPatches.For(unit.id.id);

            return new UnitPoseTrack(
                name,
                joints,
                keys,
                lights,
                CaptureReactions(track, windowStart),
                CaptureMelees(track, windowStart, windowEnd));
        }

        /// <summary>
        /// This unit's reaction-glow pings inside the turn. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Bare stamps — the recorder's reaction keyframe carries a time and
        /// nothing else — sliced from the window start exactly as the pose keys
        /// above are, and for the identical reason: <c>units</c> MAY not be
        /// cleared between turns, so this list may hold the whole combat's
        /// pings. The slice is written to be correct either way rather than to
        /// branch on the setting, per note 2 on <c>CaptureKeyframes</c>.
        /// <para>
        /// The cap drops the <i>oldest</i>. Only the newest ping at or before
        /// the cursor can ever animate, so trimming the front is invisible while
        /// trimming the back would throw away the live one.
        /// </para>
        /// </remarks>
        private static List<float> CaptureReactions(ReplayUnit track, float windowStart)
        {
            var recorded = track.keyframesLightsReactions;
            if (recorded == null)
            {
                return NoReactionPings;
            }

            var first = TurnStart(recorded.Count, i => recorded[i].time, windowStart);
            if (recorded.Count - first > PbjMessageCodec.MaxReactionPingsPerUnit)
            {
                first = recorded.Count - PbjMessageCodec.MaxReactionPingsPerUnit;
            }

            var pings = new List<float>(recorded.Count - first);
            for (var i = first; i < recorded.Count; i++)
            {
                pings.Add(recorded[i].time);
            }
            AssetReactionsSent += pings.Count;
            return pings;
        }

        /// <summary>
        /// This unit's melee swings whose windows touch the turn. M14 stage C.
        /// </summary>
        /// <remarks>
        /// Sliced by interval <em>overlap</em>, not by a start-time point test:
        /// a swing straddling the boundary has to arrive in both windows, or the
        /// client shows the second half of a shockwave with no first half and
        /// then never clears it.
        /// <para>
        /// ⚠️ The slice is what makes the cap safe. With the recorder retaining
        /// tracks between turns, <c>entitiesMelee</c> holds every swing of the
        /// fight, so capping the raw list would drop this unit's whole track a
        /// few turns in — silently, and reading exactly like a turn with no
        /// melee in it.
        /// </para>
        /// </remarks>
        private static List<MeleeTrajectory> CaptureMelees(
            ReplayUnit track, float windowStart, float windowEnd)
        {
            var recorded = track.entitiesMelee;
            if (recorded == null)
            {
                return NoMelees;
            }

            var melees = new List<MeleeTrajectory>();
            for (var i = 0; i < recorded.Count; i++)
            {
                var swing = recorded[i];
                if (!ReplayAssetPlayback.OverlapsWindow(
                        swing.timeStart, swing.timeEnd, windowStart, windowEnd))
                {
                    continue;
                }

                melees.Add(new MeleeTrajectory(
                    swing.timeStart,
                    swing.timeEnd,
                    swing.partUsed,
                    swing.shockwaveKey,
                    ToVec3(swing.posStart),
                    ToVec3(swing.posEnd)));
            }

            // Oldest first out, as with the pings: a swing that started earlier
            // is the one nearer to being over.
            while (melees.Count > PbjMessageCodec.MaxMeleesPerUnit)
            {
                melees.RemoveAt(0);
                AssetMeleesDropped++;
            }

            AssetMeleesSent += melees.Count;
            return melees;
        }

        /// <summary>
        /// One turn's projectiles, beams and one-shot effects. M14.
        /// </summary>
        /// <remarks>
        /// Sliced by window overlap and never sent whole, and the slice must not
        /// branch on <c>experimentalMode</c>. That player setting gates the
        /// game's own prune (<c>CombatReplayHelper.cs:241</c>), so on a default
        /// machine <b>nothing is ever pruned</b> and these collections still hold
        /// every effect of the fight at turn twenty — one measured fight grew
        /// <c>assetsStandalone</c> from 51 to 727 across five turns. The slice
        /// has to be correct whichever way the setting sits, so it reads the
        /// times rather than trusting the collection.
        /// <para>
        /// No <c>IsRecordingAllowed</c> gate and no unit lookup, for the reason
        /// <see cref="CaptureKeyframes"/> gives: the flag is already false by the
        /// time this runs. Unlike the unit tracks, nothing here is keyed to a
        /// living entity — which is why a turn that kills everyone can still
        /// record a great deal, and why the host says so rather than discarding
        /// it silently.
        /// </para>
        /// </remarks>
        private static AssetCapture CaptureAssets(float windowStart, float windowEnd)
        {
            var standalone = new List<StandaloneAssetTrack>();
            var projectiles = new List<ProjectileAssetTrack>();
            var beams = new List<BeamAssetTrack>();
            var trailed = 0;
            var trailPoints = 0;
            var trailOverCap = 0;

            var recorded = CombatReplayHelper.assetsStandalone;
            if (recorded != null)
            {
                for (var i = 0; i < recorded.Count; i++)
                {
                    var entry = recorded[i];
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }

                    // The id is this list's position in THIS capture, not the
                    // game's — these have no identity of their own, and the
                    // recorder's own list is pruned by index so its positions
                    // shift between turns. A within-turn label, nothing more.
                    standalone.Add(new StandaloneAssetTrack(
                        standalone.Count,
                        Head(entry),
                        ToVec3(entry.position),
                        ToVec4(entry.rotation),
                        // Load-bearing: AssignAsset writes this straight to
                        // localScale, so a lost scale is an invisible effect.
                        ToVec3(entry.scale),
                        ToVec4(entry.velocityAndDecay),
                        ToVec3(entry.positionLocal)));
                }
            }

            if (CombatReplayHelper.assetsProjectiles != null)
            {
                foreach (var pair in CombatReplayHelper.assetsProjectiles)
                {
                    var entry = pair.Value;
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }
                    var keys = new List<TransformKey>();
                    var recordedKeys = entry.keyframesTransform;
                    WindowRange(
                        recordedKeys.Count, i => recordedKeys[i].time, windowStart, windowEnd,
                        out var first, out var last);
                    for (var i = first; i <= last; i++)
                    {
                        keys.Add(new TransformKey(
                            recordedKeys[i].time,
                            ToVec3(recordedKeys[i].position),
                            ToVec4(recordedKeys[i].rotation)));
                    }

                    var trail = SliceTrail(entry.keyframesTrail, windowStart, windowEnd);
                    if (trail.Count > 0)
                    {
                        trailed++;
                        trailPoints += trail.Count;

                        // Counted here rather than where the thinning happens,
                        // because this is the last place the pre-cap length
                        // exists. TryPrepare only ever sees the result.
                        if (trail.Count > PbjMessageCodec.MaxTrailPointsPerTrack)
                        {
                            trailOverCap++;
                        }
                    }

                    projectiles.Add(new ProjectileAssetTrack(
                        pair.Key, Head(entry), ToVec3(entry.scale), keys, trail));
                }
            }

            if (CombatReplayHelper.assetsBeams != null)
            {
                foreach (var pair in CombatReplayHelper.assetsBeams)
                {
                    var entry = pair.Value;
                    if (entry == null || !InWindow(entry, windowStart, windowEnd))
                    {
                        continue;
                    }

                    var keys = new List<BeamKey>();
                    var recordedKeys = entry.keyframes;
                    WindowRange(
                        recordedKeys.Count, i => recordedKeys[i].time, windowStart, windowEnd,
                        out var first, out var last);
                    for (var i = first; i <= last; i++)
                    {
                        keys.Add(new BeamKey(
                            recordedKeys[i].time,
                            ToVec3(recordedKeys[i].position),
                            ToVec4(recordedKeys[i].rotation),
                            ToVec3(recordedKeys[i].parameters)));
                    }

                    beams.Add(new BeamAssetTrack(pair.Key, Head(entry), keys));
                }
            }

            if (trailed > 0)
            {
                // Points per turn is the number MaxTrailPointsPerTrack is sized
                // against, and the only one that would show a weapon far heavier
                // than the ~32-points-per-trail this was measured on.
                Debug.Log(NetLog.AssetTrailsSent(trailed, trailPoints, trailOverCap));
            }

            return new AssetCapture(standalone, projectiles, beams);
        }

        /// <summary>
        /// The trail points alive at any moment of the window, in emission order.
        /// </summary>
        /// <remarks>
        /// <b>Interval overlap, never the point test the transform keys use.</b>
        /// A trail point's <c>timeStart</c>/<c>timeEnd</c> are on an absolute
        /// clock of their own, and a point emitted before the window opens is
        /// still part of the visible ribbon until its life runs out. Slicing on
        /// <c>timeStart</c> with <see cref="WindowRange"/> would drop exactly
        /// those, and the trail would pop in bald at every turn boundary on any
        /// projectile that spans two turns — a loss no count and no log would
        /// show.
        /// <para>
        /// <see cref="ReplayAssetPlayback.OverlapsWindow"/> is the predicate,
        /// reused rather than restated: stage A wrote it under the coverage gate
        /// for track activation and it generalises here unchanged.
        /// </para>
        /// <para>
        /// No bracketing key on either side, unlike the transform slice. Trail
        /// points are not interpolated between — the game rebuilds each one into
        /// an <c>AraTrail.Point</c> and hands the list over as a polyline — so a
        /// neighbour outside the window is simply a point that should not be
        /// drawn.
        /// </para>
        /// </remarks>
        private static List<TrailKey> SliceTrail(
            List<ReplayKeyframeTrailPoint>? recorded, float windowStart, float windowEnd)
        {
            var trail = new List<TrailKey>();
            if (recorded == null)
            {
                return trail;
            }

            for (var i = 0; i < recorded.Count; i++)
            {
                var point = recorded[i];
                if (!ReplayAssetPlayback.OverlapsWindow(
                        point.timeStart, point.timeEnd, windowStart, windowEnd))
                {
                    continue;
                }

                trail.Add(new TrailKey(
                    point.timeStart,
                    point.timeEnd,
                    ToVec3(point.position),
                    ToVec3(point.velocity),
                    ToVec3(point.perlinDirection),
                    ToVec3(point.tangent),
                    ToVec3(point.normal),
                    ToVec4(point.color),
                    point.thickness,
                    point.texcoord));
            }

            return trail;
        }

        private static bool InWindow(ReplayEntityAsset entry, float windowStart, float windowEnd) =>
            ReplayAssetPlayback.OverlapsWindow(
                entry.timeStart, entry.timeEnd, windowStart, windowEnd);

        /// <summary>
        /// The head every asset track shares, both optional blocks included.
        /// </summary>
        /// <remarks>
        /// <c>assetKeyHash</c> is deliberately not read. It is
        /// <c>string.GetHashCode</c>, which carries no cross-process stability
        /// guarantee, so a hash minted here would match nothing on a client —
        /// silently, since a failed lookup simply shows no effect. The key
        /// travels as the string it is.
        /// </remarks>
        private static AssetTrackHead Head(ReplayEntityAsset entry)
        {
            // Null is a real value in both, and not the same as zero: an absent
            // hue means the effect keeps its prefab's own, where a hue of zero
            // is an instruction to flatten it.
            var hue = entry.assetHueOffset != null ? entry.assetHueOffset.f : (float?)null;
            var colour = entry.assetColorOverride != null
                ? new AssetColour(
                    ToVec4(entry.assetColorOverride.colorFrom),
                    ToVec4(entry.assetColorOverride.colorTo))
                : (AssetColour?)null;

            return new AssetTrackHead(
                entry.assetKey, entry.timeStart, entry.timeEnd, hue, colour);
        }

        /// <summary>
        /// The keys of one asset track that this turn's window needs, with the
        /// bracketing key on each side.
        /// </summary>
        /// <remarks>
        /// The brackets are not padding. <c>ReplayEntityAssetProjectile.ApplyTime</c>
        /// interpolates between the pair of keys straddling the requested time
        /// on an <b>absolute</b> clock, so a slice cut flush to the window leaves
        /// the cursor's first frames with nothing before them and the projectile
        /// snaps to its first in-window key instead of arriving at it. Same for
        /// the far end.
        /// <para>
        /// An empty range is returned as <c>first = 0, last = -1</c>, so the
        /// caller's loop adds nothing and the track goes out with no keys — where
        /// <c>ReplayAssetParts.TryPrepare</c> drops it as
        /// <see cref="AssetTrackFault.TooFewKeys"/>. That is the right outcome
        /// and the right place for it: a track with nothing in this window has
        /// nothing to draw, and sending it would put a frozen instance at
        /// <c>keyframes[0]</c> or at the world origin.
        /// </para>
        /// </remarks>
        private static void WindowRange(
            int count,
            Func<int, float> timeAt,
            float windowStart,
            float windowEnd,
            out int first,
            out int last)
        {
            first = count;
            last = -1;
            for (var i = 0; i < count; i++)
            {
                var time = timeAt(i);
                if (time < windowStart || time > windowEnd)
                {
                    continue;
                }
                if (i < first)
                {
                    first = i;
                }
                last = i;
            }

            if (last < 0)
            {
                first = 0;
                return;
            }
            if (first > 0)
            {
                first--;
            }
            if (last < count - 1)
            {
                last++;
            }
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

        private static Vec4 ToVec4(Vector4 v) => new Vec4(v.x, v.y, v.z, v.w);

        private static Vec4 ToVec4(Color c) => new Vec4(c.r, c.g, c.b, c.a);

        // Host-only bridge: a host never plays back, it simulates.
        public void PlayKeyframes(int turn, KeyframeCapture capture)
        {
            // Kept before playing, so a client can replay what it was told to
            // play. Otherwise pbj.replay-last is host-only and the one machine
            // whose playback is worth inspecting twice is the one that cannot.
            NetGlue.RememberPlayed(turn, capture);
            KeyframePlayer.Play(turn, capture);
        }

        public void StopKeyframes()
        {
            KeyframePlayer.Stop();

            // M16, and BEFORE the clear below discards what it is holding. This
            // is the third settle path and it is not optional: SettleWindow has
            // one call site, on a window's natural finish, so a turn whose
            // keyframes never arrived — the mutual-destruction ending, where the
            // host legitimately sends none — has no window to settle and, here at
            // combat end, no next snapshot either. Without this the final turn's
            // damage would be discarded in exactly the fight that produced most
            // of it.
            KeyframePlayer.SettlePartIntegrity();

            // M15, and only here rather than in Stop itself. Every emitter of
            // this effect means the fight is over for this client — CombatEnd,
            // Bye, or a fault — while Stop also runs between the turns of a live
            // fight, where discarding the wrecked set would put every blown-off
            // limb back on during the planning phase.
            KeyframePlayer.ClearDestruction();
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
