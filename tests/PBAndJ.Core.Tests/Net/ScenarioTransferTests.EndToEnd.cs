using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // One test, and the only one here that runs a real host and a real client
    // against each other rather than either against a message. It is the claim the
    // rest of the file supports: what leaves one machine is byte-for-byte what the
    // other writes.
    //
    // Class-level XML doc lives only in ScenarioTransferTests.cs -- /// on a partial
    // part is concatenated by the compiler into one type entry.
    public partial class ScenarioTransferTests
    {
        // ===== end to end, both sessions against each other =====

        [Fact]
        public void HostAndClient_TransferASaveWithNoManualStep()
        {
            bridge.Scenario = Save();
            var host = Host();
            var offer = Messages<ScenarioOfferMessage>(Handshake(host)).Single();

            // The client is a second machine, so it starts with nothing on disk.
            var clientBridge = new FakeGameBridge { InCombat = false };
            var client = new ClientSession("ally", "0.2.0", clientBridge);
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 0, "tok"));

            var request = Messages<ScenarioRequestMessage>(
                client.HandleMessage(ClientSession.HostConnectionId, offer)).Single();

            var delivery = Messages<ScenarioMessage>(host.HandleMessage(1, request)).Single();

            var write = Single<WriteScenarioEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, delivery));

            // Byte-for-byte, which is the whole claim: both machines now hold the
            // same save and will produce the same nameInternal join keys.
            Assert.Equal(bridge.Scenario.Digest, write.Payload.Digest);
            Assert.Equal(
                bridge.Scenario.Files[0].Content,
                write.Payload.Files.Single(f => f.Name == ScenarioPayload.ContentFileName).Content);
        }
    }
}
