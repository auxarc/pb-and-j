using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Where to start reading. The fixture the parts share: the fake transport,
    // bridge, log and mailbox a runtime is built on, and the helpers that stand a
    // host runtime up and drive a peer through the handshake over the real byte path.
    //
    // PbjRuntimeTests is one class split across this file and its siblings; this one
    // holds what they share.
    public partial class PbjRuntimeTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();
        private readonly FakeTransport transport = new FakeTransport();
        private readonly RecordingLog log = new RecordingLog();
        private readonly PbjMailbox mailbox = new PbjMailbox(64);

        private HostSession host = null!;

        private PbjRuntime HostRuntime(int maxPeers = 3)
        {
            host = new HostSession("host", "7f3a91", maxPeers, bridge, "secret", SessionRequirements.None);
            return new PbjRuntime(transport, bridge, log, mailbox, host);
        }

        private static byte[] Frame(PbjMessage message) =>
            FrameEncoder.Encode(PbjMessageCodec.Encode(message));

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name, null, null);

        /// <summary>Runs a peer through the handshake via the real byte path.</summary>
        private PbjRuntime WithHandshakenPeer(int peerId = 1)
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            mailbox.Post(new PeerBytesEvent(peerId, Frame(GoodHello())));
            runtime.Pump(0);
            return runtime;
        }
    }
}
