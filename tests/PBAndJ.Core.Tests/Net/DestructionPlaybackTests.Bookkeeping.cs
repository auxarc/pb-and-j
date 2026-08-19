using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // What the state reports about itself, and what a Clear forgets. Count and
    // WreckedUnitCount are the part-level and unit-level halves of the only
    // cross-machine check this feature has: the two machines keep the same fact in
    // different places, the host in its ECS and a client only here.
    //
    // One part of DestructionStateTests; the shared fixture is in
    // DestructionPlaybackTests.cs.
    public partial class DestructionStateTests
    {
        [Fact]
        public void WreckedUnitCount_TracksWhatTheClientHasBeenTold()
        {
            // The unit-level half of the only cross-machine check this feature
            // has: a client never sets the component, so its ECS cannot answer.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", -1f), Unit("b"), Wreck("c", -1f) });

            Assert.Equal(2, state.WreckedUnitCount);
        }

        [Fact]
        public void Clear_ForgetsHeldWrecksToo()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            state.Clear();

            Assert.Equal(0, state.WreckedUnitCount);
            Assert.False(state.TryTakeWreck("a", 0f, 100f));
            Assert.Empty(state.SettleWindow().Units);
        }

        [Fact]
        public void Count_OfAFreshState_IsNothing()
        {
            var state = new DestructionState();
            Assert.Equal(0, state.Count);
            Assert.Equal(0, state.UnitCount);
        }

        [Fact]
        public void Count_TotalsPartsAcrossUnits_AndSkipsIntactOnes()
        {
            // The only mechanical check this feature has is this number against
            // the host's own, because the two machines keep the same fact in
            // different places — the host in the ECS, a client only here.
            var state = new DestructionState();
            state.Receive(new[]
            {
                Unit("a", Part("core", 1f), Part("secondary", 2f)),
                Unit("b", Part("leg_left", 3f)),
                Unit("c"),
            });

            Assert.Equal(3, state.Count);
            Assert.Equal(2, state.UnitCount);
        }

        [Fact]
        public void Clear_ForgetsEverything()
        {
            // The driven table above all. It is keyed by unit name and socket,
            // both of which the next fight reuses, so carrying it across would
            // suppress a first drive on a visual manager that has been told
            // nothing at all.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.ShouldDrive("a", "core", 1f, out _);

            state.Clear();

            Assert.Empty(state.PartsFor("a"));
            Assert.Empty(state.SettleWindow().Parts);
            Assert.True(state.ShouldDrive("a", "core", 1f, out var first));
            Assert.True(first);
        }
    }
}
