using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Staying alive and noticing when the host does not: pings, timeouts, and
    // what a client does when it loses the host.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
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
    }
}
