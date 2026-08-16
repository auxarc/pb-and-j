using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>
    /// The send-side rules for trails and weapon lights — the ones that keep a
    /// frame decodable and a pose track alive.
    /// </summary>
    public class StageBPreparationTests
    {
        private static ProjectileAssetTrack Projectile(int trailPoints)
        {
            var trail = new TrailKey[trailPoints];
            for (var i = 0; i < trailPoints; i++)
            {
                trail[i] = TrailKeyTests.Sample(time: i, timeEnd: i + 1);
            }

            return new ProjectileAssetTrack(
                1,
                new AssetTrackHead("fx_bullet", 0f, 5f),
                new Vec3(1f, 1f, 1f),
                new[]
                {
                    new TransformKey(0f, default, default),
                    new TransformKey(1f, default, default),
                },
                trail);
        }

        // The cap is a wire-safety bound, so the thing that must be true is that
        // nothing over it ever reaches the writer — a part at every cap has to
        // stay decodable, and the reader refuses anything larger.
        [Fact]
        public void TryPrepare_TrailOverTheCap_IsThinnedToIt()
        {
            var fault = ReplayAssetParts.TryPrepare(
                Projectile(PbjMessageCodec.MaxTrailPointsPerTrack + 200), out var prepared);

            Assert.Equal(AssetTrackFault.None, fault);
            Assert.Equal(PbjMessageCodec.MaxTrailPointsPerTrack, prepared!.Trail.Count);
        }

        // Thinning keeps BOTH ends, and for a trail that is the whole reason it
        // is an acceptable degradation: the ribbon still spans its full length
        // and merely gets coarser. Dropping the oldest points instead would look
        // kinder and would truncate the ribbon in the frames right after the
        // muzzle, which is where anyone is looking.
        [Fact]
        public void TryPrepare_ThinnedTrail_KeepsTheOldestAndNewestPoints()
        {
            var count = PbjMessageCodec.MaxTrailPointsPerTrack + 200;
            ReplayAssetParts.TryPrepare(Projectile(count), out var prepared);

            Assert.Equal(0f, prepared!.Trail[0].Time);
            Assert.Equal(count - 1, prepared.Trail[prepared.Trail.Count - 1].Time);
        }

        // The measured shape: ~32 points. The cap exists for pathological input
        // and must be invisible to everything real.
        [Fact]
        public void TryPrepare_TrailAtTheMeasuredSize_IsUntouched()
        {
            ReplayAssetParts.TryPrepare(Projectile(32), out var prepared);

            Assert.Equal(32, prepared!.Trail.Count);
        }

        // Trails must not become a second reason a projectile fails to travel.
        // A projectile with no trail is the overwhelmingly common case.
        [Fact]
        public void TryPrepare_NoTrail_StillPreparesTheProjectile()
        {
            var fault = ReplayAssetParts.TryPrepare(Projectile(0), out var prepared);

            Assert.Equal(AssetTrackFault.None, fault);
            Assert.Empty(prepared!.Trail);
        }

        private static UnitPoseTrack PoseTrackWith(IReadOnlyList<UnitLightKey> lights)
        {
            var keys = new PoseKey[4];
            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = new PoseKey(i, false, false, new[] { new JointPose(default, default) });
            }

            return new UnitPoseTrack("unit_a", new[] { "joint" }, keys, lights);
        }

        [Fact]
        public void TryPrepare_CarriesLightsThrough()
        {
            var fault = PoseTracks.TryPrepare(
                PoseTrackWith(new[] { UnitLightKeyTests.Sample() }), out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Single(prepared!.Lights);
            Assert.Equal("arm_left", prepared.Lights[0].Socket);
        }

        // The asymmetry that matters: a light the client could never place must
        // not cost the unit its poses. A missing flash among flashes is
        // invisible; a mech that slides instead of walking reads as broken.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void TryPrepare_LightWithNoSocket_IsDroppedAndTheTrackSurvives(string? socket)
        {
            var fault = PoseTracks.TryPrepare(
                PoseTrackWith(new[]
                {
                    UnitLightKeyTests.Sample(socket: socket),
                    UnitLightKeyTests.Sample(socket: "arm_right"),
                }),
                out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Single(prepared!.Lights);
            Assert.Equal("arm_right", prepared.Lights[0].Socket);
            Assert.NotEmpty(prepared.Keys);
        }

        [Fact]
        public void TryPrepare_LightWithOverlongSocket_IsDroppedAndTheTrackSurvives()
        {
            var overlong = new string('s', PbjMessageCodec.MaxPoseNameLength + 1);

            var fault = PoseTracks.TryPrepare(
                PoseTrackWith(new[] { UnitLightKeyTests.Sample(socket: overlong) }),
                out var prepared);

            Assert.Equal(PoseTrackFault.None, fault);
            Assert.Empty(prepared!.Lights);
            Assert.NotEmpty(prepared.Keys);
        }

        [Fact]
        public void TryPrepare_LightsOverTheCap_AreThinnedToIt()
        {
            var lights = new UnitLightKey[PbjMessageCodec.MaxLightKeysPerUnit + 40];
            for (var i = 0; i < lights.Length; i++)
            {
                lights[i] = UnitLightKeyTests.Sample(time: i);
            }

            PoseTracks.TryPrepare(PoseTrackWith(lights), out var prepared);

            Assert.Equal(PbjMessageCodec.MaxLightKeysPerUnit, prepared!.Lights.Count);

            // Spread, not truncated: the last flash of the turn survives. A
            // truncating cap would leave every surviving flash in the opening
            // moments and the rest of the turn dark.
            Assert.Equal(lights.Length - 1, prepared.Lights[prepared.Lights.Count - 1].Time);
        }

        [Fact]
        public void TryPrepare_NoLights_LeavesAnEmptyList()
        {
            PoseTracks.TryPrepare(PoseTrackWith(new UnitLightKey[0]), out var prepared);

            Assert.Empty(prepared!.Lights);
        }
    }
}
