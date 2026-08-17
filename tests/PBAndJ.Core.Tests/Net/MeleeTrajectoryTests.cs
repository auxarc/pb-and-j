using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class MeleeTrajectoryTests
    {
        internal static MeleeTrajectory Sample(
            float timeStart = 2f, float timeEnd = 3.5f, bool partUsed = true)
        {
            return new MeleeTrajectory(
                timeStart,
                timeEnd,
                partUsed,
                "shockwave_heavy",
                new Vec3(1f, 2f, 3f),
                new Vec3(4f, 5f, 6f));
        }

        // Every getter, against distinct values. The trap here is the position
        // pair: posStart and posEnd are the same type and arrive adjacent, and
        // swapping them drags the shockwave along the reverse of the mech's
        // path — which no counter and no log line could show.
        [Fact]
        public void Constructor_KeepsEveryFieldInItsOwnSlot()
        {
            var melee = Sample();

            Assert.Equal(2f, melee.TimeStart);
            Assert.Equal(3.5f, melee.TimeEnd);
            Assert.True(melee.PartUsed);
            Assert.Equal("shockwave_heavy", melee.ShockwaveKey);
            Assert.Equal(new Vec3(1f, 2f, 3f), melee.PosStart);
            Assert.Equal(new Vec3(4f, 5f, 6f), melee.PosEnd);
        }

        [Fact]
        public void PartUsed_IsCarriedFalseAsWellAsTrue()
        {
            // It selects which time-remap curve the client evaluates
            // (timeRemapMeleeStandard vs timeRemapMeleeFallback), so the false
            // case is a different animation, not an absent one.
            Assert.False(Sample(partUsed: false).PartUsed);
        }

        [Fact]
        public void ShockwaveKey_MayBeNull()
        {
            // The host records whatever the action carried. A null key resolves
            // to a null entry client-side, which the game's own melee helper
            // early-returns on — so this must survive the wire rather than
            // being rejected here.
            var melee = new MeleeTrajectory(0f, 1f, false, null, default, default);
            Assert.Null(melee.ShockwaveKey);
        }
    }
}
