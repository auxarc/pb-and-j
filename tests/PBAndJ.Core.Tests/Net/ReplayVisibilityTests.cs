using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ReplayVisibilityTests
    {
        // No transition recorded: the unit looked, all window, like whatever the
        // snapshot says it ended as. This is the overwhelming majority of units
        // in any turn and it must cost nothing.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void WithNoTransition_HoldsTheEndState(bool endHidden, bool expected)
        {
            Assert.Equal(expected, ReplayVisibility.IsVisibleAt(
                endHidden, ReplayVisibility.None, ReplayVisibility.None, 2.5f));
        }

        // A reveal: hidden before the stamp, visible from it. The stamp is the
        // instant the host stopped hiding the unit, so the boundary belongs to
        // the visible side — matching the game's strict > comparison.
        [Theory]
        [InlineData(0f, false)]
        [InlineData(2.99f, false)]
        [InlineData(3f, true)]
        [InlineData(4f, true)]
        public void WithAReveal_IsHiddenUntilTheStamp(float time, bool expected)
        {
            Assert.Equal(expected, ReplayVisibility.IsVisibleAt(
                endHidden: false, hide: ReplayVisibility.None, reveal: 3f, time: time));
        }

        // A hide is the mirror: visible up to the stamp, hidden from it. Retreat
        // is the path that produces this, and it is an ordinary player action
        // rather than an exotic one.
        [Theory]
        [InlineData(0f, true)]
        [InlineData(2.99f, true)]
        [InlineData(3f, false)]
        [InlineData(4f, false)]
        public void WithAHide_IsVisibleUntilTheStamp(float time, bool expected)
        {
            Assert.Equal(expected, ReplayVisibility.IsVisibleAt(
                endHidden: true, hide: 3f, reveal: ReplayVisibility.None, time: time));
        }

        // Both slots, hide first. The game handles this one correctly and so
        // must we: visible, then hidden, then visible again. A single-inversion
        // rule — which an earlier revision of this design shipped — reports the
        // opening stretch as hidden, which is the one span the host drew.
        [Theory]
        [InlineData(0.5f, true)]
        [InlineData(1.5f, false)]
        [InlineData(3.5f, true)]
        public void WithAHideThenAReveal_ShowsHiddenOnlyBetweenThem(float time, bool expected)
        {
            Assert.Equal(expected, ReplayVisibility.IsVisibleAt(
                endHidden: false, hide: 1f, reveal: 3f, time: time));
        }

        // Both slots, reveal first. The game's else-if never reaches the reveal
        // while the hide is still ahead, so it draws the unit from the window
        // start. That is a defect in the game — but the host's own replay is the
        // reference for what the turn looked like, so we reproduce it rather
        // than improve on it. Diverging here would put the two machines out of
        // step for the sake of being right.
        [Theory]
        [InlineData(0.5f, true)]
        [InlineData(2.5f, true)]
        [InlineData(3.5f, false)]
        public void WithARevealThenAHide_MatchesTheGamesOwnMishandling(float time, bool expected)
        {
            Assert.Equal(expected, ReplayVisibility.IsVisibleAt(
                endHidden: true, hide: 3f, reveal: 1f, time: time));
        }

        // The hide slot wins while both are ahead of the requested time, which
        // is what the game's if/else-if ordering means and is the whole reason
        // the two cases above differ.
        [Fact]
        public void WithBothStampsAhead_TheHideWins()
        {
            Assert.True(ReplayVisibility.IsVisibleAt(
                endHidden: false, hide: 5f, reveal: 4f, time: 1f));
        }

        [Fact]
        public void None_IsNotATimeAnyWindowContains()
        {
            // Sentinel rather than a nullable: the value crosses the wire beside
            // a presence flag, and Core is netstandard2.0 without nullable
            // value-type ergonomics worth the churn here.
            Assert.True(float.IsNegativeInfinity(ReplayVisibility.None));
        }
    }
}
