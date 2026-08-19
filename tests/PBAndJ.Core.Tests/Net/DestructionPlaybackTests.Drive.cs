using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The driven table. ShouldDrive is the guard deciding whether a value is worth
    // writing to a visual at all, and whether this is the first write to it; Forget
    // drops an entry so the next drive counts as a first one again.
    //
    // One part of DestructionStateTests. Every test here drives the state directly,
    // so it uses none of the shared fixture in DestructionPlaybackTests.cs.
    public partial class DestructionStateTests
    {
        [Fact]
        public void ShouldDrive_TheFirstTime_SaysSo()
        {
            var state = new DestructionState();
            Assert.True(state.ShouldDrive("a", "core", 0.25f, out var first));
            Assert.True(first);
        }

        [Fact]
        public void ShouldDrive_ASecondTime_IsNoLongerFirst()
        {
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 0.25f, out _);

            Assert.True(state.ShouldDrive("a", "core", 0.75f, out var first));
            Assert.False(first);
        }

        [Fact]
        public void ShouldDrive_RefusesAValueThatBarelyMoved()
        {
            // The game's own 0.001 threshold. Every accepted write refreshes a
            // property block across every renderer of every socket visual, so an
            // unguarded per-frame drive is expensive in exactly the frames that
            // are already busy.
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 0.25f, out _);

            Assert.False(state.ShouldDrive("a", "core", 0.2505f, out var first));
            Assert.False(first);
        }

        [Fact]
        public void ShouldDrive_RefusesAnExactRepeat()
        {
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 0.25f, out _);

            Assert.False(state.ShouldDrive("a", "core", 0.25f, out _));
        }

        [Fact]
        public void ShouldDrive_AcceptsAValueThatMovedBackwards()
        {
            // Backwards happens on the un-wrecking path, so the guard is on
            // magnitude rather than on direction.
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 1f, out _);

            Assert.True(state.ShouldDrive("a", "core", 0f, out _));
        }

        [Fact]
        public void ShouldDrive_RefusesAPartItCannotName()
        {
            var state = new DestructionState();
            Assert.False(state.ShouldDrive(null, "core", 1f, out var a));
            Assert.False(a);
            Assert.False(state.ShouldDrive(string.Empty, "core", 1f, out _));
            Assert.False(state.ShouldDrive("a", null, 1f, out _));
            Assert.False(state.ShouldDrive("a", string.Empty, 1f, out _));
        }

        [Fact]
        public void ShouldDrive_KeepsTwoUnitsApart()
        {
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 1f, out _);

            Assert.True(state.ShouldDrive("b", "core", 1f, out var first));
            Assert.True(first);
        }

        [Fact]
        public void ShouldDrive_DoesNotLetTwoPairsCollideIntoOneKey()
        {
            // Joined on NUL rather than a printable separator. A collision reads
            // as "already driven", which is a part that silently never dissolves
            // — the hardest possible failure to notice.
            var state = new DestructionState();
            state.ShouldDrive("a", "b core", 1f, out _);

            Assert.True(state.ShouldDrive("a b", "core", 1f, out var first));
            Assert.True(first);
        }

        [Fact]
        public void Forget_MakesTheNextDriveAFirstOneAgain()
        {
            // For a caller whose drive threw after the value was recorded. At
            // rest the ramp stops moving, so a guard left holding a value the
            // visual never received would refuse every retry for ever.
            var state = new DestructionState();
            state.ShouldDrive("a", "core", 1f, out _);

            state.Forget("a", "core");

            Assert.True(state.ShouldDrive("a", "core", 1f, out var first));
            Assert.True(first);
        }

        [Fact]
        public void Forget_OfAPartItCannotName_DoesNothing()
        {
            var state = new DestructionState();
            state.Forget(null, "core");
            state.Forget(string.Empty, "core");
            state.Forget("a", null);
            state.Forget("a", string.Empty);
        }
    }
}
