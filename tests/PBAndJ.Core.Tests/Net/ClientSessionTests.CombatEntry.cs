using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Joining the host's fight (M12b): the offer, the scenario the client must
    // already hold or fetch, and the entry that follows.
    // CombatPayload is here rather than in the shared fixture because every one
    // of its call sites is in this part.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- joining the host's fight (M12b) ---

        [Fact]
        public void CombatOffer_WhenWeAlreadyHoldTheFight_LoadsItWithoutFetching()
        {
            var client = Welcomed();
            bridge.Scenario = CombatPayload();

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", bridge.Scenario.Digest, 4));

            var load = Single<BeginCombatLoadEffect>(effects);
            Assert.Equal("pbj_combat_test", load.SaveName);
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void CombatOffer_WhenWeHoldADifferentFight_FetchesItRatherThanLoadingStaleBytes()
        {
            // The scenario slot is rewritten at the start of every mission, so
            // holding the PREVIOUS fight under this exact name is the expected
            // case, not an exotic one. Only the digest tells them apart.
            var client = Welcomed();
            bridge.Scenario = CombatPayload();

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", "a-different-fight", 4));

            Assert.Empty(All<BeginCombatLoadEffect>(effects));
            Assert.IsType<ScenarioRequestMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void CombatOffer_FetchesByDIGEST_NotBySaveName()
        {
            // Regression, found by the first two-instance M8 playtest and NOT by
            // the test above, which asserted only that *a* request went out.
            //
            // ScenarioRequestMessage identifies a save by DIGEST -- deliberately,
            // so a peer never gets to name a file on the host's disk. This path
            // passed offer.SaveName instead. Both are string?, so nothing caught
            // it, and the failure is entirely remote: HostSession.ResolveRequested
            // compares the value against the scenario slot's digest, misses, and
            // falls through to "the lobby's campaign wins" -- so the client asked
            // for the fight and was sent the CAMPAIGN SAVE. It then never entered
            // combat, and the host dropped it after 120s and fought alone.
            //
            // Observed on the host as:
            //   refused scenario: sender claimed pbj_combat_test
            //     but the bytes digest to 32a3ae4e
            //   sent scenario 'pbj_fromsp' (62,076 bytes) to peer #1
            var client = Welcomed();
            bridge.Scenario = CombatPayload();

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", "the-fight-digest", 4));

            var request = Assert.IsType<ScenarioRequestMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal("the-fight-digest", request.Digest);
            Assert.NotEqual("pbj_combat_test", request.Digest);
        }

        [Fact]
        public void CombatOffer_IsNotRefusedTheWayAScenarioOfferWouldBe()
        {
            // The whole reason this is a separate message type. A client with
            // HostIsFighting set declines every ScenarioOffer -- which is exactly
            // the state it is in when the host ships a fight, so reusing M9's
            // offer here would deadlock the entry every single time.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(4));
            Assert.True(client.HostIsFighting);

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", "d1", 4));

            Assert.NotEmpty(effects);
        }

        [Fact]
        public void CombatBytes_ArriveAndAreLoadedRatherThanJustWritten()
        {
            // M9 deliberately never auto-loads a received scenario -- it tells the
            // player to. A combat entry is not a suggestion: the host is already
            // in the battle, waiting at a barrier.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", "d1", 4));
            var payload = CombatPayload();

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_combat_test", payload.Digest, payload.Files));

            Assert.Single(All<WriteScenarioEffect>(effects));
            Assert.Single(All<BeginCombatLoadEffect>(effects));
        }

        [Fact]
        public void ScenarioBytes_ForSomethingWeWereNotOfferedAsAFight_AreOnlyWritten()
        {
            var client = Welcomed();
            var payload = CombatPayload();

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_combat_test", payload.Digest, payload.Files));

            Assert.Single(All<WriteScenarioEffect>(effects));
            Assert.Empty(All<BeginCombatLoadEffect>(effects));
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void CombatLoadFinished_ReportsInEvenWhenItFailed(LoadOutcome outcome)
        {
            // Reported on failure too, and that is the point: the game's load
            // callback is success-only, so a client that says nothing costs the
            // host its entire timeout before the fight can start.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage("pbj_combat_test", "d1", 4));

            var effects = client.Handle(new CombatLoadFinishedEvent(outcome));

            var report = Assert.IsType<CombatEnteredMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(4, report.Turn);
            Assert.Equal(outcome, report.Outcome);
        }

        /// <summary>A well-formed fight payload, as the host would ship one.</summary>
        [Fact]
        public void ScenarioBytes_ForADifferentSaveThanTheFightWeWereOffered_AreOnlyWritten()
        {
            // A campaign transfer can land while a combat offer is outstanding.
            // Loading it because something was pending would drop the player into
            // the wrong save entirely.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId,
                new CombatOfferMessage(LobbySaveNames.ScenarioSlot, "d1", 4));
            var other = new ScenarioPayload("pbj_campaign", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 7, 7 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 8 }),
            });

            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new ScenarioMessage("pbj_campaign", other.Digest, other.Files));

            Assert.Single(All<WriteScenarioEffect>(effects));
            Assert.Empty(All<BeginCombatLoadEffect>(effects));
        }

        private static ScenarioPayload CombatPayload() =>
            new ScenarioPayload(LobbySaveNames.ScenarioSlot, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2, 3, 4 }),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[] { 5 }),
            });

        [Fact]
        public void BasePosition_IsMirroredWhateverTheSessionState()
        {
            // No ClientSessionState guard on purpose, and this is the trap it
            // avoids: HandleWelcome seeds that state from this machine's OWN
            // combat flag, so a peer who joined while their local game was
            // mid-fight lands in a state that says nothing true about the host.
            // Gating the mirror on it would freeze that player's map for the
            // session. The mirror is presentation and cannot desynchronise
            // anything, so it needs no such permission.
            var lobby = Welcomed();
            var fighting = Welcomed();
            fighting.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(1));

            Assert.Single(All<MirrorBaseEffect>(lobby.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f))));
            Assert.Single(All<MirrorBaseEffect>(fighting.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f))));
        }

        [Fact]
        public void BasePosition_BeforeTheHandshake_IsAProtocolViolationLikeAnythingElse()
        {
            // Not an exception for the mirror. The host controls ordering, so a
            // position arriving before Welcome means the far side is not the
            // protocol we think it is -- and the existing guard is the whole
            // reason a client can trust anything it is later told.
            var client = Client();
            client.Start();

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1f, 2f));

            Assert.Empty(All<MirrorBaseEffect>(effects));
            Assert.Single(All<DisconnectEffect>(effects));
        }
    }
}
