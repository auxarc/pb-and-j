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
    /// Held at position / rotation / facing / integrity / death on purpose. Part
    /// states, cooldowns, status effects, in-flight projectiles and terrain
    /// destruction are all out of scope: keyframe streaming is the real answer to
    /// wanting them, and adding fields until this is a save file is the failure
    /// mode to avoid.
    /// </para>
    /// </remarks>
    public readonly struct UnitSnapshot
    {
        public UnitSnapshot(
            string? name,
            Vec3 position,
            Vec4 rotation,
            Vec3 facing,
            float integrity,
            bool isDead,
            float deathTime,
            bool isHidden = false,
            bool isHiddenDetectable = false,
            bool isDeployed = true)
        {
            Name = name;
            Position = position;
            Rotation = rotation;
            Facing = facing;
            Integrity = integrity;
            IsDead = isDead;
            DeathTime = deathTime;
            IsHidden = isHidden;
            IsHiddenDetectable = isHiddenDetectable;
            IsDeployed = isDeployed;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Name { get; }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
        public Vec3 Facing { get; }
        public float Integrity { get; }
        public bool IsDead { get; }

        /// <summary>Meaningful only when <see cref="IsDead"/>.</summary>
        public float DeathTime { get; }

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
