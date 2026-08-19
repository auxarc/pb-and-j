using System.Linq;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The client's half, in the order it happens: deciding whether to ask for a save
    // it has been offered, checking what arrives before writing any of it, and then
    // the same path again driven through a real runtime instead of by hand.
    //
    // Client, InLobby, Offer, Delivery, Frame and RunTransferThroughRuntime live
    // here rather than in the shared fixture because every one of their call sites
    // is in this file.
    //
    // Class-level XML doc lives only in ScenarioTransferTests.cs -- /// on a partial
    // part is concatenated by the compiler into one type entry.
    public partial class ScenarioTransferTests
    {
        // ===== client: deciding =====

        private ClientSession Client() => new ClientSession("ally", "0.2.0", bridge);

        /// <summary>A handshook client sitting in the lobby, as at the main menu.</summary>
        private ClientSession InLobby()
        {
            bridge.InCombat = false;
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 0, "tok"));
            return client;
        }

        private static ScenarioOfferMessage Offer(ScenarioPayload of) =>
            new ScenarioOfferMessage(of.SaveName, (int)of.TotalBytes, of.Digest);

        [Fact]
        public void Client_HoldingNothing_AsksForTheOfferedScenario()
        {
            var effects = InLobby().HandleMessage(
                ClientSession.HostConnectionId, Offer(Save()));

            var request = Messages<ScenarioRequestMessage>(effects).Single();
            Assert.Equal(Save().Digest, request.Digest);
        }

        [Fact]
        public void Client_AlreadyHoldingTheSameSave_AsksForNothing()
        {
            // The reconnect case, and the reason the offer exists at all: a
            // rejoining peer holds the save by definition and should pay nothing.
            var client = InLobby();
            bridge.Scenario = Save();

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Offer(Save()));

            Assert.Empty(Messages<ScenarioRequestMessage>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("already on disk"));
        }

        [Fact]
        public void Client_HoldingADifferentSave_AsksForTheOfferedOne()
        {
            var client = InLobby();
            bridge.Scenario = Save(content: "some other combat");

            Assert.Single(Messages<ScenarioRequestMessage>(
                client.HandleMessage(ClientSession.HostConnectionId, Offer(Save()))));
        }

        [Fact]
        public void Client_WhenTheHostIsFighting_DeclinesTheOfferSilently()
        {
            // Mid-combat is the wrong moment: at best wasted bandwidth, at worst
            // an invitation to load it and lose the session.
            var client = InLobby();
            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(3));

            Assert.Empty(All<SendEffect>(
                client.HandleMessage(ClientSession.HostConnectionId, Offer(Save()))));
        }

        [Fact]
        public void Client_WhoseOwnGameIsInCombat_StillAcceptsTheOffer()
        {
            // ⚠️ The defect this replaces. The gate used to read ClientSessionState,
            // which HandleWelcome seeds from *this machine's own* bridge.InCombat —
            // so a player who joined while their own singleplayer game happened to
            // be mid-combat landed in Planning against a host sitting in its lobby,
            // silently declined every offer, never held the save, and so could
            // never ready. Nothing else in the suite could catch it: the scripted
            // bridge is never in combat, which is exactly why this test sets the
            // flag by hand.
            bridge.InCombat = true;
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 3, "tok"));

            var effects = client.HandleMessage(ClientSession.HostConnectionId, Offer(Save()));

            Assert.IsType<ScenarioRequestMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void Client_OnAManualPull_AsksWithNoDigest()
        {
            // "Send me whatever you have now" — the override for every case the
            // automatic path deliberately excludes.
            var effects = InLobby().Handle(new LocalScenarioPullEvent());

            var request = Messages<ScenarioRequestMessage>(effects).Single();
            Assert.Null(request.Digest);
        }

        [Fact]
        public void Client_OnAManualPull_AsksEvenWhenItAlreadyHoldsTheSave()
        {
            var client = InLobby();
            bridge.Scenario = Save();
            Assert.Single(Messages<ScenarioRequestMessage>(client.Handle(new LocalScenarioPullEvent())));
        }

        [Fact]
        public void Client_OnAManualPullWhilePlanning_StillAsks()
        {
            // Planning is a legitimate moment to want the save by hand — it is
            // the automatic offer that is conservative, not the command.
            bridge.InCombat = true;
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 3, "tok"));

            Assert.Single(Messages<ScenarioRequestMessage>(client.Handle(new LocalScenarioPullEvent())));
        }

        [Fact]
        public void Client_OnAManualPullBeforeWelcome_DoesNothing()
        {
            var client = Client();
            client.Start();
            Assert.Empty(client.Handle(new LocalScenarioPullEvent()));
        }

        // ===== client: receiving =====

        private static ScenarioMessage Delivery(ScenarioPayload of) =>
            new ScenarioMessage(of.SaveName, of.Digest, of.Files);

        [Fact]
        public void Client_OnAGoodScenario_WritesIt()
        {
            var effects = InLobby().HandleMessage(
                ClientSession.HostConnectionId, Delivery(Save()));

            var write = Single<WriteScenarioEffect>(effects);
            Assert.Equal(2, write.Payload.Files.Count);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("scenario 'pbj_combat_test' received"));
        }

        [Fact]
        public void Client_OnAScenarioWithADisallowedName_WritesNothing()
        {
            // A valid destination on purpose, so the refusal proved here is the
            // per-file one and not the destination guard firing first.
            var effects = InLobby().HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage(LobbySaveNames.ScenarioSlot, "whatever", new[]
                {
                    new ScenarioFile("../../.bashrc", Encoding.UTF8.GetBytes("rm -rf")),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 1 }),
                }));

            Assert.Empty(All<WriteScenarioEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("DisallowedName"));
        }

        [Fact]
        public void Client_OnAScenarioAimedOutsideTheNamespace_WritesNothing()
        {
            // M11e's own boundary. M9 never took a directory name from the wire —
            // the receiver used its own constant. The synchronised load forces every
            // peer onto the lobby's key, so the name now has to travel, and this is
            // the check that keeps a peer past the passphrase from steering a write
            // onto a singleplayer campaign.
            var effects = InLobby().HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("firstrun", "whatever", new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1 }),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 2 }),
                }));

            Assert.Empty(All<WriteScenarioEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("DisallowedDestination"));
        }

        [Fact]
        public void Client_OnAScenarioWhoseDestinationEscapesTheSaveFolder_WritesNothing()
        {
            // The case LobbyCatalogue's offer test would have let through: it checks
            // the prefix and not-the-scenario-slot, and this satisfies both.
            var effects = InLobby().HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_../../elsewhere", "whatever", new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1 }),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 2 }),
                }));

            Assert.Empty(All<WriteScenarioEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("DisallowedDestination"));
        }

        [Fact]
        public void Client_OnAnOversizedScenario_WritesNothing()
        {
            var effects = InLobby().HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("big", "whatever", new[]
                {
                    new ScenarioFile(ScenarioPayload.ContentFileName, new byte[ScenarioPayload.MaxTotalBytes]),
                    new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
                }));

            Assert.Empty(All<WriteScenarioEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("TooLarge"));
        }

        [Fact]
        public void Client_OnADigestThatDoesNotMatchTheBytes_WritesNothing()
        {
            // Truncation or substitution. The digest is recomputed from what
            // actually arrived, never taken from the sender's word.
            var effects = InLobby().HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_combat_test", "deadbeef", Save().Files));

            Assert.Empty(All<WriteScenarioEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("sender claimed deadbeef"));
        }

        [Fact]
        public void Client_RefusingAScenario_DoesNotFaultTheSession()
        {
            // A bad scenario is an annoyance, not a broken session. Dropping the
            // connection over it would turn one into a lost game.
            var client = InLobby();
            client.HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_combat_test", "deadbeef", Save().Files));

            Assert.Equal(ClientSessionState.Lobby, client.State);
        }

        // ===== the write itself =====

        private static byte[] Frame(PbjMessage message) =>
            FrameEncoder.Encode(PbjMessageCodec.Encode(message));

        /// <summary>Drives a real client runtime from Welcome to a delivered save.</summary>
        private RecordingLog RunTransferThroughRuntime(ScenarioPayload? payload = null)
        {
            var log = new RecordingLog();
            var mailbox = new PbjMailbox(64);
            bridge.InCombat = false;
            var runtime = new PbjRuntime(
                new FakeTransport(), bridge, log, mailbox, Client());

            mailbox.Post(new PeerBytesEvent(ClientSession.HostConnectionId, Frame(new WelcomeMessage(
                PbjProtocol.Version, "7f3a91", 1, "host", new[] { new PeerInfo(0, "host") }, 0, "tok"))));
            mailbox.Post(new PeerBytesEvent(
                ClientSession.HostConnectionId, Frame(Delivery(payload ?? Save()))));
            runtime.Pump(0);
            return log;
        }

        [Fact]
        public void Runtime_OnAWriteEffect_PutsTheSaveThroughTheBridgeAndSaysWhereItWent()
        {
            var log = RunTransferThroughRuntime();

            Assert.Single(bridge.WrittenScenarios);
            Assert.Equal(2, bridge.WrittenScenarios[0].Files.Count);
            Assert.True(log.Contains("run pbj.combat-load"));
        }

        /// <remarks>
        /// Found on a running two-party session (2026-08-07): a client receiving
        /// the lobby's CAMPAIGN save was told to "run pbj.combat-load to enter
        /// it". That command loads M9's combat scenario slot — following the
        /// instruction inside a co-op campaign would drop a combat scenario on
        /// top of it. The campaign case needs no manual step at all: M11d's
        /// synchronised load enters it for everyone once the lobby agrees.
        /// </remarks>
        [Fact]
        public void Runtime_OnACampaignWrite_DoesNotSendThePlayerToLoadACombatScenario()
        {
            var log = RunTransferThroughRuntime(Save("pbj_campaign"));

            Assert.False(log.Contains("pbj.combat-load"));
            Assert.True(log.Contains("the lobby will load it"));
        }

        [Fact]
        public void Runtime_WhenTheWriteFails_SaysSoRatherThanClaimingSuccess()
        {
            bridge.ScenarioWriteSucceeds = false;
            Assert.True(RunTransferThroughRuntime().Contains("could not write scenario"));
        }
    }
}
