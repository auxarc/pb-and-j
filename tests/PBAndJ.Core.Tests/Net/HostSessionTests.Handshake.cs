using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The handshake: who is admitted, who is rejected, and what a new peer is told.
    // One section of the original. Disconnect is its own file next door -- an earlier
    // draft merged the two under this name, which covered only half of what it held.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- handshake ---

        [Fact]
        public void Handle_PeerConnected_LogsButDoesNotRegister()
        {
            var host = Host();
            var effects = host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            Assert.Single(All<LogEffect>(effects));
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void HandleMessage_Hello_SendsWelcomeThenBroadcastsPeerJoined()
        {
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "r"));
            var effects = host.HandleMessage(1, GoodHello());

            var welcome = Messages<WelcomeMessage>(effects).Single();
            Assert.Equal(1, welcome.AssignedPeerId);
            Assert.Equal("7f3a91", welcome.SessionId);
            Assert.Equal("host", welcome.HostName);
            Assert.Equal(3, welcome.CurrentTurn);

            var joinBroadcast = All<BroadcastEffect>(effects).Single(b => b.Message is PeerJoinedMessage);
            Assert.Equal(1, ((PeerJoinedMessage)joinBroadcast.Message).PeerId);
            Assert.Equal(1, joinBroadcast.ExceptPeerId);
        }

        [Fact]
        public void HandleMessage_Hello_IncludesHostAndPeerInTheRoster()
        {
            var host = Host();
            var effects = host.HandleMessage(1, GoodHello());
            var welcome = Messages<WelcomeMessage>(effects).Single();
            Assert.Equal(2, welcome.Peers.Count);
            Assert.Equal(PbjPeerRegistry.HostPeerId, welcome.Peers[0].PeerId);
            Assert.Equal("host", welcome.Peers[0].Name);
            Assert.Equal(1, welcome.Peers[1].PeerId);
        }

        [Fact]
        public void HandleMessage_Hello_AddsPeerAsBarrierParticipant()
        {
            Assert.Equal(2, WithPeer().ParticipantCount);
        }

        [Fact]
        public void HandleMessage_Hello_AssignsUnits()
        {
            var host = WithPeer();
            Assert.Equal(new[] { "unit_a", "unit_c" }, host.Assignments.UnitsFor(0));
            Assert.Equal(new[] { "unit_b" }, host.Assignments.UnitsFor(1));
        }

        [Fact]
        public void HandleMessage_Hello_BroadcastsAssignmentsSoClientsKnowWhatTheyOwn()
        {
            var host = Host();
            var effects = host.HandleMessage(1, GoodHello());
            var message = All<BroadcastEffect>(effects)
                .Select(b => b.Message).OfType<AssignmentsMessage>().Single();

            Assert.Equal(2, message.Assignments.Count);
            Assert.Equal(new[] { "unit_a", "unit_c" }, message.Assignments[0].UnitNames);
            Assert.Equal(new[] { "unit_b" }, message.Assignments[1].UnitNames);
        }

        [Fact]
        public void HandleMessage_Hello_OutOfCombat_DoesNotAssign()
        {
            bridge.InCombat = false;
            var host = Host();
            var effects = host.HandleMessage(1, GoodHello());
            Assert.Empty(host.Assignments.PeerIds);
            Assert.Empty(All<BroadcastEffect>(effects).Select(b => b.Message).OfType<AssignmentsMessage>());
        }

        [Fact]
        public void HandleMessage_Hello_WithWrongMagic_RejectsAndDisconnects()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(0xDEAD, PbjProtocol.Version, "0.2.0", "ally", null, null));
            var reject = Messages<RejectMessage>(effects).Single();
            Assert.Equal(RejectReason.BadMagic, reject.Reason);
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void HandleMessage_Hello_WithVersionMismatch_RejectsWithDetail()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, 999, "0.2.0", "ally", null, null));
            var reject = Messages<RejectMessage>(effects).Single();
            Assert.Equal(RejectReason.VersionMismatch, reject.Reason);
            Assert.Equal("peer v999, host v" + PbjProtocol.Version, reject.Detail);
        }

        [Fact]
        public void HandleMessage_Hello_WithDuplicateName_Rejects()
        {
            var host = WithPeer(1, "ally");
            var effects = host.HandleMessage(2, GoodHello("ally"));
            var reject = Messages<RejectMessage>(effects).Single();
            Assert.Equal(RejectReason.DuplicateName, reject.Reason);
        }

        [Fact]
        public void HandleMessage_Hello_WhenAtCapacity_RejectsSessionFull()
        {
            var host = WithPeer(1, "a", maxPeers: 1);
            var effects = host.HandleMessage(2, GoodHello("b"));
            Assert.Equal(RejectReason.SessionFull,
                (Messages<RejectMessage>(effects).Single()).Reason);
        }

        [Fact]
        public void HandleMessage_Hello_WithBlankName_RejectsInvalidName()
        {
            // The message layer deliberately lets this through so the session
            // can answer with a clean Reject rather than a decode failure.
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "v", "   ", null, null));
            Assert.Equal(RejectReason.InvalidName,
                (Messages<RejectMessage>(effects).Single()).Reason);
        }

        [Fact]
        public void HandleMessage_Hello_Twice_DisconnectsPeer()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, GoodHello("ally"));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }
    }
}
