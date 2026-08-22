using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The pump's own housekeeping rather than any one message: the tick clock, the
    // Post door for local events, surviving a transport that throws and a mailbox
    // that overflows, refusing an effect it has no case for, and stopping. The
    // nested session that returns an unhandled effect is here, with the only test
    // that uses it.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
        // --- ticks ---

        [Fact]
        public void Pump_TicksTheSessionOnItsFirstCall()
        {
            var runtime = WithHandshakenPeer();
            // The handshake pump already ticked at 0; a peer stamped then must
            // time out once enough time passes.
            runtime.Pump(PbjProtocol.PeerTimeoutSeconds);

            Assert.Contains(log.Lines, l => l.Contains("silent for"));
        }

        [Fact]
        public void Pump_WithinTheTickInterval_DoesNotTickAgain()
        {
            var runtime = WithHandshakenPeer();
            // Would time the peer out if the throttle were not honoured.
            runtime.Pump(PbjProtocol.TickIntervalSeconds / 2);
            runtime.Pump(PbjProtocol.TickIntervalSeconds / 2);

            Assert.DoesNotContain(log.Lines, l => l.Contains("silent for"));
        }

        [Fact]
        public void Pump_PingsAndTheClientPongs()
        {
            var runtime = WithHandshakenPeer();
            runtime.Pump(PbjProtocol.PingIntervalSeconds);
            runtime.Pump(2 * PbjProtocol.PingIntervalSeconds);

            Assert.Contains(transport.MessagesTo(1), m => m is PingMessage);
        }

        /// <remarks>
        /// The ordering the two sessions' keepalive tests stand in for, run
        /// through the real pump and the real byte path. <see cref="PbjRuntime.Pump"/>
        /// drains the mailbox and ticks afterwards, so this peer's Pong is in
        /// hand before the tick that judges it — and the whole 20s frame gap
        /// must not be charged to it.
        /// </remarks>
        [Fact]
        public void Pump_AfterAFrameGap_KeepsThePeerWhoseTrafficItJustDrained()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new PongMessage(0))));

            runtime.Pump(PbjProtocol.PeerTimeoutSeconds);

            Assert.DoesNotContain(log.Lines, l => l.Contains("silent for"));
            Assert.Single(host.Peers);
        }

        /// <remarks>
        /// The client half of the same ordering. A single 30s frame gap on this
        /// side used to fault a host that had answered inside it.
        /// </remarks>
        [Fact]
        public void Pump_AfterAFrameGap_KeepsTheHostWhoseTrafficItJustDrained()
        {
            var session = new ClientSession("ally", "0.2.0", bridge);
            var runtime = new PbjRuntime(transport, bridge, log, mailbox, session);
            mailbox.Post(new PeerConnectedEvent(ClientSession.HostConnectionId, "127.0.0.1:7777"));
            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, Frame(new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 0, "tok"))));
            runtime.Pump(0);
            // Planning, not Lobby: the shared fixture's bridge reports InCombat,
            // and HandleWelcome seeds a client's state from this machine's own
            // combat flag. Asserted rather than assumed so a fixture change
            // cannot quietly turn the frame-gap assertions below into a test
            // that fails before it ever reaches them.
            Assert.Equal(ClientSessionState.Planning, session.State);

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, Frame(new PingMessage(4))));
            runtime.Pump(PbjProtocol.HostTimeoutSeconds);

            Assert.DoesNotContain(log.Lines, l => l.Contains("silent for"));
            Assert.NotEqual(ClientSessionState.Faulted, session.State);
        }

        // --- failure isolation ---

        [Fact]
        public void Pump_WhenTransportSendThrows_DisconnectsThatPeerAndContinues()
        {
            var throwing = new ThrowingTransport();
            var session = new HostSession("host", "s", 3, bridge, "secret", SessionRequirements.None);
            var runtime = new PbjRuntime(throwing, bridge, log, mailbox, session);

            mailbox.Post(new PeerBytesEvent(1, Frame(GoodHello())));
            runtime.Pump(0);

            Assert.Equal(1, throwing.Disconnected[0].PeerId);
            Assert.Contains(log.Lines, l => l.Contains("send failed"));
        }

        [Fact]
        public void Pump_ReportsMailboxOverflow()
        {
            var small = new PbjMailbox(1);
            var runtime = new PbjRuntime(transport, bridge, log, small, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None));
            small.Post(new TransportLogEvent("a"));
            small.Post(new TransportLogEvent("dropped"));
            runtime.Pump(0);

            Assert.Contains(log.Lines, l => l.Contains("mailbox overflowed — dropped 1 event"));
        }

        [Fact]
        public void Pump_ReportsOverflowOnlyOncePerBatchOfDrops()
        {
            var small = new PbjMailbox(1);
            var runtime = new PbjRuntime(transport, bridge, log, small, new HostSession("h", "s", 1, bridge, "secret", SessionRequirements.None));
            small.Post(new TransportLogEvent("a"));
            small.Post(new TransportLogEvent("dropped"));
            runtime.Pump(0);
            runtime.Pump(0);

            Assert.Single(log.Lines, l => l.Contains("mailbox overflowed"));
        }

        [Fact]
        public void Pump_WithUnsupportedEffect_Throws()
        {
            var runtime = new PbjRuntime(transport, bridge, log, mailbox, new UnsupportedEffectSession());
            mailbox.Post(new TransportLogEvent("x"));
            Assert.Throws<InvalidOperationException>(() => runtime.Pump(0));
        }

        // --- teardown ---

        [Fact]
        public void Post_QueuesALocalEvent()
        {
            var runtime = WithHandshakenPeer();
            runtime.Post(new TransportLogEvent("posted"));
            runtime.Pump(0);
            Assert.Contains(log.Lines, l => l.Contains("posted"));
        }

        [Fact]
        public void Stop_StopsTheTransportAndDrainsTheMailbox()
        {
            var runtime = HostRuntime();
            mailbox.Post(new TransportLogEvent("never seen"));
            runtime.Stop();

            Assert.Equal(1, transport.StopCalls);
            Assert.Equal(0, mailbox.Count);
        }

        [Fact]
        public void Stop_Twice_IsIdempotent()
        {
            var runtime = HostRuntime();
            runtime.Stop();
            runtime.Stop();
            Assert.Equal(1, transport.StopCalls);
        }

        [Fact]
        public void Pump_AfterStop_DoesNothing()
        {
            var runtime = HostRuntime();
            runtime.Stop();
            mailbox.Post(new PeerBytesEvent(1, Frame(GoodHello())));
            runtime.Pump(0);
            Assert.Empty(transport.Sent);
        }

        /// <summary>Returns an effect the runner has no case for.</summary>
        private sealed class UnsupportedEffectSession : IPbjSession
        {
            public IReadOnlyList<int> ConnectedPeerIds => new int[0];

            public IReadOnlyList<PbjEffect> Handle(PbjInboundEvent evt) => new PbjEffect[] { new UnsupportedEffect() };

            public IReadOnlyList<PbjEffect> HandleMessage(int peerId, PbjMessage message) => new PbjEffect[0];

            private sealed class UnsupportedEffect : PbjEffect
            {
                public override PbjEffectKind Kind => (PbjEffectKind)200;
            }
        }
    }
}
