using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The roster the screen reads (M11c). One section of the original, moved whole.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- the roster the screen reads (M11c) ---

        [Fact]
        public void LobbyRoster_PutsTheHostFirstThenPeersInJoinOrder()
        {
            var host = LobbyHost();
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("third"));

            var roster = host.LobbyRoster;
            Assert.Equal(new[] { 0, 1, 2 }, roster.Select(p => p.PeerId));
            Assert.Equal(new[] { "host", "ally", "third" }, roster.Select(p => p.Name));
        }

        [Fact]
        public void LobbyRoster_IsTheSameListTheClientsAreSent()
        {
            // The point of exposing it rather than letting a host screen build its
            // own: a separately-derived roster is one that can disagree with what
            // this host's own clients were just told.
            var host = LobbyHost();
            var effects = host.Handle(Select().Event);

            var broadcast = LobbyState(effects).Peers;
            var read = host.LobbyRoster;
            Assert.Equal(broadcast.Select(p => p.PeerId), read.Select(p => p.PeerId));
            Assert.Equal(broadcast.Select(p => p.Ready), read.Select(p => p.Ready));
        }

        [Fact]
        public void LobbyRoster_CarriesReadyFlags()
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());

            var roster = host.LobbyRoster;
            Assert.True(roster.Single(p => p.PeerId == 0).Ready);
            Assert.False(roster.Single(p => p.PeerId == 1).Ready);
        }

        [Fact]
        public void LobbyRoster_WithNoPeers_IsJustTheHost()
        {
            bridge.InCombat = false;
            var host = Host();
            Assert.Single(host.LobbyRoster);
            Assert.Equal(0, host.LobbyRoster[0].PeerId);
        }

        private string Token(int peerId, string name) =>
            StateDigest.Mix("secret:" + peerId + ":" + name);
    }
}
