using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The lobby (M11a), the counts the screen derives from it (M11c), and the
    // synchronised load it ends in (M11d).
    // All three share a part because InLobby is called from all of them, and it
    // calls Lobby, which calls CampaignSave. Separating the load would have
    // pushed InLobby into the shared fixture and those two after it, for three
    // helpers hoisted and one more file.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- lobby (M11a) ---

        /// <summary>
        /// The campaign save a lobby selects in these tests. Its real digest is
        /// used in <see cref="Lobby"/> rather than a made-up string, because since
        /// M11e readying is gated on actually holding the selected save and that
        /// check compares digests.
        /// </summary>
        private static ScenarioPayload CampaignSave() =>
            new ScenarioPayload("pbj_campaign", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2, 3 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 4 }),
            });

        private static LobbyStateMessage Lobby(
            int version = 1, string? saveKey = "pbj_campaign", bool allyReady = false) =>
            new LobbyStateMessage(version, saveKey, CampaignSave().Digest, new[]
            {
                new LobbyPeerState(0, "host", true),
                new LobbyPeerState(1, "ally", allyReady),
            });

        /// <summary>
        /// A welcomed client that has been told about a selected save <em>and</em>
        /// holds it — the ordinary case once M11e's transfer has run, and the only
        /// state from which readying is legitimate.
        /// </summary>
        private ClientSession InLobby(int version = 1)
        {
            bridge.ScenariosByKey["pbj_campaign"] = CampaignSave();
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(version));
            return client;
        }

        [Fact]
        public void LobbyState_IsUnknownBeforeTheHostSendsOne()
        {
            // -1, not 0: "never told" must be distinguishable from "the host has
            // not picked yet", which is version 0.
            var client = Welcomed();
            Assert.Equal(-1, client.LobbySelectionVersion);
            Assert.Null(client.LobbySaveKey);
            Assert.Empty(client.LobbyRoster);
            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LobbyState_IsTakenWholesale()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId, Lobby(allyReady: true));

            Assert.Equal(1, client.LobbySelectionVersion);
            Assert.Equal("pbj_campaign", client.LobbySaveKey);
            Assert.Equal(CampaignSave().Digest, client.LobbySaveDigest);
            Assert.Equal(2, client.LobbyRoster.Count);
            Assert.True(client.LobbyRoster[1].Ready);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2/2 ready"));
        }

        [Fact]
        public void LobbyState_ForANewSelection_ClearsOurReady()
        {
            // The host cleared everyone when it changed the save, so our flag
            // has to follow or the screen claims a ready the host does not hold.
            var client = InLobby();
            client.Handle(new LocalLobbyReadyEvent());
            Assert.True(client.LobbyReadySent);

            client.HandleMessage(ClientSession.HostConnectionId, Lobby(version: 2));

            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LobbyState_ForTheSameSelection_LeavesOurReadyAlone()
        {
            var client = InLobby();
            client.Handle(new LocalLobbyReadyEvent());

            // A refresh caused by somebody else joining, not by a new save.
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(version: 1, allyReady: true));

            Assert.True(client.LobbyReadySent);
        }

        [Fact]
        public void LocalLobbyReady_SendsOurAgreementForTheHostsSelection()
        {
            var client = InLobby(version: 4);
            var effects = client.Handle(new LocalLobbyReadyEvent());

            var ready = Assert.IsType<LobbyReadyMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(4, ready.SelectionVersion);
            Assert.True(client.LobbyReadySent);
        }

        [Fact]
        public void LocalLobbyReady_WithoutTheSelectedSaveYet_IsRefused()
        {
            // M11e's promise: readying means "I can load this". A peer that readies
            // without the bytes would report Unavailable when the host fires the
            // load, and the load barrier completes on failure reports — so everyone
            // else enters the campaign and this peer is stranded, with no way back
            // in once the lobby seals.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(4));

            var effects = client.Handle(new LocalLobbyReadyEvent());

            Assert.Empty(All<SendEffect>(effects));
            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LocalLobbyReady_WithASameNamedButDifferentSave_IsRefused()
        {
            // Same name, different contents is the silent-divergence case: everyone
            // loads "their" copy and the campaigns drift apart with nothing to
            // notice. Hence the digest comparison rather than a name check.
            bridge.ScenariosByKey["pbj_campaign"] = new ScenarioPayload("pbj_campaign", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 9, 9, 9 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 4 }),
            });
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(4));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalLobbyReadyEvent())));
            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LocalLobbyReady_WhenTheLobbyPublishedNoDigest_AcceptsTheSaveByName()
        {
            // A selection can reach us before its digest does. Refusing outright
            // would wedge a lobby that is otherwise fine, so the name carries it
            // until the digest arrives and the host re-offers on every selection.
            bridge.ScenariosByKey["pbj_campaign"] = CampaignSave();
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new LobbyStateMessage(
                2, "pbj_campaign", null, new[] { new LobbyPeerState(0, "host", true) }));

            Assert.IsType<LobbyReadyMessage>(
                Single<SendEffect>(client.Handle(new LocalLobbyReadyEvent())).Message);
        }

        [Fact]
        public void LocalLobbyReady_BeforeAnyLobbyState_IsRefused()
        {
            var client = Welcomed();
            var effects = client.Handle(new LocalLobbyReadyEvent());

            Assert.Empty(All<SendEffect>(effects));
            Assert.False(client.LobbyReadySent);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no lobby state received yet"));
        }

        [Fact]
        public void LocalLobbyReady_WhenTheHostHasPickedNothing_IsRefused()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(version: 0, saveKey: null));

            var effects = client.Handle(new LocalLobbyReadyEvent());

            Assert.Empty(All<SendEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("has not picked a save"));
        }

        [Fact]
        public void LocalLobbyReady_WorksEvenWhenOurOwnGameIsInCombat()
        {
            // The defect this guards against: HandleWelcome sets the state from
            // the CLIENT'S OWN bridge.InCombat, so joining while your local game
            // is mid-combat lands you in Planning. A state-based guard would
            // refuse you the lobby forever while holding a valid LobbyState —
            // and no harness test could catch it, since the scripted bridge is
            // never in combat.
            bridge.InCombat = true;
            bridge.ScenariosByKey["pbj_campaign"] = CampaignSave();
            var client = Welcomed();
            Assert.Equal(ClientSessionState.Planning, client.State);

            client.HandleMessage(ClientSession.HostConnectionId, Lobby());
            var effects = client.Handle(new LocalLobbyReadyEvent());

            Assert.IsType<LobbyReadyMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void LocalLobbyUnready_WithdrawsIt()
        {
            var client = InLobby(version: 4);
            client.Handle(new LocalLobbyReadyEvent());

            var effects = client.Handle(new LocalLobbyUnreadyEvent());

            var unready = Assert.IsType<LobbyUnreadyMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(4, unready.SelectionVersion);
            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LocalLobbyUnready_WithoutHavingReadied_SendsNothing()
        {
            // Withdrawing what was never given is a no-op, not an unlock —
            // the same gate submittedThisTurn provides for the turn barrier.
            Assert.Empty(InLobby().Handle(new LocalLobbyUnreadyEvent()));
        }

        [Fact]
        public void LocalLobbySelect_IsRefusedAndSaysSo()
        {
            // Silent refusal is the M10c connect-screen bug; say it out loud.
            var effects = InLobby().Handle(new LocalLobbySelectEvent("pbj_mine", null));

            Assert.Empty(All<SendEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("only the host picks"));
        }

        [Fact]
        public void CombatStart_ClearsOurLobbyReady()
        {
            // The host drops lobby readies during combat, so a flag surviving
            // into the fight would disagree with every roster we are sent.
            var client = InLobby();
            client.Handle(new LocalLobbyReadyEvent());

            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));

            Assert.False(client.LobbyReadySent);
        }

        [Fact]
        public void LobbyReadyFromTheHost_IsAProtocolViolation()
        {
            // Client-to-host messages must never arrive downward.
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId, new LobbyReadyMessage(1));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void LobbyUnreadyFromTheHost_IsAProtocolViolation()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new LobbyUnreadyMessage(1));
            Assert.Equal(ClientSessionState.Faulted, client.State);
        }

        [Fact]
        public void LobbyState_BeforeWelcome_IsAProtocolViolation()
        {
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby());
            Assert.Equal(ClientSessionState.Faulted, client.State);
        }

        // --- the synchronised load (M11d) ---

        [Fact]
        public void LobbyLoad_ForTheVersionWeHold_BeginsTheLoad()
        {
            var client = InLobby();
            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new LobbyLoadMessage(1, "pbj_campaign", "abc"));

            var begin = Single<BeginLoadEffect>(effects);
            Assert.Equal("pbj_campaign", begin.SaveKey);
            Assert.Equal(1, begin.SelectionVersion);
            Assert.Equal(1, client.LoadBegunVersion);
        }

        [Fact]
        public void LobbyLoad_ForAVersionWeDoNotHold_IsIgnored()
        {
            // The host advances the selection when it fires, and broadcasts the
            // new LobbyState first so this check passes. If it ever does not, a
            // refusal here is the right failure — loading a save the lobby has
            // moved on from is worse than not loading.
            var client = InLobby();
            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new LobbyLoadMessage(9, "pbj_other", null));

            Assert.Empty(All<BeginLoadEffect>(effects));
            Assert.Equal(-1, client.LoadBegunVersion);
        }

        [Fact]
        public void LobbyLoad_Repeated_IsIgnoredTheSecondTime()
        {
            // A load tears the campaign down and is not repeatable. The host's
            // edge trigger should make a duplicate unreachable, but the two
            // guards fail independently and the cost here is the same lost game.
            var client = InLobby();
            client.HandleMessage(ClientSession.HostConnectionId, new LobbyLoadMessage(1, "pbj_campaign", null));
            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new LobbyLoadMessage(1, "pbj_campaign", null));

            Assert.Empty(All<BeginLoadEffect>(effects));
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void LoadFinished_ReportsToTheHost(LoadOutcome outcome)
        {
            var client = InLobby();
            var effects = client.Handle(new LoadFinishedEvent(1, outcome));

            var sent = Assert.IsType<LobbyLoadedMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(1, sent.SelectionVersion);
            Assert.Equal(outcome, sent.Outcome);
        }

        // --- lobby counts, derived for the screen (M11c) ---

        [Fact]
        public void LobbyCounts_BeforeAnyStateArrives_AreZeroAndUnsatisfied()
        {
            var client = Welcomed();
            Assert.Equal(0, client.LobbyReadyCount);
            Assert.Equal(0, client.LobbyParticipantCount);

            // Not "everyone in an empty lobby has agreed" — the same reason
            // LobbyBarrier.IsSatisfied requires participants.
            Assert.False(client.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbyCounts_ComeFromTheRosterTheHostSent()
        {
            var client = InLobby();
            Assert.Equal(2, client.LobbyParticipantCount);
            Assert.Equal(1, client.LobbyReadyCount);
            Assert.False(client.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbyIsSatisfied_WhenTheRosterSaysEveryoneAgreed()
        {
            // The roster carries every participant and its flag, so this is the
            // host's own LobbyBarrier answer recomputed from the host's broadcast
            // rather than a second opinion.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(allyReady: true));
            Assert.Equal(2, client.LobbyReadyCount);
            Assert.True(client.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbyCounts_FollowTheLatestState()
        {
            // A client that kept a count across a roster change would show a
            // readiness that no longer exists.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(allyReady: true));
            client.HandleMessage(ClientSession.HostConnectionId, Lobby(version: 2));
            Assert.Equal(1, client.LobbyReadyCount);
            Assert.False(client.LobbyIsSatisfied);
        }
    }
}
