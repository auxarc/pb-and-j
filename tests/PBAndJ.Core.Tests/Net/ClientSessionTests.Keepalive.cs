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

        /// <remarks>
        /// The unit here is a single long LOCAL frame gap on the receiving side,
        /// which is not the same quantity as a peer that has stopped sending.
        /// The runtime drains the mailbox before it ticks, so a Ping that
        /// arrives during a stall is in hand before the tick that judges the
        /// silence — and stamping it with the pre-stall clock had that tick
        /// charge the whole stall to a host that had just proved it was alive.
        /// One 30s frame gap was enough to fault a live host.
        /// </remarks>
        [Fact]
        public void Tick_AfterAFrameGapTheHostSpokeDuring_DoesNotFaultIt()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));

            // Drained during the gap; the session clock still reads 1000.
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));

            var effects = client.Handle(new TickEvent(1000 + PbjProtocol.HostTimeoutSeconds));
            Assert.Empty(All<SetExecutionLockEffect>(effects));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        /// <remarks>
        /// The other half of the one above, and the reason it is not simply
        /// "forgive any tick that follows a long gap": traffic during the gap
        /// RESTARTS the silence clock, it does not switch it off. A host that
        /// spoke once and then died is still faulted a timeout later.
        /// </remarks>
        [Fact]
        public void Tick_AfterAFrameGapTheHostSpokeDuring_RestartsTheClockRatherThanDisablingIt()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));
            client.Handle(new TickEvent(1030));
            Assert.Equal(ClientSessionState.Planning, client.State);

            // Nothing since. The clock now runs from 1030, not from 1000.
            Assert.Equal(ClientSessionState.Planning,
                Silent(client, 1030 + PbjProtocol.HostTimeoutSeconds - 1));
            Assert.Equal(ClientSessionState.Faulted,
                Silent(client, 1030 + PbjProtocol.HostTimeoutSeconds));

            static ClientSessionState Silent(ClientSession session, double at)
            {
                session.Handle(new TickEvent(at));
                return session.State;
            }
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
