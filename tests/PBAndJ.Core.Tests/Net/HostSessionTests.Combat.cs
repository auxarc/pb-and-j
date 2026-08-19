using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Three sections of the original, and the only part assembled from non-adjacent ones:
    // combat edges and joining mid-execution (adjacent in the original), plus a peer that
    // joins mid-combat -- the 2026-08-03 defect -- which the author had placed much later,
    // after the lobby sections.
    //
    // InCombatWithPeer and SentTo are used only here.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- combat edges ---

        [Fact]
        public void CombatEntered_AnnouncesNothingYet()
        {
            // M12b re-sequenced this, and the reason is a deadlock rather than a
            // preference. CombatStart sets HostIsFighting on every client, and
            // HandleScenarioOffer refuses every offer while that is true -- so
            // announcing the fight before shipping it guarantees nobody can fetch
            // it. The announcement waits for the entry barrier.
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Empty(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void CombatReady_OffersTheFightRatherThanStartingIt()
        {
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            var offer = Assert.IsType<CombatOfferMessage>(Single<BroadcastEffect>(effects).Message);
            Assert.Equal("pbj_combat_test", offer.SaveName);
            Assert.Equal("d1", offer.Digest);
            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_OnceEveryoneIsIn_BroadcastsCombatStartThenAssignments()
        {
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();
            var broadcasts = All<BroadcastEffect>(effects).ToList();
            var startAt = broadcasts.FindIndex(b => b.Message is CombatStartMessage);
            var assignAt = broadcasts.FindIndex(b => b.Message is AssignmentsMessage);

            Assert.True(startAt >= 0, "the fight must be announced once everyone is in");
            Assert.True(assignAt > startAt, "Assignments must follow CombatStart immediately");
            Assert.Equal(0, ((CombatStartMessage)broadcasts[startAt].Message).Turn);
        }

        [Fact]
        public void CombatReady_WithNobodyConnected_StartsAtOnce()
        {
            bridge.InCombat = false;
            var host = Host();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_WhenTheFightCouldNotBeWritten_StartsAloneRatherThanHanging()
        {
            // Reachable: CanSave has refusals the glue's poll cannot outwait. The
            // session is lost either way, but the human at this machine is
            // already in a battle and cannot be left staring at it.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent(null, null)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_WhenAPeerNeverReports_StartsWithoutItAfterTheTimeout()
        {
            var host = InCombatWithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.LoadTimeoutSeconds + 1)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_WithANameButNoDigest_IsTreatedAsAFailedWrite()
        {
            // Half an answer is not an answer: without a digest a client cannot
            // tell this fight from the last one written to the same slot.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", null)).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_WithTwoPeers_WaitsForBoth()
        {
            bridge.InCombat = false;
            var host = WithPeer(maxPeers: 3);
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("ally2"));
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var first = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();

            Assert.DoesNotContain(All<BroadcastEffect>(first), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_AReportForAnAbandonedFight_IsIgnored()
        {
            // Same staleness rule every other barrier in this protocol has: a
            // report about a turn that is no longer the one being entered must
            // not count toward the current fight.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(99, LoadOutcome.Loaded)).ToList();

            Assert.Empty(effects);
        }

        [Fact]
        public void CombatEntered_AsksTheGlueToShipTheFight()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Single(All<ShipCombatEffect>(effects));
        }

        [Fact]
        public void CombatEntered_WithNobodyConnected_StillShipsTheFight()
        {
            // No peer-count shortcut, and the reason is the scenario slot rather
            // than the fight: OfferScenario hands that slot to every newcomer, so
            // a write skipped because nobody was listening would be offered to the
            // next peer to arrive -- last mission's fight, under this mission's
            // name. One synchronous save is the cheaper of the two.
            bridge.InCombat = false;
            var host = Host();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;

            var effects = host.Handle(new CombatEnteredEvent()).ToList();

            Assert.Single(All<ShipCombatEffect>(effects));
        }

        [Fact]
        public void CombatEntry_APeerThatCouldNotGetIn_IsDroppedRatherThanLeftInTheFight()
        {
            // The wedge this closes: the entry barrier stopped waiting for such a
            // peer, but the registry kept it -- so it was still dealt units and
            // the TURN barrier waited for it forever. Nobody could execute again.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Unavailable)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatEntry_APeerThatNeverReports_IsDroppedAtTheTimeout()
        {
            var host = InCombatWithPeer();
            host.Handle(new TickEvent(1000));
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new TickEvent(1000 + PbjProtocol.LoadTimeoutSeconds + 1)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void CombatEntry_WhenTheFightCouldNotBeWritten_DropsEveryoneRatherThanWedgingTheBarrier()
        {
            // Starting alone is right for the human already standing in a battle.
            // Keeping the peers connected while starting without them is not: they
            // were never offered a fight they could join, and every one of them
            // would hold the turn barrier shut.
            var host = InCombatWithPeer();

            var effects = host.Handle(new LocalCombatReadyEvent(null, null)).ToList();

            Assert.Contains(All<DisconnectEffect>(effects), d => d.PeerId == 1);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void CombatEntry_APeerThatDisconnectsMidEntry_DoesNotHoldTheFightUp()
        {
            // RemovePeer cleared every other barrier and not this one, so a peer
            // that dropped while loading the fight left the entry barrier waiting
            // on a closed socket for the full two minutes -- and the host stood in
            // the battle the whole time.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            var effects = host.Handle(new PeerDisconnectedEvent(1, "gone")).ToList();

            Assert.Contains(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        [Fact]
        public void CombatReady_AfterTheHostLeftTheFight_IsDropped()
        {
            // The glue disarms itself on the combat edge, so this should not
            // arrive -- and "should not" is exactly what the entry barrier
            // believed about late reports. Acting on it would announce a fight at
            // turn -1 from a host sitting in its lobby.
            var host = InCombatWithPeer();
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            var effects = host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1")).ToList();

            Assert.Empty(All<BroadcastEffect>(effects));
        }

        [Fact]
        public void CombatExited_MidEntry_CancelsIt()
        {
            // Otherwise a report arriving after the host abandoned the fight
            // completes the barrier and broadcasts CombatStart -- at turn -1, from
            // a host sitting in its lobby.
            var host = InCombatWithPeer();
            host.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            var effects = host.HandleMessage(1, new CombatEnteredMessage(0, LoadOutcome.Loaded)).ToList();

            Assert.DoesNotContain(All<BroadcastEffect>(effects), b => b.Message is CombatStartMessage);
        }

        /// <summary>A host in combat with one handshaken peer, ready to ship a fight.</summary>
        private HostSession InCombatWithPeer()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());
            return host;
        }

        [Fact]
        public void CombatEntered_MovesToPlanningAndResetsTheBarrier()
        {
            bridge.InCombat = false;
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(-1, null));
            bridge.InCombat = true;
            bridge.CurrentTurn = 0;
            host.Handle(new CombatEnteredEvent());

            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(0, host.Turn);
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void CombatExited_BroadcastsCombatEndAndUnlocksEveryone()
        {
            var host = WithPeer();
            bridge.InCombat = false;

            var effects = host.Handle(new CombatExitedEvent());
            // Two broadcasts now: CombatEnd, then the refreshed lobby — leaving
            // the fight puts everyone back in a lobby whose readiness is stale.
            Assert.Single(All<BroadcastEffect>(effects), e => e.Message is CombatEndMessage);
            Assert.Single(All<BroadcastEffect>(effects), e => e.Message is LobbyStateMessage);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Lobby, host.State);
        }

        [Fact]
        public void CombatExited_WhileAPeerSitsReady_StillUnlocksIt()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            Assert.Equal(0, host.ReadyCount);
            Assert.Empty(host.Assignments.PeerIds);
        }

        [Fact]
        public void CombatExited_WhileExecuting_LeavesExecutionUnlocked()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.InCombat = false;

            var effects = host.Handle(new CombatExitedEvent());
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Lobby, host.State);
        }

        // --- joining mid-execution ---

        [Fact]
        public void Hello_WhileExecuting_TellsTheNewPeerExecutionIsAlreadyUnderway()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("ally2"));

            var sent = All<SendEffect>(effects).Where(s => s.PeerId == 2).Select(s => s.Message).ToList();
            Assert.Contains(sent, m => m is WelcomeMessage);
            Assert.Contains(sent, m => m is TurnCommitMessage);
        }

        [Fact]
        public void Hello_WhilePlanning_SendsNoTurnCommit()
        {
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            Assert.DoesNotContain(All<SendEffect>(effects).Select(s => s.Message), m => m is TurnCommitMessage);
        }

        // --- a peer that joins mid-combat (the 2026-08-03 defect) ---

        private static IReadOnlyList<PbjMessage> SentTo(IEnumerable<PbjEffect> effects, int peerId) =>
            effects.OfType<SendEffect>().Where(e => e.PeerId == peerId).Select(e => e.Message).ToList();

        [Fact]
        public void Handshake_WhileInCombat_TellsTheNewcomerCombatIsHappening()
        {
            // Without this the peer never learns, HandleWelcome falls back to
            // reading its OWN combat flag, and its Execute is swallowed forever.
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            var start = SentTo(effects, 1).OfType<CombatStartMessage>().Single();
            Assert.Equal(host.Turn, start.Turn);
        }

        [Fact]
        public void Handshake_WhileInCombat_SendsCombatStartAfterTheScenarioOffer()
        {
            // CombatStart moves the client to Planning, and HandleScenarioOffer
            // ignores an offer unless it is in Lobby. Reversed, the joining peer
            // silently declines the very save it needs to play.
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            bridge.Scenario = new ScenarioPayload("pbj_combat_test", new[]
            {
                new ScenarioFile("content.zip", new byte[] { 1 }),
                new ScenarioFile("metadata.yaml", new byte[] { 2 }),
            });
            var sent = SentTo(host.HandleMessage(1, GoodHello()), 1);

            var offerAt = sent.ToList().FindIndex(m => m is ScenarioOfferMessage);
            var startAt = sent.ToList().FindIndex(m => m is CombatStartMessage);
            Assert.True(offerAt >= 0, "the scenario must still be offered");
            Assert.True(startAt > offerAt, "CombatStart must follow the scenario offer");
        }

        [Fact]
        public void Handshake_WhileExecuting_SendsCombatStartBeforeTurnCommit()
        {
            // The other side of the same constraint. TurnCommit locks the client
            // and moves it to Watching; a CombatStart arriving afterwards would
            // unlock it and leave it planning a turn that is already running.
            var host = Executing();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var sent = SentTo(host.HandleMessage(2, GoodHello("third")), 2).ToList();

            var startAt = sent.FindIndex(m => m is CombatStartMessage);
            var commitAt = sent.FindIndex(m => m is TurnCommitMessage);
            Assert.True(startAt >= 0, "CombatStart must be sent");
            Assert.True(commitAt > startAt, "TurnCommit must follow CombatStart");
        }

        [Fact]
        public void Handshake_OutOfCombat_SendsNoCombatStart()
        {
            var host = LobbyHost();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("third"));
            Assert.Empty(SentTo(effects, 2).OfType<CombatStartMessage>());
        }
    }
}
