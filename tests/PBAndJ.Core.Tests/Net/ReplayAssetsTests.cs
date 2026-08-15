using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ReplayAssetsTests
    {
        private static AssetTrackHead Head(float start = 1f, float end = 3f)
        {
            return new AssetTrackHead("fx_test", start, end);
        }

        [Fact]
        public void A_head_keeps_every_field_it_was_given()
        {
            var colour = new AssetColour(new Vec4(1f, 0f, 0f, 1f), new Vec4(0f, 0f, 1f, 0.5f));
            var head = new AssetTrackHead("fx_impact", 2f, 7f, 0.25f, colour);

            Assert.Equal("fx_impact", head.AssetKey);
            Assert.Equal(2f, head.TimeStart);
            Assert.Equal(7f, head.TimeEnd);
            Assert.Equal(0.25f, head.Hue);
            Assert.Equal(1f, head.Colour!.Value.From.X);
            Assert.Equal(0.5f, head.Colour!.Value.To.W);
        }

        [Fact]
        public void An_absent_hue_is_not_the_same_as_a_zero_one()
        {
            // Zero is a real instruction — leave the hue alone. Absence means the
            // effect keeps whatever its prefab serialised. Collapsing the two
            // would recolour effects the host never touched.
            Assert.Null(Head().Hue);
            Assert.Equal(0f, new AssetTrackHead("fx", 0f, 1f, 0f).Hue);
            Assert.NotNull(new AssetTrackHead("fx", 0f, 1f, 0f).Hue);
        }

        [Fact]
        public void An_absent_colour_is_distinguishable_from_a_black_one()
        {
            Assert.Null(Head().Colour);

            var black = new AssetColour(new Vec4(0f, 0f, 0f, 0f), new Vec4(0f, 0f, 0f, 0f));
            var head = new AssetTrackHead("fx", 0f, 1f, null, black);
            Assert.NotNull(head.Colour);
            Assert.Equal(0f, head.Colour!.Value.From.X);
        }

        [Fact]
        public void A_standalone_track_keeps_its_scale_verbatim()
        {
            // The field AssignAsset writes straight to transform.localScale. A
            // track that loses it renders an effect scaled to nothing, which is
            // indistinguishable from playback never running at all.
            var track = new StandaloneAssetTrack(
                7, Head(), new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(2.5f, 2.5f, 2.5f), new Vec4(0f, 1f, 0f, 4f), new Vec3(9f, 9f, 9f));

            Assert.Equal(7, track.Id);
            Assert.Equal(2.5f, track.Scale.X);
            Assert.Equal(2.5f, track.Scale.Z);
            Assert.Equal(4f, track.VelocityAndDecay.W);
            Assert.Equal(9f, track.PositionLocal.Y);
        }

        [Fact]
        public void A_projectile_track_with_no_keys_reads_as_empty_not_null()
        {
            // Null keys would throw at the one place that must not throw — the
            // effect pump, which loses every effect queued behind it.
            var track = new ProjectileAssetTrack(3, Head(), new Vec3(0f, 0f, 0f), null);
            Assert.NotNull(track.Keys);
            Assert.Empty(track.Keys);
        }

        [Fact]
        public void A_projectile_track_keeps_its_keys_in_order()
        {
            var keys = new[]
            {
                new TransformKey(1f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                new TransformKey(2f, new Vec3(5f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
            };
            var track = new ProjectileAssetTrack(11, Head(), new Vec3(1f, 1f, 1f), keys);

            Assert.Equal(11, track.Id);
            Assert.Equal(2, track.Keys.Count);
            Assert.Equal(1f, track.Keys[0].Time);
            Assert.Equal(5f, track.Keys[1].Position.X);
        }

        [Fact]
        public void A_beam_key_keeps_the_games_packed_parameter_triple()
        {
            // x and y go to fxHelperBeam.SetAll, z becomes the beam's length via
            // SetScale. Kept packed because the game never names them either.
            var key = new BeamKey(
                2f, new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f), new Vec3(0.5f, 0.75f, 40f));

            Assert.Equal(2f, key.Time);
            Assert.Equal(0.5f, key.Parameters.X);
            Assert.Equal(0.75f, key.Parameters.Y);
            Assert.Equal(40f, key.Parameters.Z);
        }

        [Fact]
        public void A_beam_track_with_no_keys_reads_as_empty_not_null()
        {
            var track = new BeamAssetTrack(2, Head(), null);
            Assert.NotNull(track.Keys);
            Assert.Empty(track.Keys);
            Assert.Equal(2, track.Id);
        }

        [Fact]
        public void A_beam_track_keeps_its_keys()
        {
            var keys = new[]
            {
                new BeamKey(0f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f)),
                new BeamKey(1f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 8f)),
            };
            var track = new BeamAssetTrack(5, Head(), keys);
            Assert.Equal(2, track.Keys.Count);
            Assert.Equal(8f, track.Keys[1].Parameters.Z);
        }

        [Fact]
        public void An_empty_capture_reports_itself_empty_without_nulls()
        {
            Assert.True(AssetCapture.None.IsEmpty);
            Assert.NotNull(AssetCapture.None.Standalone);
            Assert.NotNull(AssetCapture.None.Projectiles);
            Assert.NotNull(AssetCapture.None.Beams);
            Assert.Empty(AssetCapture.None.Standalone);
            Assert.Empty(AssetCapture.None.Projectiles);
            Assert.Empty(AssetCapture.None.Beams);
        }

        [Fact]
        public void A_capture_is_not_empty_when_any_one_kind_has_a_track()
        {
            var standalone = new StandaloneAssetTrack(
                1, Head(), new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f),
                new Vec4(0f, 0f, 0f, 0f), new Vec3(0f, 0f, 0f));

            Assert.False(new AssetCapture(new[] { standalone }, null, null).IsEmpty);
            Assert.False(new AssetCapture(
                null, new[] { new ProjectileAssetTrack(1, Head(), new Vec3(0f, 0f, 0f), null) }, null).IsEmpty);
            Assert.False(new AssetCapture(
                null, null, new[] { new BeamAssetTrack(1, Head(), null) }).IsEmpty);
        }

        [Fact]
        public void Every_field_of_every_kind_survives_construction()
        {
            // Deliberately exhaustive rather than spot-checked. These are pure
            // carriers, so the only way one can be wrong is by being dropped or
            // swapped with its neighbour — and a swapped position and rotation
            // is a projectile flying sideways, which no count or log would show.
            var head = new AssetTrackHead("fx_beam", 4f, 9f);

            var standalone = new StandaloneAssetTrack(
                42, head, new Vec3(1f, 2f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                new Vec3(4f, 5f, 6f), new Vec4(7f, 8f, 9f, 10f), new Vec3(11f, 12f, 13f));
            Assert.Equal("fx_beam", standalone.Head.AssetKey);
            Assert.Equal(4f, standalone.Head.TimeStart);
            Assert.Equal(9f, standalone.Head.TimeEnd);
            Assert.Equal(1f, standalone.Position.X);
            Assert.Equal(2f, standalone.Position.Y);
            Assert.Equal(3f, standalone.Position.Z);
            Assert.Equal(0.1f, standalone.Rotation.X);
            Assert.Equal(0.2f, standalone.Rotation.Y);
            Assert.Equal(0.3f, standalone.Rotation.Z);
            Assert.Equal(0.4f, standalone.Rotation.W);
            Assert.Equal(4f, standalone.Scale.X);
            Assert.Equal(7f, standalone.VelocityAndDecay.X);
            Assert.Equal(11f, standalone.PositionLocal.X);

            var projectile = new ProjectileAssetTrack(
                43, head, new Vec3(0.5f, 0.6f, 0.7f), null);
            Assert.Equal("fx_beam", projectile.Head.AssetKey);
            Assert.Equal(0.5f, projectile.Scale.X);
            Assert.Equal(0.6f, projectile.Scale.Y);
            Assert.Equal(0.7f, projectile.Scale.Z);

            var beam = new BeamAssetTrack(44, head, null);
            Assert.Equal("fx_beam", beam.Head.AssetKey);
            Assert.Equal(9f, beam.Head.TimeEnd);

            var key = new BeamKey(
                1.5f, new Vec3(20f, 21f, 22f), new Vec4(0.5f, 0.6f, 0.7f, 0.8f),
                new Vec3(30f, 31f, 32f));
            Assert.Equal(20f, key.Position.X);
            Assert.Equal(21f, key.Position.Y);
            Assert.Equal(22f, key.Position.Z);
            Assert.Equal(0.5f, key.Rotation.X);
            Assert.Equal(0.6f, key.Rotation.Y);
            Assert.Equal(0.7f, key.Rotation.Z);
            Assert.Equal(0.8f, key.Rotation.W);

            var colour = new AssetColour(new Vec4(1f, 2f, 3f, 4f), new Vec4(5f, 6f, 7f, 8f));
            Assert.Equal(1f, colour.From.X);
            Assert.Equal(5f, colour.To.X);
        }

        [Fact]
        public void A_capture_built_from_nulls_is_empty_rather_than_throwing()
        {
            var capture = new AssetCapture(null, null, null);
            Assert.True(capture.IsEmpty);
            Assert.Empty(capture.Standalone);
        }
    }
}
