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
    // Writing a received snapshot INTO the ECS, and the three things that have to
    // happen alongside the transform write: visibility, the landing data a dropped
    // unit must lose, and the arrival time.
    //
    // Safe only because a client never sets combat.Simulating -- see the comment
    // above ApplySnapshot, which is the whole licence for writing transforms here.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
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

                // M17 stage 2. The pilot's three facts, which IsUnitActive asks
                // of a DIFFERENT entity from the one this loop is holding.
                ApplyPilot(persistent, state);

                // M16 moved unitFrameIntegrity out of this loop. It is no longer
                // a value to write but a presence to mirror, and the removal case
                // reaches units this loop cannot — so the whole field is owned by
                // ReceivePartIntegrity below, in one place.
                byName.Remove(persistent.nameInternal.s);
            }

            FlushPilotState();

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
        /// Whether any pilot state moved this snapshot, for the one redraw it owes.
        /// </summary>
        private static bool pilotStatePending;

        /// <summary>
        /// Mirrors the host's three pilot facts onto the pilot entity. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>The components directly, never the helpers.</b>
        /// <c>PilotUtility.DeclareDeath</c> would additionally fire
        /// <c>OnPilotEvent</c>, zero two pilot stats and run combat-state focus
        /// events — the same class of cascade M17 stage 2 exists to avoid — and
        /// <c>CombatUtilities.ForceEjection</c> is worse. These are three
        /// component writes and nothing else.
        /// <para>
        /// <b>Both directions, because the wire carries state and not events.</b>
        /// A pilot who stops being dead on the host stops being dead here. That
        /// removal does wake one collector — <c>OverworldPilotUILinkSystem</c>
        /// takes <c>DeathStatus</c> in an <c>AnyOf(...).AddedOrRemoved()</c>, and
        /// it is <i>not</i> dormant during combat because
        /// <c>OverworldSystemsPermanent</c> is registered
        /// <c>SetAlwaysActiveAfterStack</c>. It is harmless only because its
        /// whole <c>Execute</c> is four <c>IsEntered()</c>-guarded base-view
        /// refreshes, none of which is entered during a fight. ⚠️ <b>That
        /// inertness ends the day any milestone shows a base view over combat.</b>
        /// </para>
        /// <para>
        /// ⚠️ <b>The death TIME is deliberately not carried, and is written as
        /// zero.</b> It is the host's number on the host's clock, it is
        /// <b>serialized</b> (<c>DataHelperSaveSerialization</c>), and a client
        /// that invented one would write a different number into its own
        /// checkpoint. Nothing reads it during a fight — the two views that do
        /// are the extended pilot info and the overworld debriefing. Same
        /// decision, and the same reasoning, as <c>unitFrameDefects</c>.
        /// </para>
        /// </remarks>
        private static void ApplyPilot(PersistentEntity unit, UnitSnapshot state)
        {
            var pilot = PilotOf(unit);
            if (pilot == null)
            {
                // Ordinary, not an error: an uncrewed unit has no pilot link at
                // all, and IsUnitActive's own uncrewed early-out returns true
                // before it asks any of these questions.
                return;
            }

            if (state.PilotDead)
            {
                if (!pilot.hasDeathStatus)
                {
                    pilot.AddDeathStatus(0f, state.PilotDeathCause);
                    pilotStatePending = true;
                }
                else if (!string.Equals(
                    pilot.deathStatus.cause, state.PilotDeathCause, StringComparison.Ordinal))
                {
                    pilot.ReplaceDeathStatus(0f, state.PilotDeathCause);
                }
            }
            else if (pilot.hasDeathStatus)
            {
                pilot.RemoveDeathStatus();
                pilotStatePending = true;
            }

            // Flag setters early-return on an unchanged value, so the steady
            // state is two comparisons per unit per snapshot and no collector
            // can re-fire on a no-op.
            if (pilot.isKnockedOut != state.PilotKnockedOut)
            {
                pilot.isKnockedOut = state.PilotKnockedOut;
                pilotStatePending = true;
            }
            if (pilot.isEjected != state.PilotEjected)
            {
                pilot.isEjected = state.PilotEjected;
                pilotStatePending = true;
            }
        }

        /// <summary>
        /// The one redraw a pilot change owes, once per snapshot. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Same reasoning as <c>KeyframePlayer.FlushWreckFlagBatch</c>: the unit
        /// tab list is rebuilt from <c>IsUnitActive</c>, which consults all three
        /// of these, and nothing in the reactive cascade redraws it. The view
        /// defers safely when it is not entered.
        /// <para>
        /// No scenario-state poke here, and that asymmetry is deliberate: the
        /// wreck path repays a poke <c>CombatUnitWreckingSystem</c> would have
        /// made and this path suppresses nothing, so adding one would be handing
        /// a client an accumulator bit the game never gave it.
        /// </para>
        /// </remarks>
        private static void FlushPilotState()
        {
            if (!pilotStatePending)
            {
                return;
            }
            pilotStatePending = false;

            try
            {
                if (CIViewCombatMode.ins != null)
                {
                    CIViewCombatMode.ins.RedrawUnitTabs();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[pb-and-j] the pilot-state redraw was refused: " + e.Message);
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
            // the class already reaches into several others.
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
    }
}
