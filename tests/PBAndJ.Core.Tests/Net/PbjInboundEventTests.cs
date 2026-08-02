using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjInboundEventTests
    {
        [Fact]
        public void PeerConnected_RetainsFields()
        {
            var e = new PeerConnectedEvent(1, "127.0.0.1:52104");
            Assert.Equal(PbjInboundEventKind.PeerConnected, e.Kind);
            Assert.Equal(1, e.PeerId);
            Assert.Equal("127.0.0.1:52104", e.Remote);
        }

        [Fact]
        public void PeerConnected_WithNullRemote_IsAccepted()
        {
            Assert.Null(new PeerConnectedEvent(1, null).Remote);
        }

        [Fact]
        public void PeerBytes_RetainsFields()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var e = new PeerBytesEvent(2, bytes);
            Assert.Equal(PbjInboundEventKind.PeerBytes, e.Kind);
            Assert.Equal(2, e.PeerId);
            Assert.Equal(bytes, e.Bytes);
        }

        [Fact]
        public void PeerBytes_WithNullBytes_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new PeerBytesEvent(1, null!));
            Assert.Equal("bytes", ex.ParamName);
        }

        [Fact]
        public void PeerDisconnected_RetainsFields()
        {
            var e = new PeerDisconnectedEvent(3, "transport closed");
            Assert.Equal(PbjInboundEventKind.PeerDisconnected, e.Kind);
            Assert.Equal(3, e.PeerId);
            Assert.Equal("transport closed", e.Reason);
        }

        [Fact]
        public void PeerDisconnected_WithNullReason_IsAccepted()
        {
            Assert.Null(new PeerDisconnectedEvent(3, null).Reason);
        }

        [Fact]
        public void TransportFailed_RetainsFields()
        {
            var e = new TransportFailedEvent("listener died");
            Assert.Equal(PbjInboundEventKind.TransportFailed, e.Kind);
            Assert.Equal("listener died", e.Reason);
        }

        [Fact]
        public void TransportFailed_WithNullReason_IsAccepted()
        {
            Assert.Null(new TransportFailedEvent(null).Reason);
        }

        [Fact]
        public void TransportLog_RetainsFields()
        {
            var e = new TransportLogEvent("accepted 127.0.0.1:1");
            Assert.Equal(PbjInboundEventKind.TransportLog, e.Kind);
            Assert.Equal("accepted 127.0.0.1:1", e.Line);
        }

        [Fact]
        public void TransportLog_WithNullLine_IsAccepted()
        {
            Assert.Null(new TransportLogEvent(null).Line);
        }

        [Fact]
        public void OrderApplied_RetainsFields()
        {
            var e = new OrderAppliedEvent(2, 5, OrderApplyResult.NotOwned);
            Assert.Equal(PbjInboundEventKind.OrderApplied, e.Kind);
            Assert.Equal(2, e.PeerId);
            Assert.Equal(5, e.BatchIndex);
            Assert.Equal(OrderApplyResult.NotOwned, e.Result);
        }

        [Fact]
        public void SnapshotApplied_RetainsFields()
        {
            var e = new SnapshotAppliedEvent(4, 12, "host", "local");
            Assert.Equal(PbjInboundEventKind.SnapshotApplied, e.Kind);
            Assert.Equal(4, e.Turn);
            Assert.Equal(12, e.UnitCount);
            Assert.Equal("host", e.ExpectedDigest);
            Assert.Equal("local", e.ActualDigest);
        }

        [Fact]
        public void LocalUnready_HasItsKind()
        {
            Assert.Equal(PbjInboundEventKind.LocalUnready, new LocalUnreadyEvent().Kind);
        }

        [Fact]
        public void CombatEntered_HasItsKind()
        {
            Assert.Equal(PbjInboundEventKind.CombatEntered, new CombatEnteredEvent().Kind);
        }

        [Fact]
        public void CombatExited_HasItsKind()
        {
            Assert.Equal(PbjInboundEventKind.CombatExited, new CombatExitedEvent().Kind);
        }

        [Fact]
        public void Tick_RetainsItsTime()
        {
            var e = new TickEvent(1234.5);
            Assert.Equal(PbjInboundEventKind.Tick, e.Kind);
            Assert.Equal(1234.5, e.NowSeconds);
        }

        [Fact]
        public void LocalReady_HasItsKind()
        {
            Assert.Equal(PbjInboundEventKind.LocalReady, new LocalReadyEvent().Kind);
        }

        [Fact]
        public void CommitOutcome_RetainsFields()
        {
            var committed = new CommitOutcomeEvent(3, committed: true);
            Assert.Equal(PbjInboundEventKind.CommitOutcome, committed.Kind);
            Assert.Equal(3, committed.Turn);
            Assert.True(committed.Committed);
            Assert.False(new CommitOutcomeEvent(3, committed: false).Committed);
        }

        [Fact]
        public void LocalTurnComplete_RetainsFields()
        {
            var e = new LocalTurnCompleteEvent("3f9c1a04", null);
            Assert.Equal(PbjInboundEventKind.LocalTurnComplete, e.Kind);
            Assert.Equal("3f9c1a04", e.Digest);
        }

        [Fact]
        public void LocalTurnComplete_WithNullDigest_IsAccepted()
        {
            Assert.Null(new LocalTurnCompleteEvent(null, null).Digest);
        }
    }
}
