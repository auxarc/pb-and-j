using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The plumbing: null arguments, an event kind the client does not know, and
    // the other edges that belong to no feature.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
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
    }
}
