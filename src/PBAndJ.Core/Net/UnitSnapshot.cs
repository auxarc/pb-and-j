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
            float deathTime)
        {
            Name = name;
            Position = position;
            Rotation = rotation;
            Facing = facing;
            Integrity = integrity;
            IsDead = isDead;
            DeathTime = deathTime;
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
