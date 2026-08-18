using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PartIntegrityDriveTests
    {
        [Fact]
        public void Constructor_RetainsEveryField()
        {
            var drive = new PartIntegrityDrive("pb_mech_01", "core", 0.25f, 0.5f);
            Assert.Equal("pb_mech_01", drive.Unit);
            Assert.Equal("core", drive.Socket);
            Assert.Equal(0.25f, drive.Integrity);
            Assert.Equal(0.5f, drive.Barrier);
        }
    }

    public class FrameIntegrityDriveTests
    {
        [Fact]
        public void Constructor_RetainsEveryField()
        {
            var drive = new FrameIntegrityDrive("pb_mech_01", true, 0.75f);
            Assert.Equal("pb_mech_01", drive.Unit);
            Assert.True(drive.Present);
            Assert.Equal(0.75f, drive.Integrity);
        }

        // Absence is the instruction, not a value of zero. The game's own loader
        // installs the component on every load (DataManagerSave.cs:2293-2301), so
        // a client holds one the host does not, and only an explicit "remove it"
        // puts the two back together.
        [Fact]
        public void Constructor_CarriesAbsenceAsItsOwnState()
        {
            Assert.False(new FrameIntegrityDrive("pb_mech_01", false, 0f).Present);
        }
    }

    public class PartIntegrityUpdateTests
    {
        [Fact]
        public void Nothing_IsEmptyOnBothHalves()
        {
            Assert.True(PartIntegrityUpdate.Nothing.IsEmpty);
            Assert.Empty(PartIntegrityUpdate.Nothing.Parts);
            Assert.Empty(PartIntegrityUpdate.Nothing.Frames);
        }

        [Fact]
        public void Constructor_TreatsNullListsAsEmpty()
        {
            var update = new PartIntegrityUpdate(null, null);
            Assert.Empty(update.Parts);
            Assert.Empty(update.Frames);
            Assert.True(update.IsEmpty);
        }

        // Either half alone is work to do. A frame-integrity removal with no
        // part change is the ordinary shape on a quiet turn.
        [Fact]
        public void IsEmpty_IsFalseWhenEitherHalfHasWork()
        {
            Assert.False(new PartIntegrityUpdate(
                new[] { new PartIntegrityDrive("u", "core", 1f, 1f) }, null).IsEmpty);
            Assert.False(new PartIntegrityUpdate(
                null, new[] { new FrameIntegrityDrive("u", false, 0f) }).IsEmpty);
        }
    }

    public class PartIntegrityStateTests
    {
        private static UnitSnapshot Unit(string name, params PartState[] parts) =>
            new UnitSnapshot(
                name, default, default, default, 0f, parts: parts);

        private static UnitSnapshot Framed(
            string name, bool present, float integrity, params PartState[] parts) =>
            new UnitSnapshot(
                name, default, default, default, integrity,
                parts: parts, hasFrameIntegrity: present);

        private static PartState Part(string? socket, float integrity, float barrier = 1f) =>
            new PartState(socket, integrity, barrier);

        // Silence is not information. A client that hears nothing has been told
        // nothing, and treating an absent snapshot as "everything is pristine"
        // would repair the whole battlefield on the first upstream hiccup.
        [Fact]
        public void Receive_WithNoUnits_ChangesNothing()
        {
            var state = new PartIntegrityState();
            Assert.True(state.Receive(null).IsEmpty);
            Assert.True(state.Receive(new UnitSnapshot[0]).IsEmpty);
        }

        [Fact]
        public void Receive_LeavesAHeldSetAloneWhenToldNothing()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.4f)) });
            Assert.Equal(1, state.HeldCount);

            state.Receive(null);
            Assert.Equal(1, state.HeldCount);
        }

        // First sight has no window that could show the damage arriving, so it
        // settles at once — the same rule DestructionPlayback applies to a unit
        // wreck it is seeing for the first time. This is the mid-fight join.
        [Fact]
        public void Receive_SettlesAUnitItHasNeverSeenAtOnce()
        {
            var state = new PartIntegrityState();
            var update = state.Receive(new[] { Unit("a", Part("core", 0.5f, 0.25f)) });

            var drive = Assert.Single(update.Parts);
            Assert.Equal("a", drive.Unit);
            Assert.Equal("core", drive.Socket);
            Assert.Equal(0.5f, drive.Integrity);
            Assert.Equal(0.25f, drive.Barrier);
            Assert.Equal(0, state.HeldCount);
        }

        // The second sighting is the ordinary one: the snapshot lands before the
        // keyframes of the turn it describes, so applying it now would drop the
        // health bar before the replay showed the shot.
        [Fact]
        public void Receive_HoldsAKnownUnitForTheWindow()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var update = state.Receive(new[] { Unit("a", Part("core", 0.4f)) });
            Assert.Empty(update.Parts);
            Assert.Equal(1, state.HeldCount);
        }

        [Fact]
        public void SettleWindow_ReleasesWhatTheWindowWasHolding()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.4f)) });

            var update = state.SettleWindow();
            Assert.Equal(0.4f, Assert.Single(update.Parts).Integrity);
            Assert.Equal(0, state.HeldCount);
        }

        [Fact]
        public void SettleWindow_WithNothingHeld_ChangesNothing()
        {
            Assert.True(new PartIntegrityState().SettleWindow().IsEmpty);
        }

        [Fact]
        public void SettleWindow_CarriesNoFrameIntegrity()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Framed("a", true, 0.5f, Part("core", 0.4f)) });

            Assert.Empty(state.SettleWindow().Frames);
        }

        // 🔴 The defect revision 1 of the plan shipped. Applying the NEW set here
        // would put turn T+1's damage on screen before T+1's window plays — the
        // exact causality error the hold exists to prevent. The OLD set is what
        // is now safe to show, because its window is over either way.
        [Fact]
        public void Receive_SettlesThePreviousHeldSet_NotTheNewOne()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.6f)) });

            var update = state.Receive(new[] { Unit("a", Part("core", 0.2f)) });

            Assert.Equal(0.6f, Assert.Single(update.Parts).Integrity);
            Assert.Equal(1, state.HeldCount);
            Assert.Equal(0.2f, Assert.Single(state.SettleWindow().Parts).Integrity);
        }

        // A window that played leaves nothing behind, so the next snapshot has
        // only its own set to hold. Without this the settle would double-fire.
        [Fact]
        public void Receive_AfterASettledWindow_SettlesNothingOld()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.6f)) });
            state.SettleWindow();

            Assert.Empty(state.Receive(new[] { Unit("a", Part("core", 0.2f)) }).Parts);
        }

        // The unit left the roster while its damage was still held. The held set
        // is keyed by name and settled wholesale, so it survives the unit's
        // disappearance from the snapshot that follows.
        [Fact]
        public void Receive_SettlesHeldPartsForAUnitTheNewSnapshotOmits()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.3f)) });

            var update = state.Receive(new[] { Unit("b", Part("core", 1f)) });

            Assert.Equal(2, update.Parts.Count);
            Assert.Contains(update.Parts, d => d.Unit == "a" && d.Integrity == 0.3f);
            Assert.Contains(update.Parts, d => d.Unit == "b" && d.Integrity == 1f);
        }

        // Frame integrity is not held. Nothing draws it during a fight — the
        // in-combat readers are data-driven checks and an effective-HP number —
        // so there is no causality to protect, and holding it would be machinery
        // that buys nothing.
        [Fact]
        public void Receive_AppliesFrameIntegrityImmediately_EvenForAHeldUnit()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });

            var update = state.Receive(new[] { Framed("a", true, 0.5f, Part("core", 0.4f)) });

            Assert.Empty(update.Parts);
            var frame = Assert.Single(update.Frames);
            Assert.Equal("a", frame.Unit);
            Assert.True(frame.Present);
            Assert.Equal(0.5f, frame.Integrity);
        }

        [Fact]
        public void Receive_CarriesAbsentFrameIntegrityAsARemoval()
        {
            var state = new PartIntegrityState();
            Assert.False(Assert.Single(state.Receive(new[] { Unit("a") }).Frames).Present);
        }

        // A unit with no parts still carries a frame-integrity instruction, so
        // the two halves cannot be collapsed into one walk.
        [Fact]
        public void Receive_AUnitWithNoParts_StillCarriesItsFrame()
        {
            var update = new PartIntegrityState().Receive(new[] { Unit("a") });
            Assert.Empty(update.Parts);
            Assert.Single(update.Frames);
        }

        [Fact]
        public void Receive_SkipsAUnitWithNoName()
        {
            var state = new PartIntegrityState();
            var update = state.Receive(new[]
            {
                Unit(null!, Part("core", 0.5f)),
                Unit(string.Empty, Part("core", 0.5f)),
            });
            Assert.True(update.IsEmpty);
        }

        [Fact]
        public void Receive_SkipsAPartWithNoSocket()
        {
            var state = new PartIntegrityState();
            var update = state.Receive(new[]
            {
                Unit("a", Part(null, 0.5f), Part(string.Empty, 0.5f), Part("core", 0.5f)),
            });
            Assert.Equal("core", Assert.Single(update.Parts).Socket);
        }

        // A socketless part must not be held either, or HeldCount reports work
        // the settle will never produce.
        [Fact]
        public void Receive_DoesNotHoldASocketlessPart()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part(null, 0.5f), Part("core", 0.5f)) });
            Assert.Equal(1, state.HeldCount);
        }

        [Fact]
        public void HeldCount_CountsPartsAcrossEveryUnit()
        {
            var state = new PartIntegrityState();
            var snapshot = new[]
            {
                Unit("a", Part("core", 1f), Part("secondary", 1f)),
                Unit("b", Part("core", 1f)),
            };
            state.Receive(snapshot);
            state.Receive(snapshot);
            Assert.Equal(3, state.HeldCount);
        }

        [Fact]
        public void Clear_DropsTheHeldSet()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Receive(new[] { Unit("a", Part("core", 0.4f)) });

            state.Clear();

            Assert.Equal(0, state.HeldCount);
            Assert.True(state.SettleWindow().IsEmpty);
        }

        // The next fight reuses unit names, and its first snapshot describes a
        // battlefield this state has never seen. Carrying the seen-set across
        // would hold that first snapshot for a window instead of settling it.
        [Fact]
        public void Clear_MakesEveryUnitFirstSightAgain()
        {
            var state = new PartIntegrityState();
            state.Receive(new[] { Unit("a", Part("core", 1f)) });
            state.Clear();

            Assert.Single(state.Receive(new[] { Unit("a", Part("core", 0.4f)) }).Parts);
        }
    }
}
