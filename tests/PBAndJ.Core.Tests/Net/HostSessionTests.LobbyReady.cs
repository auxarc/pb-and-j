using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Lobby select, ready and unready: the versioned selection, what clears a
    // readiness, and how the lobby barrier fills and empties.
    //
    // This is the second half of the original's longest section, whose banner reads
    // 'sealing the lobby once the campaign starts (M11e)' and now stands over the
    // first half in .Lobby.cs. Nothing here is about sealing; it is the machinery the
    // seal protects.
    //
    // It also holds the tests for the roster BROADCAST that a lobby change causes.
    // The separate .Roster.cs is a different subject: the LobbyRoster property the
    // screen reads (M11c).
    //
    // LobbyHost, LobbyState and Select are called most from this file but not only
    // from it -- 14 of 24, 8 of 10 and 18 of 27 call sites -- so all three are shared
    // fixture in the primary. LobbyHost is worth a note: it was declared under the
    // lobby (M11a) banner and not one of that section's four tests used it.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        [Fact]
        public void LobbySelect_ClearsEveryExistingReady()
        {
            // The whole reason the selection is versioned: nobody agreed to
            // the new save just because they agreed to the old one.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(1, new LobbyReadyMessage(1));
            // Satisfaction is now consumed the instant it happens: the load
            // fires and the agreement is spent, so the barrier reads unsatisfied
            // rather than staying armed. M11d.
            Assert.True(host.LoadInFlight);

            host.Handle(Select("pbj_other").Event);

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbySelect_WithTheSameSaveAgain_StillClearsReady()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            host.Handle(Select().Event);

            Assert.Equal(2, host.Selection.Version);
            Assert.Equal(0, host.LobbyReadyCount);
        }

        [Fact]
        public void LocalLobbyReady_WithNoSaveSelected_IsRefused()
        {
            bridge.InCombat = false;
            var host = Host();

            var effects = host.Handle(new LocalLobbyReadyEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no save selected"));
        }

        [Fact]
        public void LocalLobbyReady_OutsideTheLobby_IsRefused()
        {
            var host = WithPeer();
            var effects = host.Handle(new LocalLobbyReadyEvent());
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not in the lobby"));
        }

        [Fact]
        public void LocalLobbyReady_MarksTheHostAndBroadcasts()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            var effects = host.Handle(new LocalLobbyReadyEvent());

            // Host alone in the lobby, so its own ready fills the barrier — and
            // filling it now fires the load, which spends the agreement.
            Assert.True(host.LoadInFlight);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Single(All<BeginLoadEffect>(effects));
        }

        [Fact]
        public void LocalLobbyUnready_WithdrawsIt()
        {
            // Two participants, so the host's own ready does not fill the barrier
            // and immediately spend itself on a load.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            var effects = host.Handle(new LocalLobbyUnreadyEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(LobbyState(effects).Peers[0].Ready);
        }

        [Fact]
        public void LocalLobbyUnready_WhenNotReady_DoesNothing()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Empty(host.Handle(new LocalLobbyUnreadyEvent()));
        }

        [Fact]
        public void LocalLobbyUnready_OutsideTheLobby_DoesNothing()
        {
            Assert.Empty(WithPeer().Handle(new LocalLobbyUnreadyEvent()));
        }

        [Fact]
        public void Handshake_AddsThePeerToTheLobbyAndTellsEveryone()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            var effects = host.HandleMessage(1, GoodHello());

            Assert.Equal(2, host.LobbyParticipantCount);
            var state = LobbyState(effects);
            Assert.Equal(2, state.Peers.Count);
            Assert.Equal("host", state.Peers[0].Name);
            Assert.Equal("ally", state.Peers[1].Name);
            // The newcomer learns the selection from the same message.
            Assert.Equal("pbj_campaign", state.SaveKey);
        }

        [Fact]
        public void Handshake_MidLobby_UnfillsAnAlreadySatisfiedBarrier()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.True(host.LoadInFlight);

            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            host.HandleMessage(1, GoodHello());

            // They have not agreed to anything yet.
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbyReady_FromAPeer_FillsTheBarrier()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LobbyIsSatisfied);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("everyone has agreed"));
            // ...and that agreement is spent immediately on the load it was for.
            Assert.True(host.LoadInFlight);
            Assert.Single(All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>());
        }

        [Fact]
        public void LobbyReady_ForAStaleSelection_IsIgnored()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(Select("pbj_other").Event);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("the save has changed since"));
        }

        [Fact]
        public void LobbyReady_ForASelectionAheadOfTheHost_ResendsTheState()
        {
            // Nothing can legitimately put a peer ahead — only the host mints a
            // selection version and it never rewinds. So this is a misbehaving
            // or buggy peer, and the answer is the truth rather than a kick.
            var host = LobbyHost();
            host.Handle(Select().Event);

            var effects = host.HandleMessage(1, new LobbyReadyMessage(99));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.IsType<LobbyStateMessage>(Single<SendEffect>(effects).Message);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("claims lobby selection 99"));
        }

        [Fact]
        public void LobbyReady_WhileTheHostIsInCombat_IsIgnored()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new LobbyReadyMessage(0));
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("host is not in the lobby"));
        }

        [Fact]
        public void LobbyReady_BeforeHello_DisconnectsThem()
        {
            bridge.InCombat = false;
            var host = Host();
            var effects = host.HandleMessage(9, new LobbyReadyMessage(0));
            Assert.Equal("lobby ready before hello", Single<DisconnectEffect>(effects).Reason);
        }

        [Fact]
        public void LobbyUnready_BeforeHello_DisconnectsThem()
        {
            bridge.InCombat = false;
            var host = Host();
            var effects = host.HandleMessage(9, new LobbyUnreadyMessage(0));
            Assert.Equal("lobby unready before hello", Single<DisconnectEffect>(effects).Reason);
        }

        [Fact]
        public void LobbyUnready_WithdrawsAPeerReady()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.HandleMessage(1, new LobbyReadyMessage(1));
            Assert.Equal(1, host.LobbyReadyCount);

            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("lobby unready from #1"));
        }

        [Fact]
        public void LobbyUnready_ForAnotherSelection_IsIgnored()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.HandleMessage(1, new LobbyReadyMessage(1));

            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(0));

            Assert.Equal(1, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not the current selection"));
        }

        [Fact]
        public void LobbyUnready_WhenNotReady_IsANoOpNotAFault()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            var effects = host.HandleMessage(1, new LobbyUnreadyMessage(1));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(0, host.LobbyReadyCount);
        }

        [Fact]
        public void PeerLeaving_CanSatisfyTheLobbyBarrier()
        {
            // The case a ready-only check misses entirely: the last unready
            // member simply leaves. Same shape as the turn barrier's, and in
            // M11d this is the trigger, not just a log line.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LobbyIsSatisfied);

            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("everyone has agreed"));
            // And in M11d that is the trigger: filling by subtraction loads too.
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void PeerLeaving_BroadcastsTheShrunkenRoster()
        {
            var host = LobbyHost();
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Single(LobbyState(effects).Peers);
            Assert.Equal(1, host.LobbyParticipantCount);
        }

        [Fact]
        public void PeerKickedForAProtocolViolation_BroadcastsTheShrunkenRoster()
        {
            // The kick paths never reach HandleDisconnect — by the time the
            // socket closes, the registry entry is gone and it returns early.
            // Without a broadcast here the departed peer haunts every other
            // client's lobby.
            var host = LobbyHost();
            var effects = host.HandleMessage(1, new WelcomeMessage(3, "s", 1, "h", null, 0, null));

            Assert.Single(LobbyState(effects).Peers);
            Assert.Equal(1, host.LobbyParticipantCount);
        }

        [Fact]
        public void PeerKickedForADuplicateHello_BroadcastsTheShrunkenRoster()
        {
            var host = LobbyHost();
            var effects = host.HandleMessage(1, GoodHello());
            Assert.Single(LobbyState(effects).Peers);
        }

        [Fact]
        public void Rejoin_PutsThePeerBackInTheLobbyRoster()
        {
            // Reconnect holds exist to reserve UNITS, so they only happen in
            // combat — a peer that drops from the lobby is simply gone. This
            // therefore drops mid-combat and returns, which is the only way
            // HandleRejoin runs at all.
            var host = WithTickedPeer(out var token);
            host.Handle(new CombatEnteredEvent());
            host.Handle(new PeerDisconnectedEvent(1, "dropped"));
            Assert.Equal(1, host.LobbyParticipantCount);

            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, Rejoin(token, claimedPeerId: 1));

            Assert.Equal(2, host.LobbyParticipantCount);
            var state = LobbyState(effects);
            Assert.Equal(2, state.Peers.Count);
            // Rebound onto the new peer id, like everything else keyed on it.
            Assert.Equal(2, state.Peers[1].PeerId);
        }

        [Fact]
        public void CombatExited_ClearsLobbyReadinessAndAdvancesTheSelection()
        {
            // Otherwise everyone comes out of the fight already "ready", and a
            // LobbyReady still in flight from before it would be counted.
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            Assert.True(host.LoadInFlight);

            bridge.InCombat = true;
            host.Handle(new CombatEnteredEvent());
            bridge.InCombat = false;
            var effects = host.Handle(new CombatExitedEvent());

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
            // The save survives; only the agreement to it is withdrawn.
            Assert.Equal("pbj_campaign", host.Selection.SaveKey);
            // 1 for the select, 2 when the load fired and spent the agreement,
            // 3 for leaving combat. Every consumer of an agreement advances it.
            Assert.Equal(3, host.Selection.Version);
            Assert.Equal(3, LobbyState(effects).SelectionVersion);
        }

        [Fact]
        public void CombatExited_MakesAnInFlightLobbyReadyStale()
        {
            bridge.InCombat = false;
            var host = LobbyHost();
            host.Handle(Select().Event);
            bridge.InCombat = true;
            host.Handle(new CombatEnteredEvent());
            bridge.InCombat = false;
            host.Handle(new CombatExitedEvent());

            // Sent before the fight ended, arriving after.
            var effects = host.HandleMessage(1, new LobbyReadyMessage(1));

            Assert.Equal(0, host.LobbyReadyCount);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("the save has changed since"));
        }

        [Fact]
        public void LobbyState_DuringCombat_StillTracksTheRoster()
        {
            // Broadcast even while the lobby is dormant: suppressing it would
            // leave every client's roster stale at exactly the moment combat
            // ends and the lobby matters again.
            var host = WithPeer();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            var effects = host.HandleMessage(2, GoodHello("ally2"));

            Assert.Equal(3, LobbyState(effects).Peers.Count);
            // ...but the barrier says nothing while it is not in play.
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("lobby "));
        }
    }
}
