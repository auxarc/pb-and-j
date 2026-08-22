using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // IsUnitWrecked: who the client believes is down. The wake path asks it of every
    // mech it slept, so the cases that matter are the lifetime ones -- the answer has
    // to survive settling the window, and must not survive a Clear.
    //
    // One part of DestructionStateTests; the shared fixture is in
    // DestructionPlaybackTests.cs.
    public partial class DestructionStateTests
    {
        [Fact]
        public void IsUnitWrecked_OfAUnitNeverHeardOf_IsFalse()
        {
            // The overwhelmingly common case: the wake path asks this about
            // every mech it slept, and almost none of them are corpses.
            var state = new DestructionState();

            Assert.False(state.IsUnitWrecked("a"));
        }

        [Fact]
        public void IsUnitWrecked_OfAKnownIntactUnit_IsFalse()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            Assert.False(state.IsUnitWrecked("a"));
        }

        [Fact]
        public void IsUnitWrecked_OfAWreckedUnit_IsTrue()
        {
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", -1f) });

            Assert.True(state.IsUnitWrecked("a"));
        }

        [Fact]
        public void IsUnitWrecked_OfANullOrEmptyName_IsFalse()
        {
            // The join key comes off an entity that may have lost its name
            // between install and wake, so both spellings of "no name" reach
            // here on real data.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", -1f) });

            Assert.False(state.IsUnitWrecked(null));
            Assert.False(state.IsUnitWrecked(string.Empty));
        }

        [Fact]
        public void IsUnitWrecked_AfterARevival_IsFalseAgain()
        {
            // The un-wreck direction has to be expressible or a revived unit is
            // a permanent statue: the wake path reads this to decide whether to
            // hand the puppet back.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", -1f) });
            state.Receive(new[] { Unit("a") });

            Assert.False(state.IsUnitWrecked("a"));
        }

        [Fact]
        public void IsUnitWrecked_IsTrueBeforeTheWreckHasBeenPlayed()
        {
            // Held, not yet drawn — the wreck is waiting for its moment inside a
            // window. The wake still has to keep the unit down, because by the
            // time the window ends the collapse has played.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", 3f) });

            Assert.True(state.IsUnitWrecked("a"));
        }

        [Fact]
        public void IsUnitWrecked_SurvivesSettlingTheWindow()
        {
            // 🔴 The ordering coupling this test exists to guard.
            // CombatGameBridge.StopKeyframes runs KeyframePlayer.Stop() — which
            // wakes, and therefore freezes — BEFORE ClearDestruction(). The
            // natural end of a window likewise settles and then stops. So the
            // wreck set must still answer this question after SettleWindow has
            // run, or every corpse stands up at combat end and at every turn
            // boundary, silently and with no other test failing.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", 3f) });

            state.SettleWindow();

            Assert.True(state.IsUnitWrecked("a"));
        }

        // --- M17 stage 2: the ECS flag's lifetime, which is NOT this set's ---

        [Fact]
        public void ShouldHoldWreckFlagAcrossCombatEnd_IsTrue()
        {
            // 🔴 The decision, in Core so it can be pinned. Stage 2 writes
            // persistent.isWrecked, and ClearDestruction must leave it alone.
            //
            // The horn that makes this asymmetric is the FAULT case, not the
            // ordinary one: StopKeyframes -- hence ClearDestruction -- also
            // fires on Bye and on Fault, and after either the human keeps
            // playing that same fight single-player. They will set Simulating
            // themselves and reach the all-hostiles-inactive count, which
            // consults IsUnitActive, which consults isWrecked. Clearing the flag
            // would resurrect every corpse into that count and make the fight
            // unwinnable. Flipping this to false is the mutation.
            Assert.True(DestructionState.ShouldHoldWreckFlagAcrossCombatEnd);
        }

        [Fact]
        public void IsUnitWrecked_AfterClear_IsFalse()
        {
            // The other half of the same coupling: once the fight is over the
            // set is forgotten, so nothing can hold a view down into the next
            // one.
            var state = new DestructionState();
            state.Receive(new[] { Wreck("a", -1f) });

            state.Clear();

            Assert.False(state.IsUnitWrecked("a"));
        }
    }
}
