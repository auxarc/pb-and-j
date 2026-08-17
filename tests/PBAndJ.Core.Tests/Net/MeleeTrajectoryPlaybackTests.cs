using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class MeleeTrajectoryPlaybackTests
    {
        private static MeleeTrajectory Melee(float start, float end)
            => new MeleeTrajectory(start, end, true, "k", new Vec3(0f, 0f, 0f), new Vec3(1f, 0f, 0f));

        [Fact]
        public void Normalise_AtTheStart_IsZero()
        {
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 2f, out var t));
            Assert.Equal(0f, t);
        }

        [Fact]
        public void Normalise_AtTheEnd_IsOne()
        {
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 4f, out var t));
            Assert.Equal(1f, t);
        }

        [Fact]
        public void Normalise_Midway_IsAHalf()
        {
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 3f, out var t));
            Assert.Equal(0.5f, t);
        }

        [Fact]
        public void Normalise_BeforeTheStart_IsInactive()
        {
            Assert.False(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 1.99f, out var t));
            Assert.Equal(0f, t);
        }

        [Fact]
        public void Normalise_AfterTheEnd_IsInactive()
        {
            Assert.False(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 4.01f, out _));
        }

        [Fact]
        public void Normalise_IsInclusiveAtBothEnds_LikeTheGame()
        {
            // The game's own test is `timeStart <= t && timeEnd >= t`, and this
            // composes the Core helper that already encodes it rather than
            // restating the comparison — restating it is how the NaN discipline
            // below gets lost.
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 2f, out _));
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), 4f, out _));
        }

        [Fact]
        public void Normalise_OfAZeroDurationRecord_IsActiveAtZero()
        {
            // Degenerate but reachable: the division would be 0/0 = NaN, and a
            // NaN handed to AnimationCurve.Evaluate is undefined behaviour in
            // the game's own code. Zero is the well-defined start of the
            // animation, so a one-frame record shows its first frame.
            Assert.True(MeleeTrajectoryPlayback.TryNormalise(Melee(3f, 3f), 3f, out var t));
            Assert.Equal(0f, t);
        }

        [Fact]
        public void Normalise_OfANaNStamp_IsInactive()
        {
            // Hostile floats off the wire must fall inactive rather than
            // permanently active — the reason IsActiveAt exists in the shape it
            // does. A record that is always "active" would pin a shockwave on
            // screen for the whole fight.
            Assert.False(MeleeTrajectoryPlayback.TryNormalise(
                Melee(float.NaN, float.NaN), 3f, out _));
            Assert.False(MeleeTrajectoryPlayback.TryNormalise(Melee(2f, 4f), float.NaN, out _));
        }

        [Fact]
        public void Normalise_OfAnInvertedRecord_IsInactive()
        {
            // timeEnd before timeStart cannot satisfy an inclusive test at any
            // cursor, so this needs no arm of its own — asserted so a future
            // "tidy-up" that adds one is caught by a red test rather than by the
            // coverage gate.
            Assert.False(MeleeTrajectoryPlayback.TryNormalise(Melee(4f, 2f), 3f, out _));
        }
    }
}
