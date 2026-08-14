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

        private static IEnumerable<T> Messages<T>(IEnumerable<PbjEffect> effects) where T : PbjMessage =>
            effects.OfType<SendEffect>().Select(e => e.Message).OfType<T>();

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        // --- the base mirror (M12a) ---

        [Fact]
        public void LocalBasePosition_IsBroadcastToEveryone()
        {
            var host = WithPeer();

            var effects = host.Handle(new LocalBasePositionEvent(1024.5f, -37.25f));

            var message = Assert.IsType<BasePositionMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.Equal(1024.5f, message.X);
            Assert.Equal(-37.25f, message.Z);
        }

        [Fact]
        public void LocalBasePosition_WithNobodyListening_StillBroadcasts()
        {
            // A broadcast to an empty registry is already nothing, so guarding on
            // the peer count here would be a branch with no observable difference
            // -- and the 100% gate turns an indistinguishable branch into a build
            // failure rather than dead code.
            var effects = Host().Handle(new LocalBasePositionEvent(1f, 2f));

            Assert.Single(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void LocalBasePosition_RepeatedWithTheSameValue_IsSentEveryTime()
        {
            // The heartbeat's whole purpose is to repeat itself while nothing
            // moves. A session that suppressed identical updates would be
            // deciding cadence on the glue's behalf, and would silently turn the
            // heartbeat back into movement-only updates.
            var host = WithPeer();

            host.Handle(new LocalBasePositionEvent(5f, 5f));
            var second = host.Handle(new LocalBasePositionEvent(5f, 5f));

            Assert.Single(All<BroadcastEffect>(second));
        }

        [Fact]
        public void LocalBasePosition_DuringCombat_IsStillBroadcast()
        {
            // The base has a position in every state, and a client that stopped
            // hearing about it while the host was busy would simply be wrong
            // afterwards.
            var host = WithPeer();
            host.Handle(new CombatEnteredEvent());

            Assert.Single(All<BroadcastEffect>(host.Handle(new LocalBasePositionEvent(3f, 4f))));
        }

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
            Assert.Equal(3, Messages<TurnCommitMessage>(effects).Single().Turn);
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
        public void CombatEntered_AnnouncesNothingYet()
        {
            // M12b re-sequenced this, and the reason is a deadlock rather than a
            // preference. CombatStart sets HostIsFighting on every client, and
            // HandleScenarioOffer refuses every offer while that is true -- so
            // announcing the fight before shipping it guarantees nobody can fetch
            // it. The announcement waits for the entry barrier.
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Empty(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void CombatReady_OffersTheFightRatherThanStartingIt()
        {
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            var offer = Assert.IsType<CombatOfferMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.Equal("pbj_combat_test", offer.SaveName);
            Assert.Equal("d1", offer.Digest);
            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_OnceEveryoneIsIn_BroadcastsCombatStartThenAssignments()
        {
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();
            var broadcasts = All<BroadcastEffect>(effects).ToList();
            var startAt = broadcasts.FindIndex(b => b.Message is CombatStartMessage);
            var assignAt = broadcasts.FindIndex(b => b.Message is AssignmentsMessage);

            Assert.True(startAt >= 0, "the fight must be announced once everyone is in");
            Assert.True(assignAt > startAt, "Assignments must follow CombatStart immediately");
            Assert.Equal(0, ((CombatStartMessage)broadcasts[startAt].Message).Turn);
        }

        [Fact]
        public void CombatReady_WithNobodyConnected_StartsAtOnce()
        {
            bridge.InCombat = false;
            var host = Host();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_WhenTheFightCouldNotBeWritten_StartsAloneRatherThanHanging()
        {
            // Reachable: CanSave has refusals the glue's poll cannot outwait. The
            // session is lost either way, but the human at this machine is
            // already in a battle and cannot be left staring at it.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent(null, null)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_WhenAPeerNeverReports_StartsWithoutItAfterTheTimeout()
        {
            var host = InCombatWithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.LoadTimeoutSeconds + 1)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_WithANameButNoDigest_IsTreatedAsAFailedWrite()
        {
            // Half an answer is not an answer: without a digest a client cannot
            // tell this fight from the last one written to the same slot.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", null)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_WithTwoPeers_WaitsForBoth()
        {
            bridge.InCombat = false;
            var host = WithPeer(maxPeers: 3);
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("ally2"));
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var first = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();

            Assert.DoesNotContain(All<BroadcastEffect>(first), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_AReportForAnAbandonedFight_IsIgnored()
        {
            // Same staleness rule every other barrier in this protocol has: a
            // report about a turn that is no longer the one being entered must
            // not count toward the current fight.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(99, LoadOutcome.Loaded)).ToList();

            Assert.Empty(effects);
        }

        [Fact]
        public void CombatEntered_AsksTheGlueToShipTheFight()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Single(All<ShipCombatEffect>(effects));
        }

        [Fact]
        public void CombatEntered_WithNobodyConnected_StillShipsTheFight()
        {
            // No peer-count shortcut, and the reason is the scenario slot rather
            // than the fight: OfferScenario hands that slot to every newcomer, so
            // a write skipped because nobody was listening would be offered to the
            // next peer to arrive -- last mission's fight, under this mission's
            // name. One synchronous save is the cheaper of the two.
            bridge.InCombat = false;
            var host = Host();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Single(All<ShipCombatEffect>(effects));
        }

        [Fact]
        public void CombatEntry_APeerThatCouldNotGetIn_IsDroppedRatherThanLeftInTheFight()
        {
            // The wedge this closes: the entry barrier stopped waiting for such a
            // peer, but the registry kept it -- so it was still dealt units and
            // the TURN barrier waited for it forever. Nobody could execute again.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Unavailable)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_APeerThatNeverReports_IsDroppedAtTheTimeout()
        {
            var host = InCombatWithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.LoadTimeoutSeconds + 1)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void CombatEntry_WhenTheFightCouldNotBeWritten_DropsEveryoneRatherThanWedgingTheBarrier()
        {
            // Starting alone is right for the human already standing in a battle.
            // Keeping the peers connected while starting without them is not: they
            // were never offered a fight they could join, and every one of them
            // would hold the turn barrier shut.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent(null, null)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void CombatEntry_APeerThatDisconnectsMidEntry_DoesNotHoldTheFightUp()
        {
            // RemovePeer cleared every other barrier and not this one, so a peer
            // that dropped while loading the fight left the entry barrier waiting
            // on a closed socket for the full two minutes -- and the host stood in
            // the battle the whole time.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new PeerDisconnectedEvent(1, "gone")).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_AfterTheHostLeftTheFight_IsDropped()
        {
            // The glue disarms itself on the combat edge, so this should not
            // arrive -- and "should not" is exactly what the entry barrier
            // believed about late reports. Acting on it would announce a fight at
            // turn -1 from a host sitting in its lobby.
            var host = InCombatWithPeer();
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            Assert.Empty(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void CombatExited_MidEntry_CancelsIt()
        {
            // Otherwise a report arriving after the host abandoned the fight
            // completes the barrier and broadcasts CombatStart -- at turn -1, from
            // a host sitting in its lobby.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        /// <summary>A host in combat with one handshaken peer, ready to ship a fight.</summary>
        private HostSession InCombatWithPeer()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());
            return host;
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
            // Two broadcasts now: CombatEnd, then the refreshed lobby — leaving
            // the fight puts everyone back in a lobby whose readiness is stale.
            Assert.Single(All<BroadcastEffect>(effects), e => e.Message is CombatEndMessage);
            Assert.Single(All<BroadcastEffect>(effects), e => e.Message is LobbyStateMessage);
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

        // --- lobby (M11a) ---
        //
        // The lobby only runs out of combat, so these start from a bridge that
        // is not in one. WithPeer() alone would leave the host in Planning.

        private HostSession LobbyHost(int peerId = 1, string name = "ally")
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            host.HandleMessage(peerId, GoodHello(name));
            return host;
        }

        private static LobbyStateMessage LobbyState(IEnumerable<PbjEffect> effects) =>
            All<BroadcastEffect>(effects).Select(e => e.Message).OfType<LobbyStateMessage>().Last();

        private static LobbySelectEventPair Select(string key = "pbj_campaign", string? digest = "abc") =>
            new LobbySelectEventPair(key, digest);

        private sealed class LobbySelectEventPair
        {
            public LobbySelectEventPair(string? key, string? digest)
            {
                Event = new LocalLobbySelectEvent(key, digest);
            }

            public LocalLobbySelectEvent Event { get; }
        }

        [Fact]
        public void Lobby_StartsWithNothingSelectedAndOnlyTheHostPresent()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Equal(LobbySelection.None.Version, host.Selection.Version);
            Assert.False(host.Selection.HasSave);
            Assert.Equal(1, host.LobbyParticipantCount);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbySelect_AdvancesTheSelectionAndBroadcastsIt()
        {
            bridge.InCombat = false;
            var host = Host();

            var effects = host.Handle(Select().Event);

            Assert.Equal(1, host.Selection.Version);
            Assert.Equal("pbj_campaign", host.Selection.SaveKey);
            Assert.Equal("abc", host.Selection.SaveDigest);

            var state = LobbyState(effects);
            Assert.Equal(1, state.SelectionVersion);
            Assert.Equal("pbj_campaign", state.SaveKey);
            Assert.Equal("abc", state.SaveDigest);
        }

        [Fact]
        public void LobbySelect_WithNoKey_ClearsTheSelection()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            var effects = host.Handle(new LocalLobbySelectEvent(null, null));

            Assert.False(host.Selection.HasSave);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("lobby save cleared"));
        }

        [Fact]
        public void LobbySelect_OutsideTheLobby_IsIgnored()
        {
            // Mid-combat the save is already decided; changing it would be a
            // promise the session cannot keep.
            var host = WithPeer();
            var effects = host.Handle(Select().Event);

            Assert.Equal(LobbySelection.None.Version, host.Selection.Version);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not in the lobby"));
            Assert.Empty(All<BroadcastEffect>(effects));
        }

        // --- sealing the lobby once the campaign starts (M11e) ---

        /// <summary>
        /// Drives a host all the way through a completed synchronised load, which
        /// is the point the door closes.
        /// </summary>
        private HostSession LoadedHost(int peerId = 1, string name = "ally")
        {
            var host = LobbyHost(peerId, name);
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(peerId, new LobbyReadyMessage(host.Selection.Version));
            host.Handle(new LoadFinishedEvent(host.Selection.Version, LoadOutcome.Loaded));
            host.HandleMessage(peerId, new LobbyLoadedMessage(host.Selection.Version, LoadOutcome.Loaded));
            return host;
        }

        [Fact]
        public void AfterTheCampaignLoads_AStrangerIsRefusedWithAReason()
        {
            // M11e transfers on selection, so a peer arriving now would never be
            // offered the save and could never ready. Refused with a reason rather
            // than a dropped socket: silence is indistinguishable from the host
            // being down, which this project has already paid for once.
            var host = LoadedHost();
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            var effects = host.HandleMessage(9, GoodHello("newcomer"));

            var reject = Messages<RejectMessage>(effects).Single();
            Assert.Equal(RejectReason.NotAcceptingPeers, reject.Reason);
        }

        [Fact]
        public void AfterTheCampaignLoads_SomeoneWhoWasAlreadyInMayComeBack()
        {
            // ⚠️ What keeps the seal a door and not a wall. A resume token is only
            // minted when a peer held units in combat, and the seal lives in the
            // out-of-combat campaign — so a wifi blip out there produces no token
            // at all, and the ordinary recovery is a fresh Hello. Sealing against
            // that would make one dropped packet permanent.
            var host = LoadedHost();
            host.Handle(new PeerDisconnectedEvent(1, "wifi"));
            host.Handle(new PeerConnectedEvent(7, "127.0.0.1:7"));

            var effects = host.HandleMessage(7, GoodHello("ally"));

            Assert.Empty(Messages<RejectMessage>(effects));
            Assert.Single(Messages<WelcomeMessage>(effects));
        }

        [Fact]
        public void AfterTheCampaignLoads_ANamelessPeerIsRefusedByTheSealFirst()
        {
            // A null name is a malformed Hello and would be refused as InvalidName
            // anyway, but the seal is checked ahead of that on purpose: once the
            // campaign is under way, a stranger's other problems are beside the
            // point. Pinned so the ordering is a decision rather than an accident.
            var host = LoadedHost();
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            var effects = host.HandleMessage(9, new HelloMessage(
                PbjProtocol.Magic, PbjProtocol.Version, PbjProtocol.ModVersion, null, null, null));

            Assert.Equal(RejectReason.NotAcceptingPeers, Messages<RejectMessage>(effects).Single().Reason);
        }

        [Fact]
        public void BeforeTheCampaignLoads_AnyoneMayJoin()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            Assert.Empty(Messages<RejectMessage>(host.HandleMessage(9, GoodHello("newcomer"))));
        }

        [Fact]
        public void AnAbandonedLoad_LeavesTheDoorOpen()
        {
            // ⚠️ The trap this design avoids. Sealing when the load *starts* would
            // mean a load that never completed — the host's own load failing, or
            // ExpireLoads timing it out — left a session refusing joins forever
            // with no campaign ever entered and nothing able to reopen it.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(1, new LobbyReadyMessage(host.Selection.Version));
            host.Handle(new LoadFinishedEvent(host.Selection.Version, LoadOutcome.Refused));

            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));
            Assert.Empty(Messages<RejectMessage>(host.HandleMessage(9, GoodHello("newcomer"))));
        }

        [Fact]
        public void LobbySelect_ClearsEveryExistingReady()
        {
            // The whole reason the selection is versioned: nobody agreed to
            // the new save just because they agreed to the old one.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(1, new LobbyReadyMessage(1));
            // Satisfaction is now consumed the instant it happens: the load
            // fires and the agreement is spent, so the barrier reads unsatisfied
            // rather than staying armed. M11d.
            Assert.True(host.LoadInFlight);

            host.Handle(Select("pbj_other").Event);

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbySelect_WithTheSameSaveAgain_StillClearsReady()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            host.Handle(Select().Event);

            Assert.Equal(2, host.Selection.Version);
            Assert.Equal(0, host.LobbyReadyCount);
        }

        [Fact]
        public void LocalLobbyReady_WithNoSaveSelected_IsRefused()
        {
            bridge.InCombat = false;
            var host = Host();

            var effects = host.Handle(new LocalLobbyReadyEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no save selected"));
        }

        [Fact]
        public void LocalLobbyReady_OutsideTheLobby_IsRefused()
        {
            var host = WithPeer();
            var effects = host.Handle(new LocalLobbyReadyEvent());
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not in the lobby"));
        }

        [Fact]
        public void LocalLobbyReady_MarksTheHostAndBroadcasts()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            var effects = host.Handle(new LocalLobbyReadyEvent());

            // Host alone in the lobby, so its own ready fills the barrier — and
            // filling it now fires the load, which spends the agreement.
            Assert.True(host.LoadInFlight);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Single(All<BeginLoadEffect>(effects));
        }

        [Fact]
        public void LocalLobbyUnready_WithdrawsIt()
        {
            // Two participants, so the host's own ready does not fill the barrier
            // and immediately spend itself on a load.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            var effects = host.Handle(new LocalLobbyUnreadyEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(LobbyState(effects).Peers[0].Ready);
        }

        [Fact]
        public void LocalLobbyUnready_WhenNotReady_DoesNothing()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Empty(host.Handle(new LocalLobbyUnreadyEvent()));
        }

        [Fact]
        public void LocalLobbyUnready_OutsideTheLobby_DoesNothing()
        {
            Assert.Empty(WithPeer().Handle(new LocalLobbyUnreadyEvent()));
        }

        [Fact]
        public void Handshake_AddsThePeerToTheLobbyAndTellsEveryone()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            Assert.Equal(2, host.LobbyParticipantCount);
            var state = LobbyState(effects);
            Assert.Equal(2, state.Peers.Count);
            Assert.Equal("host", state.Peers[0].Name);
            Assert.Equal("ally", state.Peers[1].Name);
            // The newcomer learns the selection from the same message.
            Assert.Equal("pbj_campaign", state.SaveKey);
        }

        [Fact]
        public void Handshake_MidLobby_UnfillsAnAlreadySatisfiedBarrier()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.True(host.LoadInFlight);

            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            host.HandleMessage(1, GoodHello());

            // They have not agreed to anything yet.
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbyReady_FromAPeer_FillsTheBarrier()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LobbyIsSatisfied);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("everyone has agreed"));
            // ...and that agreement is spent immediately on the load it was for.
            Assert.True(host.LoadInFlight);
            Assert.Single(All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>());
        }

        [Fact]
        public void LobbyReady_ForAStaleSelection_IsIgnored()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(Select("pbj_other").Event);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("the save has changed since"));
        }

        [Fact]
        public void LobbyReady_ForASelectionAheadOfTheHost_ResendsTheState()
        {
            // Nothing can legitimately put a peer ahead — only the host mints a
            // selection version and it never rewinds. So this is a misbehaving
            // or buggy peer, and the answer is the truth rather than a kick.
            var host = LobbyHost();
            host.Handle(Select().Event);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(99));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.IsType<LobbyStateMessage>(Single<SendEffect>(effects).Message);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("claims lobby selection 99"));
        }

        [Fact]
        public void LobbyReady_WhileTheHostIsInCombat_IsIgnored()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new LobbyReadyMessage(0));
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("host is not in the lobby"));
        }

        [Fact]
        public void LobbyReady_BeforeHello_DisconnectsThem()
        {
            bridge.InCombat = false;
            var host = Host();
            var effects = host.HandleMessage(9, new LobbyReadyMessage(0));
            Assert.Equal("lobby ready before hello", Single<DisconnectEffect>(effects).Reason);
        }

        [Fact]
        public void LobbyUnready_BeforeHello_DisconnectsThem()
        {
            bridge.InCombat = false;
            var host = Host();
            var effects = host.HandleMessage(9, new LobbyUnreadyMessage(0));
            Assert.Equal("lobby unready before hello", Single<DisconnectEffect>(effects).Reason);
        }

        [Fact]
        public void LobbyUnready_WithdrawsAPeerReady()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.HandleMessage(1, new LobbyReadyMessage(1));
            Assert.Equal(1, host.LobbyReadyCount);

            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("lobby unready from #1"));
        }

        [Fact]
        public void LobbyUnready_ForAnotherSelection_IsIgnored()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.HandleMessage(1, new LobbyReadyMessage(1));

            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(0));

            Assert.Equal(1, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not the current selection"));
        }

        [Fact]
        public void LobbyUnready_WhenNotReady_IsANoOpNotAFault()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(1));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(0, host.LobbyReadyCount);
        }

        [Fact]
        public void PeerLeaving_CanSatisfyTheLobbyBarrier()
        {
            // The case a ready-only check misses entirely: the last unready
            // member simply leaves. Same shape as the turn barrier's, and in
            // M11d this is the trigger, not just a log line.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LobbyIsSatisfied);

            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("everyone has agreed"));
            // And in M11d that is the trigger: filling by subtraction loads too.
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void PeerLeaving_BroadcastsTheShrunkenRoster()
        {
            var host = LobbyHost();
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Single(LobbyState(effects).Peers);
            Assert.Equal(1, host.LobbyParticipantCount);
        }

        [Fact]
        public void PeerKickedForAProtocolViolation_BroadcastsTheShrunkenRoster()
        {
            // The kick paths never reach HandleDisconnect — by the time the
            // socket closes, the registry entry is gone and it returns early.
            // Without a broadcast here the departed peer haunts every other
            // client's lobby.
            var host = LobbyHost();
            var effects = host.HandleMessage(1, new WelcomeMessage(3, "s", 1, "h", null, 0, null));

            Assert.Single(LobbyState(effects).Peers);
            Assert.Equal(1, host.LobbyParticipantCount);
        }

        [Fact]
        public void PeerKickedForADuplicateHello_BroadcastsTheShrunkenRoster()
        {
            var host = LobbyHost();
            var effects = host.HandleMessage(1, GoodHello());
            Assert.Single(LobbyState(effects).Peers);
        }

        [Fact]
        public void Rejoin_PutsThePeerBackInTheLobbyRoster()
        {
            // Reconnect holds exist to reserve UNITS, so they only happen in
            // combat — a peer that drops from the lobby is simply gone. This
            // therefore drops mid-combat and returns, which is the only way
            // HandleRejoin runs at all.
            var host = WithTickedPeer(out var token);
            host.Handle(new CombatEnteredEvent());
            host.Handle(new PeerDisconnectedEvent(1, "dropped"));
            Assert.Equal(1, host.LobbyParticipantCount);

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, Rejoin(token, claimedPeerId: 1));

            Assert.Equal(2, host.LobbyParticipantCount);
            var state = LobbyState(effects);
            Assert.Equal(2, state.Peers.Count);
            // Rebound onto the new peer id, like everything else keyed on it.
            Assert.Equal(2, state.Peers[1].PeerId);
        }

        [Fact]
        public void CombatExited_ClearsLobbyReadinessAndAdvancesTheSelection()
        {
            // Otherwise everyone comes out of the fight already "ready", and a
            // LobbyReady still in flight from before it would be counted.
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.True(host.LoadInFlight);

            bridge.InCombat = true;
            host.Handle(new CombatEnteredEvent());
            bridge.InCombat = false;
            var effects = host.Handle(new CombatExitedEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
            // The save survives; only the agreement to it is withdrawn.
            Assert.Equal("pbj_campaign", host.Selection.SaveKey);
            // 1 for the select, 2 when the load fired and spent the agreement,
            // 3 for leaving combat. Every consumer of an agreement advances it.
            Assert.Equal(3, host.Selection.Version);
            Assert.Equal(3, LobbyState(effects).SelectionVersion);
        }

        [Fact]
        public void CombatExited_MakesAnInFlightLobbyReadyStale()
        {
            bridge.InCombat = false;
            var host = LobbyHost();
            host.Handle(Select().Event);
            bridge.InCombat = true;
            host.Handle(new CombatEnteredEvent());
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            // Sent before the fight ended, arriving after.
            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("the save has changed since"));
        }

        [Fact]
        public void LobbyState_DuringCombat_StillTracksTheRoster()
        {
            // Broadcast even while the lobby is dormant: suppressing it would
            // leave every client's roster stale at exactly the moment combat
            // ends and the lobby matters again.
            var host = WithPeer();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("ally2"));

            Assert.Equal(3, LobbyState(effects).Peers.Count);
            // ...but the barrier says nothing while it is not in play.
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("lobby "));
        }

        // --- a peer that joins mid-combat (the 2026-08-03 defect) ---

        private static IReadOnlyList<PbjMessage> SentTo(IEnumerable<PbjEffect> effects, int peerId) =>
            effects.OfType<SendEffect>().Where(e => e.PeerId == peerId).Select(e => e.Message).ToList();

        [Fact]
        public void Handshake_WhileInCombat_TellsTheNewcomerCombatIsHappening()
        {
            // Without this the peer never learns, HandleWelcome falls back to
            // reading its OWN combat flag, and its Execute is swallowed forever.
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            var start = SentTo(effects, 1).OfType<CombatStartMessage>().Single();
            Assert.Equal(host.Turn, start.Turn);
        }

        [Fact]
        public void Handshake_WhileInCombat_SendsCombatStartAfterTheScenarioOffer()
        {
            // CombatStart moves the client to Planning, and HandleScenarioOffer
            // ignores an offer unless it is in Lobby. Reversed, the joining peer
            // silently declines the very save it needs to play.
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            bridge.Scenario = new ScenarioPayload("pbj_combat_test", new[]
            {
                new ScenarioFile("content.zip", new byte[] { 1 }),
                new ScenarioFile("metadata.yaml", new byte[] { 2 }),
            });
            var sent = SentTo(host.HandleMessage(1, GoodHello()), 1);

            var offerAt = sent.ToList().FindIndex(m => m is ScenarioOfferMessage);
            var startAt = sent.ToList().FindIndex(m => m is CombatStartMessage);
            Assert.True(offerAt >= 0, "the scenario must still be offered");
            Assert.True(startAt > offerAt, "CombatStart must follow the scenario offer");
        }

        [Fact]
        public void Handshake_WhileExecuting_SendsCombatStartBeforeTurnCommit()
        {
            // The other side of the same constraint. TurnCommit locks the client
            // and moves it to Watching; a CombatStart arriving afterwards would
            // unlock it and leave it planning a turn that is already running.
            var host = Executing();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var sent = SentTo(host.HandleMessage(2, GoodHello("third")), 2).ToList();

            var startAt = sent.FindIndex(m => m is CombatStartMessage);
            var commitAt = sent.FindIndex(m => m is TurnCommitMessage);
            Assert.True(startAt >= 0, "CombatStart must be sent");
            Assert.True(commitAt > startAt, "TurnCommit must follow CombatStart");
        }

        [Fact]
        public void Handshake_OutOfCombat_SendsNoCombatStart()
        {
            var host = LobbyHost();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("third"));
            Assert.Empty(SentTo(effects, 2).OfType<CombatStartMessage>());
        }

        // --- the synchronised load (M11d) ---

        /// <summary>A host and one peer, both agreed, so the load has just fired.</summary>
        private HostSession Loading(out IReadOnlyList<PbjEffect> fired)
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            fired = host.HandleMessage(1, new LobbyReadyMessage(1));
            return host;
        }

        [Fact]
        public void Load_FiresWhenEveryoneHasAgreed()
        {
            var host = Loading(out var effects);

            Assert.True(host.LoadInFlight);
            var load = All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>().Single();
            Assert.Equal("pbj_campaign", load.SaveKey);
            Assert.Single(All<BeginLoadEffect>(effects));
        }

        [Fact]
        public void Load_AdvancesTheSelectionSoTheAgreementCannotBeSpentTwice()
        {
            // The heart of the design. IsSatisfied is a predicate nothing
            // consumes and the host stays in Lobby for the whole campaign, so a
            // level-triggered load would re-fire from every later barrier check
            // — including the disconnect path — and reload the original save on
            // every machine mid-play.
            var host = Loading(out _);

            Assert.Equal(2, host.Selection.Version);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void Load_BroadcastsTheNewLobbyStateBeforeTheLoadInstruction()
        {
            // Firing puts the host a version ahead of every client, and a client
            // validates LobbyLoad against the version it last heard. Reverse
            // these two and every client refuses while the host loads alone.
            var host = Loading(out var effects);

            var broadcasts = All<BroadcastEffect>(effects).Select(b => b.Message).ToList();
            var stateAt = broadcasts.FindIndex(m => m is LobbyStateMessage s && s.SelectionVersion == 2);
            var loadAt = broadcasts.FindIndex(m => m is LobbyLoadMessage);

            Assert.True(stateAt >= 0, "the advanced LobbyState must be broadcast");
            Assert.True(loadAt > stateAt, "LobbyLoad must follow the LobbyState carrying its version");
            Assert.Equal(2, Assert.IsType<LobbyLoadMessage>(broadcasts[loadAt]).SelectionVersion);
        }

        [Fact]
        public void Load_DoesNotFireASecondTimeWhileOneIsRunning()
        {
            var host = Loading(out _);
            // A peer leaving re-checks the barrier — the path that would have
            // been catastrophic.
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Empty(All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>());
        }

        [Fact]
        public void Load_WithNoSaveChosen_DoesNotFire()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LoadInFlight);
        }

        [Fact]
        public void Load_CompletesWhenEveryoneHasReported()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            Assert.True(host.LoadInFlight);

            var effects = host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2 of 2 machine(s) are in"));
        }

        [Fact]
        public void Load_CompletesEvenWhenAPeerFailed()
        {
            // The barrier waits for news, not for success.
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            var effects = host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Unavailable));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("1 of 2 machine(s) are in"));
        }

        [Fact]
        public void Load_ReportForAStaleVersion_IsIgnored()
        {
            var host = Loading(out _);
            var effects = host.HandleMessage(1, new LobbyLoadedMessage(1, LoadOutcome.Loaded));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("load OK"));
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void Load_HostReportForALoadThatIsNotRunning_IsIgnored()
        {
            // A callback outliving the load that asked for it. Acting on it would
            // complete a barrier nobody is waiting on.
            var host = LobbyHost();
            var effects = host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("load OK"));
        }

        [Fact]
        public void Load_HostFailure_AbandonsTheWholeLoad()
        {
            // The host is not a peer that can be carried on without: it is the
            // session. Dropping it would leave the others in a campaign it is
            // not in.
            var host = Loading(out _);
            var effects = host.Handle(new LoadFinishedEvent(2, LoadOutcome.Refused));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("abandoning"));
        }

        [Fact]
        public void Load_APeerLeavingMidLoad_StopsBeingWaitedFor()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));

            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("machine(s) are in"));
        }

        [Fact]
        public void Load_TimesOutAPeerThatNeverReports()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));

            // Seed-don't-judge: the first tick mints the deadline rather than
            // measuring against a clock that was never stamped.
            host.Handle(new TickEvent(1000.0));
            Assert.True(host.LoadInFlight);

            var effects = host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds + 1.0));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no word from #1"));
        }

        [Fact]
        public void Load_DoesNotTimeOutBeforeTheDeadline()
        {
            var host = Loading(out _);
            host.Handle(new TickEvent(1000.0));
            host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds - 1.0));
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void Load_HostTimingOutAbandonsRatherThanDropping()
        {
            var host = Loading(out _);
            host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));
            host.Handle(new TickEvent(1000.0));

            var effects = host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds + 1.0));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("abandoning"));
        }

        [Fact]
        public void Load_TicksWithNothingRunning_DoNothing()
        {
            var host = LobbyHost();
            host.Handle(new TickEvent(1000.0));
            Assert.False(host.LoadInFlight);
        }

        [Fact]
        public void Load_AfterCompleting_TheLobbyCanFillAgain()
        {
            // Deliberate: unanimous agreement a second time is a deliberate
            // reload. The alternative — a barrier that can never fire again —
            // is M11a's do-nothing barrier reintroduced.
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));
            Assert.False(host.LoadInFlight);

            host.Handle(new LocalLobbyReadyEvent());
            var effects = host.HandleMessage(1, new LobbyReadyMessage(2));

            Assert.True(host.LoadInFlight);
            Assert.Single(All<BeginLoadEffect>(effects));
        }

        // --- the roster the screen reads (M11c) ---

        [Fact]
        public void LobbyRoster_PutsTheHostFirstThenPeersInJoinOrder()
        {
            var host = LobbyHost();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("third"));

            var roster = host.LobbyRoster;
            Assert.Equal(new[] { 0, 1, 2 }, roster.Select(p => p.PeerId));
            Assert.Equal(new[] { "host", "ally", "third" }, roster.Select(p => p.Name));
        }

        [Fact]
        public void LobbyRoster_IsTheSameListTheClientsAreSent()
        {
            // The point of exposing it rather than letting a host screen build its
            // own: a separately-derived roster is one that can disagree with what
            // this host's own clients were just told.
            var host = LobbyHost();
            var effects = host.Handle(Select().Event);

            var broadcast = LobbyState(effects).Peers;
            var read = host.LobbyRoster;
            Assert.Equal(broadcast.Select(p => p.PeerId), read.Select(p => p.PeerId));
            Assert.Equal(broadcast.Select(p => p.Ready), read.Select(p => p.Ready));
        }

        [Fact]
        public void LobbyRoster_CarriesReadyFlags()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            var roster = host.LobbyRoster;
            Assert.True(roster.Single(p => p.PeerId == 0).Ready);
            Assert.False(roster.Single(p => p.PeerId == 1).Ready);
        }

        [Fact]
        public void LobbyRoster_WithNoPeers_IsJustTheHost()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Single(host.LobbyRoster);
            Assert.Equal(0, host.LobbyRoster[0].PeerId);
        }

        private string Token(int peerId, string name) =>
            StateDigest.Mix("secret:" + peerId + ":" + name);
    }
}
