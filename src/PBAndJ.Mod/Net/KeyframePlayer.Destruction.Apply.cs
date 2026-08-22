using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Combat.Components;
using PhantomBrigade.Combat.View;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Driving wrecks and damage onto the visuals, M15 to M17.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
        /// <summary>
        /// Plays one unit's own wreck visual, or undoes it. M15 §3.1.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>The whole of §3.1 is this call, and the point is what it does
        /// NOT do.</b> The obvious implementation — mirror the host's
        /// <c>isWrecked</c> onto the persistent entity and let the game's own
        /// reactive systems play the effect — buys an explosion at the price of
        /// <c>CombatUnitWreckingSystem</c>'s entire cascade: a <b>serialized</b>
        /// <c>unitFrameDefects</c> increment (<c>:74</c>),
        /// <c>DestroyAllActions</c> (<c>:70</c>), a scenario-state poke
        /// (<c>:108</c>), and for a player-owned unit a <b>modal pause dialog</b>
        /// (<c>:84</c>) raised mid-playback on a UI with no view stack. It would
        /// also wake <c>CombatUnitDestructionEffectSystem</c>, whose debris are
        /// real projectile entities that a client never moves or expires —
        /// thirty frozen fragments at the unit's core, per wreck.
        /// <para>
        /// <c>OnUnitDestruction</c> is the one line of that cascade we want, it
        /// is public on <c>IUnitVisualManager</c>, and it is what actually draws
        /// the wreck: pooled explosion FX and the <c>tank_destruction_full</c> /
        /// <c>mech_destruction_full</c> audio. So it is called directly, exactly
        /// as §3.2 drives the dissolve without adding a part's <c>Wrecked</c>
        /// component. <b>State belongs to the host; this is a picture of it.</b>
        /// </para>
        /// <para>
        /// Both calls are self-guarded on the manager's own <c>destroyedLast</c>
        /// flag, so a repeat is free and the pair is a true toggle — which is
        /// what makes un-wrecking expressible at all
        /// (<c>UnitVisualManager.cs:2016-2023</c>).
        /// </para>
        /// <para>
        /// ⚠️ Vanilla gates the visual on <c>!isHidden</c> (<c>:62</c>) and so do
        /// we. A wreck played on a hidden unit would draw an explosion over
        /// empty ground — and the game's own path has the same blind spot in the
        /// other direction, so this is parity rather than an improvement.
        /// </para>
        /// </remarks>
        private static void DriveWreck(IUnitVisualManager visuals, bool hidden, bool wrecked)
        {
            if (wrecked)
            {
                if (!hidden)
                {
                    visuals.OnUnitDestruction();
                }
                return;
            }
            visuals.OnUnitRevival();
        }

        /// <summary>
        /// Whether a flag write has landed since the last batch refresh.
        /// </summary>
        /// <remarks>
        /// The batch is what makes <see cref="DriveWreckFlag"/> affordable inside
        /// a per-frame loop: the two calls it defers are a full unit-tab rebuild
        /// and a scenario-state poke, and doing either per unit would spend a
        /// rebuild per corpse per frame.
        /// </remarks>
        private static bool wreckFlagsPending;

        /// <summary>
        /// Plants the host's <c>isWrecked</c> on this client's ECS. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// 🔑 <b>This is the milestone.</b> M15 drew the wreck and deliberately
        /// left the component alone; stage 2 sets it, and is only safe to do so
        /// because <see cref="WreckingPatches"/> has taken the two damaging
        /// cascades off first. <b>The patches and this call ship together</b> —
        /// this without them is the modal-dialog, frozen-debris,
        /// serialized-frame-defect cascade M15's own header spends a paragraph
        /// refusing.
        /// <para>
        /// <b>What it buys</b> is chiefly the enemy tracker:
        /// <c>CIViewCombatMode.RedrawUnitTabs</c> filters on <c>item.isWrecked</c>
        /// <i>directly</i>, not through <c>IsUnitActive</c>, and that is the
        /// artefact M16 photographed — a host at VICTORY while the client still
        /// counted six live enemies. In-world markers, the crash overlay, the
        /// execute-readiness warning and the ~20 <c>IsUnitActive</c> sites follow.
        /// </para>
        /// <para>
        /// ⚠️ <b>What it does NOT buy, said before a playtest "finds" it.</b> A
        /// client's corpse stays <b>clickable</b>: selection is a physics raycast
        /// filtered by <c>InputCombatUnitSelectionUtility.IsSelectable</c>, which
        /// tests <c>isHidden</c>, <c>isDestroyed</c>, <c>hasPosition</c> and
        /// <c>flag_untargetable</c> and consults neither <c>IsUnitActive</c> nor
        /// <c>isWrecked</c>. Roster divergence at combat end is also untouched —
        /// <c>FreeOrDestroyCombatParticipants</c> destroys enemy participants
        /// unconditionally, never consulting this flag.
        /// </para>
        /// <para>
        /// 🔴 <b>THE TRAP THIS OPENS, and the first place to look if a client's
        /// corpse ever starts ragdolling under its own physics.</b>
        /// <c>ActionUtility.CrashEntity</c> routes a unit with this flag straight
        /// into <c>UnitUtilities.OnUnitNonfunctional</c>, which sets
        /// <c>mode = Active</c> / <c>state = Dead</c> — precisely the pair
        /// <c>PuppetMaster.OnEnable</c> answers by re-activating the ragdoll, and
        /// the trap M17 stage 1 spent a build learning to avoid. Today it is
        /// unreachable because <c>isWrecked</c> is false on a client; <b>this
        /// method makes it reachable.</b> Every reachable caller was checked:
        /// <c>ActionPlaybackSystem</c> opens on <c>combat.Simulating</c>,
        /// <c>CombatCrashingSystem</c> is self-closing, and the damage and trigger
        /// paths are not driven by a non-simulating client. Level destruction was
        /// the open one and is now closed from the other end —
        /// <c>OverlapUtility.OnAreaOfEffectAgainstUnits</c> admits a unit to its
        /// hit list only when <c>!linkedPersistentEntity.isWrecked</c>, so the
        /// flag closes that route rather than opening it. What is left is
        /// <c>OverlapUtility.CheckUnitsOnDestroyedPoint</c>, whose non-turret arm
        /// does <b>not</b> re-check the flag — reachable only if this client's own
        /// <c>AreaManager</c> destroys a point, which needs scenario content or
        /// the debug console, and is the same content contingency the
        /// <c>EndCombatWithOutcome</c> prefix is priced for.
        /// </para>
        /// </remarks>
        private static void DriveWreckFlag(PersistentEntity persistent, CombatEntity? unit, bool wrecked)
        {
            if (persistent.isWrecked == wrecked)
            {
                // The setter early-returns on an unchanged value anyway; this is
                // here so the counters and the batch refresh describe CHANGES
                // rather than calls. A redraw per playback frame per corpse is
                // the cost of getting this wrong.
                return;
            }

            try
            {
                persistent.isWrecked = wrecked;

                // Vanilla writes both, one line apart, and un-writes both
                // together (CombatActionEvent then ScenarioUtility's revive).
                // Free correctness: Functional has no collector anywhere — its
                // matcher property exists with zero references — and it is read
                // at ninety-odd sites.
                persistent.isFunctional = !wrecked;

                if (wrecked && unit != null)
                {
                    // ⚠️ BEST-EFFORT, and explicitly not an invariant — never
                    // assert a collider state on the back of it. It self-guards
                    // on isWrecked, so it is inert before the write above and the
                    // order is load-bearing. Its tail PERMANENTLY removes the
                    // trigger collider from combatView.colliders, which nothing
                    // restores — CombatUnitRevive does not, and revival is a real
                    // wire path here — while our own CombatView.OnVisibility(true)
                    // re-enables every collider still in the list on the next
                    // reveal. So half of it is undone and half of it is forever.
                    // Its honest value is one frame of parity with vanilla.
                    UnitUtilities.OnHandleInactiveUnitCollision(unit);
                }

                if (wrecked)
                {
                    WreckFlagsSet++;
                }
                else
                {
                    WreckFlagsCleared++;
                }
                wreckFlagsPending = true;
            }
            catch (Exception e)
            {
                WreckFlagsRefused++;
                Debug.LogWarning(
                    "[pb-and-j] wreck flag for '"
                        + (persistent.hasNameInternal ? persistent.nameInternal.s : "?")
                        + "' was refused: " + e.Message);
            }
        }

        /// <summary>
        /// The two once-per-batch calls a flag write owes. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// <b>The redraw is the headline mechanism, not decoration.</b>
        /// <c>CIViewCombatMode.RedrawUnitTabs</c> tests <c>item.isWrecked</c>
        /// directly and <b>nothing in the reactive cascade calls it</b> — vanilla
        /// pairs the flag write with an explicit call one line later. There is a
        /// self-healing path (<c>CombatUILinkTimeline</c> redraws on any action
        /// change while not simulating) but it fires on the next action change,
        /// not now. The view defers safely when it is not entered, setting its own
        /// <c>unitRedrawScheduled</c>, so calling it outside unit-selection mode
        /// is free.
        /// <para>
        /// 🔴 <b>The scenario poke is the line suppressing
        /// <c>CombatUnitWreckingSystem</c> drops</b>, and repaying it is what
        /// makes kill-target objectives refresh on a client at all. It is NOT
        /// free: contexts accumulate by bitwise OR until consumed, so
        /// <c>OnUnitDisabled | OnExecutionEnd</c> inside one window is what lets a
        /// client run the victory count. <b>This line and the
        /// <c>EndCombatWithOutcome</c> prefix ship together or not at all.</b>
        /// </para>
        /// </remarks>
        private static void FlushWreckFlagBatch()
        {
            if (!wreckFlagsPending)
            {
                return;
            }
            wreckFlagsPending = false;

            try
            {
                if (CIViewCombatMode.ins != null)
                {
                    CIViewCombatMode.ins.RedrawUnitTabs();
                }
                CombatUtilities.AddScenarioStateRefreshContext(
                    ScenarioStateRefreshContext.OnUnitDisabled);
            }
            catch (Exception e)
            {
                WreckFlagsRefused++;
                Debug.LogWarning(
                    "[pb-and-j] the wreck-flag batch refresh was refused: " + e.Message);
            }
        }

        /// <summary>
        /// Forgets every part, for a combat or a session that has ended. M15.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Stop"/>, which runs between turns of the same
        /// fight and must not discard the wrecked set — that set is what keeps a
        /// blown-off limb missing through the planning phase.
        /// </remarks>
        internal static void ClearDestruction()
        {
            // M17 stage 1. Forgotten rather than woken, and the ordering that
            // makes it safe is CombatGameBridge.StopKeyframes': Stop() — hence
            // the wake, hence the freeze — runs BEFORE this, so the corpses are
            // already down by the time the set is dropped. Waking them here
            // would stand every one of them up for whatever remains of the
            // client's stay in the combat state. The views are instantiated per
            // combat and destroyed with the scene, so the handles die with them.
            frozen.Clear();
            Unfrozen = 0;

            // 🔴 M17 stage 2, and the sentence the next reader needs BEFORE they
            // tidy up: this method deliberately does NOT clear the ECS
            // isWrecked flag it planted. See
            // DestructionState.ShouldHoldWreckFlagAcrossCombatEnd for why, and
            // the short version is that StopKeyframes also fires on Bye and on
            // Fault, after which the human keeps playing THAT SAME FIGHT
            // single-player -- clearing the flag would resurrect every corpse
            // into the victory count and make the fight unwinnable. What
            // reclaims the flag is TeardownCampaignSystem destroying every
            // persistent entity on the way out of the campaign, not us.
            WreckFlagsSet = 0;
            WreckFlagsCleared = 0;
            WreckFlagsRefused = 0;
            wreckFlagsPending = false;
            WreckingPatches.ResetCounters();

            destruction.Clear();
            DestructionsPlayed = 0;
            DestructionsRefused = 0;
            DestructionsSettled = 0;
            WrecksPlayed = 0;
            WrecksSettled = 0;
            WrecksRefused = 0;

            // M16. The caller settles the held part damage before reaching here —
            // see CombatGameBridge.StopKeyframes — so this only forgets, and the
            // seen-set it forgets is what makes the next fight's first snapshot
            // settle at once rather than waiting for a window.
            ClearPartIntegrity();
        }

        /// <summary>
        /// Drives parts straight to their settled state, with no ramp. M15.
        /// </summary>
        /// <remarks>
        /// Resolves units by name against the live combat group rather than
        /// against <see cref="targets"/>, and that is the point of the method
        /// existing at all: the units needing this most are the ones with no
        /// track — destroyed at capture time, boneless, or hidden when the turn
        /// began — and a targets-only walk would miss every one of them.
        /// <para>
        /// The lookup is built once per call and only when there is something to
        /// place, so a turn that settles nothing — the overwhelming majority —
        /// walks no entities at all.
        /// </para>
        /// </remarks>
        private static void ApplySettled(DestructionUpdate update)
        {
            if (update.IsEmpty || !IDUtility.IsGameState("combat"))
            {
                return;
            }

            var byName = new Dictionary<string, IUnitVisualManager>();
            var hiddenByName = new Dictionary<string, bool>();
            // M17 stage 2. The entities themselves, because the flag write needs
            // the persistent entity and OnHandleInactiveUnitCollision needs the
            // combat one. Built in the same walk rather than re-resolved per
            // wreck: the walk is already paying for the link lookup.
            var unitByName = new Dictionary<string, CombatEntity>();
            var persistentByName = new Dictionary<string, PersistentEntity>();
            foreach (var unit in Contexts.sharedInstance.combat.GetGroup(CombatMatcher.UnitTag).GetEntities())
            {
                if (!unit.hasCombatView || unit.combatView.view == null
                    || unit.combatView.view.visualManager == null)
                {
                    continue;
                }
                var persistent = IDUtility.GetLinkedPersistentEntity(unit);
                if (persistent == null || !persistent.hasNameInternal)
                {
                    continue;
                }
                byName[persistent.nameInternal.s] = unit.combatView.view.visualManager;
                hiddenByName[persistent.nameInternal.s] = unit.isHidden;
                unitByName[persistent.nameInternal.s] = unit;
                persistentByName[persistent.nameInternal.s] = persistent;
            }

            // M15 section 3.1, and BEFORE the parts below. A unit settling into
            // its wreck deactivates its own effects (UnitVisualManagerSimple's
            // OnUnitDestruction ends on SetEffectsActive(false)), so driving the
            // parts first would spend the property-block refreshes on visuals
            // about to be switched off.
            var wrecks = update.Units;
            for (var i = 0; i < wrecks.Count; i++)
            {
                var wreck = wrecks[i];
                if (string.IsNullOrEmpty(wreck.Unit)
                    || !byName.TryGetValue(wreck.Unit!, out var unitVisuals))
                {
                    continue;
                }

                try
                {
                    hiddenByName.TryGetValue(wreck.Unit!, out var hidden);
                    DriveWreck(unitVisuals, hidden, wreck.Wrecked);

                    // M17 stage 2, and this is the ONLY path that can carry
                    // false: ApplyDestruction always drives a wreck, so an
                    // un-wreck can only ever arrive as a settled one.
                    if (persistentByName.TryGetValue(wreck.Unit!, out var persistent))
                    {
                        unitByName.TryGetValue(wreck.Unit!, out var unitEntity);
                        DriveWreckFlag(persistent, unitEntity, wreck.Wrecked);
                    }

                    if (!wreck.Wrecked)
                    {
                        // M17 stage 1, and only on this path: ApplyDestruction's
                        // drive is always a wreck, so an un-wreck can only ever
                        // arrive as a settled one. A revived unit still held
                        // asleep would be a statue for the rest of the fight.
                        Unfreeze(wreck.Unit);
                    }
                    WrecksSettled++;
                }
                catch (Exception e)
                {
                    WrecksRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] settling the wreck of '" + wreck.Unit + "' was refused: "
                            + e.Message);
                }
            }

            // Once for the whole settle, never per unit: the redraw is a full
            // unit-tab rebuild.
            FlushWreckFlagBatch();

            var drives = update.Parts;
            for (var i = 0; i < drives.Count; i++)
            {
                var drive = drives[i];
                if (string.IsNullOrEmpty(drive.Unit)
                    || !byName.TryGetValue(drive.Unit!, out var visuals))
                {
                    continue;
                }

                // No burst on this path. A settled part either happened in a
                // window this client never saw or was already destroyed when the
                // unit spawned; either way there is no moment to explode at, and
                // firing one would put a fresh detonation on a stump.
                try
                {
                    // Integrity unconditionally rather than on a first drive,
                    // because this path also runs the un-wrecking direction and
                    // has to be able to put a revived part back to pristine.
                    visuals.OnIntegrityChange(drive.Socket, drive.Wrecked ? 0f : 1f);
                    visuals.OnSocketDestructionChange(drive.Socket, drive.Wrecked ? 1f : 0f);
                    if (drive.Wrecked)
                    {
                        // Recorded so a window's ramp does not re-drive a part
                        // already at rest. Deliberately NOT recorded on the
                        // un-wrecking side: Receive drops that entry on purpose,
                        // so that a part destroyed a second time reads as a first
                        // drive again and gets its integrity zeroed.
                        destruction.ShouldDrive(drive.Unit, drive.Socket, 1f, out _);
                    }
                    DestructionsSettled++;
                }
                catch (Exception e)
                {
                    DestructionsRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] settling socket '" + drive.Socket + "' was refused: "
                            + e.Message);
                }
            }
        }

        /// <summary>
        /// Ramps this unit's destroyed parts to where the cursor says they are.
        /// M15.
        /// </summary>
        /// <remarks>
        /// A transcription of <c>CombatReplayHelper.ApplyTimeToUnit:1288-1303</c>
        /// with one deliberate addition and one deliberate omission.
        /// <para>
        /// <b>The addition is the burst</b>, fired on the frame the cursor
        /// crosses a part's destruction time. Vanilla's replay never fires it
        /// and cannot — <c>ReplayUnit.keyframesDestructions</c> is written and
        /// read nowhere — so transcribing vanilla faithfully would under-deliver
        /// here. 🔑 <b>Vanilla's replay is scrub parity; a client's playback is
        /// its live view</b>, and on a host the burst comes from
        /// <c>CombatPartWreckingSystem.Execute:105</c> as the part is wrecked.
        /// <c>UnitVisualUtility.OnSocketDestruction</c> is public and static, so
        /// this is the game's own call rather than a reimplementation of it.
        /// </para>
        /// <para>
        /// <b>The omission is <c>ReplaceDestructionProgress</c>.</b> Vanilla uses
        /// that component purely as its change guard; the guard lives in
        /// <see cref="DestructionState.ShouldDrive"/> instead, so writing it too
        /// would be a second copy of one fact — and the one thing a client must
        /// not do here is start writing part state.
        /// </para>
        /// <para>
        /// ⚠️ Integrity is zeroed on a part's <i>first</i> drive and the order
        /// matters: <c>OnSocketDestructionChange</c> ends by re-applying the
        /// socket's <b>stored</b> integrity, defaulting to <c>1f</c>
        /// (<c>UnitVisualManager.cs:1755</c>). Drive the dissolve without zeroing
        /// first and it renders over a part that still reads pristine — which on
        /// a tank, whose sockets may ship <c>destructionShaderEffect</c> false
        /// (<c>UnitVisualManagerSimple.cs:596</c>), can be the difference between
        /// a visible change and none at all.
        /// </para>
        /// <para>
        /// ⚠️ <b>Double-drive tripwire.</b> <c>EquipmentDestructionAnimationSystem</c>
        /// is inert on a client only because nothing replaces
        /// <c>combat.simulationTime</c> and no part carries a
        /// <c>DestructionTime</c> component there. Anything that later sets part
        /// <c>DestructionTime</c> client-side wakes that system and it will fight
        /// this drive.
        /// </para>
        /// </remarks>
        private static void ApplyDestruction(Target target)
        {
            // M15 §3.1, ahead of the parts and outside their early return. A
            // unit can be wrecked with nothing in its part set — a composite
            // member, or one whose parts were all gone already — and gating the
            // wreck on having parts would lose exactly those.
            if (target.Visuals != null
                && destruction.TryTakeWreck(target.Name, cursorPrevious, cursor))
            {
                try
                {
                    // isHidden read live rather than off the snapshot, because
                    // the visibility watch may have revealed this unit part-way
                    // through the very window we are playing.
                    var hidden = target.Unit != null && target.Unit.isHidden;
                    DriveWreck(target.Visuals, hidden, true);

                    // M17 stage 2. The batch refresh this owes is flushed by
                    // Advance, once after the whole target loop -- see
                    // FlushWreckFlagBatch. Per unit it would be a unit-tab
                    // rebuild per corpse per frame of the window.
                    var persistent = target.Unit != null
                        ? IDUtility.GetLinkedPersistentEntity(target.Unit)
                        : null;
                    if (persistent != null)
                    {
                        DriveWreckFlag(persistent, target.Unit, true);
                    }

                    WrecksPlayed++;
                }
                catch (Exception e)
                {
                    WrecksRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] wreck of '" + target.Name + "' was refused: " + e.Message);
                }
            }

            var parts = destruction.PartsFor(target.Name);
            if (parts.Count == 0)
            {
                return;
            }

            var visuals = target.Visuals;
            if (visuals == null)
            {
                return;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var progress = DestructionRamp.Progress(part.Time, cursor);

                // The crossing edge, tested before the guard below: the burst is
                // a moment rather than a value, so a part whose whole ramp falls
                // between two frames must still get its explosion. Same ordering
                // rule stage A had to move into Core as ActionFor, and stage B
                // was bitten by.
                var crossed = ReplayAssetPlayback.CrossedDuring(
                    part.Time, part.Time, cursorPrevious, cursor);

                // Nothing has happened to this part yet at this cursor, and
                // saying so is not the same as saying zero. Driving a zero would
                // register a first drive and zero the socket's integrity, which
                // is a visible change on a part the window has not reached — the
                // causality error this whole design exists to avoid, in
                // miniature.
                if (progress <= 0f && !crossed)
                {
                    continue;
                }

                if (!destruction.ShouldDrive(target.Name, part.Socket, progress, out var first)
                    && !crossed)
                {
                    continue;
                }

                try
                {
                    if (first)
                    {
                        visuals.OnIntegrityChange(part.Socket, 0f);
                    }
                    if (crossed)
                    {
                        DestructionsPlayed++;
                        // audioUsed mirrors the game's own argument at
                        // CombatPartWreckingSystem:105 — a part burst is silent
                        // on a unit that is itself being wrecked, because the
                        // unit's own destruction carries the sound. The flag is
                        // read off this machine, where it is the client's honest
                        // answer rather than the host's.
                        var linked = target.Unit != null
                            ? IDUtility.GetLinkedPersistentEntity(target.Unit)
                            : null;
                        UnitVisualUtility.OnSocketDestruction(
                            visuals, part.Socket, linked == null || !linked.isWrecked);
                    }
                    visuals.OnSocketDestructionChange(part.Socket, progress);
                }
                catch (Exception e)
                {
                    // The guard was already told this value, and the visual
                    // never got it. Mid-ramp that self-heals on the next frame,
                    // which is precisely why the resting case has to be handled
                    // deliberately: at progress 1 the value stops moving, so a
                    // poisoned guard would refuse every retry for ever and the
                    // part would simply never dissolve.
                    destruction.Forget(target.Name, part.Socket);
                    DestructionsRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] destruction of socket '" + part.Socket
                            + "' was refused: " + e.Message);
                }
            }
        }
    }
}
