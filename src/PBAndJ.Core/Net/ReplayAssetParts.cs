using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Why a captured asset track cannot travel as it stands.
    /// </summary>
    /// <remarks>
    /// Every one of these is a <b>per-track</b> drop, and that is the deliberate
    /// difference from <see cref="PoseTrackFault"/>, whose unrepairable grades
    /// take the whole turn down to transform-only.
    /// <para>
    /// The reason is what a player sees. One unit sliding among walking ones is
    /// a mixture that reads as a broken game, so poses degrade uniformly or not
    /// at all. One missing impact flash among a turn's worth of impact flashes
    /// is invisible — it is also exactly what the game's own pool exhaustion
    /// does on a busy turn, so a client that drops one is showing a shape the
    /// host itself can produce. Demoting a whole turn's effects for one bad key
    /// would trade an invisible loss for a visible one.
    /// </para>
    /// </remarks>
    public enum AssetTrackFault
    {
        /// <summary>Usable as it stands, or after thinning.</summary>
        None = 0,

        /// <summary>
        /// No pool key, so nothing on the client could resolve it.
        /// </summary>
        /// <remarks>
        /// Also what a null track reports. A missing track and a track naming
        /// nothing are the same event from the client's side — an effect that
        /// cannot be looked up — and giving them separate grades would invent a
        /// distinction no caller could act on differently.
        /// </remarks>
        NoKey = 1,

        /// <summary>
        /// The pool key is longer than
        /// <see cref="PbjMessageCodec.MaxAssetKeyLength"/>.
        /// </summary>
        /// <remarks>
        /// Caught here rather than at encode, for the reason
        /// <see cref="PoseTrackFault.NameTooLong"/> is: <c>PbjWriter</c> throws
        /// above its own string limit and <c>PbjRuntime.SendTo</c> encodes
        /// <i>outside</i> its try block, so a throw there does not fail one
        /// message — it empties the effect pump and takes everything queued
        /// behind it.
        /// </remarks>
        KeyTooLong = 2,

        /// <summary>
        /// A projectile or beam with fewer than two keys.
        /// </summary>
        /// <remarks>
        /// The origin-freeze hazard, and a drop rather than a repair because
        /// there is nothing to repair it with.
        /// <c>ReplayEntityAssetProjectile.ApplyTime</c> returns early below two
        /// keys — but <c>AssignAsset</c> has <b>already</b> placed and shown the
        /// instance, at <c>keyframes[0]</c> or at the world origin when there
        /// are none. Sending such a track renders a projectile frozen at the
        /// map origin; sending nothing renders nothing, which is the honest
        /// outcome.
        /// </remarks>
        TooFewKeys = 3,
    }

    /// <summary>
    /// Preparing captured asset tracks for the wire, and cutting a turn's worth
    /// of them into parts.
    /// </summary>
    /// <remarks>
    /// Pure, and here under the gate rather than in the glue because every rule
    /// below fails invisibly. An asset silently dropped is an effect that never
    /// plays; a part boundary off by one is a turn the client can never
    /// reassemble and therefore a turn with no effects at all — and both look
    /// exactly like "the feature is not built yet".
    /// <para>
    /// <b>Parts are counted, not measured.</b> M8's poses take one track per
    /// message because a single pose track can reach hundreds of kilobytes, so
    /// a byte budget there would need a rule for the track that alone exceeds
    /// it — an arm nothing can reach once the caps bound a single track, and an
    /// unreachable arm fails the coverage gate. Asset tracks are the opposite
    /// shape: many small ones. A fixed count per part is therefore provable
    /// straight from the caps, which
    /// <c>Encode_ReplayAssetsAtEveryCap_StaysUnderTheFrameLimit</c> pins rather
    /// than trusting the arithmetic.
    /// </para>
    /// </remarks>
    public static class ReplayAssetParts
    {
        private static readonly AssetCapture[] NoParts = new AssetCapture[0];

        /// <summary>Checks one standalone track over.</summary>
        public static AssetTrackFault TryPrepare(
            StandaloneAssetTrack? track, out StandaloneAssetTrack? prepared)
        {
            prepared = null;
            var fault = CheckKey(track?.Head.AssetKey);
            if (fault != AssetTrackFault.None)
            {
                return fault;
            }

            // Nothing to thin: a standalone effect is a placement, not a path.
            prepared = track;
            return AssetTrackFault.None;
        }

        /// <summary>Checks and thins one projectile track.</summary>
        public static AssetTrackFault TryPrepare(
            ProjectileAssetTrack? track, out ProjectileAssetTrack? prepared)
        {
            prepared = null;
            var fault = CheckKey(track?.Head.AssetKey);
            if (fault != AssetTrackFault.None)
            {
                return fault;
            }
            if (track!.Keys.Count < 2)
            {
                return AssetTrackFault.TooFewKeys;
            }

            prepared = new ProjectileAssetTrack(
                track.Id, track.Head, track.Scale,
                TrackThinning.Thin(track.Keys, PbjMessageCodec.MaxAssetKeysPerTrack));
            return AssetTrackFault.None;
        }

        /// <summary>Checks and thins one beam track.</summary>
        public static AssetTrackFault TryPrepare(
            BeamAssetTrack? track, out BeamAssetTrack? prepared)
        {
            prepared = null;
            var fault = CheckKey(track?.Head.AssetKey);
            if (fault != AssetTrackFault.None)
            {
                return fault;
            }
            if (track!.Keys.Count < 2)
            {
                return AssetTrackFault.TooFewKeys;
            }

            prepared = new BeamAssetTrack(
                track.Id, track.Head,
                TrackThinning.Thin(track.Keys, PbjMessageCodec.MaxAssetKeysPerTrack));
            return AssetTrackFault.None;
        }

        /// <summary>
        /// Cuts one turn's prepared tracks into wire-sized parts.
        /// </summary>
        /// <remarks>
        /// Parts are slices of the three collections <b>concatenated</b> —
        /// standalone, then projectiles, then beams — so a part may straddle two
        /// kinds and most parts of a busy turn carry only one. The client
        /// reassembles by concatenating in the same order, which is why neither
        /// side may think in whole kinds.
        /// <para>
        /// <paramref name="dropped"/> counts the tracks past what
        /// <see cref="PbjMessageCodec.MaxAssetPartsPerTurn"/> parts can hold.
        /// The cap exists because a peer sizes its accumulator from the part
        /// count it is told, so an unbounded turn is an allocation the sender
        /// chooses for the receiver.
        /// <para>
        /// It is a <b>backstop, not a policy</b>, and the distinction is worth
        /// stating so nobody reads intent into what gets dropped. Capacity is
        /// four thousand-odd tracks against a measured worst case of 1091 —
        /// and that 1091 was the host's whole unpruned accumulation over five
        /// turns, not one window's slice. What goes is simply the tail of the
        /// concatenated order, which falls on beams before projectiles before
        /// standalone effects; that is an artefact of the packing order rather
        /// than a judgement about which effects matter least. A turn that
        /// reaches this cap has left the shape any of this was measured
        /// against, and the log line saying so is the useful output.
        /// </para>
        /// </para>
        /// </remarks>
        public static IReadOnlyList<AssetCapture> Split(AssetCapture? capture, out int dropped)
        {
            dropped = 0;
            if (capture == null)
            {
                return NoParts;
            }

            var standalone = capture.Standalone;
            var projectiles = capture.Projectiles;
            var beams = capture.Beams;

            var total = standalone.Count + projectiles.Count + beams.Count;
            if (total == 0)
            {
                return NoParts;
            }

            var capacity = PbjMessageCodec.MaxAssetPartsPerTurn * PbjMessageCodec.MaxAssetsPerPart;
            if (total > capacity)
            {
                dropped = total - capacity;
                total = capacity;
            }

            var parts = new List<AssetCapture>();
            var taken = 0;
            var s = 0;
            var p = 0;
            var b = 0;
            while (taken < total)
            {
                var room = total - taken;
                if (room > PbjMessageCodec.MaxAssetsPerPart)
                {
                    room = PbjMessageCodec.MaxAssetsPerPart;
                }

                var partStandalone = new List<StandaloneAssetTrack>();
                var partProjectiles = new List<ProjectileAssetTrack>();
                var partBeams = new List<BeamAssetTrack>();

                while (room > 0 && s < standalone.Count)
                {
                    partStandalone.Add(standalone[s++]);
                    room--;
                    taken++;
                }
                while (room > 0 && p < projectiles.Count)
                {
                    partProjectiles.Add(projectiles[p++]);
                    room--;
                    taken++;
                }
                while (room > 0 && b < beams.Count)
                {
                    partBeams.Add(beams[b++]);
                    room--;
                    taken++;
                }

                parts.Add(new AssetCapture(partStandalone, partProjectiles, partBeams));
            }

            return parts;
        }

        // A null track and a track naming nothing arrive here as the same
        // thing, which is why the caller may pass `track?.Head.AssetKey`
        // without a null check of its own: both are effects the client cannot
        // look up, and NoKey says so once.
        private static AssetTrackFault CheckKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return AssetTrackFault.NoKey;
            }
            return key!.Length > PbjMessageCodec.MaxAssetKeyLength
                ? AssetTrackFault.KeyTooLong
                : AssetTrackFault.None;
        }
    }
}
