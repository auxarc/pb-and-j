using System;
using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
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

    public class DestructionStateTests
    {
        private static UnitSnapshot Unit(string? name, params PartDestruction[] parts)
        {
            return new UnitSnapshot(
                name,
                default,
                default,
                default,
                1f,
                wreckedParts: parts);
        }

        private static PartDestruction Part(string? socket, float time)
        {
            return new PartDestruction(socket, time);
        }

        private static UnitSnapshot Wreck(string? name, float at, params PartDestruction[] parts)
        {
            return new UnitSnapshot(
                name, default, default, default, 1f,
                isWrecked: true, wreckedAt: at, wreckedParts: parts);
        }

        [Fact]
        public void Receive_OfANullSnapshot_SettlesNothing()
        {
            var state = new DestructionState();
            Assert.Empty(state.Receive(null).Parts);
        }

        [Fact]
        public void Receive_OfAnEmptySnapshot_SettlesNothingAndForgetsNothing()
        {
            // Silence is not "nothing is wrecked any more". A client that heard
            // nothing has been told nothing, and un-dissolving the battlefield on
            // an empty message would be the loudest possible way to say so.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            Assert.Empty(state.Receive(new UnitSnapshot[0]).Parts);
            Assert.Single(state.PartsFor("a"));
        }

        [Fact]
        public void Receive_OfANewlyWreckedPart_HoldsItForTheWindow()
        {
            // The whole causality rule: this part's moment is still to come, so
            // settling it here would blow the limb off before the replay shows
            // the hit landing.
            var state = new DestructionState();
            Assert.Empty(state.Receive(new[] { Unit("a", Part("core", 1f)) }).Parts);
        }

        [Fact]
        public void Receive_OfAPartAlreadyKnown_SettlesItAtOnce()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var settled = state.Receive(new[] { Unit("a", Part("core", 1f)) }).Parts;

            var drive = Assert.Single(settled);
            Assert.Equal("a", drive.Unit);
            Assert.Equal("core", drive.Socket);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Receive_OfAPreBattleWreck_SettlesItOnFirstSight()
        {
            // The negative stamp is the tell. Without this arm a client joining
            // mid-fight shows pristine limbs on every already-damaged unit until
            // some window happens to play.
            var state = new DestructionState();

            var drive = Assert.Single(state.Receive(new[] { Unit("a", Part("leg_left", -100f)) }).Parts);
            Assert.Equal("leg_left", drive.Socket);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Receive_OfAPartThatLeftTheSet_UnwrecksIt()
        {
            // CombatUnitRevive is real. The set is live, so absence is an
            // instruction and not a gap.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var settled = state.Receive(new[] { Unit("a") }).Parts;

            var drive = Assert.Single(settled);
            Assert.Equal("core", drive.Socket);
            Assert.False(drive.Wrecked);
        }

        [Fact]
        public void Receive_OfARevivedPart_MakesTheNextWreckAFirstDriveAgain()
        {
            // The integrity zeroing rides on "first drive". If the revive left
            // the last-driven value behind, a part destroyed a second time would
            // dissolve over an integrity nobody ever zeroed.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            Assert.True(state.ShouldDrive("a", "core", 1f, out _));

            state.Receive(new[] { Unit("a") });

            Assert.True(state.ShouldDrive("a", "core", 0.5f, out var first));
            Assert.True(first);
        }

        [Fact]
        public void Receive_SettlesLastTurnsHeldParts_WithNoWindowInBetween()
        {
            // The convergence backstop, and the case it exists for: the host
            // sends no keyframes at all for a turn whose every tracked unit
            // died, so nothing ever settles that turn's parts except this.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var settled = state.Receive(new[] { Unit("a", Part("core", 1f), Part("secondary", 9f)) }).Parts;

            var drive = Assert.Single(settled);
            Assert.Equal("core", drive.Socket);
            Assert.Equal(new[] { "core", "secondary" }, SocketsOf(state.PartsFor("a")));
        }

        [Fact]
        public void Receive_IgnoresUnitsWithNoName()
        {
            // The name is the only join key there is; a record without one can
            // never be placed on a unit, so holding it would only grow the table.
            var state = new DestructionState();
            Assert.Empty(state.Receive(new[] { Unit(null, Part("core", -1f)) }).Parts);
            Assert.Empty(state.Receive(new[] { Unit(string.Empty, Part("core", -1f)) }).Parts);
        }

        [Fact]
        public void Receive_IgnoresPartsWithNoSocket()
        {
            var state = new DestructionState();
            Assert.Empty(state.Receive(new[] { Unit("a", Part(null, -1f), Part(string.Empty, -1f)) }).Parts);
        }

        [Fact]
        public void Receive_IgnoresASocketlessPartLeavingTheSet()
        {
            // The removal walk reads the same field and must skip the same way,
            // or a nameless socket arrives at the glue as a drive it cannot place.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part(null, 1f)) });
            Assert.Empty(state.Receive(new[] { Unit("a") }).Parts);
        }

        [Fact]
        public void Settle_WithNothingHeld_IsEmpty()
        {
            var state = new DestructionState();
            Assert.Empty(state.SettleWindow().Parts);
        }

        [Fact]
        public void Settle_ReleasesWhatTheWindowWasHolding()
        {
            // The short tail: a part wrecked a tenth of a second before the
            // window ends has ramped a fifth of the way and would otherwise sit
            // half-dissolved through the whole planning phase.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var drive = Assert.Single(state.SettleWindow().Parts);
            Assert.Equal("a", drive.Unit);
            Assert.Equal("core", drive.Socket);
            Assert.True(drive.Wrecked);
        }

        [Fact]
        public void Settle_IsNotRepeatable()
        {
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.SettleWindow();

            Assert.Empty(state.SettleWindow().Parts);
        }

        [Fact]
        public void Receive_ReplacesWhatTheLastWindowWasHolding()
        {
            // A held set belongs to one turn. Carrying it forward would settle a
            // part twice and, worse, would let an unplayed turn's holdings leak
            // into the next window's ramp.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 1f), Part("secondary", 9f)) });

            var drive = Assert.Single(state.SettleWindow().Parts);
            Assert.Equal("secondary", drive.Socket);
        }

        [Fact]
        public void PartsFor_AnUnknownUnit_IsEmpty()
        {
            var state = new DestructionState();
            Assert.Empty(state.PartsFor("nobody"));
            Assert.Empty(state.PartsFor(null));
            Assert.Empty(state.PartsFor(string.Empty));
        }

        [Fact]
        public void PartsFor_IsTheWholeCumulativeSet()
        {
            // Not just the held part of it. The older entries resolve to a flat 1
            // through the ramp, and re-driving them is what re-dissolves a view
            // rebuilt mid-window.
            var state = new DestructionState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 1f), Part("secondary", 9f)) });

            Assert.Equal(new[] { "core", "secondary" }, SocketsOf(state.PartsFor("a")));
        }

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
        public void Update_Nothing_IsEmptyOnBothLists()
        {
            Assert.True(DestructionUpdate.Nothing.IsEmpty);
            Assert.Empty(DestructionUpdate.Nothing.Parts);
            Assert.Empty(DestructionUpdate.Nothing.Units);
        }

        [Fact]
        public void Update_WithEitherListPopulated_IsNotEmpty()
        {
            Assert.False(new DestructionUpdate(
                new[] { new DestructionDrive("a", "core", true) }, null).IsEmpty);
            Assert.False(new DestructionUpdate(
                null, new[] { new UnitWreckDrive("a", true) }).IsEmpty);
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

        private static string?[] SocketsOf(IReadOnlyList<PartDestruction> parts)
        {
            var sockets = new string?[parts.Count];
            for (var i = 0; i < parts.Count; i++)
            {
                sockets[i] = parts[i].Socket;
            }
            return sockets;
        }
    }
}
