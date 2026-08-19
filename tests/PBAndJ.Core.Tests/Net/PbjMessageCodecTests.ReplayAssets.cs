using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Replayed effects (M14): standalone, projectile and beam tracks, exhaustively
    // per field, because a swapped position and rotation is a projectile flying
    // sideways and no count or round-trip-the-type test would show it. Then the
    // absent-versus-zero hue, the empty capture, the decode caps, and the
    // frame-limit bound.
    // TrailedProjectile lives here rather than in the shared fixture because both
    // of its call sites are in this part.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        private static ProjectileAssetTrack TrailedProjectile(int id, int keys, int points)
        {
            var plain = ProjectileAsset(id, keys);
            var trail = new TrailKey[points];
            for (var i = 0; i < points; i++)
            {
                // Every field a different value, and none of them equal to any
                // other field's. Five Vec3s in a row are interchangeable to the
                // compiler; a tangent that arrives in the normal's slot is a
                // trail lit from the wrong side.
                trail[i] = new TrailKey(
                    i * 0.1f,
                    i * 0.1f + 0.5f,
                    new Vec3(i + 1, i + 2, i + 3),
                    new Vec3(i + 10, i + 11, i + 12),
                    new Vec3(i + 20, i + 21, i + 22),
                    new Vec3(i + 30, i + 31, i + 32),
                    new Vec3(i + 40, i + 41, i + 42),
                    new Vec4(0.11f, 0.22f, 0.33f, 0.44f),
                    0.9f + i,
                    0.05f + i);
            }

            return new ProjectileAssetTrack(
                plain.Id, plain.Head, plain.Scale, plain.Keys, trail);
        }

        // --- replayed effects (M14) ---

        // Exhaustive on purpose. A swapped position and rotation is a
        // projectile flying sideways and a swapped scale is an effect nobody
        // can see, and no count, log line or round-trip-the-type test would
        // show either.
        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfAStandaloneTrack()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(9, 2, 5, new AssetCapture(
                new[] { StandaloneAsset(11) }, null, null)));

            Assert.Equal(9, decoded.Turn);
            Assert.Equal(2, decoded.PartIndex);
            Assert.Equal(5, decoded.PartCount);

            var track = Assert.Single(decoded.Assets.Standalone);
            Assert.Equal(11, track.Id);
            Assert.Equal("fx_impact_11", track.Head.AssetKey);
            Assert.Equal(1.5f, track.Head.TimeStart);
            Assert.Equal(3.25f, track.Head.TimeEnd);
            Assert.Null(track.Head.Hue);
            Assert.Null(track.Head.Colour);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { track.Position.X, track.Position.Y, track.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { track.Rotation.X, track.Rotation.Y, track.Rotation.Z, track.Rotation.W });
            Assert.Equal(new[] { 1.5f, 1.5f, 1.5f }, new[] { track.Scale.X, track.Scale.Y, track.Scale.Z });
            Assert.Equal(
                new[] { 4f, 5f, 6f, 0.75f },
                new[]
                {
                    track.VelocityAndDecay.X, track.VelocityAndDecay.Y,
                    track.VelocityAndDecay.Z, track.VelocityAndDecay.W,
                });
            Assert.Equal(
                new[] { 7f, 8f, 9f },
                new[] { track.PositionLocal.X, track.PositionLocal.Y, track.PositionLocal.Z });
        }

        // Ten fields, each read back into a rebuilt AraTrail.Point by the game's
        // own ApplyTime. Asserted one at a time and against distinct values,
        // because the five Vec3s are the same type in the same run and the codec
        // would happily swap two of them forever.
        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfATrailPoint()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(4, 0, 1, new AssetCapture(
                null, new[] { TrailedProjectile(7, 2, 1) }, null)));

            var point = Assert.Single(Assert.Single(decoded.Assets.Projectiles).Trail);
            Assert.Equal(0f, point.Time);
            Assert.Equal(0.5f, point.TimeEnd);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { point.Position.X, point.Position.Y, point.Position.Z });
            Assert.Equal(new[] { 10f, 11f, 12f }, new[] { point.Velocity.X, point.Velocity.Y, point.Velocity.Z });
            Assert.Equal(
                new[] { 20f, 21f, 22f },
                new[] { point.PerlinDirection.X, point.PerlinDirection.Y, point.PerlinDirection.Z });
            Assert.Equal(new[] { 30f, 31f, 32f }, new[] { point.Tangent.X, point.Tangent.Y, point.Tangent.Z });
            Assert.Equal(new[] { 40f, 41f, 42f }, new[] { point.Normal.X, point.Normal.Y, point.Normal.Z });
            Assert.Equal(
                new[] { 0.11f, 0.22f, 0.33f, 0.44f },
                new[] { point.Colour.X, point.Colour.Y, point.Colour.Z, point.Colour.W });
            Assert.Equal(0.9f, point.Thickness);
            Assert.Equal(0.05f, point.Texcoord);
        }

        // Emission order is the ribbon's geometry: SetPoints treats the last
        // point as the head. A codec that reversed the list would pass every
        // per-field assertion above and still turn every trail inside out.
        [Fact]
        public void RoundTrip_ReplayAssets_KeepsTrailPointsInEmissionOrder()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(4, 0, 1, new AssetCapture(
                null, new[] { TrailedProjectile(7, 2, 4) }, null)));

            var trail = Assert.Single(decoded.Assets.Projectiles).Trail;
            Assert.Equal(4, trail.Count);
            Assert.Equal(new[] { 0f, 0.1f, 0.2f, 0.3f }, new[]
            {
                trail[0].Time, trail[1].Time, trail[2].Time, trail[3].Time,
            });
        }

        // The common case by a wide margin — 106 of 109 measured projectiles —
        // and the one that must not cost anything or break stage A's shape.
        [Fact]
        public void RoundTrip_ReplayAssets_AProjectileWithoutATrailStaysEmpty()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, new[] { ProjectileAsset(7, 2) }, null)));

            Assert.Empty(Assert.Single(decoded.Assets.Projectiles).Trail);
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfAProjectileKey()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, new[] { ProjectileAsset(7, 2) }, null)));

            var track = Assert.Single(decoded.Assets.Projectiles);
            Assert.Equal(7, track.Id);
            Assert.Equal("fx_bullet_7", track.Head.AssetKey);
            Assert.Equal(new[] { 2f, 2f, 2f }, new[] { track.Scale.X, track.Scale.Y, track.Scale.Z });
            Assert.Equal(2, track.Keys.Count);

            var key = track.Keys[1];
            Assert.Equal(0.05f, key.Time);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { key.Position.X, key.Position.Y, key.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W });
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfABeamKey()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, null, new[] { BeamAsset(8, 2) })));

            var track = Assert.Single(decoded.Assets.Beams);
            Assert.Equal(8, track.Id);
            Assert.Equal("fx_beam_8", track.Head.AssetKey);
            Assert.Equal(2, track.Keys.Count);

            var key = track.Keys[1];
            Assert.Equal(0.05f, key.Time);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { key.Position.X, key.Position.Y, key.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W });
            Assert.Equal(
                new[] { 0.5f, 0.25f, 3f },
                new[] { key.Parameters.X, key.Parameters.Y, key.Parameters.Z });
        }

        // Absence and zero are different instructions: an absent hue leaves the
        // prefab's own alone, a hue of zero flattens it. A sentinel float could
        // not tell them apart inside a 0..1 block, which is why both blocks are
        // present-flagged.
        [Fact]
        public void RoundTrip_ReplayAssets_KeepsAnAbsentHueApartFromAZeroOne()
        {
            var zero = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1, hue: 0f) }, null, null)));
            var absent = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1) }, null, null)));

            Assert.Equal(0f, zero.Assets.Standalone[0].Head.Hue);
            Assert.Null(absent.Assets.Standalone[0].Head.Hue);
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesBothEndsOfAColour()
        {
            var colour = new AssetColour(
                new Vec4(0.1f, 0.2f, 0.3f, 1f), new Vec4(0.9f, 0.8f, 0.7f, 0.5f));

            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1, hue: 0.25f, colour: colour) }, null, null)));

            var head = decoded.Assets.Standalone[0].Head;
            Assert.Equal(0.25f, head.Hue);
            Assert.NotNull(head.Colour);
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 1f },
                new[]
                {
                    head.Colour!.Value.From.X, head.Colour.Value.From.Y,
                    head.Colour.Value.From.Z, head.Colour.Value.From.W,
                });
            Assert.Equal(
                new[] { 0.9f, 0.8f, 0.7f, 0.5f },
                new[]
                {
                    head.Colour.Value.To.X, head.Colour.Value.To.Y,
                    head.Colour.Value.To.Z, head.Colour.Value.To.W,
                });
        }

        [Fact]
        public void RoundTrip_ReplayAssetsWithNoCapture_ReadsBackAsAnEmptyOne()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, null));

            Assert.True(decoded.Assets.IsEmpty);
        }

        [Fact]
        public void Decode_ReplayAssetsWithTooManyParts_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.ReplayAssets);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxAssetPartsPerTurn + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Decode_ReplayAssetsWithTooManyTracksOfOneKind_Throws(int kind)
        {
            var writer = ReplayAssetsHeader();
            for (var i = 0; i < kind; i++)
            {
                writer.WriteInt32(0);
            }
            writer.WriteInt32(PbjMessageCodec.MaxAssetsPerPart + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_AProjectileWithTooManyKeys_Throws()
        {
            var writer = ReplayAssetsHeader();
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            WriteAssetTrackHeadForTest(writer);
            writer.WriteSingle(1f);
            writer.WriteSingle(1f);
            writer.WriteSingle(1f);
            writer.WriteInt32(PbjMessageCodec.MaxAssetKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_ABeamWithTooManyKeys_Throws()
        {
            var writer = ReplayAssetsHeader();
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            WriteAssetTrackHeadForTest(writer);
            writer.WriteInt32(PbjMessageCodec.MaxAssetKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        private static PbjWriter ReplayAssetsHeader()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.ReplayAssets);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            return writer;
        }

        // id, then the head: key, start, end, and both present-flags clear.
        private static void WriteAssetTrackHeadForTest(PbjWriter writer)
        {
            writer.WriteInt32(1);
            writer.WriteString("fx");
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);
            writer.WriteBool(false);
            writer.WriteBool(false);
        }

        // The size claim the counted-parts decision rests on, proved at three
        // full lists rather than at the one a sender packs — a decoder cannot
        // assume the sender packed the way we do, so the bound has to hold for
        // anything it will accept. Names are at their cap too, for the reason
        // the pose sibling above spells out.
        [Fact]
        public void Encode_ReplayAssetsAtEveryCap_StaysUnderTheFrameLimit()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength);
            var colour = new AssetColour(
                new Vec4(1f, 1f, 1f, 1f), new Vec4(1f, 1f, 1f, 1f));
            var head = new AssetTrackHead(key, 0f, 5f, 0.5f, colour);

            var standalone = new StandaloneAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < standalone.Length; i++)
            {
                standalone[i] = new StandaloneAssetTrack(
                    i, head, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 0f), new Vec3(0f, 0f, 0f));
            }

            var transforms = new TransformKey[PbjMessageCodec.MaxAssetKeysPerTrack];
            for (var i = 0; i < transforms.Length; i++)
            {
                transforms[i] = new TransformKey(i, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f));
            }
            // Trails at their cap too, and this is the term that actually
            // decides the bound: a trail point is 92 bytes against a transform
            // key's 32, so MaxTrailPointsPerTrack is what stands between a full
            // part and the 1 MiB frame limit. Stage A's version of this test
            // predates trails, and leaving it unchanged would have gone on
            // "proving" a bound for a message shape we no longer send.
            var trail = new TrailKey[PbjMessageCodec.MaxTrailPointsPerTrack];
            for (var i = 0; i < trail.Length; i++)
            {
                trail[i] = new TrailKey(
                    i, i + 1,
                    new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f),
                    new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f),
                    new Vec4(1f, 1f, 1f, 1f), 1f, 1f);
            }
            var projectiles = new ProjectileAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < projectiles.Length; i++)
            {
                projectiles[i] = new ProjectileAssetTrack(
                    i, head, new Vec3(1f, 1f, 1f), transforms, trail);
            }

            var beamKeys = new BeamKey[PbjMessageCodec.MaxAssetKeysPerTrack];
            for (var i = 0; i < beamKeys.Length; i++)
            {
                beamKeys[i] = new BeamKey(
                    i, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f));
            }
            var beams = new BeamAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < beams.Length; i++)
            {
                beams[i] = new BeamAssetTrack(i, head, beamKeys);
            }

            var bytes = PbjMessageCodec.Encode(new ReplayAssetsMessage(
                1, 0, PbjMessageCodec.MaxAssetPartsPerTurn,
                new AssetCapture(standalone, projectiles, beams)));

            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a fully capped asset part was {bytes.Length} bytes, over the frame limit");

            // Pinned, not merely bounded. A fully capped part measures ~712 KiB
            // of the 1 MiB limit, so the real headroom is about 1.44x and the
            // trail term alone is half the message — raising
            // MaxTrailPointsPerTrack past ~112 breaches the frame. "Under the
            // limit" would still pass at 99% full and tell nobody the next cap
            // bump is the one that breaks decode.
            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength * 3 / 4,
                $"a fully capped asset part was {bytes.Length} bytes, which has eaten "
                    + "the headroom the trail cap was sized to leave");
        }
    }
}
