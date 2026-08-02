using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class KeyframePlaybackTests
    {
        private static readonly Vec4 Identity = new Vec4(0f, 0f, 0f, 1f);

        // A quarter turn about Y: (0, sin45, 0, cos45).
        private static readonly Vec4 YawNinety =
            new Vec4(0f, 0.70710678f, 0f, 0.70710678f);

        private static UnitTrack Track(params TransformKey[] keys) =>
            new UnitTrack("pb_mech_01", keys);

        private static TransformKey Key(float time, float x, Vec4 rotation) =>
            new TransformKey(time, new Vec3(x, 0f, 0f), rotation);

        [Fact]
        public void TrySample_EmptyTrack_ReportsNothingToSample()
        {
            Assert.False(KeyframePlayback.TrySample(
                Track(), 0f, out var position, out var rotation));

            // Identity rather than a zero quaternion: a caller that ignores the
            // bool gets something it can safely hand to a transform.
            Assert.Equal(0f, position.X);
            Assert.Equal(1f, rotation.W);
        }

        [Fact]
        public void TrySample_NullTrack_ReportsNothingToSample()
        {
            Assert.False(KeyframePlayback.TrySample(null, 0f, out _, out _));
        }

        // A unit that never moved records one key. It still has a position, and
        // the client still has to place it.
        [Fact]
        public void TrySample_SingleKey_ReturnsThatKeyAtEveryTime()
        {
            var track = Track(Key(10f, 7f, YawNinety));

            Assert.True(KeyframePlayback.TrySample(track, -5f, out var before, out _));
            Assert.True(KeyframePlayback.TrySample(track, 99f, out var after, out var rotation));
            Assert.Equal(7f, before.X);
            Assert.Equal(7f, after.X);
            Assert.Equal(YawNinety.Y, rotation.Y, 5);
        }

        [Fact]
        public void TrySample_AtAKeyTime_ReturnsThatKeyExactly()
        {
            var track = Track(Key(0f, 0f, Identity), Key(1f, 10f, Identity), Key(2f, 30f, Identity));

            Assert.True(KeyframePlayback.TrySample(track, 1f, out var position, out _));
            Assert.Equal(10f, position.X);
        }

        [Fact]
        public void TrySample_BetweenKeys_InterpolatesPositionLinearly()
        {
            var track = Track(Key(0f, 0f, Identity), Key(2f, 10f, Identity));

            Assert.True(KeyframePlayback.TrySample(track, 0.5f, out var position, out _));
            Assert.Equal(2.5f, position.X);
        }

        // The ordinary case for a real track: a turn records ~50 keys, so almost
        // every sample falls in a segment well past the first one.
        [Fact]
        public void TrySample_InAMiddleSegment_UsesThatSegmentsNeighbours()
        {
            var track = Track(
                Key(0f, 0f, Identity),
                Key(1f, 10f, Identity),
                Key(2f, 20f, Identity),
                Key(3f, 100f, Identity));

            Assert.True(KeyframePlayback.TrySample(track, 2.25f, out var position, out _));
            Assert.Equal(40f, position.X);
        }

        [Fact]
        public void TrySample_InterpolatesEveryPositionAxis()
        {
            var track = new UnitTrack("u", new[]
            {
                new TransformKey(0f, new Vec3(0f, 0f, 0f), Identity),
                new TransformKey(1f, new Vec3(2f, 4f, 8f), Identity),
            });

            Assert.True(KeyframePlayback.TrySample(track, 0.5f, out var position, out _));
            Assert.Equal(1f, position.X);
            Assert.Equal(2f, position.Y);
            Assert.Equal(4f, position.Z);
        }

        // Before the first key and after the last, playback holds rather than
        // extrapolating: a track that ends early means the unit stopped being
        // recorded, not that it kept going.
        [Fact]
        public void TrySample_OutsideTheTrack_ClampsToTheEndpoints()
        {
            var track = Track(Key(5f, 1f, Identity), Key(6f, 2f, Identity));

            Assert.True(KeyframePlayback.TrySample(track, 0f, out var before, out _));
            Assert.True(KeyframePlayback.TrySample(track, 100f, out var after, out _));
            Assert.Equal(1f, before.X);
            Assert.Equal(2f, after.X);
        }

        [Fact]
        public void TrySample_NormalisesTheInterpolatedRotation()
        {
            var track = Track(Key(0f, 0f, Identity), Key(1f, 0f, YawNinety));

            Assert.True(KeyframePlayback.TrySample(track, 0.5f, out _, out var rotation));

            var magnitude = (float)System.Math.Sqrt(
                rotation.X * rotation.X + rotation.Y * rotation.Y +
                rotation.Z * rotation.Z + rotation.W * rotation.W);
            Assert.Equal(1f, magnitude, 5);
        }

        // Matches Unity's Quaternion.Lerp, which takes the shortest arc. Without
        // the flip a unit turning through the q/-q boundary spins the long way
        // round on the client and looks nothing like what the host did.
        [Fact]
        public void TrySample_TakesTheShorterArcBetweenEquivalentRotations()
        {
            var negatedIdentity = new Vec4(0f, 0f, 0f, -1f);
            var track = Track(Key(0f, 0f, Identity), Key(1f, 0f, negatedIdentity));

            Assert.True(KeyframePlayback.TrySample(track, 0.5f, out _, out var rotation));

            // Identity and its negation are the same rotation, so every point
            // between them must be that rotation too — not a 360-degree sweep.
            Assert.Equal(0f, rotation.X, 5);
            Assert.Equal(0f, rotation.Y, 5);
            Assert.Equal(0f, rotation.Z, 5);
            Assert.Equal(1f, System.Math.Abs(rotation.W), 5);
        }

        // The recorder really does stamp two keys with the same time: its own
        // final sample at execution end, and the one capture appends from the
        // authoritative read. Neither a divide by zero nor a NaN may come out of
        // that, at any time in the window.
        [Fact]
        public void TrySample_DuplicateTimedKeys_NeverDivideByZero()
        {
            var track = Track(Key(0f, 0f, Identity), Key(1f, 5f, Identity), Key(1f, 9f, Identity));

            // At the duplicated instant the last key wins — that is the one read
            // from the same place the snapshot is, so playback ends where the
            // correction already put the unit.
            Assert.True(KeyframePlayback.TrySample(track, 1f, out var atEnd, out _));
            Assert.Equal(9f, atEnd.X);

            // And nothing in between produces a NaN.
            for (var time = 0f; time <= 1f; time += 0.05f)
            {
                Assert.True(KeyframePlayback.TrySample(track, time, out var mid, out var rotation));
                Assert.False(float.IsNaN(mid.X));
                Assert.False(float.IsNaN(rotation.W));
            }
        }

        // The last key of every track is captured from the same read the
        // snapshot is, so sampling at the end of the window must land exactly on
        // it. This is the property the harness gate checks end to end.
        [Fact]
        public void TrySample_AtTheEndOfTheWindow_IsTheFinalKeyExactly()
        {
            var track = Track(Key(15f, 0f, Identity), Key(15.1f, 3f, Identity), Key(20f, 42f, YawNinety));

            Assert.True(KeyframePlayback.TrySample(track, 20f, out var position, out var rotation));
            Assert.Equal(42f, position.X);
            Assert.Equal(YawNinety.Y, rotation.Y, 5);
        }

        // Bit-exact, not merely close. A recorded quaternion is rarely of exactly
        // unit length, so re-normalising it at an endpoint would shift it by a
        // few ulps — enough to break the equality against the snapshot that the
        // whole design rests on.
        [Fact]
        public void TrySample_AtAKey_ReturnsTheRecordedRotationUnaltered()
        {
            // Deliberately not unit length: this is what a real recording looks
            // like once it has been through a lerp inside the engine.
            var drifted = new Vec4(0f, 0.7071f, 0f, 0.7071f);
            var track = Track(Key(0f, 0f, Identity), Key(1f, 42f, drifted));

            Assert.True(KeyframePlayback.TrySample(track, 1f, out _, out var rotation));
            Assert.Equal(drifted.X, rotation.X);
            Assert.Equal(drifted.Y, rotation.Y);
            Assert.Equal(drifted.Z, rotation.Z);
            Assert.Equal(drifted.W, rotation.W);
        }

        // An all-zero quaternion is not a rotation and cannot be normalised. It
        // should never reach us, but a corrupt or default-constructed one must
        // produce identity rather than NaN in somebody's transform.
        [Fact]
        public void TrySample_DegenerateRotation_FallsBackToIdentity()
        {
            var zero = new Vec4(0f, 0f, 0f, 0f);
            var track = Track(Key(0f, 0f, zero), Key(1f, 10f, zero));

            Assert.True(KeyframePlayback.TrySample(track, 0.5f, out var position, out var rotation));
            Assert.Equal(5f, position.X);
            Assert.Equal(0f, rotation.X);
            Assert.Equal(0f, rotation.Y);
            Assert.Equal(0f, rotation.Z);
            Assert.Equal(1f, rotation.W);

            // And landing exactly on such a key, where nothing is interpolated,
            // must still not hand a zero quaternion to a transform.
            Assert.True(KeyframePlayback.TrySample(track, 0f, out _, out var atKey));
            Assert.Equal(1f, atKey.W);
        }

        [Fact]
        public void TrySample_DegenerateRotationOnOneKeyOnly_StillNormalises()
        {
            var zero = new Vec4(0f, 0f, 0f, 0f);
            var track = Track(Key(0f, 0f, zero), Key(1f, 0f, YawNinety));

            Assert.True(KeyframePlayback.TrySample(track, 0.9f, out _, out var rotation));

            var magnitude = (float)System.Math.Sqrt(
                rotation.X * rotation.X + rotation.Y * rotation.Y +
                rotation.Z * rotation.Z + rotation.W * rotation.W);
            Assert.Equal(1f, magnitude, 5);
        }
    }
}
