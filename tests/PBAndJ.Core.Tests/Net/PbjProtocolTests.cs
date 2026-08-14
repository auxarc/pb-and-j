using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjProtocolTests
    {
        [Fact]
        public void Version_IsThree()
        {
            // Pinned deliberately: bumping the wire format must be an explicit
            // edit here and in Write_MinimalOrder_ProducesExactBytes.
            // v2 (M5e) added ResumeToken to Welcome. The message types added
            // earlier in M5 left every existing layout alone and so kept v1.
            // v3 (M7) added GameBuild and Passphrase to Hello and Rejoin, for
            // play between two machines; M6's Keyframes was a new type only.
            Assert.Equal(3, PbjProtocol.Version);
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
            Assert.Equal("0.13.0", PbjProtocol.ModVersion);
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
