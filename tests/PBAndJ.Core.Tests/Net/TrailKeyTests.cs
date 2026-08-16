using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class TrailKeyTests
    {
        internal static TrailKey Sample(float time = 1f, float timeEnd = 2f)
        {
            return new TrailKey(
                time,
                timeEnd,
                new Vec3(1f, 2f, 3f),
                new Vec3(4f, 5f, 6f),
                new Vec3(7f, 8f, 9f),
                new Vec3(10f, 11f, 12f),
                new Vec3(13f, 14f, 15f),
                new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                0.5f,
                0.6f);
        }

        // Every getter read on purpose. Plain carrier types drop below 100%
        // METHOD coverage unless each one is touched, and stage A paid for
        // learning that the hard way — but the real reason to assert
        // exhaustively is that these five Vec3s are interchangeable to the
        // compiler and not to the renderer. A tangent swapped with a normal is
        // a trail lit from the wrong side, which no count and no log would show.
        [Fact]
        public void Constructor_KeepsEveryFieldInItsOwnSlot()
        {
            var key = Sample();

            Assert.Equal(1f, key.Time);
            Assert.Equal(2f, key.TimeEnd);
            Assert.Equal(new Vec3(1f, 2f, 3f), key.Position);
            Assert.Equal(new Vec3(4f, 5f, 6f), key.Velocity);
            Assert.Equal(new Vec3(7f, 8f, 9f), key.PerlinDirection);
            Assert.Equal(new Vec3(10f, 11f, 12f), key.Tangent);
            Assert.Equal(new Vec3(13f, 14f, 15f), key.Normal);
            Assert.Equal(new Vec4(0.1f, 0.2f, 0.3f, 0.4f), key.Colour);
            Assert.Equal(0.5f, key.Thickness);
            Assert.Equal(0.6f, key.Texcoord);
        }
    }

    public class ProjectileTrailTests
    {
        private static ProjectileAssetTrack Track(params TrailKey[] trail)
        {
            return new ProjectileAssetTrack(
                7,
                new AssetTrackHead("fx_bullet", 0f, 5f),
                new Vec3(1f, 1f, 1f),
                new[] { new TransformKey(0f, default, default), new TransformKey(1f, default, default) },
                trail);
        }

        // Empty, never null. A client must not have to tell "no trail" from
        // "trail not sent" — the game's own ApplyTime skips its trail block on
        // either, so the two are one instruction and one shape.
        [Fact]
        public void Trail_WhenNotSupplied_IsEmptyRatherThanNull()
        {
            var track = new ProjectileAssetTrack(
                1, new AssetTrackHead("fx", 0f, 1f), default, null);

            Assert.NotNull(track.Trail);
            Assert.Empty(track.Trail);
        }

        // The overwhelmingly common case, and the one the default argument
        // exists to keep cheap: stage A's call sites pass four arguments and
        // must keep compiling and keep meaning "no trail".
        [Fact]
        public void Trail_DefaultArgument_LeavesStageACallSitesMeaningNoTrail()
        {
            var track = new ProjectileAssetTrack(
                1,
                new AssetTrackHead("fx", 0f, 1f),
                default,
                new[] { new TransformKey(0f, default, default) });

            Assert.Empty(track.Trail);
        }

        // Emission order is the ribbon's geometry, not a sorting convenience:
        // SetPoints treats the LAST point as the head and snaps it to the
        // instance's transform, so a reversed list turns the trail inside out.
        [Fact]
        public void Trail_PreservesEmissionOrder()
        {
            var track = Track(
                TrailKeyTests.Sample(time: 1f),
                TrailKeyTests.Sample(time: 2f),
                TrailKeyTests.Sample(time: 3f));

            Assert.Equal(3, track.Trail.Count);
            Assert.Equal(1f, track.Trail[0].Time);
            Assert.Equal(3f, track.Trail[2].Time);
        }
    }
}
