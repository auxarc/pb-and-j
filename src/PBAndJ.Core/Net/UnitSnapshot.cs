using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// A rotation on the wire. Mirrors <c>UnityEngine.Quaternion</c> the way
    /// <see cref="Vec3"/> mirrors <c>Vector3</c>.
    /// </summary>
    public readonly struct Vec4
    {
        public Vec4(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float W { get; }
    }

    /// <summary>
    /// One of a unit's parts that the host has wrecked, and when.
    /// </summary>
    /// <remarks>
    /// The socket is the join key, exactly as it is for
    /// <see cref="UnitLightKey"/>: the client's visual manager is keyed by
    /// socket name and an unknown one no-ops safely, so nothing here has to
    /// survive as an id across two processes.
    /// <para>
    /// ⚠️ Built by walking <c>EquipmentUtility.GetPartsInUnit</c> for
    /// <c>isWrecked</c> at capture, and deliberately <b>not</b> from
    /// <c>ReplayUnit.keyframesDestructions</c>, which looks purpose-built and is
    /// not: it is written at <c>CombatReplayHelper.cs:1914</c> and read nowhere
    /// in the shipped game, and it carries a recorder defect besides —
    /// dependency-wrecked parts record the <i>triggering</i> part rather than
    /// themselves (<c>EquipmentUtility.cs:3116-3117</c>, <c>:3126-3128</c> both
    /// pass <c>partHit</c>).
    /// </para>
    /// <para>
    /// <see cref="Time"/> is on the host's absolute combat clock, and a negative
    /// value is a real state rather than an absence: a unit spawning with an
    /// already-destroyed part is stamped <c>-100</c>
    /// (<c>UnitUtilities.cs:2041</c>), while every in-combat wreck takes
    /// <c>combat.simulationTime</c>, which never goes below zero. That sign is
    /// what lets a client tell "wrecked before the fight" from "wrecked in a
    /// window I might still play", with no window bounds to consult.
    /// </para>
    /// </remarks>
    public readonly struct PartDestruction
    {
        public PartDestruction(string? socket, float time)
        {
            Socket = socket;
            Time = time;
        }

        /// <summary>The mount socket — <c>part.partParentUnit.socket</c>.</summary>
        public string? Socket { get; }

        /// <summary>When it was wrecked, on the host's absolute clock.</summary>
        public float Time { get; }
    }

    /// <summary>
    /// One of a unit's parts and how damaged it is. M16.
    /// </summary>
    /// <remarks>
    /// The two fields the game's damage resolution actually writes
    /// (<c>CombatDamageSystem.cs:511,563</c>) and the two a save round-trips
    /// (<c>DataHelperSaveSerialization.cs:1297-1298</c>,
    /// <c>DataManagerSave.cs:2750-2751</c>). Nothing in the mod touched either
    /// before M16, so a client's parts sat at their combat-setup values for the
    /// whole fight — which is why its health bars, its post-combat frame
    /// integrity and any save it took were all wrong together.
    /// <para>
    /// <b>Every part travels, never only the damaged ones.</b> "Absent means
    /// pristine" is wrong for integrity: combat setup writes each part's value
    /// from the unit's pre-combat frame integrity
    /// (<c>EquipmentUtility.cs:677-684</c>), so a part legitimately starts below
    /// one and a sparse encoding could not tell that from undamaged. Barrier
    /// happens not to share that — <c>UnitUtilities.cs:2043-2046</c> resets it to
    /// <c>1f</c> at combat-unit creation — but one dense rule beats a dense rule
    /// and a sparse rule in the same record.
    /// </para>
    /// <para>
    /// Deliberately kept separate from <see cref="PartDestruction"/> rather than
    /// merged into a single <c>(socket, integrity, barrier, wreckTime)</c> list,
    /// which would be smaller and is the obvious end state. M15 shipped days
    /// before this with its unit-wreck path still unverified in game, and
    /// rewriting the membership test <see cref="DestructionState.Receive"/>
    /// performs on that list would have invalidated the verification that does
    /// exist. The redundancy is a few socket strings a turn.
    /// </para>
    /// </remarks>
    public readonly struct PartState
    {
        public PartState(string? socket, float integrity, float barrier)
        {
            Socket = socket;
            Integrity = integrity;
            Barrier = barrier;
        }

        /// <summary>The mount socket — <c>part.partParentUnit.socket</c>.</summary>
        public string? Socket { get; }

        public float Integrity { get; }

        public float Barrier { get; }
    }

    /// <summary>
    /// One unit's authoritative state at the end of an executed turn, as the
    /// host saw it. Hard-set on a client.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate type from <see cref="UnitState"/> rather than a
    /// widening of it. <see cref="UnitState"/>'s field set <em>is</em> the
    /// digest's definition, so adding rotation to it would silently change every
    /// digest value and permanently couple "what we sync" to "what we compare" —
    /// which should be free to diverge. Rotation is visually important but a poor
    /// divergence signal: a turret mid-sweep would report a difference that does
    /// not matter.
    /// <para>
    /// Rotation travels as a quaternion <em>and</em> a facing vector, not one
    /// derived from the other. <c>ReplaceRotation</c> and <c>ReplaceFacing</c>
    /// are separate component writes in the game, and a facing vector alone loses
    /// roll and forces the client to invent an up-axis — client-side derivation
    /// being exactly what host authority exists to eliminate.
    /// </para>
    /// <para>
    /// Held at position / rotation / facing / integrity / visibility, plus the
    /// wrecked-part set M15 added. Cooldowns, status effects, in-flight
    /// projectiles and terrain destruction remain out of scope: keyframe
    /// streaming is the real answer to wanting them, and adding fields until
    /// this is a save file is the failure mode to avoid.
    /// </para>
    /// <para>
    /// 🐛 <b>It used to carry <c>IsDead</c> and <c>DeathTime</c>, and those were
    /// dead wire fields for their whole life.</b> <c>DeathStatus</c> is a
    /// <i>pilot</i> component: its only runtime writer guards
    /// <c>isPilotTag</c> (<c>PilotUtility.DeclareDeath:2717</c>) and its only
    /// other writer is the pilot branch of the save loader
    /// (<c>DataManagerSave.cs:2129</c>). A unit persistent entity never has one,
    /// so capture read a constant false and the client's <c>ReplaceDeathStatus</c>
    /// was unreachable on real data. Removed in the same break that added the
    /// parts, rather than left as a field that documents a fact about pilots in
    /// the middle of a type about units.
    /// </para>
    /// </remarks>
    public readonly struct UnitSnapshot
    {
        private static readonly PartDestruction[] NoParts = new PartDestruction[0];
        private static readonly PartState[] NoPartStates = new PartState[0];

        public UnitSnapshot(
            string? name,
            Vec3 position,
            Vec4 rotation,
            Vec3 facing,
            float integrity,
            bool isHidden = false,
            bool isHiddenDetectable = false,
            bool isDeployed = true,
            bool hasArrivalTime = false,
            float arrivalTime = 0f,
            bool isWrecked = false,
            float wreckedAt = 0f,
            IReadOnlyList<PartDestruction>? wreckedParts = null,
            IReadOnlyList<PartState>? parts = null,
            bool hasFrameIntegrity = false,
            bool pilotDead = false,
            string? pilotDeathCause = null,
            bool pilotKnockedOut = false,
            bool pilotEjected = false)
        {
            Parts = parts ?? NoPartStates;
            HasFrameIntegrity = hasFrameIntegrity;
            PilotDead = pilotDead;
            PilotDeathCause = pilotDeathCause;
            PilotKnockedOut = pilotKnockedOut;
            PilotEjected = pilotEjected;
            Name = name;
            Position = position;
            Rotation = rotation;
            Facing = facing;
            Integrity = integrity;
            IsHidden = isHidden;
            IsHiddenDetectable = isHiddenDetectable;
            IsDeployed = isDeployed;
            HasArrivalTime = hasArrivalTime;
            ArrivalTime = arrivalTime;
            IsWrecked = isWrecked;
            WreckedAt = wreckedAt;
            WreckedParts = wreckedParts ?? NoParts;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Name { get; }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
        public Vec3 Facing { get; }
        public float Integrity { get; }

        /// <summary>
        /// Whether the host is drawing this unit at all.
        /// </summary>
        /// <remarks>
        /// Added after a playtest found an enemy mech on the host and not on the
        /// client, and it is worth writing down why it took a playtest. The
        /// digest never noticed: it hashes name, position and integrity, none of
        /// which visibility touches, so correction reported <c>OK</c> every turn
        /// while the two machines were showing different battlefields.
        /// <para>
        /// A client cannot derive this. The game's own detector,
        /// <c>ScenarioUtility.RevealHiddenUnits</c>, is genuine line-of-sight
        /// fog of war — and its only caller triggers on <c>SimulationTime</c>,
        /// which a client never advances. So the flag is frozen at whatever the
        /// scenario save it loaded happened to say, for the rest of the fight.
        /// That dormancy is also what makes replicating it safe: nothing on the
        /// client will fight the host over the value.
        /// </para>
        /// </remarks>
        public bool IsHidden { get; }

        /// <summary>
        /// Hidden, but close enough to be picked up by the host's detector.
        /// </summary>
        /// <remarks>
        /// Travels beside <see cref="IsHidden"/> rather than being inferred from
        /// it, because the game sets and clears the two together and a client
        /// holding one without the other is a state the game never produces.
        /// <para>
        /// It is not decoration: <c>CombatScenarioStateSystem</c> counts the
        /// hostiles remaining as <c>isHiddenDetectable || IsUnitActive(...)</c>
        /// and declares victory when that count reaches zero. A client left with
        /// a stale <c>true</c> keeps counting a unit the host has already
        /// revealed and killed.
        /// </para>
        /// </remarks>
        public bool IsHiddenDetectable { get; }

        /// <summary>
        /// Whether this unit is a participant in the fight yet.
        /// </summary>
        /// <remarks>
        /// Not the same question as <see cref="IsHidden"/>, and carried because
        /// the game's overlay eligibility check rejects on this <i>before</i> it
        /// looks at visibility (<c>CIHelperOverlays.IsUnitUsableForOverlay</c>).
        /// Revealing a unit that is not deployed would put its mesh on screen
        /// with no marker, no overlay and no entry in the unit bar.
        /// <para>
        /// Measured to be already true on the client for the units this fix was
        /// written for — the save records deployment independently of visibility
        /// — so this is not what was broken. It is carried because the game's own
        /// reveal path sets it in the same breath as the visibility flags, which
        /// means it can be false, and because the wire is being broken anyway.
        /// </para>
        /// </remarks>
        public bool IsDeployed { get; }

        /// <summary>
        /// Whether the host holds an arrival time for this unit at all.
        /// </summary>
        /// <remarks>
        /// Carried separately from the value because presence is what the game
        /// branches on in three places — <c>ScenarioUtility.cs:3652</c> exempts
        /// a hostile from salvage when it is absent, and both
        /// <c>CIHelperOverlays.cs:1051</c> and <c>PathUtility.cs:95</c> test it
        /// before reading anything.
        /// <para>
        /// A single float could not express it, and the reason is specific
        /// rather than theoretical: a client manufactures the component for
        /// itself. <c>DataManagerSave.cs:3047</c> adds an arrival time to
        /// <i>every</i> deployed unit on load, and the save writer stamps
        /// <c>-1</c> where the host had no component (<c>hasArrivalTime ? value
        /// : -1f</c>, <c>DataHelperSaveSerialization.cs:571</c>). So a host's
        /// player squad — which never receives one at all
        /// (<c>CombatScenarioSetupSystem.cs:390</c>) — arrives on a client as
        /// present-with--1. Present-and-negative and absent are therefore both
        /// real states describing different machines, and a wire that collapsed
        /// them would make the correction a no-op for exactly the units that
        /// need it.
        /// </para>
        /// </remarks>
        public bool HasArrivalTime { get; }

        /// <summary>
        /// When this unit arrived, on the host's simulation clock. Meaningful
        /// only when <see cref="HasArrivalTime"/>.
        /// </summary>
        /// <remarks>
        /// This is the reveal timestamp, which is why it is worth a wire move
        /// for what looks like a cosmetic field. Every path in the game that
        /// clears <c>isHidden</c> during a fight ends by calling
        /// <c>ScenarioUtility.UpdateArrivalTime</c> (<c>:5603</c>), which writes
        /// the current simulation time — five sites, and a repo-wide search
        /// finds no sixth. So a client that knows this value knows when the
        /// host stopped hiding the unit.
        /// <para>
        /// Left un-sent, a revealed unit reads <c>-1</c> on a client against the
        /// host's real value, and — more visibly — the client has no way to tell
        /// a unit revealed at the very end of a turn from one that was there all
        /// along, so it pops the unit into the battlefield a whole window early.
        /// </para>
        /// </remarks>
        public float ArrivalTime { get; }

        /// <summary>
        /// Whether the host has wrecked this unit itself. M15 §3.1.
        /// </summary>
        /// <remarks>
        /// A different question from every part being wrecked, and from
        /// <see cref="Integrity"/> reaching zero. Only the <c>Wrecked</c>
        /// component drives the wreck visual, and a client can never derive it:
        /// it is set exclusively during damage resolution
        /// (<c>EquipmentUtility:3244</c>), which a client does not run.
        /// <para>
        /// ⚠️ The client drives <c>IUnitVisualManager.OnUnitDestruction()</c>
        /// from this and <b>never sets the component</b>, for a much sharper
        /// reason than the parts have. The flag's own reactive system,
        /// <c>CombatUnitWreckingSystem</c>, does far more than play an effect:
        /// it increments <c>unitFrameDefects</c>, which is <b>serialized</b>
        /// (<c>DataHelperSaveSerialization.cs:480</c>); it calls
        /// <c>DestroyAllActions</c>; it pokes scenario state with
        /// <c>OnUnitDisabled</c>; and for a player-owned unit it raises a
        /// <b>modal pause dialog</b> (<c>CIViewDialogConfirmation.ins.Open</c>)
        /// mid-playback, on a UI with no view stack. Setting the flag to buy an
        /// explosion would be paying all of that.
        /// </para>
        /// <para>
        /// Not sent as a wrecked-parts-are-all-present inference either. A unit
        /// can be wrecked while parts survive, and parts can all be gone on a
        /// unit the host has not wrecked; the two facts are set in different
        /// places and only one of them draws the explosion.
        /// </para>
        /// </remarks>
        public bool IsWrecked { get; }

        /// <summary>
        /// When it was wrecked, on the host's absolute clock. Meaningful only
        /// when <see cref="IsWrecked"/>.
        /// </summary>
        /// <remarks>
        /// Same sign convention as <see cref="PartDestruction.Time"/>: negative
        /// means "no moment in this fight to wait for", so the client plays the
        /// wreck at once instead of holding it for a window. That covers a unit
        /// that spawned wrecked and a unit whose moment the host could not name.
        /// <para>
        /// ⚠️ <b>Derived host-side, because the game stores no unit-level
        /// destruction time.</b> It is the newest <see cref="WreckedParts"/>
        /// stamp, which is exact rather than approximate: wrecking a unit wrecks
        /// every part it still has, at one instant, in the same loop that sets
        /// the flag (<c>EquipmentUtility.cs:3247-3255</c>). A unit that reached
        /// the end with every part already gone was wrecked when the last one
        /// went, which is the same number. Kept on the wire rather than
        /// recomputed on the client so that improving the derivation — a patch
        /// recording the real instant — changes only the host.
        /// </para>
        /// </remarks>
        public float WreckedAt { get; }

        /// <summary>
        /// Every part of this unit the host currently has wrecked. M15.
        /// </summary>
        /// <remarks>
        /// <b>The live cumulative set, never a per-turn delta</b>, and that is
        /// the whole design rather than a convenience. The game's own replay
        /// walks the live wrecked set on every frame it draws, which silently
        /// covers three cases a delta loses: parts wrecked in earlier turns,
        /// parts already wrecked when the unit spawned, and a view rebuild —
        /// the progress dictionary lives on the view component and resets with
        /// it. It also makes <b>un-wrecking</b> expressible at all
        /// (<c>CombatUnitRevive.cs:16-49</c>): a socket that leaves this list is
        /// a removal instruction, where an add-only wire would leave a revived
        /// part dissolved for the rest of the fight.
        /// <para>
        /// Being state rather than an event, it also serves as the convergence
        /// backstop for a client that never played the window the wreck
        /// happened in — and that is not a corner case but the wreck-heavy one.
        /// The host sends no keyframes at all when a turn has no surviving
        /// tracked unit (<c>HostSession.cs:1959</c>), which is exactly a
        /// mutual-destruction final volley. Ordering is what makes it safe: the
        /// host sends the snapshot before the keyframes from one site, over TCP,
        /// and effects execute in order, so this always lands before the first
        /// playback frame of the turn it describes.
        /// </para>
        /// <para>
        /// ⚠️ The client drives the <b>visual</b> from this and never the
        /// <c>Wrecked</c> component. Adding that component fires
        /// <c>CombatPartWreckingSystem</c>, which writes
        /// <c>integrityNormalized</c> and <c>barrierNormalized</c> to zero — and
        /// <c>EquipmentUtility.RefreshFrameIntegrityFromParts</c> sums exactly
        /// those into <c>unitFrameIntegrity</c>, which is both a digest input
        /// and a saved field. State belongs to the host; this is a picture of it.
        /// </para>
        /// </remarks>
        public IReadOnlyList<PartDestruction> WreckedParts { get; }

        /// <summary>
        /// Every part of this unit and how damaged it is. M16.
        /// </summary>
        /// <remarks>
        /// The live values, like <see cref="WreckedParts"/> and for the same
        /// reason: a delta loses a client that joined late, a client whose window
        /// never played, and a view rebuilt mid-fight. Being state rather than an
        /// event is also what lets the client's own
        /// <c>EquipmentUtility.RefreshFrameIntegrityFromParts</c> come out right
        /// after combat with no patch at all — the recompute is correct once its
        /// inputs are.
        /// </remarks>
        public IReadOnlyList<PartState> Parts { get; }

        /// <summary>
        /// Whether the host has a <c>unitFrameIntegrity</c> component at all.
        /// M16.
        /// </summary>
        /// <remarks>
        /// Travels because it varies per unit and because a client cannot
        /// possibly derive it — see <see cref="FrameIntegrityDrive.Present"/> for
        /// the mechanism, which is that the two machines take different paths
        /// into combat and only one of them strips the component.
        /// <para>
        /// It is what makes <see cref="Integrity"/> honest. Before M16 that field
        /// was captured as <c>has ? f : 0f</c> and applied unconditionally, so it
        /// carried a zero that meant "absent on the host" and wrote it as a real
        /// value on the client. The digest agreed afterwards
        /// (<c>StateDigest.cs:104</c>) because both machines then read zero,
        /// which is exactly how a field can be wrong for a whole milestone
        /// without any counter noticing.
        /// </para>
        /// </remarks>
        public bool HasFrameIntegrity { get; }

        /// <summary>
        /// Whether this unit's pilot is dead on the host. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Read by <c>ScenarioUtility.IsUnitActive</c> as
        /// <c>persistentEntity.hasDeathStatus</c> (<c>:6427</c>), on the PILOT's
        /// persistent entity reached through the unit's <c>entityLinkPilot</c> —
        /// which is why one pilot per unit makes this record the right home and
        /// no second message type is needed.
        /// <para>
        /// ⚠️ A client cannot derive it. The producer is
        /// <c>CombatPilotStatReactionSystem</c>, which triggers on
        /// <c>PersistentMatcher.PilotStatValues</c> and locally invents a death
        /// from <c>hp &lt;= 0</c> — inventing a cause, concussing the entity,
        /// forcing an ejection and raising the same modal dialog M17 spends a
        /// patch suppressing. So the pilot's stat values deliberately do NOT
        /// travel and the conclusion does.
        /// </para>
        /// <para>
        /// 🔑 <b>Applied by writing the component, never the helper.</b>
        /// <c>PilotUtility.DeclareDeath</c> additionally fires
        /// <c>OnPilotEvent</c>, zeroes two pilot stats and runs combat-state
        /// focus events — the same class of cascade this milestone exists to
        /// avoid. <c>AddDeathStatus(time, cause)</c> is the whole write.
        /// </para>
        /// </remarks>
        public bool PilotDead { get; }

        /// <summary>
        /// The host's own word for how the pilot died. Meaningful only when
        /// <see cref="PilotDead"/>.
        /// </summary>
        /// <remarks>
        /// Carried rather than manufactured client-side because the cause is a
        /// content string the game shows to the player, and a client that
        /// invented "trauma" for a pilot the host recorded differently would be
        /// wrong in the one place the difference is visible.
        /// <para>
        /// Capped at <see cref="PbjMessageCodec.MaxPilotDeathCauseLength"/>
        /// characters, and the cap <b>refuses</b> rather than truncating: a
        /// shortened cause travels as a string the host never wrote, which is a
        /// lie the far side cannot detect. Null and empty stay distinct — null
        /// is "the host said nothing".
        /// </para>
        /// </remarks>
        public string? PilotDeathCause { get; }

        /// <summary>
        /// Whether this unit's pilot is knocked out on the host. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// A different question from <see cref="PilotDead"/>, asked separately by
        /// <c>IsUnitActive</c> (<c>:6431</c>) under its own opt-out parameter, so
        /// collapsing the two would change the answer for every caller that
        /// passes <c>inactiveIfPilotKnockedOut: false</c>.
        /// <para>
        /// Safe to plant: <b>no matcher <c>PersistentMatcher.KnockedOut</c>
        /// exists at all</b>, so nothing on the client reacts to the write.
        /// </para>
        /// </remarks>
        public bool PilotKnockedOut { get; }

        /// <summary>
        /// Whether this unit's pilot has ejected on the host. M17 stage 2.
        /// </summary>
        /// <remarks>
        /// Read by <c>IsUnitActive</c> (<c>:6427</c>) beside the death status.
        /// <c>PersistentMatcher.Ejected</c> has exactly one reference and it is a
        /// non-reactive <c>IGroup</c> in
        /// <c>OverworldCombatOutcomeProcessingSystem</c>, a system a client never
        /// triggers — its collector is <c>CombatOutcomeProcessing.Added()</c>,
        /// and outcome processing is host work.
        /// </remarks>
        public bool PilotEjected { get; }

        /// <summary>
        /// Projects onto the narrower type the digest is defined over.
        /// </summary>
        /// <remarks>
        /// One projection, in one place, so the host's digest describes exactly
        /// the set of units its snapshot carries. If capture and digest were
        /// allowed to disagree about which units exist, the client's
        /// post-correction comparison would fail for reasons that have nothing
        /// to do with correction.
        /// </remarks>
        public UnitState ToUnitState()
        {
            return new UnitState(Name, Position, Integrity);
        }
    }
}
