using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Rejoin, resume tokens and the grace window. One section of the original.
    //
    // WithTickedPeer and Rejoin live here rather than in the primary: 21 of 22 and 10 of
    // 11 of their call sites are in this file, and the single stray in each is one test
    // in .LobbyReady.cs.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- reconnect ---

        /// <summary>A peer that has handshaken and been ticked, so it can be held.</summary>
        private HostSession WithTickedPeer(out string token, int peerId = 1, string name = "ally")
        {
            var host = Host();
            host.Handle(new TickEvent(1000));
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            var welcome = Messages<WelcomeMessage>(host.HandleMessage(peerId, GoodHello(name))).Single();
            token = welcome.ResumeToken!;
            return host;
        }

        private static RejoinMessage Rejoin(string token, int claimedPeerId = 1, string name = "ally",
            string session = "7f3a91") =>
            new RejoinMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name, session, claimedPeerId, token, null, null);

        [Fact]
        public void Welcome_IssuesAResumeToken()
        {
            WithTickedPeer(out var token);
            Assert.False(string.IsNullOrEmpty(token));
        }

        [Fact]
        public void ResumeToken_IsNotDerivableFromAnythingOnTheWire()
        {
            // Two sessions identical but for their secret must issue different
            // tokens, or the token is no credential at all — session id, peer id
            // and player name all reach every client.
            var a = new HostSession("host", "7f3a91", 3, bridge, "secret-a", SessionRequirements.None);
            var b = new HostSession("host", "7f3a91", 3, bridge, "secret-b", SessionRequirements.None);
            a.Handle(new TickEvent(1000));
            b.Handle(new TickEvent(1000));

            var tokenA = (Messages<WelcomeMessage>(a.HandleMessage(1, GoodHello())).Single()).ResumeToken;
            var tokenB = (Messages<WelcomeMessage>(b.HandleMessage(1, GoodHello())).Single()).ResumeToken;
            Assert.NotEqual(tokenA, tokenB);
        }

        [Fact]
        public void Disconnect_HoldsThePeersUnitsInsteadOfReassigning()
        {
            // Reassigning here would deal the combat again over the remaining
            // peers and destroy the binding a rejoin needs.
            var host = WithTickedPeer(out _);
            var before = host.Assignments.UnitsFor(1);
            Assert.NotEmpty(before);

            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Equal(before, host.Assignments.UnitsFor(1));
        }

        [Fact]
        public void Disconnect_StillFreesTheBarrierImmediately()
        {
            // Holding units must not mean holding the turn.
            var host = WithTickedPeer(out _);
            Assert.Equal(2, host.ParticipantCount);

            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Equal(1, host.ParticipantCount);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void Rejoin_RebindsTheSameUnitsToTheNewPeerId()
        {
            var host = WithTickedPeer(out var token);
            var held = host.Assignments.UnitsFor(1);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, Rejoin(token));

            Assert.Equal(held, host.Assignments.UnitsFor(2));
            Assert.Empty(host.Assignments.UnitsFor(1));
            var welcome = (WelcomeMessage)All<SendEffect>(effects).Select(s => s.Message)
                .OfType<WelcomeMessage>().Single();
            Assert.Equal(2, welcome.AssignedPeerId);
        }

        [Fact]
        public void Rejoin_DoesNotReshuffleEveryoneElse()
        {
            var host = WithTickedPeer(out var token);
            var hostUnitsBefore = host.Assignments.UnitsFor(0);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, Rejoin(token));

            Assert.Equal(hostUnitsBefore, host.Assignments.UnitsFor(0));
        }

        [Fact]
        public void Rejoin_IssuesAFreshTokenForTheNewId()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var welcome = (WelcomeMessage)All<SendEffect>(host.HandleMessage(2, Rejoin(token)))
                .Select(s => s.Message).OfType<WelcomeMessage>().Single();
            Assert.NotEqual(token, welcome.ResumeToken);
        }

        [Fact]
        public void Rejoin_WithAWrongToken_IsRefused()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2, Rejoin("not-the-token"))).Single();
            Assert.Equal(RejectReason.BadResumeToken, reject.Reason);
        }

        [Fact]
        public void Rejoin_ClaimingAPeerIdThatNeverLeft_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(
                host.HandleMessage(2, Rejoin(token, claimedPeerId: 7))).Single();
            Assert.Equal(RejectReason.BadResumeToken, reject.Reason);
        }

        [Fact]
        public void Rejoin_ToAnotherSession_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(
                host.HandleMessage(2, Rejoin(token, session: "someone-else"))).Single();
            Assert.Equal(RejectReason.UnknownSession, reject.Reason);
        }

        [Fact]
        public void Rejoin_WithABadProtocolVersion_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2, new RejoinMessage(PbjProtocol.Magic, 999, "0.2.0", "ally", "7f3a91", 1, token, null, null))).Single();
            Assert.Equal(RejectReason.VersionMismatch, reject.Reason);
        }

        [Fact]
        public void Rejoin_WithBadMagic_IsRefusedWithNoVersionDetail()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2, new RejoinMessage(0xDEAD, PbjProtocol.Version, "0.2.0", "ally", "7f3a91", 1, token, null, null))).Single();
            Assert.Equal(RejectReason.BadMagic, reject.Reason);
            Assert.Null(reject.Detail);
        }

        [Fact]
        public void Rejoin_FromAnAlreadyRegisteredConnection_IsAViolation()
        {
            var host = WithTickedPeer(out var token);
            var effects = host.HandleMessage(1, Rejoin(token));
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void Rejoin_WhileExecuting_TellsThePeerTheTurnIsAlreadyRunning()
        {
            var host = WithTickedPeer(out var token);
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var sent = All<SendEffect>(host.HandleMessage(2, Rejoin(token))).Select(s => s.Message).ToList();
            Assert.Contains(sent, m => m is TurnCommitMessage);
        }

        [Fact]
        public void Hello_CannotTakeAHeldPlayersNameDuringTheGraceWindow()
        {
            // Otherwise a stranger steals the name and the real owner's rejoin
            // is refused as a duplicate through no fault of its own.
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2, GoodHello("ally"))).Single();
            Assert.Equal(RejectReason.DuplicateName, reject.Reason);
            Assert.Equal("reserved for a reconnect", reject.Detail);
        }

        [Fact]
        public void Hello_WithADifferentName_IsStillAcceptedDuringAHold()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var welcome = All<SendEffect>(host.HandleMessage(2, GoodHello("someone-else")))
                .Select(s => s.Message).OfType<WelcomeMessage>().SingleOrDefault();
            Assert.NotNull(welcome);
        }

        [Fact]
        public void GraceExpiry_ReleasesTheUnitsAndReassigns()
        {
            // Pruning is not bookkeeping — it is the only path that puts a
            // permanently-gone player's units back into play.
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.NotEmpty(host.Assignments.UnitsFor(1));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.ReconnectGraceSeconds));
            Assert.Empty(host.Assignments.UnitsFor(1));
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is AssignmentsMessage);
        }

        [Fact]
        public void GraceExpiry_GivesTheUnitsBackToTheRemainingPlayers()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new TickEvent(1000 + PbjProtocol.ReconnectGraceSeconds));

            Assert.Equal(new[] { "unit_a", "unit_b", "unit_c" }, host.Assignments.UnitsFor(0));
        }

        [Fact]
        public void Rejoin_AfterTheGraceExpired_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new TickEvent(1000 + PbjProtocol.ReconnectGraceSeconds));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2, Rejoin(token))).Single();
            Assert.Equal(RejectReason.BadResumeToken, reject.Reason);
        }

        [Fact]
        public void GraceExpiry_BeforeTheDeadline_ChangesNothing()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            host.Handle(new TickEvent(1000 + PbjProtocol.ReconnectGraceSeconds - 1));
            Assert.NotEmpty(host.Assignments.UnitsFor(1));
        }

        [Fact]
        public void Tick_WithNoHolds_DoesNoExpiryWork()
        {
            var host = WithTickedPeer(out _);
            // Inside the peer timeout, so the only thing that could broadcast
            // here is expiry work — and there is none pending.
            Assert.Empty(All<BroadcastEffect>(host.Handle(new TickEvent(1001))));
        }

        [Fact]
        public void Disconnect_OutOfCombat_HoldsNothing()
        {
            // No units are assigned outside combat, so there is nothing to hold
            // and the normal reassign path applies.
            bridge.InCombat = false;
            var host = Host();
            host.Handle(new TickEvent(1000));
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            host.HandleMessage(1, GoodHello());

            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var welcome = All<SendEffect>(host.HandleMessage(2, GoodHello("ally")))
                .Select(s => s.Message).OfType<WelcomeMessage>().SingleOrDefault();
            Assert.NotNull(welcome);
        }

        [Fact]
        public void Rejoin_WhenTheSessionFilledUpMeanwhile_IsRefused()
        {
            var host = new HostSession("host", "7f3a91", 1, bridge, "secret", SessionRequirements.None);
            host.Handle(new TickEvent(1000));
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var token = (Messages<WelcomeMessage>(host.HandleMessage(1, GoodHello())).Single()).ResumeToken!;
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            // Someone else takes the only slot while the hold stands.
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("other"));

            host.Handle(new PeerConnectedEvent(3, "127.0.0.1:3"));
            var reject = Messages<RejectMessage>(host.HandleMessage(3, Rejoin(token))).Single();
            Assert.Equal(RejectReason.SessionFull, reject.Reason);
        }

        [Fact]
        public void Hello_WithNoName_IsNotConfusedWithAHeldName()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = Messages<RejectMessage>(host.HandleMessage(2,
                new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", null, null, null))).Single();
            Assert.Equal(RejectReason.InvalidName, reject.Reason);
        }

        // The one Constructor_ test not in .Construction.cs, and it was in the
        // reconnect section of the original: the session secret is what mints the
        // resume tokens the rest of this file is about.
        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankSessionSecret_Throws(string? secret)
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession("h", "s", 3, bridge, secret!, SessionRequirements.None));
            Assert.Equal("sessionSecret", ex.ParamName);
        }

        [Fact]
        public void Disconnect_BeforeAnyTick_HoldsNothing()
        {
            // Without a tick there is no clock to expire a hold with, so holding
            // one would strand those units for the rest of the combat.
            var host = WithPeer();
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Empty(host.Assignments.UnitsFor(1));
        }
    }
}
