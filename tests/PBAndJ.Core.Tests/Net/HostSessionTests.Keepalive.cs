using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Pings, timeouts and what counts as a peer being alive. One section of the original.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
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
    }
}
