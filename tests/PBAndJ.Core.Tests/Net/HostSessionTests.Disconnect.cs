using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // A peer going away: what the others are told, and what the barrier does about it.
    // One section of the original, moved whole.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- disconnect ---

        [Fact]
        public void Handle_PeerDisconnected_BroadcastsPeerLeft()
        {
            var host = WithPeer();
            var effects = host.Handle(new PeerDisconnectedEvent(1, "transport closed"));
            var left = Assert.IsType<PeerLeftMessage>(All<BroadcastEffect>(effects).First().Message);
            Assert.Equal(1, left.PeerId);
            Assert.Empty(host.Peers);
            Assert.Equal(1, host.ParticipantCount);
        }

        [Fact]
        public void Handle_PeerDisconnected_ForUnknownPeer_ProducesNoEffects()
        {
            Assert.Empty(Host().Handle(new PeerDisconnectedEvent(99, "gone")));
        }

        [Fact]
        public void HandleMessage_Bye_RemovesPeerAndDisconnects()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ByeMessage("quitting"));
            Assert.Empty(host.Peers);
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        [Fact]
        public void HandleMessage_WithHostOnlyMessage_DisconnectsPeer()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new TurnCommitMessage(3));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
            Assert.Empty(host.Peers);
        }

        [Fact]
        public void HandleMessage_HostOnlyMessage_FromUnregisteredPeer_StillDisconnects()
        {
            var host = Host();
            var effects = host.HandleMessage(7, new TurnCommitMessage(3));
            Assert.Equal(7, Single<DisconnectEffect>(effects).PeerId);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("#7 '?'"));
        }

        [Fact]
        public void HandleMessage_Ready_BeforeHello_DisconnectsPeer()
        {
            var host = Host();
            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }

        [Fact]
        public void HandleMessage_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Host().HandleMessage(1, null!));
            Assert.Equal("message", ex.ParamName);
        }

        [Fact]
        public void Disconnect_ArrivingTwice_IsHandledOnce()
        {
            // Since M5b a peer has both a writer and a receive thread, and both
            // post a PeerDisconnectedEvent when the socket goes. The second must
            // be inert, not a second PeerLeft broadcast or a double unassign.
            var host = WithPeer();
            var first = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            var second = host.Handle(new PeerDisconnectedEvent(1, "send failed"));

            Assert.Single(All<BroadcastEffect>(first), b => b.Message is PeerLeftMessage);
            Assert.Empty(second);
        }
    }
}
