using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjRuntimeTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();
        private readonly FakeTransport transport = new FakeTransport();
        private readonly RecordingLog log = new RecordingLog();
        private readonly PbjMailbox mailbox = new PbjMailbox(64);

        private HostSession host = null!;

        private PbjRuntime HostRuntime(int maxPeers = 3)
        {
            host = new HostSession("host", "7f3a91", maxPeers, bridge);
            return new PbjRuntime(transport, bridge, log, mailbox, host);
        }

        private static byte[] Frame(PbjMessage message) =>
            FrameEncoder.Encode(PbjMessageCodec.Encode(message));

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name);

        /// <summary>Runs a peer through the handshake via the real byte path.</summary>
        private PbjRuntime WithHandshakenPeer(int peerId = 1)
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            mailbox.Post(new PeerBytesEvent(peerId, Frame(GoodHello())));
            runtime.Pump(0);
            return runtime;
        }

        // --- construction ---

        [Fact]
        public void Constructor_WithNullTransport_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(null!, bridge, log, mailbox, new HostSession("h", "s", 1, bridge)));
            Assert.Equal("transport", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, null!, log, mailbox, new HostSession("h", "s", 1, bridge)));
            Assert.Equal("bridge", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullLog_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, null!, mailbox, new HostSession("h", "s", 1, bridge)));
            Assert.Equal("log", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullMailbox_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, log, null!, new HostSession("h", "s", 1, bridge)));
            Assert.Equal("mailbox", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullSession_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(
                () => new PbjRuntime(transport, bridge, log, mailbox, null!));
            Assert.Equal("session", ex.ParamName);
        }

        [Fact]
        public void Session_ExposesTheSession()
        {
            var runtime = HostRuntime();
            Assert.Same(host, runtime.Session);
        }

        // --- decoding ---

        [Fact]
        public void Pump_WithEmptyMailbox_ProducesNoTransportCalls()
        {
            HostRuntime().Pump(0);
            Assert.Empty(transport.Sent);
        }

        [Fact]
        public void Pump_PeerBytes_DecodesAndDispatchesMessage()
        {
            WithHandshakenPeer();
            Assert.IsType<WelcomeMessage>(transport.MessagesTo(1).Single());
        }

        [Fact]
        public void Pump_PeerBytes_SplitAcrossTwoEvents_DispatchesOnce()
        {
            var runtime = HostRuntime();
            var frame = Frame(GoodHello());
            var first = frame.Take(3).ToArray();
            var second = frame.Skip(3).ToArray();

            mailbox.Post(new PeerBytesEvent(1, first));
            runtime.Pump(0);
            Assert.Empty(transport.Sent);

            mailbox.Post(new PeerBytesEvent(1, second));
            runtime.Pump(0);
            Assert.Single(transport.MessagesTo(1));
        }

        [Fact]
        public void Pump_TwoMessagesInOneByteChunk_DispatchesBoth()
        {
            var runtime = HostRuntime();
            var chunk = Frame(GoodHello()).Concat(Frame(new ReadyMessage(3, null))).ToArray();
            mailbox.Post(new PeerBytesEvent(1, chunk));
            runtime.Pump(0);

            var messages = transport.MessagesTo(1);
            Assert.IsType<WelcomeMessage>(messages[0]);
            Assert.Contains(log.Lines, l => l.Contains("ready from #1"));
        }

        [Fact]
        public void Pump_PeerBytes_WithMalformedFrame_DisconnectsPeerAndLogs()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, new byte[] { 0, 0, 0, 0 }));
            runtime.Pump(0);

            Assert.Equal(1, transport.Disconnected.Single().PeerId);
            Assert.Contains(log.Lines, l => l.Contains("malformed frame"));
        }

        [Fact]
        public void Pump_PeerBytes_WithUndecodableMessage_DisconnectsPeerAndLogs()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, FrameEncoder.Encode(new byte[] { 200 })));
            runtime.Pump(0);

            Assert.Equal(1, transport.Disconnected.Single().PeerId);
            Assert.Contains(log.Lines, l => l.Contains("undecodable message"));
        }

        [Fact]
        public void Pump_PeerDisconnected_DiscardsThatPeersDecoderState()
        {
            // A reconnecting peer reusing the id must not inherit half a frame.
            var runtime = HostRuntime();
            var frame = Frame(GoodHello());
            mailbox.Post(new PeerBytesEvent(1, frame.Take(3).ToArray()));
            runtime.Pump(0);

            mailbox.Post(new PeerDisconnectedEvent(1, "closed"));
            runtime.Pump(0);

            // The rest of the old frame is now garbage to a fresh decoder.
            mailbox.Post(new PeerBytesEvent(1, frame.Skip(3).ToArray()));
            runtime.Pump(0);
            Assert.Empty(transport.MessagesTo(1));
        }

        // --- effects ---

        [Fact]
        public void Pump_SendEffect_WritesAnEncodedFrame()
        {
            WithHandshakenPeer();
            var sent = transport.Sent.Single();
            Assert.Equal(1, sent.PeerId);
            Assert.Equal(FrameEncoder.HeaderLength + PbjMessageCodec.Encode(
                (WelcomeMessage)transport.MessagesTo(1).Single()).Length, sent.Frame.Length);
        }

        [Fact]
        public void Pump_BroadcastEffect_WritesToEveryPeerExceptTheExcluded()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, Frame(GoodHello("a"))));
            mailbox.Post(new PeerBytesEvent(2, Frame(GoodHello("b"))));
            runtime.Pump(0);

            // Peer 2's join is broadcast to peer 1 but not back to peer 2.
            Assert.Contains(transport.MessagesTo(1), m => m is PeerJoinedMessage);
            Assert.DoesNotContain(transport.MessagesTo(2), m => m is PeerJoinedMessage);
        }

        [Fact]
        public void Pump_DisconnectEffect_CallsTransportDisconnect()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, Frame(new HelloMessage(0xDEAD, 1, "v", "ally"))));
            runtime.Pump(0);
            Assert.Equal(1, transport.Disconnected.Single().PeerId);
        }

        [Fact]
        public void Pump_ApplyOrderEffect_CallsGameBridge()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal("unit_b", bridge.Applied.Single().OwnerName);
        }

        [Fact]
        public void Pump_ApplyOrderEffect_WhenGameRefuses_LogsRejection()
        {
            bridge.ApplyResults["unit_b"] = OrderApplyResult.OutOfWindow;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Contains(log.Lines, l => l.Contains("order REJECTED from #1: unit_b 'move_run' — OutOfWindow"));
        }

        [Fact]
        public void Pump_CommitTurnEffect_CommitsThenBroadcastsInTheSamePump()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal(1, bridge.CommitCalls);
            Assert.Contains(transport.MessagesTo(1), m => m is TurnCommitMessage);
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void Pump_CommitTurnEffect_WhenGameRefuses_BroadcastsNothing()
        {
            // The reason CommitTurnEffect feeds its outcome back rather than
            // being fire-and-forget.
            bridge.CommitSucceeds = false;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal(1, bridge.CommitCalls);
            Assert.DoesNotContain(transport.MessagesTo(1), m => m is TurnCommitMessage);
            Assert.Contains(log.Lines, l => l.Contains("commit REFUSED"));
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Contains(false, bridge.LockCalls);
        }

        [Fact]
        public void Pump_SetExecutionLockEffect_CallsGameBridge()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);
            Assert.Contains(true, bridge.LockCalls);
        }

        [Fact]
        public void Pump_LogEffect_WritesToLog()
        {
            WithHandshakenPeer();
            Assert.Contains(log.Lines, l => l.Contains("handshake ok: #1 'ally'"));
        }

        [Fact]
        public void Pump_ProcessesEveryQueuedEventInOneCall()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerConnectedEvent(1, "r"));
            mailbox.Post(new PeerBytesEvent(1, Frame(GoodHello())));
            mailbox.Post(new TransportLogEvent("hello from the transport"));
            runtime.Pump(0);

            Assert.Contains(log.Lines, l => l.Contains("peer connected: #1"));
            Assert.Contains(log.Lines, l => l.Contains("handshake ok"));
            Assert.Contains(log.Lines, l => l.Contains("hello from the transport"));
        }

        // --- failure isolation ---

        [Fact]
        public void Pump_WhenTransportSendThrows_DisconnectsThatPeerAndContinues()
        {
            var throwing = new ThrowingTransport();
            var session = new HostSession("host", "s", 3, bridge);
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
            var runtime = new PbjRuntime(transport, bridge, log, small, new HostSession("h", "s", 1, bridge));
            small.Post(new TransportLogEvent("a"));
            small.Post(new TransportLogEvent("dropped"));
            runtime.Pump(0);

            Assert.Contains(log.Lines, l => l.Contains("mailbox overflowed — dropped 1 event"));
        }

        [Fact]
        public void Pump_ReportsOverflowOnlyOncePerBatchOfDrops()
        {
            var small = new PbjMailbox(1);
            var runtime = new PbjRuntime(transport, bridge, log, small, new HostSession("h", "s", 1, bridge));
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
