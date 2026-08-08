using System.Collections.Generic;
using System.Linq;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    /// <summary>
    /// M9: the host offers its combat save, a peer that wants it asks, and the
    /// bytes cross. Replaces the hand-carried folder copy stage 2 needed.
    /// </summary>
    public class ScenarioTransferTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private static ScenarioPayload Save(string name = "pbj_combat_test", string content = "zipped")
        {
            return new ScenarioPayload(name, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, Encoding.UTF8.GetBytes(content)),
                new ScenarioFile(ScenarioPayload.MetadataFileName, Encoding.UTF8.GetBytes("ver: 1")),
            });
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        private static IEnumerable<T> Messages<T>(IEnumerable<PbjEffect> effects) where T : PbjMessage =>
            effects.OfType<SendEffect>().Select(s => s.Message).OfType<T>();

        // ===== host: offering =====

        private HostSession Host() =>
            new HostSession("host", "7f3a91", 3, bridge, "secret", SessionRequirements.None);

        private static HelloMessage GoodHello(string name = "ally") =>
            new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", name, null, null);

        private IReadOnlyList<PbjEffect> Handshake(HostSession host, int peerId = 1)
        {
            host.Handle(new PeerConnectedEvent(peerId, "127.0.0.1:1"));
            return host.HandleMessage(peerId, GoodHello());
        }

        [Fact]
        public void Host_WithASave_OffersItOnHandshake()
        {
            bridge.Scenario = Save();
            var offer = Messages<ScenarioOfferMessage>(Handshake(Host())).Single();

            Assert.Equal("pbj_combat_test", offer.SaveName);
            Assert.Equal(Save().Digest, offer.Digest);
            Assert.Equal((int)Save().TotalBytes, offer.TotalBytes);
        }

        [Fact]
        public void Host_WithNoSave_OffersNothingAndSaysNothing()
        {
            // The overwhelmingly common case on a fresh host. Not an error, so
            // it must not produce a warning that trains people to ignore them.
            var effects = Handshake(Host());
            Assert.Empty(Messages<ScenarioOfferMessage>(effects));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("scenario"));
        }

        [Fact]
        public void Host_WithAnUnusableSave_WarnsRatherThanOffering()
        {
            // A save directory that exists but is missing metadata.yaml would
            // otherwise be a session where the transfer silently never happens.
            bridge.Scenario = new ScenarioPayload("pbj_combat_test", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1 }),
            });

            var effects = Handshake(Host());
            Assert.Empty(Messages<ScenarioOfferMessage>(effects));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("not offering scenario") && l.Line.Contains("MissingRequiredFile"));
        }

        [Fact]
        public void Host_OffersToARejoiningPeerToo()
        {
            // A returning peer normally declines, but the offer still has to
            // reach it — otherwise a peer whose save was deleted between sessions
            // has no way back short of the manual copy M9 exists to remove.
            bridge.Scenario = Save();
            var host = Host();
            host.Handle(new TickEvent(1000));
            var token = Messages<WelcomeMessage>(Handshake(host, 1)).Single().ResumeToken;
            host.Handle(new PeerDisconnectedEvent(1, "dropped"));

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, new RejoinMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.2.0", "ally", "7f3a91", 1,
                token, null, null));

            Assert.Single(Messages<ScenarioOfferMessage>(effects));
        }

        // ===== host: serving =====

        [Fact]
        public void Host_OnRequest_SendsEveryFile()
        {
            bridge.Scenario = Save();
            var host = Host();
            Handshake(host);

            var sent = Messages<ScenarioMessage>(
                host.HandleMessage(1, new ScenarioRequestMessage(Save().Digest))).Single();

            Assert.Equal("pbj_combat_test", sent.SaveName);
            Assert.Equal(Save().Digest, sent.Digest);
            Assert.Equal(2, sent.Files.Count);
            Assert.Equal(Encoding.UTF8.GetBytes("zipped"), sent.Files[0].Content);
        }

        [Fact]
        public void Host_OnRequestWithNoSave_SaysSoRatherThanSendingAnEmptyOne()
        {
            var host = Host();
            Handshake(host);

            var effects = host.HandleMessage(1, new ScenarioRequestMessage(null));
            Assert.Empty(Messages<ScenarioMessage>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no combat save to send"));
        }

        // --- the lobby's campaign save (M11e) ---

        [Fact]
        public void Host_OnSelectingACampaignSave_OffersItToEveryone()
        {
            // The fake bridge starts in combat, and a host in combat is not in
            // its lobby — LocalLobbySelect would be ignored outright.
            bridge.InCombat = false;
            // The heart of M11e: the bytes move when the save is chosen, not when
            // someone fails to load it. By the time anyone readies, everyone can.
            var campaign = Save("pbj_campaign", "campaign-bytes");
            bridge.ScenariosByKey["pbj_campaign"] = campaign;
            var host = Host();
            Handshake(host);

            var effects = host.Handle(new LocalLobbySelectEvent("pbj_campaign", campaign.Digest));

            var offer = Assert.IsType<ScenarioOfferMessage>(
                Single<BroadcastEffect>(All<BroadcastEffect>(effects)
                    .Where(b => b.Message is ScenarioOfferMessage)).Message);
            Assert.Equal("pbj_campaign", offer.SaveName);
            Assert.Equal(campaign.Digest, offer.Digest);
        }

        [Fact]
        public void Host_OnSelectingASaveItCannotSend_SaysSoRatherThanGoingQuiet()
        {
            // The fake bridge starts in combat, and a host in combat is not in
            // its lobby — LocalLobbySelect would be ignored outright.
            bridge.InCombat = false;
            // Otherwise every peer sits unready forever and the lobby never starts,
            // with nothing anywhere saying why.
            bridge.ScenariosByKey["pbj_broken"] = new ScenarioPayload("pbj_broken", new[]
            {
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 1 }),
            });
            var host = Host();
            Handshake(host);

            var effects = host.Handle(new LocalLobbySelectEvent("pbj_broken", "whatever"));

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is ScenarioOfferMessage);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("MissingRequiredFile"));
        }

        [Fact]
        public void Host_OnSelectingTheScenarioSlot_DoesNotOfferItTwice()
        {
            // The fake bridge starts in combat, and a host in combat is not in
            // its lobby — LocalLobbySelect would be ignored outright.
            bridge.InCombat = false;
            // The slot is already offered on handshake; re-offering it as a
            // campaign would be a second copy of the same bytes for nothing.
            bridge.Scenario = Save();
            var host = Host();
            Handshake(host);

            var effects = host.Handle(new LocalLobbySelectEvent(
                LobbySaveNames.ScenarioSlot, Save().Digest));

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is ScenarioOfferMessage);
        }

        [Fact]
        public void Host_OnRequestMatchingNeitherSave_ServesTheLobbysCampaign()
        {
            // The fake bridge starts in combat, and a host in combat is not in
            // its lobby — LocalLobbySelect would be ignored outright.
            bridge.InCombat = false;
            // A peer in a lobby is waiting on the campaign, so that is what makes
            // progress. Resolving by digest rather than by a name off the wire is
            // what keeps the host from reading its own disk under a peer's word.
            var campaign = Save("pbj_campaign", "campaign-bytes");
            bridge.Scenario = Save();
            bridge.ScenariosByKey["pbj_campaign"] = campaign;
            var host = Host();
            Handshake(host);
            host.Handle(new LocalLobbySelectEvent("pbj_campaign", campaign.Digest));

            var effects = host.HandleMessage(1, new ScenarioRequestMessage("deadbeef"));

            var sent = Assert.IsType<ScenarioMessage>(Single<SendEffect>(
                All<SendEffect>(effects).Where(s => s.Message is ScenarioMessage)).Message);
            Assert.Equal("pbj_campaign", sent.SaveName);
        }

        [Fact]
        public void Host_OnRequestMatchingTheCampaignDigest_ServesTheCampaign()
        {
            // The fake bridge starts in combat, and a host in combat is not in
            // its lobby — LocalLobbySelect would be ignored outright.
            bridge.InCombat = false;
            var campaign = Save("pbj_campaign", "campaign-bytes");
            bridge.Scenario = Save();
            bridge.ScenariosByKey["pbj_campaign"] = campaign;
            var host = Host();
            Handshake(host);
            host.Handle(new LocalLobbySelectEvent("pbj_campaign", campaign.Digest));

            var effects = host.HandleMessage(1, new ScenarioRequestMessage(campaign.Digest));

            var sent = Assert.IsType<ScenarioMessage>(Single<SendEffect>(
                All<SendEffect>(effects).Where(s => s.Message is ScenarioMessage)).Message);
            Assert.Equal("pbj_campaign", sent.SaveName);
        }

        [Fact]
        public void Host_WithNoCombatSaveAtAll_StillServesTheLobbysCampaign()
        {
            // The ordinary campaign co-op host: it has never run pbj.combat-save,
            // so there is no scenario slot to check the digest against. That must
            // not stop the campaign transfer — M9's slot and M11e's campaign are
            // two saves on one mechanism, not one save with two names.
            bridge.InCombat = false;
            var campaign = Save("pbj_campaign", "campaign-bytes");
            bridge.ScenariosByKey["pbj_campaign"] = campaign;
            var host = Host();
            Handshake(host);
            host.Handle(new LocalLobbySelectEvent("pbj_campaign", campaign.Digest));

            var effects = host.HandleMessage(1, new ScenarioRequestMessage(campaign.Digest));

            var sent = Assert.IsType<ScenarioMessage>(Single<SendEffect>(
                All<SendEffect>(effects).Where(s => s.Message is ScenarioMessage)).Message);
            Assert.Equal("pbj_campaign", sent.SaveName);
        }

        [Fact]
        public void Host_OnRequestWithAnUnusableSave_RefusesRatherThanSending()
        {
            bridge.Scenario = new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
            {
                new ScenarioFile("../escape", new byte[] { 1 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 2 }),
            });
            var host = Host();
            Handshake(host);

            var effects = host.HandleMessage(1, new ScenarioRequestMessage(null));
            Assert.Empty(Messages<ScenarioMessage>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("DisallowedName"));
        }

        [Fact]
        public void Host_ThatReSavedBetweenOfferAndRequest_SendsTheCurrentSaveAndSaysSo()
        {
            bridge.Scenario = Save();
            var host = Host();
            Handshake(host);

            var stale = Save().Digest;
            bridge.Scenario = Save(content: "a different combat");

            var effects = host.HandleMessage(1, new ScenarioRequestMessage(stale));

            // Always make progress: the receiver validates against the digest on
            // the Scenario message itself, so serving the current save is both
            // simpler and never leaves the peer with nothing.
            var sent = Messages<ScenarioMessage>(effects).Single();
            Assert.Equal(bridge.Scenario.Digest, sent.Digest);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("but the bytes digest to"));
        }

        [Fact]
        public void Host_IgnoresARequestFromAnUnregisteredPeer()
        {
            bridge.Scenario = Save();
            var host = Host();
            Assert.Empty(host.HandleMessage(99, new ScenarioRequestMessage(null)));
        }

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
