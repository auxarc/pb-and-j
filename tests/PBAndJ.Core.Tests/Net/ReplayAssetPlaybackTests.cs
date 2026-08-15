using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ReplayAssetPlaybackTests
    {
        [Theory]
        [InlineData(1f, 3f, 0.9f, false)]
        [InlineData(1f, 3f, 1f, true)]      // inclusive at the start, as the game has it
        [InlineData(1f, 3f, 2f, true)]
        [InlineData(1f, 3f, 3f, true)]      // and inclusive at the end
        [InlineData(1f, 3f, 3.1f, false)]
        public void IsActiveAt_matches_the_games_own_inclusive_test(
            float start, float end, float time, bool expected)
        {
            Assert.Equal(expected, ReplayAssetPlayback.IsActiveAt(start, end, time));
        }

        [Fact]
        public void A_zero_length_track_is_active_at_its_own_instant()
        {
            // The game stamps these itself when an effect begins and ends inside
            // one sample, so refusing them would drop real effects.
            Assert.True(ReplayAssetPlayback.IsActiveAt(2f, 2f, 2f));
            Assert.False(ReplayAssetPlayback.IsActiveAt(2f, 2f, 2.01f));
        }

        [Theory]
        [InlineData(1f, 3f, 0.5f, AssetTrackPhase.Pending)]
        [InlineData(1f, 3f, 1f, AssetTrackPhase.Active)]
        [InlineData(1f, 3f, 3f, AssetTrackPhase.Active)]
        [InlineData(1f, 3f, 4f, AssetTrackPhase.Expired)]
        public void PhaseAt_separates_not_yet_from_never_again(
            float start, float end, float time, AssetTrackPhase expected)
        {
            Assert.Equal(expected, ReplayAssetPlayback.PhaseAt(start, end, time));
        }

        [Fact]
        public void An_effect_that_lives_entirely_between_two_frames_is_still_shown()
        {
            // The rule a point test gets wrong. A muzzle flash is under a tenth
            // of a second; a frame is a thirtieth. The host showed it, so a
            // client sampling only at instants would step over it silently.
            Assert.False(ReplayAssetPlayback.IsActiveAt(1.01f, 1.05f, 1.00f));
            Assert.False(ReplayAssetPlayback.IsActiveAt(1.01f, 1.05f, 1.10f));
            Assert.True(ReplayAssetPlayback.CrossedDuring(1.01f, 1.05f, 1.00f, 1.10f));
        }

        [Theory]
        [InlineData(5f, 6f, 1f, 2f, false)]   // wholly after the interval
        [InlineData(0f, 0.5f, 1f, 2f, false)] // wholly before it
        [InlineData(1.5f, 1.6f, 1f, 2f, true)]
        [InlineData(0f, 1.5f, 1f, 2f, true)]  // straddles the start
        [InlineData(1.5f, 9f, 1f, 2f, true)]  // straddles the end
        [InlineData(1f, 2f, 1f, 2f, true)]    // exactly the interval
        public void CrossedDuring_is_an_interval_overlap(
            float start, float end, float from, float to, bool expected)
        {
            Assert.Equal(expected, ReplayAssetPlayback.CrossedDuring(start, end, from, to));
        }

        [Fact]
        public void CrossedDuring_tolerates_a_cursor_that_moved_backwards()
        {
            // A scrub, or a window restarting. Ordering the pair rather than
            // trusting it keeps this from silently answering false for every
            // track, which would look exactly like "no VFX on the client".
            Assert.True(ReplayAssetPlayback.CrossedDuring(1.5f, 1.6f, 2f, 1f));
            Assert.False(ReplayAssetPlayback.CrossedDuring(5f, 6f, 2f, 1f));
        }

        [Fact]
        public void CrossedDuring_degrades_to_a_point_test_on_the_first_frame()
        {
            // previous == current, which is what the first frame of a window
            // passes. Nothing was skipped before the window began.
            Assert.True(ReplayAssetPlayback.CrossedDuring(1f, 3f, 2f, 2f));
            Assert.False(ReplayAssetPlayback.CrossedDuring(1f, 3f, 4f, 4f));
        }

        [Theory]
        [InlineData(10f, 12f, 5f, 10f, true)]   // touches the window's end
        [InlineData(0f, 5f, 5f, 10f, true)]     // touches its start
        [InlineData(6f, 7f, 5f, 10f, true)]
        [InlineData(11f, 12f, 5f, 10f, false)]
        [InlineData(0f, 4.9f, 5f, 10f, false)]
        public void OverlapsWindow_decides_what_the_capture_slice_holds(
            float start, float end, float windowStart, float windowEnd, bool expected)
        {
            Assert.Equal(
                expected, ReplayAssetPlayback.OverlapsWindow(start, end, windowStart, windowEnd));
        }

        [Fact]
        public void A_track_outliving_the_window_is_still_in_the_slice()
        {
            // Projectiles still in flight at turn end are stamped timeEnd =
            // simTime + 1f by the game's own fixup, so they always outlive the
            // window. Excluding them would drop every shot fired near the end of
            // a turn.
            Assert.True(ReplayAssetPlayback.OverlapsWindow(4.9f, 6f, 0f, 5f));
        }

        [Theory]
        [InlineData(2f, 5f, 3f)]
        [InlineData(2f, 2f, 0f)]
        [InlineData(2f, 1f, 0f)]    // clamped, never negative
        public void LocalTime_is_clamped_at_zero(float start, float time, float expected)
        {
            Assert.Equal(expected, ReplayAssetPlayback.LocalTime(start, time));
        }
    }
}
