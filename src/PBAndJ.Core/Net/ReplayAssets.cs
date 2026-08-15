using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// A colour the host applied to a replayed effect, as two interpolated ends.
    /// </summary>
    /// <remarks>
    /// The wire form of the game's <c>DataBlockColorInterpolated</c>, which is
    /// two colours and nothing else — so it reconstructs client-side from values
    /// and no data-object reference ever crosses. That is the whole reason the
    /// three asset kinds can travel at all: everything the game hangs off them
    /// is either a value or a live reference, and §8 of the recon file sorted
    /// them once.
    /// </remarks>
    public readonly struct AssetColour
    {
        public AssetColour(Vec4 from, Vec4 to)
        {
            From = from;
            To = to;
        }

        /// <summary>RGBA, in the game's own 0..1 range.</summary>
        public Vec4 From { get; }

        public Vec4 To { get; }
    }

    /// <summary>
    /// What every replayed asset track carries, whatever kind it is.
    /// </summary>
    /// <remarks>
    /// A struct held by each kind rather than a base class, because these travel
    /// as plain data and the three kinds share no behaviour — only fields. The
    /// game models them with inheritance because its versions own a live
    /// <c>AssetLinker</c> and a pooled instance; ours own neither.
    /// <para>
    /// <see cref="Hue"/> and <see cref="Colour"/> are nullable because the game's
    /// are genuinely absent most of the time (<c>DataBlockFloat01</c> and
    /// <c>DataBlockColorInterpolated</c> are class references it leaves null),
    /// and "absent" is not the same as "zero" — a hue offset of zero is a real
    /// instruction to leave the hue alone, while absence means the effect uses
    /// its prefab's own.
    /// </para>
    /// </remarks>
    public readonly struct AssetTrackHead
    {
        public AssetTrackHead(
            string? assetKey,
            float timeStart,
            float timeEnd,
            float? hue = null,
            AssetColour? colour = null)
        {
            AssetKey = assetKey;
            TimeStart = timeStart;
            TimeEnd = timeEnd;
            Hue = hue;
            Colour = colour;
        }

        /// <summary>
        /// The pool key. Travels as a <b>string</b>, deliberately.
        /// </summary>
        /// <remarks>
        /// ⚠️ The game keeps an <c>assetKeyHash</c> beside it and compares on that
        /// when stealing an instance (<c>CombatReplayHelper.cs:1506</c>), and it
        /// must <b>never</b> cross the wire: <c>string.GetHashCode</c> carries no
        /// cross-process stability guarantee, so a hash minted on the host would
        /// match nothing on a client — silently, since a failed steal simply
        /// shows no effect. The game itself computes it locally at capture
        /// (<c>:1544</c>); so does the client, from this string.
        /// </remarks>
        public string? AssetKey { get; }

        /// <summary>Simulation time on the host's clock, as the recorder stamped it.</summary>
        public float TimeStart { get; }

        /// <summary>
        /// When the effect ends.
        /// </summary>
        /// <remarks>
        /// Routinely <i>past</i> the turn window, and that is not an error to
        /// correct: standalone tracks are stamped <c>timeStart + pool.lifetime</c>
        /// and anything still live at execution end is stamped
        /// <c>simTime + 1f</c> by the game's own fixup. A slice that dropped
        /// those would drop every effect near the end of a turn.
        /// </remarks>
        public float TimeEnd { get; }

        /// <summary>The game's <c>DataBlockFloat01</c>, or absent.</summary>
        public float? Hue { get; }

        /// <summary>The game's <c>DataBlockColorInterpolated</c>, or absent.</summary>
        public AssetColour? Colour { get; }
    }

    /// <summary>
    /// A one-shot effect: impacts, muzzle flashes, destruction bursts.
    /// </summary>
    /// <remarks>
    /// The wire form of <c>ReplayEntityAssetStandalone</c>, and by far the most
    /// numerous kind — measured at 51 rising to 727 entries across five turns on
    /// one fight, though that figure is the host's unpruned accumulation rather
    /// than a per-turn slice.
    /// <para>
    /// <b>These have no identity of their own</b>, and no identity is invented
    /// for them either. The game keeps them in a bare list, unlike projectiles
    /// and beams which are keyed by entity id. <see cref="Id"/> is a
    /// <i>within-one-turn label</i> synthesised at capture, and nothing more.
    /// </para>
    /// <para>
    /// ⚠️ <b>It is not cross-turn identity and cannot be made into any.</b> An
    /// earlier revision of this comment claimed it let a client tell "the same
    /// burst, still burning" from "another burst"; the decompile refutes that.
    /// <c>CombatReplayHelper.OnExecutionStart</c> prunes <c>assetsStandalone</c>
    /// by index (<c>:244-253</c>), so positions shift every turn: the same
    /// still-burning entry gets a different number next turn, and a different
    /// entry inherits this one's. Worse, the prune is gated on
    /// <c>experimentalMode</c> — a player setting — so whether it shifts at all
    /// differs between machines. Cross-turn identity would need a persistent map
    /// keyed on the game's own track objects, which nothing here has.
    /// <b>Do not dedupe on this across turns.</b> A long effect straddling a
    /// window boundary is simply sent again next turn, which is correct for a
    /// playback that builds a fresh track set per turn and unwinds it at stop.
    /// </para>
    /// <para>
    /// <c>parentPresent</c> deliberately does not travel. It is a live
    /// <c>Transform</c>, and the recon file's §8 already prescribes falling back
    /// to world space; measured, only 3 of 51 early-turn entries were parented.
    /// <see cref="PositionLocal"/> still travels because it is the position the
    /// game would use in that fallback.
    /// </para>
    /// </remarks>
    public sealed class StandaloneAssetTrack
    {
        public StandaloneAssetTrack(
            int id,
            AssetTrackHead head,
            Vec3 position,
            Vec4 rotation,
            Vec3 scale,
            Vec4 velocityAndDecay,
            Vec3 positionLocal)
        {
            Id = id;
            Head = head;
            Position = position;
            Rotation = rotation;
            Scale = scale;
            VelocityAndDecay = velocityAndDecay;
            PositionLocal = positionLocal;
        }

        /// <summary>
        /// A label within one turn. Synthesised at capture, not a game id, and
        /// <b>not</b> stable across turns — see the type's remarks.
        /// </summary>
        public int Id { get; }

        public AssetTrackHead Head { get; }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }

        /// <summary>
        /// ⚠️ Load-bearing, and zero is fatal.
        /// </summary>
        /// <remarks>
        /// <c>ReplayEntityAssetStandalone.AssignAsset</c> writes
        /// <c>transform.localScale</c> straight from this field, so a track that
        /// arrives with a default scale renders an effect scaled to nothing —
        /// invisible, silent, and indistinguishable from playback never running.
        /// Found the hard way by a probe that spent two runs rendering nothing.
        /// </remarks>
        public Vec3 Scale { get; }

        /// <summary>Direction times speed in xyz, decay time in w.</summary>
        public Vec4 VelocityAndDecay { get; }

        public Vec3 PositionLocal { get; }
    }

    /// <summary>
    /// A projectile in flight, as a path the client re-walks.
    /// </summary>
    /// <remarks>
    /// The wire form of <c>ReplayEntityAssetProjectile</c>, reusing M6's
    /// <see cref="TransformKey"/> verbatim — the game's own
    /// <c>ReplayKeyframeTransform</c> is a time, a position and a rotation, which
    /// is exactly what M6 already moves for unit tracks.
    /// <para>
    /// ⚠️ <b>Never let a slice leave fewer than two keys.</b>
    /// <c>ReplayEntityAssetProjectile.ApplyTime</c> returns early below two, but
    /// <c>AssignAsset</c> has <i>already</i> placed and shown the instance — at
    /// <c>keyframes[0]</c>, or at <b>the world origin</b> when there are none. A
    /// projectile frozen at the origin is the visible result. Keep the bracketing
    /// keys or drop the track for that window; there is no third option.
    /// </para>
    /// <para>
    /// Trails are deliberately absent. <c>keyframesTrail</c> was empty on every
    /// sample taken and <c>ReplayKeyframeTrailPoint</c> is ten fields — easily
    /// the heaviest shape in the system — so it is not designed in blind. Capture
    /// logs when a projectile had them, the way <c>NetLog.PosesNotCaptured</c>
    /// reports losses no count would show.
    /// </para>
    /// </remarks>
    public sealed class ProjectileAssetTrack
    {
        private static readonly TransformKey[] NoKeys = new TransformKey[0];

        public ProjectileAssetTrack(
            int id, AssetTrackHead head, Vec3 scale, IReadOnlyList<TransformKey>? keys)
        {
            Id = id;
            Head = head;
            Scale = scale;
            Keys = keys ?? NoKeys;
        }

        /// <summary>The host's combat entity id — the game's own key for these.</summary>
        /// <remarks>
        /// Process-local, and that is fine here in a way it is not for units: a
        /// client never resolves it to an entity, it only uses it to recognise
        /// the same projectile arriving again in the next turn's slice. A
        /// projectile still flying at turn end is recaptured under this same id,
        /// and a client that <i>replaced</i> its track rather than merging would
        /// strand the instance the old track still held.
        /// </remarks>
        public int Id { get; }

        public AssetTrackHead Head { get; }
        public Vec3 Scale { get; }

        /// <summary>Ascending by time. Two or more, or the track must not be sent.</summary>
        public IReadOnlyList<TransformKey> Keys { get; }
    }

    /// <summary>One sampled instant of a beam.</summary>
    /// <remarks>
    /// The wire form of <c>ReplayKeyframeBeam</c>. <see cref="Parameters"/> is
    /// the game's own packed triple, read straight back out at playback:
    /// <c>x</c> and <c>y</c> go to <c>fxHelperBeam.SetAll</c> and <c>z</c>
    /// becomes the beam's length through <c>SetScale</c>. Kept packed rather than
    /// named because the game never names them either, and inventing names we
    /// cannot verify would be a guess recorded as fact.
    /// </remarks>
    public readonly struct BeamKey
    {
        public BeamKey(float time, Vec3 position, Vec4 rotation, Vec3 parameters)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
            Parameters = parameters;
        }

        public float Time { get; }
        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
        public Vec3 Parameters { get; }
    }

    /// <summary>A beam over one turn.</summary>
    /// <remarks>
    /// The wire form of <c>ReplayEntityAssetBeam</c>. Same two-key rule and same
    /// origin hazard as <see cref="ProjectileAssetTrack"/>: its
    /// <c>AssignAsset</c> places the instance at <c>keyframes[0]</c> or at the
    /// origin, before <c>ApplyTime</c> has any say.
    /// </remarks>
    public sealed class BeamAssetTrack
    {
        private static readonly BeamKey[] NoKeys = new BeamKey[0];

        public BeamAssetTrack(int id, AssetTrackHead head, IReadOnlyList<BeamKey>? keys)
        {
            Id = id;
            Head = head;
            Keys = keys ?? NoKeys;
        }

        /// <inheritdoc cref="ProjectileAssetTrack.Id"/>
        public int Id { get; }

        public AssetTrackHead Head { get; }

        /// <summary>Ascending by time. Two or more, or the track must not be sent.</summary>
        public IReadOnlyList<BeamKey> Keys { get; }
    }

    /// <summary>
    /// One turn's replayed effects, as the host's bridge hands them over.
    /// </summary>
    /// <remarks>
    /// The three kinds travel together because the game drives them through one
    /// shared code path — <c>CheckAssetTrackActivation</c> then
    /// <c>ApplyTimeToActiveAssetTracks</c> — so carrying them separately would
    /// split one feature across three wires for no gain.
    /// </remarks>
    public sealed class AssetCapture
    {
        private static readonly StandaloneAssetTrack[] NoStandalone = new StandaloneAssetTrack[0];
        private static readonly ProjectileAssetTrack[] NoProjectiles = new ProjectileAssetTrack[0];
        private static readonly BeamAssetTrack[] NoBeams = new BeamAssetTrack[0];

        /// <summary>Nothing recorded — a client, or a turn with the recorder off.</summary>
        public static readonly AssetCapture None = new AssetCapture(null, null, null);

        public AssetCapture(
            IReadOnlyList<StandaloneAssetTrack>? standalone,
            IReadOnlyList<ProjectileAssetTrack>? projectiles,
            IReadOnlyList<BeamAssetTrack>? beams)
        {
            Standalone = standalone ?? NoStandalone;
            Projectiles = projectiles ?? NoProjectiles;
            Beams = beams ?? NoBeams;
        }

        public IReadOnlyList<StandaloneAssetTrack> Standalone { get; }
        public IReadOnlyList<ProjectileAssetTrack> Projectiles { get; }
        public IReadOnlyList<BeamAssetTrack> Beams { get; }

        /// <summary>Whether anything at all is here.</summary>
        public bool IsEmpty => Standalone.Count == 0 && Projectiles.Count == 0 && Beams.Count == 0;
    }
}
