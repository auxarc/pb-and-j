using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ClientSessionTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private ClientSession Client() => new ClientSession("ally", "0.2.0", bridge);

        private static WelcomeMessage Welcome(int turn = 3) =>
            new WelcomeMessage(PbjProtocol.Version, "7f3a91", 1, "host",
                new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") }, turn, "tok");

        /// <summary>A client that has completed the handshake.</summary>
        private ClientSession Welcomed(int turn = 3)
        {
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, Welcome(turn));
            return client;
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        // --- construction and handshake ---

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankPlayerName_Throws(string? name)
        {
            var ex = Assert.Throws<ArgumentException>(() => new ClientSession(name!, "v", bridge));
            Assert.Equal("playerName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new ClientSession("ally", "v", null!));
            Assert.Equal("bridge", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullModVersion_IsAccepted()
        {
            var hello = (HelloMessage)Single<SendEffect>(new ClientSession("ally", null!, bridge).Start()).Message;
            Assert.Equal(string.Empty, hello.ModVersion);
        }

        [Fact]
        public void Start_SendsHelloWithProtocolAndName()
        {
            var hello = Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
            Assert.Equal(PbjProtocol.Magic, hello.Magic);
            Assert.Equal(PbjProtocol.Version, hello.ProtocolVersion);
            Assert.Equal("0.2.0", hello.ModVersion);
            Assert.Equal("ally", hello.PlayerName);
        }

        [Fact]
        public void Handle_PeerConnected_SendsHello()
        {
            var effects = Client().Handle(new PeerConnectedEvent(0, "host"));
            Assert.IsType<HelloMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void HandleMessage_Welcome_StoresIdentityAndEntersPlanning()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, Welcome());

            Assert.Equal(1, client.PeerId);
            Assert.Equal("7f3a91", client.SessionId);
            Assert.Equal("host", client.HostName);
            Assert.Equal(3, client.Turn);
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("welcome | peer #1"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2 participants"));
        }

        [Fact]
        public void HandleMessage_Welcome_WhenHostNotInCombat_EntersLobby()
        {
            bridge.InCombat = false;
            Assert.Equal(ClientSessionState.Lobby, Welcomed().State);
        }

        [Fact]
        public void HandleMessage_Welcome_Twice_FaultsAndDisconnects()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, Welcome());
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Theory]
        [InlineData(RejectReason.BadMagic)]
        [InlineData(RejectReason.VersionMismatch)]
        [InlineData(RejectReason.SessionFull)]
        [InlineData(RejectReason.DuplicateName)]
        [InlineData(RejectReason.InvalidName)]
        [InlineData(RejectReason.NotAcceptingPeers)]
        public void HandleMessage_Reject_LogsReasonAndFaults(RejectReason reason)
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new RejectMessage(reason, "nope"));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains(reason.ToString()));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_BeforeWelcome_Faults()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("before Welcome"));
        }

        [Fact]
        public void HandleMessage_WithClientOnlyMessage_Faults()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new ReadyMessage(3, null));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void HandleMessage_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Client().HandleMessage(0, null!));
            Assert.Equal("message", ex.ParamName);
        }

        // --- roster ---

        // --- assignments ---

        [Fact]
        public void HandleMessage_Assignments_StoresOnlyItsOwnUnits()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a", "unit_c" }),
                new PeerAssignment(1, new[] { "unit_b" }),
            }));

            Assert.Equal(new[] { "unit_b" }, client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control: unit_b"));
        }

        [Fact]
        public void HandleMessage_Assignments_WithNoUnitsForUs_SaysSo()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a" }),
            }));

            Assert.Empty(client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control no units"));
        }

        [Fact]
        public void HandleMessage_Assignments_ReplacesPreviousOwnership()
        {
            var client = Welcomed();
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_b" }) }));
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_c" }) }));
            Assert.Equal(new[] { "unit_c" }, client.OwnedUnits);
        }

        [Fact]
        public void OwnedUnits_IsEmptyBeforeAnyAssignment()
        {
            Assert.Empty(Welcomed().OwnedUnits);
        }

        [Fact]
        public void HandleMessage_PeerJoined_Logs()
        {
            Assert.Contains("#2", Single<LogEffect>(Welcomed().HandleMessage(0, new PeerJoinedMessage(2, "ally2"))).Line);
        }

        [Fact]
        public void HandleMessage_PeerLeft_Logs()
        {
            Assert.Contains("peer left: #2", Single<LogEffect>(
                Welcomed().HandleMessage(0, new PeerLeftMessage(2, "ally2"))).Line);
        }

        // --- the turn cycle ---

        [Fact]
        public void Handle_LocalReady_SendsReadyWithCapturedOrders()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_c", 0f, 2f));
            var client = Welcomed();
            var effects = client.Handle(new LocalReadyEvent());

            var ready = Assert.IsType<ReadyMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(3, ready.Turn);
            Assert.Single(ready.Orders);
            Assert.Equal("unit_c", ready.Orders[0].OwnerName);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_LocalReady_WhenNotPlanning_ProducesNoEffects()
        {
            var client = Client();
            Assert.Empty(client.Handle(new LocalReadyEvent()));
        }

        [Fact]
        public void Handle_LocalReady_Twice_SendsReadyAgain()
        {
            // Ready is idempotent by design; the host replaces the batch.
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));
            Assert.Single(All<SendEffect>(client.Handle(new LocalReadyEvent())));
        }

        [Fact]
        public void HandleMessage_TurnCommit_LocksExecutionAndWatches()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnCommit_ForUnexpectedTurn_ResyncsInsteadOfFaulting()
        {
            // The host can force-execute past us via scenario content.
            var client = Welcomed(turn: 3);
            client.HandleMessage(0, new TurnCommitMessage(7));
            Assert.Equal(7, client.Turn);
            Assert.Equal(ClientSessionState.Watching, client.State);
        }

        [Fact]
        public void HandleMessage_TurnComplete_UnlocksAndAdvancesToNextTurn()
        {
            var client = Welcomed();
            client.HandleMessage(0, new TurnCommitMessage(3));
            var effects = client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));

            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(4, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithMatchingDigest_LogsOk()
        {
            bridge.Digest = "3f9c1a04";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "3f9c1a04"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("digest 3f9c1a04 OK"));
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithDifferentDigest_LogsDiverged()
        {
            bridge.Digest = "bbbb2222";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "aaaa1111"));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("DIVERGED | host aaaa1111 | local bbbb2222"));
        }

        // --- un-ready ---

        [Fact]
        public void LocalUnready_AfterSubmitting_SendsUnreadyAndUnlocksExecution()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());

            var effects = client.Handle(new LocalUnreadyEvent());
            var unready = (UnreadyMessage)Single<SendEffect>(effects).Message;
            Assert.Equal(3, unready.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void LocalUnready_WithNothingSubmitted_SendsNothing()
        {
            var effects = Welcomed().Handle(new LocalUnreadyEvent());
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void LocalUnready_IsRefusedOnceTheHostHasCommitted()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        [Fact]
        public void LocalUnready_ThenReady_SubmitsAgain()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.Handle(new LocalUnreadyEvent());

            Assert.IsType<ReadyMessage>(Single<SendEffect>(client.Handle(new LocalReadyEvent())).Message);
        }

        // --- combat lifecycle from the host ---

        [Fact]
        public void CombatStart_MovesToPlanningAtTheHostsTurnAndUnlocks()
        {
            bridge.InCombat = false;
            var client = Welcomed(-1);
            Assert.Equal(ClientSessionState.Lobby, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(0, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatStart_ClearsAStaleSubmission()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        [Fact]
        public void CombatEnd_ReturnsToLobbyDropsOwnedUnitsAndUnlocks()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new AssignmentsMessage(new[]
            {
                new PeerAssignment(1, new[] { "unit_b" }),
            }));
            Assert.NotEmpty(client.OwnedUnits);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.Equal(ClientSessionState.Lobby, client.State);
            Assert.Empty(client.OwnedUnits);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatEnd_WhileWatchingStillUnlocks()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void OwnCombatEdges_AreLoggedButChangeNothing()
        {
            // A client's local InCombat is not authoritative — it learns combat
            // state from the host. These arms exist so the event does not throw.
            var client = Welcomed();

            foreach (var evt in new PbjInboundEvent[] { new CombatEnteredEvent(), new CombatExitedEvent() })
            {
                var effects = client.Handle(evt);
                Assert.Single(All<LogEffect>(effects));
                Assert.DoesNotContain(effects, e => !(e is LogEffect));
                Assert.Equal(ClientSessionState.Planning, client.State);
            }
        }

        [Fact]
        public void OrderApplied_IsIgnored()
        {
            // Clients never apply remote orders; the arm exists so the event,
            // which the shared runtime can produce, does not throw.
            Assert.Empty(Welcomed().Handle(new OrderAppliedEvent(1, 0, OrderApplyResult.Applied)));
        }

        // --- order results ---

        [Fact]
        public void OrderResult_IsReportedAndChangesNoState()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId, new OrderResultMessage(3, 2, new[]
            {
                new RejectedOrder(1, OrderApplyResult.NotOwned),
            }));

            Assert.Single(All<LogEffect>(effects));
            Assert.DoesNotContain(effects, e => !(e is LogEffect));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        // --- reconnect ---

        [Fact]
        public void Start_WithAResumeToken_SendsRejoinRatherThanHello()
        {
            var client = new ClientSession("ally", "0.2.0", bridge, "7f3a91", 1, "tok");
            var rejoin = Assert.IsType<RejoinMessage>(Single<SendEffect>(client.Start()).Message);

            Assert.Equal(PbjProtocol.Magic, rejoin.Magic);
            Assert.Equal(PbjProtocol.Version, rejoin.ProtocolVersion);
            Assert.Equal("ally", rejoin.PlayerName);
            Assert.Equal("7f3a91", rejoin.SessionId);
            Assert.Equal(1, rejoin.ClaimedPeerId);
            Assert.Equal("tok", rejoin.ResumeToken);
        }

        [Fact]
        public void Start_WithNoResumeToken_SendsHello()
        {
            Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
        }

        [Fact]
        public void Welcome_StoresTheResumeTokenForALaterReturn()
        {
            Assert.Equal("tok", Welcomed().ResumeToken);
        }

        // --- snapshot correction ---

        private static UnitSnapshot Snap(string name, float x = 1f) =>
            new UnitSnapshot(name, new Vec3(x, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f, false, 0f);

        [Fact]
        public void Snapshot_ClearsStaleLocalOrdersBeforeApplying()
        {
            // A client's planned orders never execute, so by turn 3 its timeline
            // is junk and CaptureLocalOrders would re-send orders already run.
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })).ToList();

            var clearAt = effects.FindIndex(e => e is ClearLocalOrdersEffect);
            var applyAt = effects.FindIndex(e => e is ApplySnapshotEffect);
            Assert.True(clearAt >= 0 && applyAt > clearAt);
        }

        [Fact]
        public void Snapshot_CarriesTheHostsDigestOnTheEffect()
        {
            var client = Welcomed();
            var apply = Single<ApplySnapshotEffect>(client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })));

            Assert.Equal(3, apply.Turn);
            Assert.Equal("abc", apply.ExpectedDigest);
            Assert.Equal("unit_b", Assert.Single(apply.Units).Name);
        }

        [Fact]
        public void SnapshotApplied_WithAMatchingDigest_ReportsTheCorrectionVerified()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("corrected") && l.Line.Contains("OK"));
        }

        [Fact]
        public void SnapshotApplied_WithAMismatchedDigest_ReportsItLoudly()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "def"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("STILL DIVERGED"));
        }

        [Fact]
        public void SnapshotApplied_ChangesNoState()
        {
            var client = Welcomed();
            client.Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        // --- keepalive ---

        [Fact]
        public void Ping_IsAnsweredWithAMatchingPong()
        {
            var pong = Assert.IsType<PongMessage>(
                Single<SendEffect>(Welcomed().HandleMessage(ClientSession.HostConnectionId, new PingMessage(42))).Message);
            Assert.Equal(42, pong.Nonce);
        }

        [Fact]
        public void Ping_DuringTheHandshake_IsAnsweredRatherThanTreatedAsAViolation()
        {
            // Refusing would have the host reap a peer that is perfectly alive.
            var client = Client();
            client.Start();

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));
            Assert.IsType<PongMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(ClientSessionState.Handshaking, client.State);
        }

        [Fact]
        public void Ping_ChangesNoState()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(3, client.Turn);
        }

        [Fact]
        public void Tick_FirstOne_SeedsRatherThanJudging()
        {
            Assert.Empty(Welcomed().Handle(new TickEvent(9_999_999)));
        }

        [Fact]
        public void Tick_AfterTheHostTimeout_FaultsAndUnlocksExecution()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));

            var effects = client.Handle(new TickEvent(1000 + PbjProtocol.HostTimeoutSeconds));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Tick_BeforeTheHostTimeout_DoesNothing()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));

            Assert.Empty(client.Handle(new TickEvent(1000 + PbjProtocol.HostTimeoutSeconds - 1)));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        [Fact]
        public void AnyInboundMessage_KeepsTheHostAlive()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));
            client.Handle(new TickEvent(1025));
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));

            Assert.Empty(client.Handle(new TickEvent(1040)));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        [Fact]
        public void HostTimeout_IsLongerThanThePeerTimeout()
        {
            // The host is the side that hitches, and a client fault is terminal.
            Assert.True(PbjProtocol.HostTimeoutSeconds > PbjProtocol.PeerTimeoutSeconds);
        }

        // --- loss of the host ---

        [Fact]
        public void Handle_TransportFailed_UnlocksExecutionAndFaults()
        {
            // The single most important client behaviour: a lost host must never
            // leave the local execute button permanently disabled.
            var client = Welcomed();
            client.HandleMessage(0, new TurnCommitMessage(3));
            var effects = client.Handle(new TransportFailedEvent("connection reset"));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_PeerDisconnected_UnlocksExecutionAndFaults()
        {
            var client = Welcomed();
            var effects = client.Handle(new PeerDisconnectedEvent(0, null));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_Bye_ClosesCleanlyAndUnlocks()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new ByeMessage("host shutting down"));
            Assert.Equal(ClientSessionState.Closed, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_Bye_WithNoReason_StillCloses()
        {
            Assert.Equal(ClientSessionState.Closed,
                WithBye(null).State);

            ClientSession WithBye(string? reason)
            {
                var client = Welcomed();
                client.HandleMessage(0, new ByeMessage(reason));
                return client;
            }
        }

        [Fact]
        public void Handle_AfterClosed_ProducesNoEffects()
        {
            var client = Welcomed();
            client.HandleMessage(0, new ByeMessage("host shutting down"));
            Assert.Empty(client.Handle(new LocalReadyEvent()));
            Assert.Empty(client.HandleMessage(0, new TurnCommitMessage(4)));
        }

        [Fact]
        public void Handle_AfterFaulted_ProducesNoEffects()
        {
            var client = Welcomed();
            client.Handle(new TransportFailedEvent("gone"));
            Assert.Empty(client.Handle(new LocalReadyEvent()));
            Assert.Empty(client.HandleMessage(0, new TurnCommitMessage(4)));
        }

        // --- plumbing ---

        [Fact]
        public void Handle_TransportLog_ForwardsTheLine()
        {
            Assert.Equal("connected", Single<LogEffect>(Client().Handle(new TransportLogEvent("connected"))).Line);
        }

        [Fact]
        public void Handle_TransportLog_WithNoLine_LogsPlaceholder()
        {
            Assert.Equal("unknown", Single<LogEffect>(Client().Handle(new TransportLogEvent(null))).Line);
        }

        [Fact]
        public void Handle_PeerBytes_ProducesNoEffects()
        {
            Assert.Empty(Client().Handle(new PeerBytesEvent(0, new byte[] { 1 })));
        }

        [Fact]
        public void Handle_LocalTurnComplete_ProducesNoEffects()
        {
            // A client does not simulate, so its own execution-end hook carries
            // no authority — the host's TurnComplete drives the cycle.
            Assert.Empty(Welcomed().Handle(new LocalTurnCompleteEvent("d", null)));
        }

        [Fact]
        public void Handle_CommitOutcome_ProducesNoEffects()
        {
            Assert.Empty(Welcomed().Handle(new CommitOutcomeEvent(3, true)));
        }

        [Fact]
        public void ConnectedPeerIds_IsJustTheHost()
        {
            Assert.Equal(new[] { ClientSession.HostConnectionId }, Client().ConnectedPeerIds.ToArray());
        }

        [Fact]
        public void Handle_WithNullEvent_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Client().Handle(null!));
            Assert.Equal("evt", ex.ParamName);
        }

        [Fact]
        public void Handle_WithUnsupportedEventKind_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => Client().Handle(new UnsupportedEvent()));
        }

        private sealed class UnsupportedEvent : PbjInboundEvent
        {
            public override PbjInboundEventKind Kind => (PbjInboundEventKind)200;
        }
    }
}
