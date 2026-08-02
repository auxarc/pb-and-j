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

        public UnitTrack(string? name, IReadOnlyList<TransformKey>? transforms)
        {
            Name = name;
            Transforms = transforms ?? NoKeys;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Name { get; }

        /// <summary>Ascending by <see cref="TransformKey.Time"/>.</summary>
        public IReadOnlyList<TransformKey> Transforms { get; }
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

        /// <summary>
        /// Nothing was recorded. Not an error: a client never captures, and a
        /// host whose scenario runs with prediction disabled has no recorder
        /// data to capture. Snapshot correction is unaffected either way.
        /// </summary>
        public static readonly KeyframeCapture None = new KeyframeCapture(0f, 0f, null);

        public KeyframeCapture(float windowStart, float windowEnd, IReadOnlyList<UnitTrack>? tracks)
        {
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            Tracks = tracks ?? NoTracks;
        }

        public float WindowStart { get; }
        public float WindowEnd { get; }
        public IReadOnlyList<UnitTrack> Tracks { get; }
    }
}
