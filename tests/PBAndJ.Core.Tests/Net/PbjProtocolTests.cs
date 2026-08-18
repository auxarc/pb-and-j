using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjProtocolTests
    {
        [Fact]
        public void Version_IsNine()
        {
            // Pinned deliberately: bumping the wire format must be an explicit
            // edit here and in Write_MinimalOrder_ProducesExactBytes.
            // v2 (M5e) added ResumeToken to Welcome. The message types added
            // earlier in M5 left every existing layout alone and so kept v1.
            // v3 (M7) added GameBuild and Passphrase to Hello and Rejoin, for
            // play between two machines; M6's Keyframes was a new type only.
            // v4 (M13) is the FIRST bump for a changed layout rather than an
            // added type: the visibility fix appends three bytes to every unit
            // record inside Snapshot. A v3 peer would read one unit's
            // visibility bytes as the next unit's name length.
            // v5 (M8's leftovers) is the same case a second time and in the same
            // record: an arrival-time flag and float, five more bytes a unit.
            // v6 (M14 stage B) is the first to change TWO layouts at once: a
            // trail point list appended to every projectile inside ReplayAssets,
            // and a weapon light list appended to every unit inside Poses. Both
            // are counted lists after existing fields, so a v5 peer reads a
            // projectile's trail count as the next projectile's id.
            // v7 (M14 stage C) changes the same Poses unit record again: a
            // reaction-ping list and a melee-trajectory list appended after the
            // weapon lights. A v6 peer reads the ping count as the end of the
            // message. Paired into one break for the stage B reason.
            // v8 (M15) is the first move to the Snapshot unit record since v5,
            // and the first ever to REMOVE from a layout as well as add: a
            // counted wrecked-part list goes in where isDead and deathTime came
            // out, so a v7 peer reads the part count as isDead and every byte
            // after it is rubbish. The removal is not tidying — DeathStatus is a
            // pilot component, so both fields were constant on a unit for their
            // whole life. The unit's own IsWrecked/WreckedAt go in on the same
            // move, so M15's two halves cost one break between them.
            // v9 (M16) appends a frame-integrity presence bit and a second
            // counted part list — every part's integrity and barrier — to the
            // same unit record. A v8 peer stops reading before both, and a v9
            // peer reading a v8 record takes the next unit's name length as a
            // part count, so there is no partial compatibility to preserve.
            Assert.Equal(9, PbjProtocol.Version);
        }

        [Fact]
        public void Timeouts_GiveTheHostMoreRopeThanItGivesAPeer()
        {
            // The host is the side that hitches, and a client fault is terminal.
            Assert.True(PbjProtocol.HostTimeoutSeconds > PbjProtocol.PeerTimeoutSeconds);
            Assert.True(PbjProtocol.PeerTimeoutSeconds > PbjProtocol.PingIntervalSeconds);
            Assert.True(PbjProtocol.PingIntervalSeconds > PbjProtocol.TickIntervalSeconds);
        }

        [Fact]
        public void Magic_SpellsPbj1()
        {
            Assert.Equal(0x504A4231, PbjProtocol.Magic);
        }

        [Fact]
        public void Check_WithCorrectMagicAndVersion_ReturnsNull()
        {
            Assert.Null(PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version));
        }

        [Fact]
        public void Check_WithWrongMagic_ReturnsBadMagic()
        {
            Assert.Equal(RejectReason.BadMagic, PbjProtocol.Check(0xDEAD, PbjProtocol.Version));
        }

        [Fact]
        public void Check_WithOlderVersion_ReturnsVersionMismatch()
        {
            Assert.Equal(RejectReason.VersionMismatch,
                PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version - 1));
        }

        [Fact]
        public void Check_WithNewerVersion_ReturnsVersionMismatch()
        {
            Assert.Equal(RejectReason.VersionMismatch,
                PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version + 1));
        }

        // --- compatibility (v3) ---

        private static RejectReason? Compat(
            string peerMod = "0.3.0",
            string? peerBuild = "b8339",
            string? required = null,
            string? offered = null) =>
            PbjProtocol.CheckCompatibility("0.3.0", peerMod, "b8339", peerBuild, required, offered);

        [Fact]
        public void CheckCompatibility_WithEverythingMatching_ReturnsNull()
        {
            Assert.Null(Compat());
        }

        [Fact]
        public void CheckCompatibility_WithADifferentModVersion_ReturnsModVersionMismatch()
        {
            Assert.Equal(RejectReason.ModVersionMismatch, Compat(peerMod: "0.2.0"));
        }

        [Fact]
        public void CheckCompatibility_WithADifferentGameBuild_ReturnsGameBuildMismatch()
        {
            Assert.Equal(RejectReason.GameBuildMismatch, Compat(peerBuild: "b8000"));
        }

        // The harness has no game at all, and that is a legitimate peer — it is
        // how every gate since M4 has been run. An absent build is "cannot say",
        // not "does not match".
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CheckCompatibility_WithNoGameBuildReported_SkipsTheBuildCheck(string? peerBuild)
        {
            Assert.Null(Compat(peerBuild: peerBuild));
        }

        [Fact]
        public void CheckCompatibility_WhenTheHostHasNoGameBuild_SkipsTheBuildCheck()
        {
            Assert.Null(PbjProtocol.CheckCompatibility("0.3.0", "0.3.0", null, "b8339", null, null));
        }

        // Same rule as the game build, and it has to be the same rule: the
        // harness declares neither, and it is the peer every in-game gate since
        // M4 has been run with.
        [Theory]
        [InlineData(null, "0.3.0")]
        [InlineData("", "0.3.0")]
        [InlineData("0.3.0", null)]
        [InlineData("0.3.0", "")]
        [InlineData(null, null)]
        public void CheckCompatibility_WithAnUndeclaredModVersion_SkipsTheModCheck(
            string? hostMod, string? peerMod)
        {
            Assert.Null(PbjProtocol.CheckCompatibility(hostMod, peerMod, null, null, null, null));
        }

        [Fact]
        public void CheckCompatibility_WithTheRightPassphrase_ReturnsNull()
        {
            Assert.Null(Compat(required: "hunter2", offered: "hunter2"));
        }

        [Theory]
        [InlineData("wrong")]
        [InlineData("")]
        [InlineData(null)]
        public void CheckCompatibility_WithTheWrongPassphrase_ReturnsBadPassphrase(string? offered)
        {
            Assert.Equal(RejectReason.BadPassphrase, Compat(required: "hunter2", offered: offered));
        }

        [Fact]
        public void CheckCompatibility_ComparesThePassphraseExactly()
        {
            Assert.Equal(RejectReason.BadPassphrase, Compat(required: "hunter2", offered: "HUNTER2"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CheckCompatibility_WithNoPassphraseRequired_AcceptsAnything(string? required)
        {
            Assert.Null(Compat(required: required, offered: "whatever"));
        }

        // Deliberate ordering: a peer that cannot authenticate learns only that,
        // not our exact mod version or game build.
        [Fact]
        public void CheckCompatibility_ChecksThePassphraseBeforeAnythingElse()
        {
            Assert.Equal(RejectReason.BadPassphrase, PbjProtocol.CheckCompatibility(
                "0.3.0", "0.1.0", "b8339", "b0000", "hunter2", "wrong"));
        }

        [Fact]
        public void CheckCompatibility_ChecksTheModVersionBeforeTheGameBuild()
        {
            // The mod is the thing we control; a mismatch there is the more
            // actionable message of the two.
            Assert.Equal(RejectReason.ModVersionMismatch, PbjProtocol.CheckCompatibility(
                "0.3.0", "0.1.0", "b8339", "b0000", null, null));
        }

        // Pinned so bumping it is deliberate, and so the pairing with
        // mod/metadata.yaml is visible from here. Peers compare this exactly, so
        // a build where the two disagree is refused — which is how a packaged
        // harness once went out announcing a stale version and was rejected on
        // the far machine. The Makefile enforces the half this test cannot see.
        [Fact]
        public void ModVersion_MatchesTheShippedModMetadata()
        {
            // 0.13.0 for M8's pose wire. Poses = 31 is a new message type, which
            // by this project's own rule does not move the wire version — but it
            // moves this one, in the same commit that added it rather than at
            // release. That ordering is not hygiene: a host broadcasts the parts
            // to every peer on every executed turn, so a peer admitted by a
            // matching version string but built without the type would fault on
            // the first turn of the first fight.
            //
            // 0.12.0 for M12b·2. No message type moved and no layout changed —
            // what moved is IPbjGameBridge, which is hashed as part of the wire
            // surface because OrderApplyResult and RejectReason cross it as raw
            // int casts. The bump is honest rather than ceremonial: 0.11.0 is a
            // build in which the host enters combat and nothing ever writes the
            // fight, so it hangs at the point this release exists to fix, and
            // refusing it is the correct outcome.
            //
            // 0.11.0 was M12b's first half: CombatOffer and CombatEntered were new
            // types, and the meaning of an existing one moved — CombatStart now
            // arrives only once everyone is in the fight.
            // 0.14.0 for M13, the visibility fix. This one moves PbjProtocol.Version
            // as well, which no release before it has done — the snapshot's unit
            // record grew, so an older peer decodes every unit after the first
            // out of step rather than merely meeting an unknown message.
            // 0.15.0 for M8's leftovers, which moves the wire version for the
            // same reason a second time: the arrival time a client needs both to
            // stop diverging and to know when the host revealed a unit.
            // 0.16.0 for M14's ReplayAssets. This one does NOT move
            // PbjProtocol.Version — a new message type leaves every existing
            // layout alone — so the mod version is the only thing standing
            // between a peer built without the type and a host that broadcasts
            // it on every executed turn.
            // 0.17.0 for M14 stage B — trails and weapon lights — which moves
            // PbjProtocol.Version to 6 alongside it. Pairing the two features in
            // one release is what makes that a single wire break instead of two.
            // 0.18.0 for the scenario digest merging numbered content parts. No
            // layout moves, so PbjProtocol.Version stays at 6 — but a save over
            // MaxPartBytes digests differently than it did in 0.17.0, so two
            // peers across that change disagree about whether a client already
            // holds a large fight. A semantic break under an identical layout is
            // exactly the case the mod version exists to catch.
            // 0.19.0 for M14 stage C — reaction pings and melee trajectories,
            // both appended to every unit inside Poses — which moves
            // PbjProtocol.Version to 7 alongside it. Paired into one break for
            // the same reason stage B paired trails with weapon lights.
            // 0.20.0 for M15 — per-part destruction visuals — which moves
            // PbjProtocol.Version to 8. The snapshot's unit record gains the
            // wrecked-part list, the unit's own wreck flag and stamp, and loses
            // the two dead death fields — all in one break, rather than keeping
            // a field known to be constant alive until some later one.
            // 0.21.0 for M16 — per-part integrity sync — which moves
            // PbjProtocol.Version to 9. The unit record gains every part's
            // integrity and barrier, plus the presence bit that finally makes
            // the frame-integrity field honest: it used to travel as a bare 0f
            // that meant "absent on the host" and was written as a real value on
            // the client, which the digest could not see because both machines
            // then read zero.
            Assert.Equal("0.21.0", PbjProtocol.ModVersion);
        }

        [Fact]
        public void SessionRequirements_None_DemandsNothing()
        {
            Assert.False(SessionRequirements.None.RequiresPassphrase);
            Assert.Null(SessionRequirements.None.ModVersion);
            Assert.Null(SessionRequirements.None.GameBuild);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("hunter2", true)]
        public void SessionRequirements_RequiresPassphrase_TracksWhetherOneWasSet(
            string? passphrase, bool expected)
        {
            Assert.Equal(expected,
                new SessionRequirements("0.3.0", "b8339", passphrase).RequiresPassphrase);
        }

        [Fact]
        public void HandshakeTimeout_IsShorterThanThePeerTimeout()
        {
            // A silent stranger must be dropped sooner than an established peer:
            // it has proven nothing, and on an internet-facing port it costs a
            // connection slot for free.
            Assert.True(PbjProtocol.HandshakeTimeoutSeconds < PbjProtocol.PeerTimeoutSeconds);
        }

        [Fact]
        public void Check_ChecksMagicBeforeVersion()
        {
            // Wrong magic means it is not our protocol at all, so the version
            // number is meaningless and must not drive the reported reason.
            Assert.Equal(RejectReason.BadMagic, PbjProtocol.Check(0, PbjProtocol.Version + 99));
        }
    }
}
