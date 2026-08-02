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

        private HostSession Host(int maxPeers = 3) => new HostSession("host", "7f3a91", maxPeers, bridge, "secret", SessionRequirements.None);

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name, null, null);

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
            var ex = Assert.Throws<ArgumentException>(() => new HostSession(name!, "s", 3, bridge, "secret", SessionRequirements.None));
            Assert.Equal("hostName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithBlankSessionId_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => new HostSession("h", " ", 3, bridge, "secret", SessionRequirements.None));
            Assert.Equal("sessionId", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new HostSession("h", "s", 3, null!, "secret", SessionRequirements.None));
            Assert.Equal("bridge", ex.ParamName);
        }

        // No permissive default: "accept anything" has to be spelled
        // SessionRequirements.None at the call site, so opening a session to
        // anyone is always something someone typed.
        [Fact]
        public void Constructor_WithNullRequirements_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new HostSession("h", "s", 3, bridge, "secret", null!));
            Assert.Equal("requirements", ex.ParamName);
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
            var effects = host.HandleMessage(1, new HelloMessage(0xDEAD, PbjProtocol.Version, "0.2.0", "ally", null, null));
            var reject = Assert.IsType<RejectMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(RejectReason.BadMagic, reject.Reason);
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void HandleMessage_Hello_WithVersionMismatch_RejectsWithDetail()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, 999, "0.2.0", "ally", null, null));
            var reject = Assert.IsType<RejectMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(RejectReason.VersionMismatch, reject.Reason);
            Assert.Equal("peer v999, host v" + PbjProtocol.Version, reject.Detail);
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
            var effects = host.HandleMessage(1, new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "v", "   ", null, null));
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

        [Fact]
        public void Disconnect_ArrivingTwice_IsHandledOnce()
        {
            // Since M5b a peer has both a writer and a receive thread, and both
            // post a PeerDisconnectedEvent when the socket goes. The second must
            // be inert, not a second PeerLeft broadcast or a double unassign.
            var host = WithPeer();
            var first = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            var second = host.Handle(new PeerDisconnectedEvent(1, "send failed"));

            Assert.Single(All<BroadcastEffect>(first), b => b.Message is PeerLeftMessage);
            Assert.Empty(second);
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

            var effects = host.Handle(new LocalTurnCompleteEvent("3f9c1a04", null, null));
            var complete = (TurnCompleteMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is TurnCompleteMessage).Message;
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

            var effects = host.Handle(new LocalTurnCompleteEvent("d", null, null));
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(4, host.Turn);
            Assert.Equal(0, host.ReadyCount);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_LocalTurnComplete_WhenNotExecuting_ProducesNoEffects()
        {
            Assert.Empty(WithPeer().Handle(new LocalTurnCompleteEvent("d", null, null)));
        }

        // --- un-ready ---

        [Fact]
        public void Unready_AfterReady_ClearsThatPeersReadiness()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            Assert.Equal(1, host.ReadyCount);

            host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void Unready_DiscardsTheSubmittedBatchSoItIsNotCommittedLater()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.HandleMessage(1, new UnreadyMessage(3));

            // Host readies alone; the barrier is satisfied because the peer is
            // no longer ready... it is, so nothing commits. Re-ready with nothing.
            host.HandleMessage(1, new ReadyMessage(3, null));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Empty(All<ApplyOrderEffect>(effects));
        }

        [Fact]
        public void Unready_WhenNotReady_IsANoOp()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new UnreadyMessage(3));

            Assert.Equal(0, host.ReadyCount);
            Assert.Empty(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void Unready_IsIdempotent()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.HandleMessage(1, new UnreadyMessage(3));
            host.HandleMessage(1, new UnreadyMessage(3));

            Assert.Equal(0, host.ReadyCount);
            Assert.Equal(HostSessionState.Planning, host.State);
        }

        [Fact]
        public void Unready_ForAnotherTurn_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.HandleMessage(1, new UnreadyMessage(2));

            Assert.Equal(1, host.ReadyCount);
        }

        [Fact]
        public void Unready_WhileExecuting_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            Assert.Equal(HostSessionState.Executing, host.State);

            var effects = host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void LocalUnready_AfterTheHostReadied_ClearsItAndUnlocks()
        {
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            Assert.Equal(1, host.ReadyCount);

            var effects = host.Handle(new LocalUnreadyEvent());
            Assert.Equal(0, host.ReadyCount);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void LocalUnready_WhenTheHostWasNotReady_IsANoOp()
        {
            Assert.Empty(WithPeer().Handle(new LocalUnreadyEvent()));
        }

        [Fact]
        public void LocalUnready_WhileExecuting_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            Assert.Empty(host.Handle(new LocalUnreadyEvent()));
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void LocalUnready_StopsATurnThatWouldOtherwiseHaveCommitted()
        {
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            host.Handle(new LocalUnreadyEvent());

            // The peer readying should no longer fill the barrier.
            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Empty(All<CommitTurnEffect>(effects));
        }

        [Fact]
        public void Unready_FromAnUnregisteredPeer_DisconnectsIt()
        {
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));

            var effects = host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        // --- order results ---

        [Fact]
        public void Commit_SendsEachSubmittingPeerAnOrderResult()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());

            // The apply effects fold their outcomes back before the commit runs;
            // the runtime does that, so drive it by hand here.
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }
            var outcome = host.Handle(new CommitOutcomeEvent(3, true));

            var result = (OrderResultMessage)Single<SendEffect>(outcome).Message;
            Assert.Equal(3, result.Turn);
            Assert.Equal(1, result.Accepted);
            Assert.Empty(result.Rejected);
        }

        [Fact]
        public void Commit_ReportsAnUnownedOrderByItsBatchIndex()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_a") }));
            var effects = host.Handle(new LocalReadyEvent());
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }
            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;

            Assert.Equal(1, result.Accepted);
            var rejected = Assert.Single(result.Rejected);
            Assert.Equal(1, rejected.Index);
            Assert.Equal(OrderApplyResult.NotOwned, rejected.Reason);
        }

        [Fact]
        public void Commit_ReportsAGameRejectionByItsBatchIndex()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            var applies = All<ApplyOrderEffect>(effects).ToList();
            host.Handle(new OrderAppliedEvent(1, applies[0].BatchIndex, OrderApplyResult.Applied));
            host.Handle(new OrderAppliedEvent(1, applies[1].BatchIndex, OrderApplyResult.Invalid));

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Equal(1, result.Accepted);
            var rejected = Assert.Single(result.Rejected);
            Assert.Equal(1, rejected.Index);
            Assert.Equal(OrderApplyResult.Invalid, rejected.Reason);
        }

        [Fact]
        public void Commit_SendsAnOrderResultEvenToAPeerThatSubmittedNothing()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Equal(0, result.Accepted);
            Assert.Empty(result.Rejected);
        }

        [Fact]
        public void Commit_SendsOrderResultsBeforeBroadcastingTurnCommit()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, true)).ToList();

            var resultAt = effects.FindIndex(e => e is SendEffect send && send.Message is OrderResultMessage);
            var commitAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is TurnCommitMessage);
            Assert.True(resultAt >= 0 && commitAt >= 0);
            Assert.True(resultAt < commitAt, "OrderResult must reach the peer before it is told execution began.");
        }

        [Fact]
        public void RefusedCommit_SendsNoOrderResult()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());

            var effects = host.Handle(new CommitOutcomeEvent(3, false));
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void RefusedCommit_DiscardsAccumulatedResultsSoTheyDoNotLeakIntoTheNextCommit()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, false));

            // Re-ready with a clean, fully-owned batch: the earlier unowned
            // rejection must not still be attached to this peer.
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Empty(result.Rejected);
            Assert.Equal(1, result.Accepted);
        }

        [Fact]
        public void Disconnect_DiscardsThatPeersAccumulatedResults()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            // The departed peer is gone, so the host commits alone and there is
            // nobody left to send a result to.
            var effects = host.Handle(new CommitOutcomeEvent(3, true));
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void OrderApplied_ProducesNoEffectsOfItsOwn()
        {
            var host = WithPeer();
            Assert.Empty(host.Handle(new OrderAppliedEvent(1, 0, OrderApplyResult.Applied)));
        }

        // --- reconnect ---

        /// <summary>A peer that has handshaken and been ticked, so it can be held.</summary>
        private HostSession WithTickedPeer(out string token, int peerId = 1, string name = "ally")
        {
            var host = Host();
            host.Handle(new TickEvent(1000));
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            var welcome = (WelcomeMessage)Single<SendEffect>(host.HandleMessage(peerId, GoodHello(name))).Message;
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

            var tokenA = ((WelcomeMessage)Single<SendEffect>(a.HandleMessage(1, GoodHello())).Message).ResumeToken;
            var tokenB = ((WelcomeMessage)Single<SendEffect>(b.HandleMessage(1, GoodHello())).Message).ResumeToken;
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

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2, Rejoin("not-the-token"))).Message;
            Assert.Equal(RejectReason.BadResumeToken, reject.Reason);
        }

        [Fact]
        public void Rejoin_ClaimingAPeerIdThatNeverLeft_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = (RejectMessage)Single<SendEffect>(
                host.HandleMessage(2, Rejoin(token, claimedPeerId: 7))).Message;
            Assert.Equal(RejectReason.BadResumeToken, reject.Reason);
        }

        [Fact]
        public void Rejoin_ToAnotherSession_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = (RejectMessage)Single<SendEffect>(
                host.HandleMessage(2, Rejoin(token, session: "someone-else"))).Message;
            Assert.Equal(RejectReason.UnknownSession, reject.Reason);
        }

        [Fact]
        public void Rejoin_WithABadProtocolVersion_IsRefused()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2, new RejoinMessage(PbjProtocol.Magic, 999, "0.2.0", "ally", "7f3a91", 1, token, null, null))).Message;
            Assert.Equal(RejectReason.VersionMismatch, reject.Reason);
        }

        [Fact]
        public void Rejoin_WithBadMagic_IsRefusedWithNoVersionDetail()
        {
            var host = WithTickedPeer(out var token);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2, new RejoinMessage(0xDEAD, PbjProtocol.Version, "0.2.0", "ally", "7f3a91", 1, token, null, null))).Message;
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

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2, GoodHello("ally"))).Message;
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

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2, Rejoin(token))).Message;
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
            var token = ((WelcomeMessage)Single<SendEffect>(host.HandleMessage(1, GoodHello())).Message).ResumeToken!;
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            // Someone else takes the only slot while the hold stands.
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("other"));

            host.Handle(new PeerConnectedEvent(3, "127.0.0.1:3"));
            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(3, Rejoin(token))).Message;
            Assert.Equal(RejectReason.SessionFull, reject.Reason);
        }

        [Fact]
        public void Hello_WithNoName_IsNotConfusedWithAHeldName()
        {
            var host = WithTickedPeer(out _);
            host.Handle(new PeerDisconnectedEvent(1, "closed"));
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));

            var reject = (RejectMessage)Single<SendEffect>(host.HandleMessage(2,
                new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", null, null, null))).Message;
            Assert.Equal(RejectReason.InvalidName, reject.Reason);
        }

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

        // --- snapshots ---

        private static UnitSnapshot Snap(string name) =>
            new UnitSnapshot(name, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f, false, 0f);

        private HostSession Executing()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            return host;
        }

        [Fact]
        public void TurnComplete_BroadcastsTheSnapshotAfterTurnComplete()
        {
            // Snapshot-first would make the client's digest already match when it
            // compared, silencing the divergence diagnostic permanently.
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, null))
                .ToList();

            var completeAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is TurnCompleteMessage);
            var snapshotAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is SnapshotMessage);
            Assert.True(completeAt >= 0 && snapshotAt > completeAt);
        }

        [Fact]
        public void TurnComplete_SnapshotCarriesTheExecutedTurnAndTheSameDigest()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, null));
            var snapshot = (SnapshotMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is SnapshotMessage).Message;

            // The executed turn, captured at commit time — not read back from the
            // bridge, which has already advanced.
            Assert.Equal(3, snapshot.Turn);
            Assert.Equal("abc", snapshot.Digest);
            Assert.Equal("unit_a", Assert.Single(snapshot.Units).Name);
        }

        [Fact]
        public void TurnComplete_WithNoUnits_StillBroadcastsASnapshot()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", null, null));
            var snapshot = (SnapshotMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is SnapshotMessage).Message;
            Assert.Empty(snapshot.Units);
        }

        // --- build compatibility and the handshake deadline (M7) ---

        private HostSession Guarded(string? passphrase = null) =>
            new HostSession("host", "7f3a91", 3, bridge, "secret",
                new SessionRequirements("0.2.0", "b8339", passphrase));

        private static HelloMessage Hello(
            string mod = "0.2.0", string? build = "b8339", string? passphrase = null, string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, mod, name, build, passphrase);

        private static RejectMessage RejectedBy(IEnumerable<PbjEffect> effects) =>
            (RejectMessage)effects.OfType<SendEffect>().Single(s => s.Message is RejectMessage).Message;

        [Fact]
        public void Hello_WithAMatchingBuild_IsWelcomed()
        {
            var effects = Guarded().HandleMessage(1, Hello());
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        // Without this a friend on a different mod build connects perfectly and
        // then diverges on every turn, which reads as a netcode bug.
        [Fact]
        public void Hello_WithADifferentModVersion_IsRejectedAndDisconnected()
        {
            var host = Guarded();
            var effects = host.HandleMessage(1, Hello(mod: "0.1.0"));

            Assert.Equal(RejectReason.ModVersionMismatch, RejectedBy(effects).Reason);
            Assert.Single(All<DisconnectEffect>(effects));
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void Hello_WithADifferentGameBuild_IsRejected()
        {
            var effects = Guarded().HandleMessage(1, Hello(build: "b0001"));
            Assert.Equal(RejectReason.GameBuildMismatch, RejectedBy(effects).Reason);
        }

        // The standalone harness is a legitimate peer with no game to report.
        [Fact]
        public void Hello_WithNoGameBuild_IsAccepted()
        {
            var effects = Guarded().HandleMessage(1, Hello(build: null));
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        [Fact]
        public void Hello_WithoutTheRequiredPassphrase_IsRejected()
        {
            var effects = Guarded("hunter2").HandleMessage(1, Hello(passphrase: "wrong"));
            Assert.Equal(RejectReason.BadPassphrase, RejectedBy(effects).Reason);
        }

        [Fact]
        public void Hello_WithTheRequiredPassphrase_IsWelcomed()
        {
            var effects = Guarded("hunter2").HandleMessage(1, Hello(passphrase: "hunter2"));
            Assert.Contains(All<SendEffect>(effects), s => s.Message is WelcomeMessage);
        }

        // A returning peer is as unauthenticated as a new one: the resume token
        // proves which departure this is, not that the sender belongs here.
        [Fact]
        public void Rejoin_WithoutTheRequiredPassphrase_IsRejected()
        {
            var host = Guarded("hunter2");
            var token = ((WelcomeMessage)All<SendEffect>(host.HandleMessage(1, Hello(passphrase: "hunter2")))
                .Single(s => s.Message is WelcomeMessage).Message).ResumeToken;
            host.Handle(new PeerDisconnectedEvent(1, "dropped"));

            var effects = host.HandleMessage(2, new RejoinMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", "ally", "7f3a91", 1, token, "b8339", "wrong"));

            Assert.Equal(RejectReason.BadPassphrase, RejectedBy(effects).Reason);
        }

        [Fact]
        public void ASocketThatNeverSaysHello_IsDroppedAfterTheHandshakeDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));

            var effects = host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("never handshook"));
        }

        [Fact]
        public void ASocketWithinTheHandshakeDeadline_IsLeftAlone()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds - 1))));
        }

        [Fact]
        public void APeerThatHandshook_IsNotDroppedByTheHandshakeDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));
            host.HandleMessage(1, Hello());

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1))));
            Assert.Single(host.Peers);
        }

        // A rejected socket is already being disconnected; the deadline must not
        // queue a second disconnect for it on the next tick.
        [Fact]
        public void ARejectedSocket_IsNotAlsoDroppedByTheDeadline()
        {
            var host = Guarded();
            host.Handle(new PeerConnectedEvent(1, "203.0.113.7:5000"));
            host.Handle(new TickEvent(0));
            host.HandleMessage(1, Hello(mod: "0.1.0"));

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(PbjProtocol.HandshakeTimeoutSeconds + 1))));
        }

        [Fact]
        public void SnapshotApplied_IsIgnoredOnTheHost()
        {
            Assert.Empty(WithPeer().Handle(new SnapshotAppliedEvent(3, 1, "a", "a")));
        }

        // --- keyframes (M6) ---

        private static KeyframeCapture Motion() =>
            new KeyframeCapture(15f, 20f, new[]
            {
                new UnitTrack("unit_a", new[]
                {
                    new TransformKey(15f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(20f, new Vec3(9f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            });

        // Keyframes are presentation; the snapshot is the correction the digest
        // is checked against. The correction must never queue behind them.
        [Fact]
        public void TurnComplete_BroadcastsKeyframesAfterTheSnapshot()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()))
                .ToList();

            var snapshotAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is SnapshotMessage);
            var keyframesAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is KeyframesMessage);
            Assert.True(snapshotAt >= 0 && keyframesAt > snapshotAt);
        }

        [Fact]
        public void TurnComplete_KeyframesCarryTheExecutedTurnAndTheWindow()
        {
            var effects = Executing()
                .Handle(new LocalTurnCompleteEvent("abc", new[] { Snap("unit_a") }, Motion()));
            var keyframes = (KeyframesMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is KeyframesMessage).Message;

            Assert.Equal(3, keyframes.Turn);
            Assert.Equal(15f, keyframes.WindowStart);
            Assert.Equal(20f, keyframes.WindowEnd);
            Assert.Equal("unit_a", Assert.Single(keyframes.Tracks).Name);
        }

        // A scenario with prediction disabled records nothing. That must cost an
        // empty broadcast, not an empty message.
        [Fact]
        public void TurnComplete_WithNothingRecorded_BroadcastsNoKeyframes()
        {
            var effects = Executing().Handle(new LocalTurnCompleteEvent("abc", null, null));

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is KeyframesMessage);
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is SnapshotMessage);
        }

        // Keyframes are client-bound, so a peer sending them upward is a protocol
        // violation and gets the same treatment Snapshot or Welcome would. Pinned
        // because the temptation with a new message type is to give it a quiet
        // ignore-arm, which would make it the one client-bound message a peer may
        // forge freely.
        [Fact]
        public void Keyframes_FromAPeer_AreAProtocolViolationLikeAnyClientBoundMessage()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new KeyframesMessage(3, 0f, 5f, null));

            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        // --- keepalive ---

        [Fact]
        public void Tick_FirstOne_SeedsPeersRatherThanJudgingThem()
        {
            // In-game the clock is the process uptime, so it can be enormous at
            // session start. An unseeded peer would look silent since zero.
            var host = WithPeer();
            var effects = host.Handle(new TickEvent(9_999_999));

            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Single(host.Peers);
        }

        [Fact]
        public void Tick_AfterAPeerFirstSpokeBetweenTicks_DoesNotThrow()
        {
            // The realistic order in-game: the runtime ticks on its first pump,
            // then the peer's Hello arrives. That stamps the inbound clock, so
            // the next tick skips the seeding path — and must still find a ping
            // clock to compare against.
            var host = Host();
            host.Handle(new TickEvent(1000));
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            host.HandleMessage(1, GoodHello());

            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(1001))));
        }

        [Fact]
        public void Tick_AfterThePingInterval_PingsAQuietPeer()
        {
            var host = WithPeer();
            host.Handle(new TickEvent(1000));

            var ping = Assert.IsType<PingMessage>(
                Single<SendEffect>(host.Handle(new TickEvent(1000 + PbjProtocol.PingIntervalSeconds))).Message);
            Assert.Equal(0, ping.Nonce);

            var next = Assert.IsType<PingMessage>(
                Single<SendEffect>(host.Handle(new TickEvent(1000 + 2 * PbjProtocol.PingIntervalSeconds))).Message);
            Assert.Equal(1, next.Nonce);
        }

        [Fact]
        public void Tick_BeforeThePingInterval_SendsNothing()
        {
            var host = WithPeer();
            host.Handle(new TickEvent(1000));
            Assert.Empty(All<SendEffect>(host.Handle(new TickEvent(1001))));
        }

        [Fact]
        public void Tick_AfterTheTimeout_DropsTheSilentPeer()
        {
            var host = WithPeer();
            host.Handle(new TickEvent(1000));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.PeerTimeoutSeconds));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is PeerLeftMessage);
        }

        [Fact]
        public void Tick_TimingOutTheLastBlockingPeer_CommitsTheTurn()
        {
            // A dead peer must never wedge the session.
            var host = WithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new LocalReadyEvent());

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.PeerTimeoutSeconds));
            Assert.Single(All<CommitTurnEffect>(effects));
        }

        [Fact]
        public void Tick_WhileExecuting_StillTimesOutASilentPeer()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            Assert.Equal(HostSessionState.Executing, host.State);
            host.Handle(new TickEvent(1000));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.PeerTimeoutSeconds));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        [Fact]
        public void AnyInboundMessage_KeepsAPeerAlive()
        {
            var host = WithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new TickEvent(1015));

            // Silent for 15s, then speaks — the clock restarts from there.
            host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(1025))));
        }

        [Fact]
        public void Pong_CountsAsTrafficAndProducesNothing()
        {
            var host = WithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new TickEvent(1015));

            Assert.Empty(host.HandleMessage(1, new PongMessage(7)));
            Assert.Empty(All<DisconnectEffect>(host.Handle(new TickEvent(1025))));
        }

        [Fact]
        public void Tick_WithNoPeers_DoesNothing()
        {
            Assert.Empty(Host().Handle(new TickEvent(1000)));
        }

        // --- combat edges ---

        [Fact]
        public void CombatEntered_BroadcastsCombatStartThenAssignments()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();
            var broadcasts = All<BroadcastEffect>(effects).ToList();
            var startAt = broadcasts.FindIndex(b => b.Message is CombatStartMessage);
            var assignAt = broadcasts.FindIndex(b => b.Message is AssignmentsMessage);

            Assert.True(startAt >= 0, "combat entry must announce itself");
            Assert.True(assignAt > startAt, "Assignments must follow CombatStart immediately");
            Assert.Equal(0, ((CombatStartMessage)broadcasts[startAt].Message).Turn);
        }

        [Fact]
        public void CombatEntered_MovesToPlanningAndResetsTheBarrier()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(-1, null));
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());

            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(0, host.Turn);
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void CombatExited_BroadcastsCombatEndAndUnlocksEveryone()
        {
            var host = WithPeer();
            bridge.InCombat = false;

            var effects = host.Handle(new CombatExitedEvent());
            Assert.IsType<CombatEndMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Lobby, host.State);
        }

        [Fact]
        public void CombatExited_WhileAPeerSitsReady_StillUnlocksIt()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            Assert.Equal(0, host.ReadyCount);
            Assert.Empty(host.Assignments.PeerIds);
        }

        [Fact]
        public void CombatExited_WhileExecuting_LeavesExecutionUnlocked()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.InCombat = false;

            var effects = host.Handle(new CombatExitedEvent());
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Lobby, host.State);
        }

        // --- joining mid-execution ---

        [Fact]
        public void Hello_WhileExecuting_TellsTheNewPeerExecutionIsAlreadyUnderway()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("ally2"));

            var sent = All<SendEffect>(effects).Where(s => s.PeerId == 2).Select(s => s.Message).ToList();
            Assert.Contains(sent, m => m is WelcomeMessage);
            Assert.Contains(sent, m => m is TurnCommitMessage);
        }

        [Fact]
        public void Hello_WhilePlanning_SendsNoTurnCommit()
        {
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            Assert.DoesNotContain(All<SendEffect>(effects).Select(s => s.Message), m => m is TurnCommitMessage);
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
