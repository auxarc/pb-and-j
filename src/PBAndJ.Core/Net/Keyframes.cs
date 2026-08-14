using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One sampled pose of a unit during execution: where it was, and which way
    /// it faced, at one instant of simulation time.
    /// </summary>
    /// <remarks>
    /// The wire form of the game's <c>ReplayKeyframeTransform</c>, which the host
    /// records every <c>unitSamplingInterval</c> (0.1 s) while simulating.
    /// Deliberately transform-only: the recorder also keeps per-unit state
    /// (heat, six part integrities) and full skeletal poses, and both are out of
    /// scope for M6. Poses in particular are orders of magnitude heavier and
    /// need the puppet machinery the replay UI turns on, which a client cannot
    /// assume is available.
    /// <para>
    /// No facing vector, unlike <see cref="UnitSnapshot"/>: the recorder never
    /// captured one, and inventing it on the host would be the client-side
    /// derivation host authority exists to eliminate. Facing is corrected by the
    /// snapshot at the end of the turn.
    /// </para>
    /// </remarks>
    public readonly struct TransformKey
    {
        public TransformKey(float time, Vec3 position, Vec4 rotation)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
        }

        /// <summary>Simulation time, on the host's clock, within the turn window.</summary>
        public float Time { get; }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
    }

    /// <summary>
    /// One unit's motion over one executed turn.
    /// </summary>
    /// <remarks>
    /// Keyed by the persistent entity's internal name, exactly as
    /// <see cref="UnitSnapshot"/> is. The game's own recorder keys its tracks by
    /// <c>combatEntity.id.id</c>, a process-local ECS id that means nothing in
    /// another process, so capture re-keys before anything reaches the wire.
    /// <para>
    /// An empty track is meaningful and is not dropped: it says "this unit was in
    /// the combat and did not move", which a client must be able to tell apart
    /// from "this unit is not in the combat at all".
    /// </para>
    /// </remarks>
    public sealed class UnitTrack
    {
        private static readonly TransformKey[] NoKeys = new TransformKey[0];

        public UnitTrack(
            string? name,
            IReadOnlyList<TransformKey>? transforms,
            float revealTime = ReplayVisibility.None,
            float hideTime = ReplayVisibility.None)
        {
            Name = name;
            Transforms = transforms ?? NoKeys;
            RevealTime = revealTime;
            HideTime = hideTime;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Name { get; }

        /// <summary>Ascending by <see cref="TransformKey.Time"/>.</summary>
        public IReadOnlyList<TransformKey> Transforms { get; }

        /// <summary>
        /// When the host revealed this unit during the window, or
        /// <see cref="ReplayVisibility.None"/>.
        /// </summary>
        /// <remarks>
        /// The recorder's own <c>keyframeReveal</c> (<c>CombatReplayHelper.cs:1937</c>),
        /// clipped to the window at capture. Clipping is not tidiness: the field
        /// is a single slot that <c>experimentalMode</c> never clears between
        /// turns, so an unclipped read would replay a reveal from three turns ago
        /// on every turn since.
        /// <para>
        /// Absence is meaningful and is the common case. A unit activated during
        /// the <i>planning</i> phase has no reveal stamp at all — recording is off
        /// outside execution — while the recorder does hold it from the window's
        /// first frame, so the host drew it throughout and so must a client. An
        /// earlier revision of this design used the unit's <c>ArrivalTime</c> here
        /// instead and hid every such unit for up to a quarter-second.
        /// </para>
        /// <para>
        /// Travels here rather than on <see cref="UnitSnapshot"/> because this is
        /// the message the player receives. The snapshot's <c>ArrivalTime</c>
        /// covers the units that have no track at all, which is the only case it
        /// can cover — and the game has no hide-time component at all, so the
        /// hide direction could never have gone that way.
        /// </para>
        /// </remarks>
        public float RevealTime { get; }

        /// <summary>
        /// When the host hid this unit during the window, or
        /// <see cref="ReplayVisibility.None"/>.
        /// </summary>
        /// <remarks>
        /// The recorder's <c>keyframeHidden</c> (<c>CombatReplayHelper.cs:1930</c>),
        /// clipped the same way. Retreat is what writes it — an ordinary player
        /// action, not an exotic one — so without this a retreating unit vanishes
        /// at the start of the window instead of walking off.
        /// </remarks>
        public float HideTime { get; }
    }

    /// <summary>
    /// One joint's local placement within its parent, at one instant.
    /// </summary>
    /// <remarks>
    /// The wire form of the game's <c>ReplayKeyframeUnitJoint</c>. Both values
    /// are <b>local</b> space — the recorder stores <c>localPosition</c> /
    /// <c>localRotation</c> and playback writes them straight back to the same
    /// two properties. Saying so matters because
    /// <see cref="PoseTracks.Remap"/> fills an absent joint with the bone's
    /// current local pose, which is only a coherent thing to do in local space.
    /// </remarks>
    public readonly struct JointPose
    {
        public JointPose(Vec3 position, Vec4 rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
    }

    /// <summary>
    /// One sampled skeletal pose of a unit: every recorded joint at one instant.
    /// </summary>
    /// <remarks>
    /// The wire form of the game's <c>ReplayKeyframeUnitPose</c>.
    /// <para>
    /// The two equipment flags are not decoration and are the reason a pose is
    /// not merely "a time and an array of joints". They pin the weapon
    /// transforms onto the palm joints for that frame, which is what keeps a
    /// rifle in the hand through a firing animation. A pose model that dropped
    /// them would break the milestone in its showcase moment.
    /// </para>
    /// </remarks>
    public sealed class PoseKey
    {
        private static readonly JointPose[] NoJoints = new JointPose[0];

        public PoseKey(
            float time,
            bool syncLeftEquipment,
            bool syncRightEquipment,
            IReadOnlyList<JointPose>? joints)
        {
            Time = time;
            SyncLeftEquipment = syncLeftEquipment;
            SyncRightEquipment = syncRightEquipment;
            Joints = joints ?? NoJoints;
        }

        /// <summary>Simulation time, on the host's clock, within the turn window.</summary>
        public float Time { get; }

        public bool SyncLeftEquipment { get; }
        public bool SyncRightEquipment { get; }

        /// <summary>Parallel to <see cref="UnitPoseTrack.Joints"/>, same order.</summary>
        public IReadOnlyList<JointPose> Joints { get; }
    }

    /// <summary>
    /// One unit's skeletal animation over one executed turn.
    /// </summary>
    /// <remarks>
    /// Keyed by the persistent entity's internal name, exactly as
    /// <see cref="UnitTrack"/> is, and travelling beside that unit's transform
    /// track rather than inside it: a client that has one and not the other must
    /// still be able to play what it has.
    /// <para>
    /// <see cref="Joints"/> carries the host's bone <i>names</i>, in the host's
    /// recorded order, so the client can rebuild each key into its own bone
    /// order and its own bone count. The game's playback loop indexes a dense
    /// array up to the client's own <c>recordedBones.Count</c> with no length
    /// guard whatsoever, so handing it an array built to anyone else's shape is
    /// an exception every frame.
    /// </para>
    /// </remarks>
    public sealed class UnitPoseTrack
    {
        private static readonly PoseKey[] NoKeys = new PoseKey[0];
        private static readonly string[] NoJoints = new string[0];

        public UnitPoseTrack(
            string? name, IReadOnlyList<string>? joints, IReadOnlyList<PoseKey>? keys)
        {
            Name = name;
            Joints = joints ?? NoJoints;
            Keys = keys ?? NoKeys;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Name { get; }

        /// <summary>Host bone names, in host recorded order.</summary>
        public IReadOnlyList<string> Joints { get; }

        /// <summary>Ascending by <see cref="PoseKey.Time"/>.</summary>
        public IReadOnlyList<PoseKey> Keys { get; }
    }

    /// <summary>
    /// One turn's worth of recorded motion, as the host's bridge hands it over.
    /// </summary>
    /// <remarks>
    /// A type rather than three out-parameters, for the same reason
    /// <see cref="LocalTurnCompleteEvent"/> carries its digest and units
    /// together: the window and the tracks describe one instant of one walk over
    /// the recorder, and letting a caller obtain them separately is what would
    /// let them drift apart.
    /// </remarks>
    public sealed class KeyframeCapture
    {
        private static readonly UnitTrack[] NoTracks = new UnitTrack[0];
        private static readonly UnitPoseTrack[] NoPoses = new UnitPoseTrack[0];

        /// <summary>
        /// Nothing was recorded. Not an error: a client never captures, and a
        /// host whose scenario runs with prediction disabled has no recorder
        /// data to capture. Snapshot correction is unaffected either way.
        /// </summary>
        public static readonly KeyframeCapture None = new KeyframeCapture(0f, 0f, null);

        public KeyframeCapture(
            float windowStart,
            float windowEnd,
            IReadOnlyList<UnitTrack>? tracks,
            IReadOnlyList<UnitPoseTrack>? poses = null)
        {
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            Tracks = tracks ?? NoTracks;
            Poses = poses ?? NoPoses;
        }

        /// <summary>
        /// Simulation time at the start of the executed turn.
        /// </summary>
        /// <remarks>
        /// This is the host's <c>CombatReplayHelper.turnStartTime</c> verbatim,
        /// which is worth stating because M8 needs that value on the client to
        /// seed the game's own scrubber — and having it here means it never has
        /// to travel a second time. <see cref="WindowEnd"/> likewise yields the
        /// host's <c>previewTimeLimit</c>, which is only its rounded integer.
        /// Two turn-dependent statics the recon file called "values a client must
        /// be told" are therefore already being told, by M6.
        /// </remarks>
        public float WindowStart { get; }
        public float WindowEnd { get; }
        public IReadOnlyList<UnitTrack> Tracks { get; }

        /// <summary>
        /// Skeletal poses for the same turn, or empty when this turn plays
        /// transform-only.
        /// </summary>
        /// <remarks>
        /// Empty is a first-class outcome, not a failure: it is exactly M6's
        /// known-good behaviour and the only fallback that keeps every unit
        /// moving the same way as every other.
        /// </remarks>
        public IReadOnlyList<UnitPoseTrack> Poses { get; }
    }
}
