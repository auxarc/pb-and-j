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
    // Reading the authoritative unit state OUT of the ECS: the snapshot the host
    // sends, the digest a client checks itself against, and the three part-level
    // readers (wrecked parts, part states, wreck moment), each called exactly once and
    // only from CaptureSnapshot -- WreckMomentOf consumes WreckedPartsOf's RESULT,
    // handed to it by CaptureSnapshot, so the three never call one another.
    //
    // Applying a snapshot is the other direction and lives in
    // CombatGameBridge.Snapshot.Apply.cs. Nothing here writes to the ECS.
    //
    // One part of CombatGameBridge, a single class split across files. The
    // class-level prose, the ECS state queries and the interface declaration
    // all live in CombatGameBridge.cs. This file uses // rather than /// so
    // the compiler cannot concatenate summaries from twelve parts into one
    // type entry in PBAndJ.Mod.xml.
    internal sealed partial class CombatGameBridge
    {
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
    }
}
