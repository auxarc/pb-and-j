using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The pump's intake: an empty mailbox, bytes arriving from a peer and becoming
    // dispatched messages however they are chunked, what becomes of a peer whose
    // frame or message will not decode, and the decoder state that goes when the
    // peer does.
    //
    // It also holds the test that a single Pump drains everything queued -- bytes, a
    // connection and a transport log line together. That test sat at the end of the
    // effects section, where every other member is named for the effect it
    // exercises; this one is not, and the pump's handling of posted events is
    // established here. The `// --- decoding ---` banner went with it: this file's
    // name carries the subject, and the banner would have headed a test that is not
    // decoding.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
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
            // Welcome, then the Assignments broadcast.
            Assert.IsType<WelcomeMessage>(transport.MessagesTo(1)[0]);
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
            Assert.IsType<WelcomeMessage>(transport.MessagesTo(1)[0]);
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
    }
}
