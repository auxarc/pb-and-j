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
                new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") }, turn);

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
            Assert.Empty(Welcomed().Handle(new LocalTurnCompleteEvent("d")));
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
