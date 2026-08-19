using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // A part's destruction arriving, being held for the window, and settling: what
    // Receive makes of a snapshot's part list, what SettleWindow then releases, and
    // what PartsFor answers afterwards.
    //
    // SocketsOf lives here rather than in the shared fixture because every one of
    // its call sites does. One part of DestructionStateTests; the fixture the tests
    // below build snapshots with is in DestructionPlaybackTests.cs.
    public partial class DestructionStateTests
    {
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
