using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Per-type field fidelity for the message types that do not have a part of
    // their own: the handshake pair, the roster notifications, ready / commit /
    // complete, assignments, order results, the combat edges, base position and
    // ping/pong. This part is defined by subtraction -- the snapshot, keyframe,
    // pose, asset, scenario and lobby types each have their own file -- which is
    // why it is the longest list of the shortest tests.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        // --- per-type field fidelity ---

        [Fact]
        public void Encode_Hello_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new HelloMessage(PbjProtocol.Magic, 1, "a", "b", "c", "d"));
            var expected = new byte[]
            {
                0x01,                               // type Hello
                0x31, 0x42, 0x4A, 0x50,             // magic 0x504A4231 little-endian
                0x01, 0x00, 0x00, 0x00,             // protocolVersion 1
                0x01, 0x00, 0x00, 0x00, 0x61,       // modVersion "a"
                0x01, 0x00, 0x00, 0x00, 0x62,       // playerName "b"
                0x01, 0x00, 0x00, 0x00, 0x63,       // gameBuild "c"
                0x01, 0x00, 0x00, 0x00, 0x64,       // passphrase "d"
            };
            Assert.Equal(expected, bytes);
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void RoundTrip_CombatEntered_PreservesTheTurnAndOutcome(LoadOutcome outcome)
        {
            var m = RoundTrip(new CombatEnteredMessage(4, outcome));
            Assert.Equal(4, m.Turn);
            Assert.Equal(outcome, m.Outcome);
        }

        [Fact]
        public void RoundTrip_CombatOffer_PreservesTheFightsIdentity()
        {
            // The digest is the load-bearing field: the scenario slot is
            // rewritten every mission, so the name alone cannot tell this fight
            // from the last one.
            var m = RoundTrip(new CombatOfferMessage("pbj_combat_test", "d1", 4));
            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal("d1", m.Digest);
            Assert.Equal(4, m.Turn);
        }

        [Fact]
        public void RoundTrip_BasePosition_PreservesBothCoordinates()
        {
            var m = RoundTrip(new BasePositionMessage(1024.5f, -37.25f));

            Assert.Equal(1024.5f, m.X);
            Assert.Equal(-37.25f, m.Z);
        }

        [Fact]
        public void Encode_BasePosition_IsNineBytes_SoNoHeightCanBeHidingInIt()
        {
            // A type byte and two floats, and nothing else. This is the guard on
            // the design decision rather than on the arithmetic: the receiving
            // machine snaps to its own ground, and a third coordinate appearing
            // here later would mean somebody had started sending the host's idea
            // of a surface the client renders for itself.
            Assert.Equal(1 + 4 + 4, PbjMessageCodec.Encode(new BasePositionMessage(1f, 2f)).Length);
        }

        [Fact]
        public void RoundTrip_Hello_PreservesTheBuildAndPassphrase()
        {
            var m = RoundTrip(new HelloMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", "2.2.2-b8339", "hunter2"));
            Assert.Equal("2.2.2-b8339", m.GameBuild);
            Assert.Equal("hunter2", m.Passphrase);
        }

        // The harness has no game and sets no passphrase against a local host.
        [Fact]
        public void RoundTrip_Hello_WithNoBuildOrPassphrase_KeepsThemNull()
        {
            var m = RoundTrip(new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", null, null));
            Assert.Null(m.GameBuild);
            Assert.Null(m.Passphrase);
        }

        [Fact]
        public void RoundTrip_Rejoin_PreservesTheBuildAndPassphrase()
        {
            var m = RoundTrip(new RejoinMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", "7f3a91", 1, "tok",
                "2.2.2-b8339", "hunter2"));
            Assert.Equal("2.2.2-b8339", m.GameBuild);
            Assert.Equal("hunter2", m.Passphrase);
        }

        [Fact]
        public void RoundTrip_Hello_PreservesFields()
        {
            var m = RoundTrip(new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally", null, null));
            Assert.Equal(PbjProtocol.Magic, m.Magic);
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("0.2.0", m.ModVersion);
            Assert.Equal("ally", m.PlayerName);
        }

        [Fact]
        public void RoundTrip_Welcome_PreservesFieldsAndPeerRoster()
        {
            var peers = new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") };
            var m = RoundTrip(new WelcomeMessage(1, "7f3a91", 1, "host", peers, 3, "tok"));
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("7f3a91", m.SessionId);
            Assert.Equal(1, m.AssignedPeerId);
            Assert.Equal("host", m.HostName);
            Assert.Equal(3, m.CurrentTurn);
            Assert.Equal(2, m.Peers.Count);
            Assert.Equal(1, m.Peers[1].PeerId);
            Assert.Equal("ally", m.Peers[1].Name);
        }

        [Fact]
        public void RoundTrip_Welcome_WithNoPeers_PreservesEmptyRoster()
        {
            Assert.Empty(RoundTrip(new WelcomeMessage(1, "s", 0, "host", null, 0, "tok")).Peers);
        }

        [Theory]
        [InlineData(RejectReason.BadMagic)]
        [InlineData(RejectReason.VersionMismatch)]
        [InlineData(RejectReason.SessionFull)]
        [InlineData(RejectReason.DuplicateName)]
        [InlineData(RejectReason.InvalidName)]
        [InlineData(RejectReason.NotAcceptingPeers)]
        public void RoundTrip_Reject_PreservesEveryReason(RejectReason reason)
        {
            var m = RoundTrip(new RejectMessage(reason, "detail"));
            Assert.Equal(reason, m.Reason);
            Assert.Equal("detail", m.Detail);
        }

        [Fact]
        public void RoundTrip_Reject_WithNullDetail_PreservesNull()
        {
            Assert.Null(RoundTrip(new RejectMessage(RejectReason.BadMagic, null)).Detail);
        }

        [Fact]
        public void RoundTrip_PeerJoined_PreservesFields()
        {
            var m = RoundTrip(new PeerJoinedMessage(2, "ally2"));
            Assert.Equal(2, m.PeerId);
            Assert.Equal("ally2", m.Name);
        }

        [Fact]
        public void RoundTrip_PeerLeft_PreservesFields()
        {
            var m = RoundTrip(new PeerLeftMessage(3, "ally3"));
            Assert.Equal(3, m.PeerId);
            Assert.Equal("ally3", m.Name);
        }

        [Fact]
        public void RoundTrip_Ready_PreservesTurnAndOrders()
        {
            var orders = new[]
            {
                new OrderPayload("move_run", "unit_a", 0f, 2f),
                new OrderPayload("attack_primary", "unit_b", 1.5f, 0.75f, targetedEntityName: "enemy_1"),
            };
            var m = RoundTrip(new ReadyMessage(3, orders));
            Assert.Equal(3, m.Turn);
            Assert.Equal(2, m.Orders.Count);
            Assert.Equal("move_run", m.Orders[0].Blueprint);
            Assert.Equal("enemy_1", m.Orders[1].TargetedEntityName);
        }

        [Fact]
        public void RoundTrip_Ready_WithNoOrders_PreservesEmptyBatch()
        {
            Assert.Empty(RoundTrip(new ReadyMessage(5, null)).Orders);
        }

        [Fact]
        public void RoundTrip_TurnCommit_PreservesTurn()
        {
            Assert.Equal(7, RoundTrip(new TurnCommitMessage(7)).Turn);
        }

        [Fact]
        public void RoundTrip_TurnComplete_PreservesTurnAndDigest()
        {
            var m = RoundTrip(new TurnCompleteMessage(7, "3f9c1a04"));
            Assert.Equal(7, m.Turn);
            Assert.Equal("3f9c1a04", m.Digest);
        }

        [Fact]
        public void RoundTrip_Assignments_PreservesEveryPeersUnits()
        {
            var m = RoundTrip(new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a", "unit_c" }),
                new PeerAssignment(1, new[] { "unit_b" }),
            }));
            Assert.Equal(2, m.Assignments.Count);
            Assert.Equal(0, m.Assignments[0].PeerId);
            Assert.Equal(new[] { "unit_a", "unit_c" }, m.Assignments[0].UnitNames);
            Assert.Equal(new[] { "unit_b" }, m.Assignments[1].UnitNames);
        }

        [Fact]
        public void RoundTrip_Assignments_WithPeerHoldingNoUnits_PreservesEmpty()
        {
            var m = RoundTrip(new AssignmentsMessage(new[] { new PeerAssignment(2, null) }));
            Assert.Empty(m.Assignments[0].UnitNames);
        }

        [Fact]
        public void RoundTrip_Assignments_WithNoEntries_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new AssignmentsMessage(null)).Assignments);
        }

        [Fact]
        public void Decode_AssignmentsWithTooManyUnits_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Assignments);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxUnitsPerPeer + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_AssignmentsWithNullUnitName_ReadsAsEmptyString()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Assignments);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteString(null);
            var m = Assert.IsType<AssignmentsMessage>(PbjMessageCodec.Decode(writer.ToArray()));
            Assert.Equal(string.Empty, m.Assignments[0].UnitNames[0]);
        }

        [Fact]
        public void RoundTrip_Bye_PreservesReason()
        {
            Assert.Equal("host shutting down", RoundTrip(new ByeMessage("host shutting down")).Reason);
        }

        [Fact]
        public void RoundTrip_Bye_WithNullReason_PreservesNull()
        {
            Assert.Null(RoundTrip(new ByeMessage(null)).Reason);
        }

        [Fact]
        public void RoundTrip_Unready_PreservesTurn()
        {
            Assert.Equal(9, RoundTrip(new UnreadyMessage(9)).Turn);
        }

        [Fact]
        public void RoundTrip_OrderResult_PreservesCountsAndEveryRejection()
        {
            var m = RoundTrip(new OrderResultMessage(4, 2, new[]
            {
                new RejectedOrder(0, OrderApplyResult.NotOwned),
                new RejectedOrder(3, OrderApplyResult.OutOfWindow),
            }));

            Assert.Equal(4, m.Turn);
            Assert.Equal(2, m.Accepted);
            Assert.Equal(2, m.Rejected.Count);
            Assert.Equal(0, m.Rejected[0].Index);
            Assert.Equal(OrderApplyResult.NotOwned, m.Rejected[0].Reason);
            Assert.Equal(3, m.Rejected[1].Index);
            Assert.Equal(OrderApplyResult.OutOfWindow, m.Rejected[1].Reason);
        }

        [Fact]
        public void RoundTrip_OrderResult_WithNullRejections_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new OrderResultMessage(4, 0, null)).Rejected);
        }

        [Fact]
        public void Decode_OrderResultWithTooManyRejections_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.OrderResult);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxOrdersPerReady + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void RoundTrip_CombatStart_PreservesTurn()
        {
            Assert.Equal(0, RoundTrip(new CombatStartMessage(0)).Turn);
        }

        [Fact]
        public void Encode_CombatEnd_IsTypeByteOnly()
        {
            // The type byte is the whole message; adding a body later is a wire break.
            Assert.Equal(new byte[] { (byte)PbjMessageType.CombatEnd }, PbjMessageCodec.Encode(new CombatEndMessage()));
        }

        [Fact]
        public void RoundTrip_PingAndPong_PreserveTheNonce()
        {
            Assert.Equal(int.MaxValue, RoundTrip(new PingMessage(int.MaxValue)).Nonce);
            Assert.Equal(-7, RoundTrip(new PongMessage(-7)).Nonce);
        }
    }
}
