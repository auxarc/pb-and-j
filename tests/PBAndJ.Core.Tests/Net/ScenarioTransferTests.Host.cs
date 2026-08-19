using System.Collections.Generic;
using System.Linq;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The host's half: which save it offers and when, and what it serves when a peer
    // asks. M11e widened both questions from the combat scenario to the combat
    // scenario or the lobby's campaign save, which is what the second banner marks.
    //
    // Host, GoodHello and Handshake live here rather than in the shared fixture:
    // outside this file only the end-to-end test touches any of them.
    //
    // Class-level XML doc lives only in ScenarioTransferTests.cs -- /// on a partial
    // part is concatenated by the compiler into one type entry.
    public partial class ScenarioTransferTests
    {
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
    }
}
