using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ClientSessionTests
    {
        private readonly FakeGameBridge bridge = new FakeGameBridge();

        private ClientSession Client() => new ClientSession("ally", "0.2.0", bridge);

        private static WelcomeMessage Welcome(int turn = 3) =>
            new WelcomeMessage(PbjProtocol.Version, "7f3a91", 1, "host",
                new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") }, turn, "tok");

        /// <summary>A client that has completed the handshake.</summary>
        private ClientSession Welcomed(int turn = 3)
        {
            var client = Client();
            client.Start();
            client.HandleMessage(ClientSession.HostConnectionId, Welcome(turn));
            return client;
        }

        private static T Single<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>().Single();

        private static IEnumerable<T> All<T>(IEnumerable<PbjEffect> effects) where T : PbjEffect =>
            effects.OfType<T>();

        // --- the base mirror (M12a) ---

        [Fact]
        public void BasePosition_BecomesAMirrorEffect()
        {
            var client = Welcomed();

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new BasePositionMessage(1024.5f, -37.25f));

            var mirror = Single<MirrorBaseEffect>(effects);
            Assert.Equal(1024.5f, mirror.X);
            Assert.Equal(-37.25f, mirror.Z);
        }

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

        // --- construction and handshake ---

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void Constructor_WithBlankPlayerName_Throws(string? name)
        {
            var ex = Assert.Throws<ArgumentException>(() => new ClientSession(name!, "v", bridge));
            Assert.Equal("playerName", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullBridge_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new ClientSession("ally", "v", null!));
            Assert.Equal("bridge", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNullModVersion_IsAccepted()
        {
            var hello = (HelloMessage)Single<SendEffect>(new ClientSession("ally", null!, bridge).Start()).Message;
            Assert.Equal(string.Empty, hello.ModVersion);
        }

        [Fact]
        public void Start_SendsHelloWithProtocolAndName()
        {
            var hello = Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
            Assert.Equal(PbjProtocol.Magic, hello.Magic);
            Assert.Equal(PbjProtocol.Version, hello.ProtocolVersion);
            Assert.Equal("0.2.0", hello.ModVersion);
            Assert.Equal("ally", hello.PlayerName);
        }

        [Fact]
        public void Handle_PeerConnected_SendsHello()
        {
            var effects = Client().Handle(new PeerConnectedEvent(0, "host"));
            Assert.IsType<HelloMessage>(Single<SendEffect>(effects).Message);
        }

        [Fact]
        public void HandleMessage_Welcome_StoresIdentityAndEntersPlanning()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, Welcome());

            Assert.Equal(1, client.PeerId);
            Assert.Equal("7f3a91", client.SessionId);
            Assert.Equal("host", client.HostName);
            Assert.Equal(3, client.Turn);
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("welcome | peer #1"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2 participants"));
        }

        [Fact]
        public void HandleMessage_Welcome_WhenHostNotInCombat_EntersLobby()
        {
            bridge.InCombat = false;
            Assert.Equal(ClientSessionState.Lobby, Welcomed().State);
        }

        [Fact]
        public void HandleMessage_Welcome_Twice_FaultsAndDisconnects()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, Welcome());
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Theory]
        [InlineData(RejectReason.BadMagic)]
        [InlineData(RejectReason.VersionMismatch)]
        [InlineData(RejectReason.SessionFull)]
        [InlineData(RejectReason.DuplicateName)]
        [InlineData(RejectReason.InvalidName)]
        [InlineData(RejectReason.NotAcceptingPeers)]
        public void HandleMessage_Reject_LogsReasonAndFaults(RejectReason reason)
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new RejectMessage(reason, "nope"));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains(reason.ToString()));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Rejection_AfterARefusal_IsRetainedSoTheScreenCanSayWhichThingWasWrong()
        {
            // The reason used to be logged and dropped. A connect screen that can
            // only say "failed" sends someone to check their firewall when the
            // real answer is that they typed the passphrase wrong.
            var client = Client();
            client.Start();
            client.HandleMessage(0, new RejectMessage(RejectReason.BadPassphrase, "nope"));

            Assert.Equal(RejectReason.BadPassphrase, client.Rejection);
        }

        [Fact]
        public void Rejection_BeforeAnyRefusal_IsNullRatherThanNone()
        {
            // None is a real RejectReason value, so it cannot double as "no
            // refusal has happened" — a screen reading it would announce one.
            var client = Client();
            client.Start();

            Assert.Null(client.Rejection);
        }

        [Fact]
        public void Rejection_AfterAWelcome_StaysNull()
        {
            Assert.Null(Welcomed().Rejection);
        }

        [Fact]
        public void HandleMessage_BeforeWelcome_Faults()
        {
            var client = Client();
            client.Start();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("before Welcome"));
        }

        [Fact]
        public void HandleMessage_WithClientOnlyMessage_Faults()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new ReadyMessage(3, null));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.Single(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void HandleMessage_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Client().HandleMessage(0, null!));
            Assert.Equal("message", ex.ParamName);
        }

        // --- roster ---

        // --- assignments ---

        [Fact]
        public void HandleMessage_Assignments_StoresOnlyItsOwnUnits()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a", "unit_c" }),
                new PeerAssignment(1, new[] { "unit_b" }),
            }));

            Assert.Equal(new[] { "unit_b" }, client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control: unit_b"));
        }

        [Fact]
        public void HandleMessage_Assignments_WithNoUnitsForUs_SaysSo()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a" }),
            }));

            Assert.Empty(client.OwnedUnits);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("you control no units"));
        }

        [Fact]
        public void HandleMessage_Assignments_ReplacesPreviousOwnership()
        {
            var client = Welcomed();
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_b" }) }));
            client.HandleMessage(0, new AssignmentsMessage(new[] { new PeerAssignment(1, new[] { "unit_c" }) }));
            Assert.Equal(new[] { "unit_c" }, client.OwnedUnits);
        }

        [Fact]
        public void OwnedUnits_IsEmptyBeforeAnyAssignment()
        {
            Assert.Empty(Welcomed().OwnedUnits);
        }

        [Fact]
        public void HandleMessage_PeerJoined_Logs()
        {
            Assert.Contains("#2", Single<LogEffect>(Welcomed().HandleMessage(0, new PeerJoinedMessage(2, "ally2"))).Line);
        }

        [Fact]
        public void HandleMessage_PeerLeft_Logs()
        {
            Assert.Contains("peer left: #2", Single<LogEffect>(
                Welcomed().HandleMessage(0, new PeerLeftMessage(2, "ally2"))).Line);
        }

        // --- the turn cycle ---

        [Fact]
        public void Handle_LocalReady_SendsReadyWithCapturedOrders()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_c", 0f, 2f));
            var client = Welcomed();
            var effects = client.Handle(new LocalReadyEvent());

            var ready = Assert.IsType<ReadyMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(3, ready.Turn);
            Assert.Single(ready.Orders);
            Assert.Equal("unit_c", ready.Orders[0].OwnerName);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        // --- the Ready batch is filtered to what we own (found in the stage 2 run) ---
        //
        // A client's local ECS holds the enemy AI's planned actions too, and they
        // do not carry the AIAction tag there — the first two-game turn submitted
        // 13 enemy orders alongside 3 of the host's, all rejected. Harmless, but it
        // wastes the wire, eats the 256-order cap and buries genuine rejections.

        /// <summary>A welcomed client that has been dealt <paramref name="units"/>.</summary>
        private ClientSession Assigned(params string[] units)
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId,
                new AssignmentsMessage(new[] { new PeerAssignment(1, units) }));
            return client;
        }

        [Fact]
        public void Handle_LocalReady_SendsOnlyOrdersForUnitsWeOwn()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_b", 0f, 2f));

            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Assigned("unit_a", "unit_b").Handle(new LocalReadyEvent())).Message);

            Assert.Equal(2, ready.Orders.Count);
            Assert.DoesNotContain(ready.Orders, o => o.OwnerName == "enemy_01");
        }

        [Fact]
        public void Handle_LocalReady_SaysWhatItDropped()
        {
            // No silent filtering: if a genuine order ever goes missing here, the
            // count is the only thing that would show it.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));

            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("1 order") && l.Line.Contains("not ours"));
        }

        [Fact]
        public void Handle_LocalReady_WithNothingToDrop_SaysNothingAboutIt()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("not ours"));
        }

        [Fact]
        public void Handle_LocalReady_BeforeAnyAssignment_SendsEverythingAndLetsTheHostDecide()
        {
            // The filter is a courtesy over a host-authoritative check, not a
            // second enforcement point. A client that has not been told what it
            // owns must defer rather than silently withhold a real order.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_c", 0f, 2f));
            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Welcomed().Handle(new LocalReadyEvent())).Message);
            Assert.Single(ready.Orders);
        }

        [Fact]
        public void Handle_LocalReady_WhenEveryOrderIsDropped_StillReadies()
        {
            // The barrier waits on every participant. Filtering everything away
            // must submit an empty batch, never skip the Ready — that would
            // deadlock the turn for both players.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));

            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            var ready = Assert.IsType<ReadyMessage>(Single<SendEffect>(effects).Message);

            Assert.Empty(ready.Orders);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        // An order with no owner needs no arm here: OrderPayload's constructor
        // already refuses a null or empty ownerName, which is a stronger place
        // for that guarantee to live than this filter.

        [Fact]
        public void Handle_LocalReady_MatchesOwnerNamesExactly()
        {
            // nameInternal is the join key everything else is addressed by; a
            // case-insensitive match here would let a near-miss through and turn
            // a clean rejection into a confusing one.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "Unit_A", 0f, 2f));
            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Assigned("unit_a").Handle(new LocalReadyEvent())).Message);
            Assert.Empty(ready.Orders);
        }

        [Fact]
        public void Handle_LocalReady_WhenNotPlanning_ProducesNoEffects()
        {
            var client = Client();
            Assert.Empty(client.Handle(new LocalReadyEvent()));
        }

        [Fact]
        public void Handle_LocalReady_Twice_SendsReadyAgain()
        {
            // Ready is idempotent by design; the host replaces the batch.
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));
            Assert.Single(All<SendEffect>(client.Handle(new LocalReadyEvent())));
        }

        [Fact]
        public void HandleMessage_TurnCommit_LocksExecutionAndWatches()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnCommit_ForUnexpectedTurn_ResyncsInsteadOfFaulting()
        {
            // The host can force-execute past us via scenario content.
            var client = Welcomed(turn: 3);
            client.HandleMessage(0, new TurnCommitMessage(7));
            Assert.Equal(7, client.Turn);
            Assert.Equal(ClientSessionState.Watching, client.State);
        }

        [Fact]
        public void HandleMessage_TurnComplete_UnlocksAndAdvancesToNextTurn()
        {
            var client = Welcomed();
            client.HandleMessage(0, new TurnCommitMessage(3));
            var effects = client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));

            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(4, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithMatchingDigest_LogsOk()
        {
            bridge.Digest = "3f9c1a04";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "3f9c1a04"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("digest 3f9c1a04 OK"));
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithDifferentDigest_LogsDiverged()
        {
            bridge.Digest = "bbbb2222";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "aaaa1111"));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("DIVERGED | host aaaa1111 | local bbbb2222"));
        }

        // --- un-ready ---

        [Fact]
        public void LocalUnready_AfterSubmitting_SendsUnreadyAndUnlocksExecution()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());

            var effects = client.Handle(new LocalUnreadyEvent());
            var unready = (UnreadyMessage)Single<SendEffect>(effects).Message;
            Assert.Equal(3, unready.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void LocalUnready_WithNothingSubmitted_SendsNothing()
        {
            var effects = Welcomed().Handle(new LocalUnreadyEvent());
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void LocalUnready_IsRefusedOnceTheHostHasCommitted()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        [Fact]
        public void LocalUnready_ThenReady_SubmitsAgain()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.Handle(new LocalUnreadyEvent());

            Assert.IsType<ReadyMessage>(Single<SendEffect>(client.Handle(new LocalReadyEvent())).Message);
        }

        // --- combat lifecycle from the host ---

        [Fact]
        public void CombatStart_MovesToPlanningAtTheHostsTurnAndUnlocks()
        {
            bridge.InCombat = false;
            var client = Welcomed(-1);
            Assert.Equal(ClientSessionState.Lobby, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(0, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatStart_ClearsAStaleSubmission()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        [Fact]
        public void CombatEnd_ReturnsToLobbyDropsOwnedUnitsAndUnlocks()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new AssignmentsMessage(new[]
            {
                new PeerAssignment(1, new[] { "unit_b" }),
            }));
            Assert.NotEmpty(client.OwnedUnits);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.Equal(ClientSessionState.Lobby, client.State);
            Assert.Empty(client.OwnedUnits);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatEnd_WhileWatchingStillUnlocks()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void OwnCombatEdges_AreLoggedButChangeNothing()
        {
            // A client's local InCombat is not authoritative — it learns combat
            // state from the host. These arms exist so the event does not throw.
            var client = Welcomed();

            foreach (var evt in new PbjInboundEvent[] { new CombatEnteredEvent(), new CombatExitedEvent() })
            {
                var effects = client.Handle(evt);
                Assert.Single(All<LogEffect>(effects));
                Assert.DoesNotContain(effects, e => !(e is LogEffect));
                Assert.Equal(ClientSessionState.Planning, client.State);
            }
        }

        [Fact]
        public void LocalCombatReady_IsLoggedRatherThanThrownOn()
        {
            // Only a host ships a fight, but the glue that writes one is armed by
            // an effect and answers frames later — long enough for the player to
            // have stopped hosting and joined someone else. Without this arm the
            // default case throws, and NetGlue.Pump turns a throw into
            // "networking stopped" for the rest of the process.
            var client = Welcomed();

            var effects = client.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            Assert.Single(All<LogEffect>(effects));
            Assert.DoesNotContain(effects, e => !(e is LogEffect));
        }

        [Fact]
        public void OrderApplied_IsIgnored()
        {
            // Clients never apply remote orders; the arm exists so the event,
            // which the shared runtime can produce, does not throw.
            Assert.Empty(Welcomed().Handle(new OrderAppliedEvent(1, 0, OrderApplyResult.Applied)));
        }

        // --- order results ---

        [Fact]
        public void OrderResult_IsReportedAndChangesNoState()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId, new OrderResultMessage(3, 2, new[]
            {
                new RejectedOrder(1, OrderApplyResult.NotOwned),
            }));

            Assert.Single(All<LogEffect>(effects));
            Assert.DoesNotContain(effects, e => !(e is LogEffect));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        // --- reconnect ---

        [Fact]
        public void Start_WithAResumeToken_SendsRejoinRatherThanHello()
        {
            var client = new ClientSession("ally", "0.2.0", bridge, "7f3a91", 1, "tok");
            var rejoin = Assert.IsType<RejoinMessage>(Single<SendEffect>(client.Start()).Message);

            Assert.Equal(PbjProtocol.Magic, rejoin.Magic);
            Assert.Equal(PbjProtocol.Version, rejoin.ProtocolVersion);
            Assert.Equal("ally", rejoin.PlayerName);
            Assert.Equal("7f3a91", rejoin.SessionId);
            Assert.Equal(1, rejoin.ClaimedPeerId);
            Assert.Equal("tok", rejoin.ResumeToken);
        }

        [Fact]
        public void Start_WithNoResumeToken_SendsHello()
        {
            Assert.IsType<HelloMessage>(Single<SendEffect>(Client().Start()).Message);
        }

        [Fact]
        public void Welcome_StoresTheResumeTokenForALaterReturn()
        {
            Assert.Equal("tok", Welcomed().ResumeToken);
        }

        // --- snapshot correction ---

        private static UnitSnapshot Snap(string name, float x = 1f) =>
            new UnitSnapshot(name, new Vec3(x, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 1f, false, 0f);

        [Fact]
        public void Snapshot_ClearsStaleLocalOrdersBeforeApplying()
        {
            // A client's planned orders never execute, so by turn 3 its timeline
            // is junk and CaptureLocalOrders would re-send orders already run.
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })).ToList();

            var clearAt = effects.FindIndex(e => e is ClearLocalOrdersEffect);
            var applyAt = effects.FindIndex(e => e is ApplySnapshotEffect);
            Assert.True(clearAt >= 0 && applyAt > clearAt);
        }

        [Fact]
        public void Snapshot_CarriesTheHostsDigestOnTheEffect()
        {
            var client = Welcomed();
            var apply = Single<ApplySnapshotEffect>(client.HandleMessage(ClientSession.HostConnectionId,
                new SnapshotMessage(3, "abc", new[] { Snap("unit_b") })));

            Assert.Equal(3, apply.Turn);
            Assert.Equal("abc", apply.ExpectedDigest);
            Assert.Equal("unit_b", Assert.Single(apply.Units).Name);
        }

        // --- keyframes (M6) ---

        private static KeyframesMessage Motion(int turn = 3) =>
            new KeyframesMessage(turn, 15f, 20f, new[]
            {
                new UnitTrack("unit_b", new[]
                {
                    new TransformKey(15f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(20f, new Vec3(9f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            });

        [Fact]
        public void Keyframes_StartPlaybackCarryingTheTurnAndTheWindow()
        {
            var play = Single<PlayKeyframesEffect>(
                Welcomed().HandleMessage(ClientSession.HostConnectionId, Motion()));

            Assert.Equal(3, play.Turn);
            Assert.Equal(15f, play.Capture.WindowStart);
            Assert.Equal(20f, play.Capture.WindowEnd);
            Assert.Equal("unit_b", Assert.Single(play.Capture.Tracks).Name);
        }

        [Fact]
        public void Keyframes_AreReported()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, Motion());
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("keyframes received"));
        }

        // Playback is presentation only, so receiving it must not move the
        // client's own idea of the turn or unlock anything.
        [Fact]
        public void Keyframes_ChangeNoSessionState()
        {
            var client = Welcomed();
            var before = client.State;
            var turn = client.Turn;

            client.HandleMessage(ClientSession.HostConnectionId, Motion());

            Assert.Equal(before, client.State);
            Assert.Equal(turn, client.Turn);
        }

        // A turn ending, a host vanishing or a session closing all leave a
        // playback mid-flight. Each one has to stop it, or units keep sliding
        // through whatever comes next.
        [Fact]
        public void CombatEnd_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void Bye_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().HandleMessage(ClientSession.HostConnectionId, new ByeMessage("done"));
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void AFaultingHost_StopsAnyPlaybackInFlight()
        {
            var effects = Welcomed().Handle(new TransportFailedEvent("socket died"));
            Assert.Single(All<StopKeyframesEffect>(effects));
        }

        [Fact]
        public void SnapshotApplied_WithAMatchingDigest_ReportsTheCorrectionVerified()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("corrected") && l.Line.Contains("OK"));
        }

        [Fact]
        public void SnapshotApplied_WithAMismatchedDigest_ReportsItLoudly()
        {
            var effects = Welcomed().Handle(new SnapshotAppliedEvent(3, 2, "abc", "def"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("STILL DIVERGED"));
        }

        [Fact]
        public void SnapshotApplied_ChangesNoState()
        {
            var client = Welcomed();
            client.Handle(new SnapshotAppliedEvent(3, 2, "abc", "abc"));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        // --- keepalive ---

        [Fact]
        public void Ping_IsAnsweredWithAMatchingPong()
        {
            var pong = Assert.IsType<PongMessage>(
                Single<SendEffect>(Welcomed().HandleMessage(ClientSession.HostConnectionId, new PingMessage(42))).Message);
            Assert.Equal(42, pong.Nonce);
        }

        [Fact]
        public void Ping_DuringTheHandshake_IsAnsweredRatherThanTreatedAsAViolation()
        {
            // Refusing would have the host reap a peer that is perfectly alive.
            var client = Client();
            client.Start();

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));
            Assert.IsType<PongMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(ClientSessionState.Handshaking, client.State);
        }

        [Fact]
        public void Ping_ChangesNoState()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(3, client.Turn);
        }

        [Fact]
        public void Tick_FirstOne_SeedsRatherThanJudging()
        {
            Assert.Empty(Welcomed().Handle(new TickEvent(9_999_999)));
        }

        [Fact]
        public void Tick_AfterTheHostTimeout_FaultsAndUnlocksExecution()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));

            var effects = client.Handle(new TickEvent(1000 + PbjProtocol.HostTimeoutSeconds));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Tick_BeforeTheHostTimeout_DoesNothing()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));

            Assert.Empty(client.Handle(new TickEvent(1000 + PbjProtocol.HostTimeoutSeconds - 1)));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        [Fact]
        public void AnyInboundMessage_KeepsTheHostAlive()
        {
            var client = Welcomed();
            client.Handle(new TickEvent(1000));
            client.Handle(new TickEvent(1025));
            client.HandleMessage(ClientSession.HostConnectionId, new PingMessage(1));

            Assert.Empty(client.Handle(new TickEvent(1040)));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }

        [Fact]
        public void HostTimeout_IsLongerThanThePeerTimeout()
        {
            // The host is the side that hitches, and a client fault is terminal.
            Assert.True(PbjProtocol.HostTimeoutSeconds > PbjProtocol.PeerTimeoutSeconds);
        }

        // --- loss of the host ---

        [Fact]
        public void Handle_TransportFailed_UnlocksExecutionAndFaults()
        {
            // The single most important client behaviour: a lost host must never
            // leave the local execute button permanently disabled.
            var client = Welcomed();
            client.HandleMessage(0, new TurnCommitMessage(3));
            var effects = client.Handle(new TransportFailedEvent("connection reset"));

            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_PeerDisconnected_UnlocksExecutionAndFaults()
        {
            var client = Welcomed();
            var effects = client.Handle(new PeerDisconnectedEvent(0, null));
            Assert.Equal(ClientSessionState.Faulted, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_Bye_ClosesCleanlyAndUnlocks()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new ByeMessage("host shutting down"));
            Assert.Equal(ClientSessionState.Closed, client.State);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_Bye_WithNoReason_StillCloses()
        {
            Assert.Equal(ClientSessionState.Closed,
                WithBye(null).State);

            ClientSession WithBye(string? reason)
            {
                var client = Welcomed();
                client.HandleMessage(0, new ByeMessage(reason));
                return client;
            }
        }

        [Fact]
        public void Handle_AfterClosed_ProducesNoEffects()
        {
            var client = Welcomed();
            client.HandleMessage(0, new ByeMessage("host shutting down"));
            Assert.Empty(client.Handle(new LocalReadyEvent()));
            Assert.Empty(client.HandleMessage(0, new TurnCommitMessage(4)));
        }

        [Fact]
        public void Handle_AfterFaulted_ProducesNoEffects()
        {
            var client = Welcomed();
            client.Handle(new TransportFailedEvent("gone"));
            Assert.Empty(client.Handle(new LocalReadyEvent()));
            Assert.Empty(client.HandleMessage(0, new TurnCommitMessage(4)));
        }

        // --- plumbing ---

        [Fact]
        public void Handle_TransportLog_ForwardsTheLine()
        {
            Assert.Equal("connected", Single<LogEffect>(Client().Handle(new TransportLogEvent("connected"))).Line);
        }

        [Fact]
        public void Handle_TransportLog_WithNoLine_LogsPlaceholder()
        {
            Assert.Equal("unknown", Single<LogEffect>(Client().Handle(new TransportLogEvent(null))).Line);
        }

        [Fact]
        public void Handle_PeerBytes_ProducesNoEffects()
        {
            Assert.Empty(Client().Handle(new PeerBytesEvent(0, new byte[] { 1 })));
        }

        [Fact]
        public void Handle_LocalTurnComplete_ProducesNoEffects()
        {
            // A client does not simulate, so its own execution-end hook carries
            // no authority — the host's TurnComplete drives the cycle.
            Assert.Empty(Welcomed().Handle(new LocalTurnCompleteEvent("d", null, null)));
        }

        [Fact]
        public void Handle_CommitOutcome_ProducesNoEffects()
        {
            Assert.Empty(Welcomed().Handle(new CommitOutcomeEvent(3, true)));
        }

        [Fact]
        public void ConnectedPeerIds_IsJustTheHost()
        {
            Assert.Equal(new[] { ClientSession.HostConnectionId }, Client().ConnectedPeerIds.ToArray());
        }

        [Fact]
        public void Handle_WithNullEvent_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Client().Handle(null!));
            Assert.Equal("evt", ex.ParamName);
        }

        [Fact]
        public void Handle_WithUnsupportedEventKind_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => Client().Handle(new UnsupportedEvent()));
        }

        private sealed class UnsupportedEvent : PbjInboundEvent
        {
            public override PbjInboundEventKind Kind => (PbjInboundEventKind)200;
        }

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

        // --- lobby counts, derived for the screen (M11c) ---

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
