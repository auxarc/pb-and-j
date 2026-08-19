using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The unit's own wreck, as against the individual parts in .Parts.cs -- its
    // whole life on a client: held, settled, taken at the crossing edge, marked
    // shown, and un-wrecked again.
    //
    // One part of DestructionStateTests; the shared fixture is in
    // DestructionPlaybackTests.cs.
    public partial class DestructionStateTests
    {
        // ---- M15 §3.1, the unit's own wreck ----

        [Fact]
        public void Receive_OfANewlyWreckedUnit_HoldsItForTheWindow()
        {
            // Same causality rule the parts follow, and it matters more here:
            // settling on arrival would detonate the unit before the replay
            // showed the killing blow landing.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });

            Assert.Empty(state.Receive(new[] { Wreck("a", 3f) }).Units);
        }

        [Fact]
        public void Receive_OfAUnitWreckedOnFirstSight_SettlesItAtOnce()
        {
            // A client joining mid-fight has no window in which this unit ever
            // died. Holding it would leave a standing corpse until some later
            // turn happened to play.
            var state = new DestructionState();

            var drive = Assert.Single(state.Receive(new[] { Wreck("a", 3f) }).Units);
            Assert.Equal("a", drive.Unit);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Receive_OfAUnitWreckedWithNoNameableMoment_SettlesItAtOnce()
        {
            // Negative is the host saying "no moment in this fight to wait for"
            // — a unit that spawned wrecked, or one whose instant it could not
            // derive. Same convention the parts use.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });

            var drive = Assert.Single(state.Receive(new[] { Wreck("a", -100f) }).Units);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Receive_OfAUnitTheHostUnwrecked_AsksForARevival()
        {
            // CombatUnitRevive clears the flag on a host. Expressible only
            // because the game gives us OnUnitRevival to answer it with.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            var drive = Assert.Single(state.Receive(new[] { Unit("a") }).Units);
            Assert.Equal("a", drive.Unit);
            Assert.False(drive.Wrecked);
        }

        [Fact]
        public void Receive_SettlesAHeldWreck_WhenNoWindowEverPlayedIt()
        {
            // 🔑 The wreck-heavy case, and the one this half exists for: the
            // host sends NO keyframes at all for a turn whose every tracked unit
            // died, so no window ever claims the held wreck. Keying on "already
            // known" instead of "already shown" loses it silently and for ever —
            // which is exactly the bug this test was written against.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            var drive = Assert.Single(state.Receive(new[] { Wreck("a", 3f) }).Units);
            Assert.Equal("a", drive.Unit);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Receive_AsksNothingMore_OnceTheWreckHasBeenShown()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });
            state.SettleWindow();

            Assert.Empty(state.Receive(new[] { Wreck("a", 3f) }).Units);
        }

        [Fact]
        public void Settle_MarksTheWreckShown_SoTheNextSnapshotLetsItLie()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });
            Assert.Single(state.SettleWindow().Units);

            Assert.Empty(state.Receive(new[] { Wreck("a", 3f) }).Units);
        }

        [Fact]
        public void Receive_AfterARevival_ShowsASecondWreck()
        {
            // The revival clears the shown flag, which is what makes a unit
            // destroyed twice draw twice.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });
            state.SettleWindow();
            state.Receive(new[] { Unit("a") });

            var drive = Assert.Single(state.Receive(new[] { Wreck("a", -1f) }).Units);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Settle_ReleasesAWreckTheWindowNeverReached()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            var drive = Assert.Single(state.SettleWindow().Units);
            Assert.Equal("a", drive.Unit);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void TakeWreck_FiresOnTheFrameTheCursorCrossesTheMoment()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            Assert.False(state.TryTakeWreck("a", 1f, 2.5f));
            Assert.True(state.TryTakeWreck("a", 2.5f, 3.5f));
        }

        [Fact]
        public void TakeWreck_UsesAnIntervalTest_NotAPointOne()
        {
            // A frame gap during playback is routinely longer than the events
            // inside it, so "is the cursor past it" loses exactly the wrecks
            // that happen between two frames.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });

            Assert.True(state.TryTakeWreck("a", 2.99f, 3.01f));
        }

        [Fact]
        public void TakeWreck_ConsumesTheWreck_SoTheWindowCannotReplayIt()
        {
            // Taking and testing have to be one operation: a wreck is a one-shot
            // and a caller that asked, then acted, would have to remember
            // separately not to ask again.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });
            Assert.True(state.TryTakeWreck("a", 2f, 4f));

            Assert.False(state.TryTakeWreck("a", 2f, 4f));
        }

        [Fact]
        public void TakeWreck_ConsumesIt_SoSettleDoesNotFireItAgain()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a") });
            state.Receive(new[] { Wreck("a", 3f) });
            state.TryTakeWreck("a", 2f, 4f);

            Assert.Empty(state.SettleWindow().Units);
        }

        [Fact]
        public void TakeWreck_LeavesOtherUnitsHeld()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a"), Unit("b") });
            state.Receive(new[] { Wreck("a", 3f), Wreck("b", 3f) });
            state.TryTakeWreck("a", 2f, 4f);

            var drive = Assert.Single(state.SettleWindow().Units);
            Assert.Equal("b", drive.Unit);
        }

        [Fact]
        public void TakeWreck_OfAUnitWithNothingHeld_IsFalse()
        {
            var state = new DestructionState();
            Assert.False(state.TryTakeWreck("a", 0f, 100f));
            Assert.False(state.TryTakeWreck(null, 0f, 100f));
            Assert.False(state.TryTakeWreck(string.Empty, 0f, 100f));
        }
    }
}
