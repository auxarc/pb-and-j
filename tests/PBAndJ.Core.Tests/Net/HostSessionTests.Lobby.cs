using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The lobby (M11a), and the first half of sealing it once the campaign starts
    // (M11e): who may still get in after a load, and who may not.
    //
    // The M11e section was 467 lines -- over the 500 budget once a header is added --
    // and split at the seam between its two subjects: the door, here; the
    // ready/select machinery, in .LobbyReady.cs. The size budget is what forced the
    // question; the seam is where the subject actually changes.
    //
    // LoadedHost is called only here, by the three AfterTheCampaignLoads tests. The
    // prose that used to sit under this banner explained LobbyHost, so it travelled
    // with LobbyHost to HostSessionTests.cs rather than staying to describe a helper
    // that is no longer in this file.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- lobby (M11a) ---
        [Fact]
        public void Lobby_StartsWithNothingSelectedAndOnlyTheHostPresent()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Equal(LobbySelection.None.Version, host.Selection.Version);
            Assert.False(host.Selection.HasSave);
            Assert.Equal(1, host.LobbyParticipantCount);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void LobbySelect_AdvancesTheSelectionAndBroadcastsIt()
        {
            bridge.InCombat = false;
            var host = Host();

            var effects = host.Handle(Select().Event);

            Assert.Equal(1, host.Selection.Version);
            Assert.Equal("pbj_campaign", host.Selection.SaveKey);
            Assert.Equal("abc", host.Selection.SaveDigest);

            var state = LobbyState(effects);
            Assert.Equal(1, state.SelectionVersion);
            Assert.Equal("pbj_campaign", state.SaveKey);
            Assert.Equal("abc", state.SaveDigest);
        }

        [Fact]
        public void LobbySelect_WithNoKey_ClearsTheSelection()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(Select().Event);

            var effects = host.Handle(new LocalLobbySelectEvent(null, null));

            Assert.False(host.Selection.HasSave);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("lobby save cleared"));
        }

        [Fact]
        public void LobbySelect_OutsideTheLobby_IsIgnored()
        {
            // Mid-combat the save is already decided; changing it would be a
            // promise the session cannot keep.
            var host = WithPeer();
            var effects = host.Handle(Select().Event);

            Assert.Equal(LobbySelection.None.Version, host.Selection.Version);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("not in the lobby"));
            Assert.Empty(All<BroadcastEffect>(effects));
        }

        // --- sealing the lobby once the campaign starts (M11e) ---

        /// <summary>
        /// Drives a host all the way through a completed synchronised load, which
        /// is the point the door closes.
        /// </summary>
        private HostSession LoadedHost(int peerId = 1, string name = "ally")
        {
            var host = LobbyHost(peerId, name);
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(peerId, new LobbyReadyMessage(host.Selection.Version));
            host.Handle(new LoadFinishedEvent(host.Selection.Version, LoadOutcome.Loaded));
            host.HandleMessage(peerId, new LobbyLoadedMessage(host.Selection.Version, LoadOutcome.Loaded));
            return host;
        }

        [Fact]
        public void AfterTheCampaignLoads_AStrangerIsRefusedWithAReason()
        {
            // M11e transfers on selection, so a peer arriving now would never be
            // offered the save and could never ready. Refused with a reason rather
            // than a dropped socket: silence is indistinguishable from the host
            // being down, which this project has already paid for once.
            var host = LoadedHost();
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            var effects = host.HandleMessage(9, GoodHello("newcomer"));

            var reject = Messages<RejectMessage>(effects).Single();
            Assert.Equal(RejectReason.NotAcceptingPeers, reject.Reason);
        }

        [Fact]
        public void AfterTheCampaignLoads_SomeoneWhoWasAlreadyInMayComeBack()
        {
            // ⚠️ What keeps the seal a door and not a wall. A resume token is only
            // minted when a peer held units in combat, and the seal lives in the
            // out-of-combat campaign — so a wifi blip out there produces no token
            // at all, and the ordinary recovery is a fresh Hello. Sealing against
            // that would make one dropped packet permanent.
            var host = LoadedHost();
            host.Handle(new PeerDisconnectedEvent(1, "wifi"));
            host.Handle(new PeerConnectedEvent(7, "127.0.0.1:7"));

            var effects = host.HandleMessage(7, GoodHello("ally"));

            Assert.Empty(Messages<RejectMessage>(effects));
            Assert.Single(Messages<WelcomeMessage>(effects));
        }

        [Fact]
        public void AfterTheCampaignLoads_ANamelessPeerIsRefusedByTheSealFirst()
        {
            // A null name is a malformed Hello and would be refused as InvalidName
            // anyway, but the seal is checked ahead of that on purpose: once the
            // campaign is under way, a stranger's other problems are beside the
            // point. Pinned so the ordering is a decision rather than an accident.
            var host = LoadedHost();
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            var effects = host.HandleMessage(9, new HelloMessage(
                PbjProtocol.Magic, PbjProtocol.Version, PbjProtocol.ModVersion, null, null, null));

            Assert.Equal(RejectReason.NotAcceptingPeers, Messages<RejectMessage>(effects).Single().Reason);
        }

        [Fact]
        public void BeforeTheCampaignLoads_AnyoneMayJoin()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));

            Assert.Empty(Messages<RejectMessage>(host.HandleMessage(9, GoodHello("newcomer"))));
        }

        [Fact]
        public void AnAbandonedLoad_LeavesTheDoorOpen()
        {
            // ⚠️ The trap this design avoids. Sealing when the load *starts* would
            // mean a load that never completed — the host's own load failing, or
            // ExpireLoads timing it out — left a session refusing joins forever
            // with no campaign ever entered and nothing able to reopen it.
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            host.HandleMessage(1, new LobbyReadyMessage(host.Selection.Version));
            host.Handle(new LoadFinishedEvent(host.Selection.Version, LoadOutcome.Refused));

            host.Handle(new PeerConnectedEvent(9, "127.0.0.1:9"));
            Assert.Empty(Messages<RejectMessage>(host.HandleMessage(9, GoodHello("newcomer"))));
        }
    }
}
