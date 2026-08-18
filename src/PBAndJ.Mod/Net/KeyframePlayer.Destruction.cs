using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using PBAndJ.Core.Net;
using PhantomBrigade;
using PhantomBrigade.Combat;
using PhantomBrigade.Combat.View;
using PhantomBrigade.Data;
using UnityEngine;

namespace PBAndJ.Mod.Net
{
    // Receiving and settling part damage and wrecks, M15 to M17.
    //
    // One part of KeyframePlayer, a single class split across files.
    // Class-level prose lives ONLY in KeyframePlayer.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry -- a defect the XML doc diff caught
    // during the SelfTest split.
    internal static partial class KeyframePlayer
    {
        /// <summary>
        /// Which parts this client believes are wrecked, and whether it is yet
        /// entitled to say so. M15.
        /// </summary>
        /// <remarks>
        /// Static and outliving any one window on purpose. It is fed by the
        /// snapshot, which arrives whether or not a window follows, and the
        /// backstop it provides is precisely for the turns where playback never
        /// happens — so tying its lifetime to <see cref="targets"/> would
        /// discard the state in exactly the case it exists for.
        /// </remarks>
        private static readonly DestructionState destruction = new DestructionState();

        /// <summary>
        /// Which parts hold which damage, and when this client may say so. M16.
        /// </summary>
        /// <remarks>
        /// Static and outliving any one window for the same reason
        /// <see cref="destruction"/> is, and settled on three paths rather than
        /// one — see <see cref="PartIntegrityState"/>.
        /// </remarks>
        private static readonly PartIntegrityState partIntegrity = new PartIntegrityState();

        /// <summary>Parts whose integrity was written into this client's ECS. M16.</summary>
        /// <remarks>
        /// 🔑 <b>Read this beside the cross-machine <c>partState</c> digest or
        /// that digest is vacuous.</b> Two machines with no damage agree
        /// perfectly with the sync entirely unwired; a non-zero count here is what
        /// says the comparison had anything to compare.
        /// </remarks>
        internal static int PartsSynced { get; private set; }

        /// <summary>Parts named by a snapshot that this client could not resolve. M16.</summary>
        /// <remarks>
        /// Expected to be zero. The two machines loaded the same save, so a
        /// non-zero reading is a roster or content divergence rather than a
        /// timing one, and it is the reading that separates "the sync did
        /// nothing" from "the sync had nothing to write to".
        /// </remarks>
        internal static int PartsUnresolved { get; private set; }

        /// <summary>Part or frame writes that threw inside game code. M16.</summary>
        internal static int PartsRefused { get; private set; }

        /// <summary>Parts waiting for a window to settle them. M16.</summary>
        internal static int PartsHeld => partIntegrity.HeldCount;

        /// <summary>
        /// Part destructions whose moment the cursor crossed. M15.
        /// </summary>
        /// <remarks>
        /// Counted at the crossing edge rather than per drive, for the same
        /// reason <see cref="LightsFired"/> is: the ramp re-drives a part on
        /// every frame of its half-second, so counting calls would report the
        /// frame rate.
        /// <para>
        /// ⚠️ A count proves the burst was <i>called</i>, never that anything was
        /// seen — and here that gap is wider than usual. The dissolve is consumed
        /// only where a socket visual ships <c>destructionShaderEffect</c> true
        /// (<c>UnitVisualManagerSimple.cs:596</c>), which is content and may be
        /// false on exactly the tanks this was written for.
        /// </para>
        /// </remarks>
        internal static int DestructionsPlayed { get; private set; }

        /// <summary>Part drives that threw inside game code.</summary>
        internal static int DestructionsRefused { get; private set; }

        /// <summary>Parts settled outside a window — the convergence backstop.</summary>
        internal static int DestructionsSettled { get; private set; }

        /// <summary>
        /// How many parts this machine has been TOLD are wrecked, and on how
        /// many units. M15.
        /// </summary>
        /// <remarks>
        /// Zero on a host, which never receives a snapshot, and that asymmetry is
        /// the reading: a host's own count comes off the ECS, and a client's can
        /// only come from here because a client never sets the component. Compare
        /// the host's ECS figure against this one on the client and a divergence
        /// is visible — which nothing else on either machine can show, since
        /// <c>DestructionProgress</c> is not a digest input.
        /// </remarks>
        internal static int HeldDestructions => destruction.Count;

        internal static int HeldDestructionUnits => destruction.UnitCount;

        /// <summary>
        /// Unit wrecks played at the crossing edge during a window. M15 §3.1.
        /// </summary>
        /// <remarks>
        /// The number to compare against the host's own wrecked-unit count over
        /// a turn. ⚠️ It can legitimately fall short of it, and by a knowable
        /// amount rather than mysteriously: a wreck only reaches this counter
        /// when the unit still has a transform track to be a target for, so a
        /// unit hidden at turn start, or one the ECS destroyed outright, lands in
        /// <see cref="WrecksSettled"/> instead. <b>The sum is the figure that
        /// must match, never this one alone.</b>
        /// </remarks>
        internal static int WrecksPlayed { get; private set; }

        /// <summary>Wrecks and revivals applied outside a window.</summary>
        internal static int WrecksSettled { get; private set; }

        /// <summary>Wreck drives that threw inside game code.</summary>
        internal static int WrecksRefused { get; private set; }

        /// <summary>How many units this client has been told are wrecked.</summary>
        internal static int HeldWreckedUnits => destruction.WreckedUnitCount;

        /// <summary>
        /// Corpses currently held asleep rather than re-posed. M17 stage 1.
        /// </summary>
        /// <remarks>
        /// A live count, not a running total, and the acceptance test reads it
        /// twice for that reason: it must equal the wrecked-unit count after the
        /// window that killed them <b>and still equal it a turn later</b>. A
        /// figure that doubles on the second turn is the append-instead-of-replace
        /// defect, which is the one way this feature can look like it works while
        /// leaking a handle per unit per turn.
        /// </remarks>
        internal static int FrozenUnits => frozen.Count;

        /// <summary>Corpses handed back to their animator by a revival.</summary>
        internal static int Unfrozen { get; private set; }


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
        /// <summary>
        /// Takes a snapshot's wrecked-part sets and settles what it may. M15.
        /// </summary>
        /// <remarks>
        /// Called from the bridge's snapshot apply, which is where the host's
        /// authority over part state lands. Everything it settles here is a part
        /// whose destruction has no moment left in any window this client can
        /// still play — one it was already told about, or one the unit spawned
        /// with — so applying it on arrival is the whole point rather than a
        /// compromise.
        /// </remarks>
        internal static void ReceiveDestruction(IReadOnlyList<UnitSnapshot> snapshot)
        {
            ApplySettled(destruction.Receive(snapshot));
        }

        /// <summary>
        /// Takes a snapshot's part damage and writes whatever is due now. M16.
        /// </summary>
        /// <remarks>
        /// What is due now is the <i>previous</i> snapshot's set, not this one's:
        /// this one describes a turn whose replay has not played yet, and showing
        /// its damage first is the causality error the hold exists to prevent.
        /// The frame-integrity half is not held at all — nothing draws it during
        /// a fight.
        /// </remarks>
        internal static void ReceivePartIntegrity(IReadOnlyList<UnitSnapshot> snapshot)
        {
            ApplyPartIntegrity(partIntegrity.Receive(snapshot));
        }

        /// <summary>
        /// Writes whatever the window was holding, because it is over. M16.
        /// </summary>
        /// <remarks>
        /// Called from two places and both are load-bearing: the end of a window
        /// that played, and combat end. The second is what keeps the final turn's
        /// damage, since an absent window never reaches the first and there is no
        /// next snapshot after a fight.
        /// </remarks>
        internal static void SettlePartIntegrity()
        {
            ApplyPartIntegrity(partIntegrity.SettleWindow());
        }

        /// <summary>
        /// Forgets every part's damage, for a combat that has ended. M16.
        /// </summary>
        internal static void ClearPartIntegrity()
        {
            partIntegrity.Clear();
            PartsSynced = 0;
            PartsUnresolved = 0;
            PartsRefused = 0;
        }

        /// <summary>
        /// Writes part damage and frame-integrity presence into this client's own
        /// ECS. M16.
        /// </summary>
        /// <remarks>
        /// Resolved against the <b>persistent</b> context by name rather than
        /// against the combat group, and that is not a stylistic choice. This runs
        /// at combat end as well as between turns, when combat entities and their
        /// views are being torn down — and it is the persistent entities that
        /// carry the fields a save will record, so they are the ones that have to
        /// be right.
        /// <para>
        /// ⚠️ <b>Deliberately unguarded by any last-written-value table.</b> M15
        /// needs one because its ramp re-drives a part on every frame of a
        /// half-second; this runs at most three times a turn, so a guard would
        /// buy nothing measurable and cost two real failure modes — the
        /// throw-after-record poisoning <c>DestructionState.Forget</c> exists to
        /// undo, and a permanent standoff with M15's synthesised <c>1f</c> on the
        /// un-wreck path, which would bypass any guard we added. Writing
        /// unconditionally is what makes that placeholder self-correcting.
        /// </para>
        /// <para>
        /// The visual call goes beside the component write because the two are
        /// separate systems in the game: <c>CombatDamageSystem.cs:585</c> drives
        /// the model's damage appearance directly and nothing reacts to the
        /// component to do it, so a component-only write would leave a client's
        /// mech looking pristine while its health bar read correctly.
        /// </para>
        /// </remarks>
        private static void ApplyPartIntegrity(PartIntegrityUpdate update)
        {
            if (update.IsEmpty)
            {
                return;
            }

            var frames = update.Frames;
            for (var i = 0; i < frames.Count; i++)
            {
                var persistent = IDUtility.GetPersistentEntity(frames[i].Unit);
                if (persistent == null)
                {
                    continue;
                }

                try
                {
                    if (frames[i].Present)
                    {
                        persistent.ReplaceUnitFrameIntegrity(frames[i].Integrity);
                    }
                    else if (persistent.hasUnitFrameIntegrity)
                    {
                        // The removal a client cannot skip. Its own save loader
                        // installed this component (DataManagerSave.cs:2293-2301)
                        // where the host's combat setup stripped it, so the two
                        // disagree from combat entry and only an explicit remove
                        // closes it.
                        persistent.RemoveUnitFrameIntegrity();
                    }
                }
                catch (Exception e)
                {
                    PartsRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] frame integrity for '" + frames[i].Unit + "' was refused: "
                            + e.Message);
                }
            }

            var parts = update.Parts;
            for (var i = 0; i < parts.Count; i++)
            {
                var persistent = IDUtility.GetPersistentEntity(parts[i].Unit);
                if (persistent == null)
                {
                    PartsUnresolved++;
                    continue;
                }

                var part = EquipmentUtility.GetPartInUnit(persistent, parts[i].Socket, false);
                if (part == null)
                {
                    PartsUnresolved++;
                    continue;
                }

                try
                {
                    part.ReplaceIntegrityNormalized(parts[i].Integrity);
                    part.ReplaceBarrierNormalized(parts[i].Barrier);
                    DriveVisualIntegrity(persistent, parts[i].Socket, parts[i].Integrity);
                    PartsSynced++;
                }
                catch (Exception e)
                {
                    PartsRefused++;
                    Debug.LogWarning(
                        "[pb-and-j] part state for socket '" + parts[i].Socket + "' was refused: "
                            + e.Message);
                }
            }
        }

        /// <summary>
        /// Tells the unit's visual manager what its socket's integrity now is.
        /// </summary>
        /// <remarks>
        /// Silently skipped when there is no view, which is the ordinary case at
        /// combat end and for a unit that never had one. The component write above
        /// is the half that must not be skipped; this one is appearance.
        /// </remarks>
        private static void DriveVisualIntegrity(
            PersistentEntity persistent, string? socket, float integrity)
        {
            var unit = IDUtility.GetLinkedCombatEntity(persistent);
            if (unit == null || !unit.hasCombatView || unit.combatView.view == null
                || unit.combatView.view.visualManager == null)
            {
                return;
            }
            unit.combatView.view.visualManager.OnIntegrityChange(socket, integrity);
        }
    }
}
