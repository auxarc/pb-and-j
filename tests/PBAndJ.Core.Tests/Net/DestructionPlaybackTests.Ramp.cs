using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The dissolve ramp: how far a wrecked part has dissolved at a given cursor,
    // including the two arguments that are not a time at all -- the negative stamp
    // of a part wrecked before the battle, and a NaN in either position.
    //
    // A separate class from the DestructionStateTests parts beside it; the two
    // shared one file before the split.
    public class DestructionRampTests
    {
        [Fact]
        public void Progress_BeforeTheWreck_IsNothing()
        {
            // Not "a small amount". A part the cursor has not reached has not
            // been hit, and any non-zero value here would zero its integrity a
            // frame early.
            Assert.Equal(0f, DestructionRamp.Progress(4f, 3.5f));
        }

        [Fact]
        public void Progress_AtTheWreckItself_IsNothing()
        {
            // The ramp opens at zero: the game's Clamp01 of (t - t) / 0.5.
            Assert.Equal(0f, DestructionRamp.Progress(4f, 4f));
        }

        [Fact]
        public void Progress_AfterTheFullRamp_IsComplete()
        {
            Assert.Equal(1f, DestructionRamp.Progress(4f, 4.5f));
            Assert.Equal(1f, DestructionRamp.Progress(4f, 40f));
        }

        [Fact]
        public void Progress_HalfWay_FollowsEaseOutSine()
        {
            // sin(0.5 * pi/2), not 0.5 — the curve is the visible half of this.
            Assert.Equal(
                (float)Math.Sin(0.5 * Math.PI / 2.0),
                DestructionRamp.Progress(4f, 4.25f),
                4);
        }

        [Fact]
        public void Progress_IsMonotonic_AcrossTheRamp()
        {
            var last = -1f;
            for (var i = 0; i <= 20; i++)
            {
                var value = DestructionRamp.Progress(0f, i * 0.025f);
                Assert.True(value >= last, "the ramp went backwards at step " + i);
                last = value;
            }
        }

        [Fact]
        public void Progress_OfAPreBattleWreck_IsCompleteFromAnyCursor()
        {
            // The spawn sentinel (UnitUtilities.cs:2041). A unit that arrives
            // with a part already gone must look that way on the first frame,
            // not ramp into it.
            Assert.Equal(1f, DestructionRamp.Progress(-100f, 0f));
        }

        [Fact]
        public void Progress_OfANaNCursor_IsNothing()
        {
            // The comparison is written negated for exactly this: NaN fails
            // every ordering test, so a naive `t <= 0` would fall through to the
            // sine and put a NaN into a shader property block.
            Assert.Equal(0f, DestructionRamp.Progress(1f, float.NaN));
            Assert.Equal(0f, DestructionRamp.Progress(float.NaN, 1f));
        }
    }
}
