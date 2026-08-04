using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Message types are data carriers, not validators — see
    // docs/design/networking.md, "Messages do not validate; sessions do".
    // These tests therefore assert field retention and list normalisation only.
    public class PbjMessageTests
    {
        [Fact]
        public void Hello_RetainsFields()
        {
            var m = new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally", null, null);
            Assert.Equal(PbjMessageType.Hello, m.Type);
            Assert.Equal(PbjProtocol.Magic, m.Magic);
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("0.2.0", m.ModVersion);
            Assert.Equal("ally", m.PlayerName);
        }

        [Fact]
        public void Hello_AcceptsBlankNameSoTheSessionCanRejectItCleanly()
        {
            // A guard here would turn "peer sent an empty name" into a decode
            // failure and a disconnect, instead of a Reject{InvalidName}.
            var m = new HelloMessage(0, 0, null, "   ", null, null);
            Assert.Null(m.ModVersion);
            Assert.Equal("   ", m.PlayerName);
        }

        [Fact]
        public void Welcome_RetainsFields()
        {
            var peers = new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") };
            var m = new WelcomeMessage(1, "7f3a91", 1, "host", peers, 3, "tok");
            Assert.Equal(PbjMessageType.Welcome, m.Type);
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("7f3a91", m.SessionId);
            Assert.Equal(1, m.AssignedPeerId);
            Assert.Equal("host", m.HostName);
            Assert.Equal(2, m.Peers.Count);
            Assert.Equal(3, m.CurrentTurn);
        }

        [Fact]
        public void Welcome_WithNullPeers_NormalisesToEmpty()
        {
            Assert.Empty(new WelcomeMessage(1, "s", 1, "h", null, 0, "tok").Peers);
        }

        [Fact]
        public void PeerInfo_RetainsFields()
        {
            var info = new PeerInfo(4, "someone");
            Assert.Equal(4, info.PeerId);
            Assert.Equal("someone", info.Name);
        }

        [Fact]
        public void Reject_RetainsFields()
        {
            var m = new RejectMessage(RejectReason.VersionMismatch, "peer v999, host v1");
            Assert.Equal(PbjMessageType.Reject, m.Type);
            Assert.Equal(RejectReason.VersionMismatch, m.Reason);
            Assert.Equal("peer v999, host v1", m.Detail);
        }

        [Fact]
        public void PeerJoined_RetainsFields()
        {
            var m = new PeerJoinedMessage(2, "ally2");
            Assert.Equal(PbjMessageType.PeerJoined, m.Type);
            Assert.Equal(2, m.PeerId);
            Assert.Equal("ally2", m.Name);
        }

        [Fact]
        public void PeerLeft_RetainsFields()
        {
            var m = new PeerLeftMessage(2, "ally2");
            Assert.Equal(PbjMessageType.PeerLeft, m.Type);
            Assert.Equal(2, m.PeerId);
            Assert.Equal("ally2", m.Name);
        }

        [Fact]
        public void Ready_RetainsFields()
        {
            var orders = new[] { new OrderPayload("move_run", "unit_a", 0f, 2f) };
            var m = new ReadyMessage(3, orders);
            Assert.Equal(PbjMessageType.Ready, m.Type);
            Assert.Equal(3, m.Turn);
            Assert.Single(m.Orders);
        }

        [Fact]
        public void Ready_WithNullOrders_NormalisesToEmpty()
        {
            Assert.Empty(new ReadyMessage(0, null).Orders);
        }

        [Fact]
        public void TurnCommit_RetainsFields()
        {
            var m = new TurnCommitMessage(7);
            Assert.Equal(PbjMessageType.TurnCommit, m.Type);
            Assert.Equal(7, m.Turn);
        }

        [Fact]
        public void TurnComplete_RetainsFields()
        {
            var m = new TurnCompleteMessage(7, "3f9c1a04");
            Assert.Equal(PbjMessageType.TurnComplete, m.Type);
            Assert.Equal(7, m.Turn);
            Assert.Equal("3f9c1a04", m.Digest);
        }

        [Fact]
        public void Assignments_RetainsEntries()
        {
            var m = new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_b" }) });
            Assert.Equal(PbjMessageType.Assignments, m.Type);
            Assert.Single(m.Assignments);
            Assert.Equal(1, m.Assignments[0].PeerId);
            Assert.Equal(new[] { "unit_b" }, m.Assignments[0].UnitNames);
        }

        [Fact]
        public void Assignments_WithNullEntries_NormalisesToEmpty()
        {
            Assert.Empty(new AssignmentsMessage(null).Assignments);
        }

        [Fact]
        public void PeerAssignment_WithNullUnits_NormalisesToEmpty()
        {
            Assert.Empty(new PeerAssignment(1, null).UnitNames);
        }

        [Fact]
        public void Bye_RetainsFields()
        {
            var m = new ByeMessage("host shutting down");
            Assert.Equal(PbjMessageType.Bye, m.Type);
            Assert.Equal("host shutting down", m.Reason);
        }

        [Fact]
        public void Bye_WithNullReason_IsAccepted()
        {
            Assert.Null(new ByeMessage(null).Reason);
        }

        [Fact]
        public void LobbyState_RetainsFields()
        {
            var m = new LobbyStateMessage(3, "pbj_campaign", "3f9c1a04", new[]
            {
                new LobbyPeerState(0, "host", true),
            });
            Assert.Equal(PbjMessageType.LobbyState, m.Type);
            Assert.Equal(3, m.SelectionVersion);
            Assert.Equal("pbj_campaign", m.SaveKey);
            Assert.Equal("3f9c1a04", m.SaveDigest);
            Assert.Single(m.Peers);
        }

        [Fact]
        public void LobbyState_WithNullPeers_NormalisesToEmpty()
        {
            Assert.Empty(new LobbyStateMessage(0, null, null, null).Peers);
        }

        [Fact]
        public void LobbyPeerState_RetainsFields()
        {
            var state = new LobbyPeerState(4, "someone", true);
            Assert.Equal(4, state.PeerId);
            Assert.Equal("someone", state.Name);
            Assert.True(state.Ready);
        }

        [Fact]
        public void LobbyReady_RetainsFields()
        {
            var m = new LobbyReadyMessage(3);
            Assert.Equal(PbjMessageType.LobbyReady, m.Type);
            Assert.Equal(3, m.SelectionVersion);
        }

        [Fact]
        public void LobbyUnready_RetainsFields()
        {
            var m = new LobbyUnreadyMessage(3);
            Assert.Equal(PbjMessageType.LobbyUnready, m.Type);
            Assert.Equal(3, m.SelectionVersion);
        }
    }
}
