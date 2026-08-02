using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class HostSessionTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private HostSession Host(int maxPeers = 3) => new HostSession("host", "7f3a91", maxPeers, bridge);

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name);

        private static OrderPayload Order(string owner) => new OrderPayload("move_run", owner, 0f, 2f);

        /// <summary>Connects and handshakes a peer, discarding the effects.</summary>
        private HostSession WithPeer(int peerId = 1, string name = "ally", int maxPeers = 3)
        {
            var host = Host(maxPeers);
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            host.HandleMessage(peerId, GoodHello(name));
            return host;
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        // --- construction ---

        [Fact]
        public void Constructor_StartsInPlanningWhenAlreadyInCombat()
        {
            Assert.Equal(HostSessionState.Planning, Host().State);
        }

        [Fact]
        public void Constructor_StartsInLobbyWhenNotInCombat()
        {
            bridge.InCombat = false;
            Assert.Equal(HostSessionState.Lobby, Host().State);
        }

        [Fact]
        public void Constructor_CountsTheHostAsAParticipant()
        {
            Assert.Equal(1, Host().ParticipantCount);
        }

        [Fact]
        public void Constructor_TakesTheCurrentTurnFromTheBridge()
        {
            bridge.CurrentTurn = 9;
            Assert.Equal(9, Host().Turn);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankHostName_Throws(string? name)
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession(name!, "s", 3, bridge));
            Assert.Equal("hostName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithBlankSessionId_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession("h", " ", 3, bridge));
            Assert.Equal("sessionId", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new HostSession("h", "s", 3, null!));
            Assert.Equal("bridge", ex.ParamName);
        }

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

            var welcome = Assert.IsType<WelcomeMessage>(Single<SendEffect>(effects).Message);
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
            var welcome = (WelcomeMessage)Single<SendEffect>(effects).Message;
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
            var effects = host.HandleMessage(1, new HelloMessage(0xDEAD, PbjProtocol.Version, "0.2.0", "ally"));
            var reject = Assert.IsType<RejectMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(RejectReason.BadMagic, reject.Reason);
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void HandleMessage_Hello_WithVersionMismatch_RejectsWithDetail()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, 999, "0.2.0", "ally"));
            var reject = Assert.IsType<RejectMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(RejectReason.VersionMismatch, reject.Reason);
            Assert.Equal("peer v999, host v1", reject.Detail);
        }

        [Fact]
        public void HandleMessage_Hello_WithDuplicateName_Rejects()
        {
            var host = WithPeer(1, "ally");
            var effects = host.HandleMessage(2, GoodHello("ally"));
            var reject = Assert.IsType<RejectMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(RejectReason.DuplicateName, reject.Reason);
        }

        [Fact]
        public void HandleMessage_Hello_WhenAtCapacity_RejectsSessionFull()
        {
            var host = WithPeer(1, "a", maxPeers: 1);
            var effects = host.HandleMessage(2, GoodHello("b"));
            Assert.Equal(RejectReason.SessionFull,
                ((RejectMessage)Single<SendEffect>(effects).Message).Reason);
        }

        [Fact]
        public void HandleMessage_Hello_WithBlankName_RejectsInvalidName()
        {
            // The message layer deliberately lets this through so the session
            // can answer with a clean Reject rather than a decode failure.
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "v", "   "));
            Assert.Equal(RejectReason.InvalidName,
                ((RejectMessage)Single<SendEffect>(effects).Message).Reason);
        }

        [Fact]
        public void HandleMessage_Hello_Twice_DisconnectsPeer()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, GoodHello("ally"));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        // --- disconnect ---

        [Fact]
        public void Handle_PeerDisconnected_BroadcastsPeerLeft()
        {
            var host = WithPeer();
            var effects = host.Handle(new PeerDisconnectedEvent(1, "transport closed"));
            var left = Assert.IsType<PeerLeftMessage>(All<BroadcastEffect>(effects).First().Message);
            Assert.Equal(1, left.PeerId);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void Handle_PeerDisconnected_ForUnknownPeer_ProducesNoEffects()
        {
            Assert.Empty(Host().Handle(new PeerDisconnectedEvent(99, "gone")));
        }

        [Fact]
        public void HandleMessage_Bye_RemovesPeerAndDisconnects()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ByeMessage("quitting"));
            Assert.Empty(host.Peers);
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        [Fact]
        public void HandleMessage_WithHostOnlyMessage_DisconnectsPeer()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new TurnCommitMessage(3));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void HandleMessage_HostOnlyMessage_FromUnregisteredPeer_StillDisconnects()
        {
            var host = Host();
            var effects = host.HandleMessage(7, new TurnCommitMessage(3));
            Assert.Equal(7, Single<DisconnectEffect>(effects).PeerId);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("#7 '?'"));
        }

        [Fact]
        public void HandleMessage_Ready_BeforeHello_DisconnectsPeer()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        [Fact]
        public void HandleMessage_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Host().HandleMessage(1, null!));
            Assert.Equal("message", ex.ParamName);
        }

        // --- the barrier ---

        [Fact]
        public void HandleMessage_Ready_FromOnlyClient_WaitsForHost()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            Assert.Empty(All<CommitTurnEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("barrier 1/2"));
        }

        [Fact]
        public void Handle_LocalReady_WhenNoClientsReady_DoesNotCommit()
        {
            var host = WithPeer();
            Assert.Empty(All<CommitTurnEffect>(host.Handle(new LocalReadyEvent())));
        }

        [Fact]
        public void Handle_LocalReady_WhenAllReady_AppliesOrdersThenCommits()
        {
            // The money test: apply -> commit. Nothing is broadcast until the
            // commit is confirmed by a CommitOutcomeEvent.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());

            Assert.Collection(effects,
                e => Assert.Contains("committing turn 3", Assert.IsType<LogEffect>(e).Line),
                e => Assert.Equal("unit_b", Assert.IsType<ApplyOrderEffect>(e).Order.OwnerName),
                e => Assert.Contains("applied 1 remote order", Assert.IsType<LogEffect>(e).Line),
                e => Assert.Equal(3, Assert.IsType<CommitTurnEffect>(e).Turn));
            Assert.Empty(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void Handle_CommitOutcome_WhenCommitted_BroadcastsTurnCommitAndLocks()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, committed: true));

            Assert.Equal(3, Assert.IsType<TurnCommitMessage>(Single<BroadcastEffect>(effects).Message).Turn);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void Handle_CommitOutcome_WhenRefused_UnlocksPeersAndStaysInPlanning()
        {
            // ConfirmExecution refuses silently in four normal situations; if we
            // had already broadcast TurnCommit, every peer would wait forever.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, committed: false));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("commit REFUSED for turn 3"));
            Assert.Empty(All<BroadcastEffect>(effects));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void HandleMessage_Ready_Twice_ReplacesPreviousOrderSet()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_b") }));
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Single(All<ApplyOrderEffect>(effects));
        }

        [Fact]
        public void HandleMessage_Ready_ForStaleTurn_IsIgnored()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(2, null));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("stale ready"));
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void HandleMessage_Ready_ForFutureTurn_ResyncsInsteadOfDisconnecting()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(4, null));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(3, Assert.IsType<TurnCommitMessage>(Single<SendEffect>(effects).Message).Turn);
        }

        [Fact]
        public void HandleMessage_Ready_DuringExecution_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("stale ready"));
        }

        [Fact]
        public void Handle_LocalReady_WhenNotPlanning_ProducesNoEffects()
        {
            bridge.InCombat = false;
            Assert.Empty(Host().Handle(new LocalReadyEvent()));
        }

        [Fact]
        public void Handle_PeerDisconnected_WhileHostReady_CommitsImmediately()
        {
            // A dead peer must never wedge the session.
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Equal(3, Single<CommitTurnEffect>(effects).Turn);
        }

        // --- ownership enforcement ---

        [Fact]
        public void HandleMessage_Ready_WithOrderForUnownedUnit_DropsThatOrder()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            var effects = host.Handle(new LocalReadyEvent());

            Assert.Empty(All<ApplyOrderEffect>(effects));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("order REJECTED from #1: unit_a is not assigned"));
        }

        [Fact]
        public void HandleMessage_Ready_WithMixedOwnership_AppliesOnlyOwnedOrders()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a"), Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Equal("unit_b", Single<ApplyOrderEffect>(effects).Order.OwnerName);
        }

        [Fact]
        public void HandleMessage_Ready_WithAllOrdersUnowned_StillMarksPeerReady()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            Assert.Equal(1, host.ReadyCount);
        }

        // --- turn completion ---

        [Fact]
        public void Handle_LocalTurnComplete_BroadcastsTheCommittedTurnNotTheAdvancedOne()
        {
            // The ECS advances currentTurn before the sim runs, so reading it
            // back here would report turn+1.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.CurrentTurn = 4;

            var effects = host.Handle(new LocalTurnCompleteEvent("3f9c1a04"));
            var complete = Assert.IsType<TurnCompleteMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.Equal(3, complete.Turn);
            Assert.Equal("3f9c1a04", complete.Digest);
        }

        [Fact]
        public void Handle_LocalTurnComplete_ReturnsToPlanningAndUnlocks()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.CurrentTurn = 4;

            var effects = host.Handle(new LocalTurnCompleteEvent("d"));
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(4, host.Turn);
            Assert.Equal(0, host.ReadyCount);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_LocalTurnComplete_WhenNotExecuting_ProducesNoEffects()
        {
            Assert.Empty(WithPeer().Handle(new LocalTurnCompleteEvent("d")));
        }

        // --- transport failure and teardown ---

        [Fact]
        public void Handle_TransportFailed_ClosesSessionAndUnlocks()
        {
            var host = WithPeer();
            var effects = host.Handle(new TransportFailedEvent("listener died"));
            Assert.Equal(HostSessionState.Closed, host.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("listener died"));
        }

        [Fact]
        public void Handle_TransportFailed_WithNoReason_StillLogs()
        {
            Assert.Contains(All<LogEffect>(Host().Handle(new TransportFailedEvent(null))),
                l => l.Line.Contains("unknown"));
        }

        [Fact]
        public void Handle_AfterClosed_ProducesNoEffects()
        {
            var host = Host();
            host.Handle(new TransportFailedEvent("x"));
            Assert.Empty(host.Handle(new LocalReadyEvent()));
            Assert.Empty(host.HandleMessage(1, GoodHello()));
        }

        [Fact]
        public void Handle_TransportLog_ForwardsTheLine()
        {
            Assert.Equal("accepted 127.0.0.1:1",
                Single<LogEffect>(Host().Handle(new TransportLogEvent("accepted 127.0.0.1:1"))).Line);
        }

        [Fact]
        public void Handle_TransportLog_WithNoLine_LogsPlaceholder()
        {
            Assert.Equal("unknown", Single<LogEffect>(Host().Handle(new TransportLogEvent(null))).Line);
        }

        [Fact]
        public void Handle_PeerBytes_ProducesNoEffects()
        {
            // Raw bytes are decoded by the runtime and arrive via HandleMessage.
            Assert.Empty(Host().Handle(new PeerBytesEvent(1, new byte[] { 1 })));
        }

        [Fact]
        public void Handle_WithNullEvent_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Host().Handle(null!));
            Assert.Equal("evt", ex.ParamName);
        }

        [Fact]
        public void Handle_WithUnsupportedEventKind_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => Host().Handle(new UnsupportedEvent()));
        }

        private sealed class UnsupportedEvent : PbjInboundEvent
        {
            public override PbjInboundEventKind Kind => (PbjInboundEventKind)200;
        }
    }
}
