using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class NetLogTests
    {
        // --- session lifecycle ---

        [Fact]
        public void HostListening_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] host listening on 127.0.0.1:27600 | protocol v1 | slots 3",
                NetLog.HostListening("127.0.0.1", 27600, 1, 3));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void HostListening_WithBlankAddress_Throws(string? address)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.HostListening(address!, 1, 1, 1));
            Assert.Equal("bindAddress", ex.ParamName);
        }

        [Fact]
        public void ClientConnecting_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] connecting to 127.0.0.1:27600 as 'ally'",
                NetLog.ClientConnecting("127.0.0.1", 27600, "ally"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ClientConnecting_WithBlankHost_Throws(string? host)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.ClientConnecting(host!, 1, "ally"));
            Assert.Equal("hostAddress", ex.ParamName);
        }

        [Fact]
        public void ClientConnecting_WithBlankName_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.ClientConnecting("h", 1, "  "));
            Assert.Equal("playerName", ex.ParamName);
        }

        [Fact]
        public void SessionClosed_PluralisesPeers()
        {
            Assert.Equal("[pb-and-j] session closed | 0 peers | listener stopped", NetLog.SessionClosed(0));
            Assert.Equal("[pb-and-j] session closed | 1 peer | listener stopped", NetLog.SessionClosed(1));
        }

        [Fact]
        public void PumpFailed_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] networking stopped after an error — NullReferenceException",
                NetLog.PumpFailed("NullReferenceException"));
        }

        [Fact]
        public void PumpFailed_WithBlankDetail_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.PumpFailed(" "));
            Assert.Equal("detail", ex.ParamName);
        }

        [Fact]
        public void TransportFailed_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] transport failed — socket closed", NetLog.TransportFailed("socket closed"));
        }

        [Fact]
        public void TransportFailed_WithBlankDetail_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.TransportFailed(""));
            Assert.Equal("detail", ex.ParamName);
        }

        // --- handshake ---

        [Fact]
        public void PeerConnected_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] peer connected: #1 from 127.0.0.1:52104",
                NetLog.PeerConnected(1, "127.0.0.1:52104"));
        }

        [Fact]
        public void PeerConnected_WithUnknownRemote_UsesPlaceholder()
        {
            Assert.Equal("[pb-and-j] peer connected: #1 from ?", NetLog.PeerConnected(1, null));
        }

        [Fact]
        public void HandshakeOk_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] handshake ok: #1 'ally' | protocol v1 | mod v0.2.0",
                NetLog.HandshakeOk(1, "ally", 1, "0.2.0"));
        }

        [Fact]
        public void HandshakeOk_WithUnknownModVersion_UsesPlaceholder()
        {
            Assert.EndsWith("mod v?", NetLog.HandshakeOk(1, "ally", 1, null));
        }

        [Fact]
        public void HandshakeOk_WithBlankName_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.HandshakeOk(1, "  ", 1, "0.2.0"));
            Assert.Equal("name", ex.ParamName);
        }

        [Fact]
        public void HandshakeRejected_WithDetail_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] rejected 'ally2': VersionMismatch (peer v999, host v1)",
                NetLog.HandshakeRejected("ally2", RejectReason.VersionMismatch, "peer v999, host v1"));
        }

        [Fact]
        public void HandshakeRejected_WithoutDetail_OmitsParentheses()
        {
            Assert.Equal(
                "[pb-and-j] rejected 'ally2': SessionFull",
                NetLog.HandshakeRejected("ally2", RejectReason.SessionFull, null));
        }

        [Fact]
        public void HandshakeRejected_WithBlankName_UsesPlaceholder()
        {
            Assert.Equal(
                "[pb-and-j] rejected '?': InvalidName",
                NetLog.HandshakeRejected("", RejectReason.InvalidName, null));
        }

        [Fact]
        public void Welcomed_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] welcome | peer #1 | session 7f3a91 | host 'host' | turn 3",
                NetLog.Welcomed(1, "7f3a91", "host", 3));
        }

        [Fact]
        public void Welcomed_WithMissingFields_UsesPlaceholders()
        {
            Assert.Equal(
                "[pb-and-j] welcome | peer #1 | session ? | host '?' | turn 0",
                NetLog.Welcomed(1, null, null, 0));
        }

        [Fact]
        public void PeerLeft_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] peer left: #1 'ally' (transport closed)",
                NetLog.PeerLeft(1, "ally", "transport closed"));
        }

        [Fact]
        public void PeerLeft_WithMissingFields_UsesPlaceholders()
        {
            Assert.Equal("[pb-and-j] peer left: #1 '?' (?)", NetLog.PeerLeft(1, null, null));
        }

        [Fact]
        public void SessionSummary_ListsParticipants()
        {
            Assert.Equal(
                "[pb-and-j] session: 2 participants (host #0 'host', #1 'ally')",
                NetLog.SessionSummary(new[] { "host #0 'host'", "#1 'ally'" }));
        }

        [Fact]
        public void SessionSummary_WithOneParticipant_UsesSingular()
        {
            Assert.Equal(
                "[pb-and-j] session: 1 participant (host #0 'host')",
                NetLog.SessionSummary(new[] { "host #0 'host'" }));
        }

        [Fact]
        public void SessionSummary_WithNoParticipants_OmitsTheList()
        {
            Assert.Equal("[pb-and-j] session: 0 participants", NetLog.SessionSummary(new string[0]));
        }

        [Fact]
        public void SessionSummary_WithNullList_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => NetLog.SessionSummary(null!));
            Assert.Equal("participants", ex.ParamName);
        }

        [Fact]
        public void Assignment_ListsUnitsPerPeer()
        {
            var assignments = UnitAssignmentPlanner.Plan(
                new[] { 0, 1 }, new[] { "unit_a", "unit_b", "unit_c" });
            Assert.Equal(
                "[pb-and-j] assignment: #0 <- unit_a, unit_c | #1 <- unit_b",
                NetLog.Assignment(assignments));
        }

        [Fact]
        public void Assignment_WithPeerHoldingNoUnits_SaysNone()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, new[] { "unit_a" });
            Assert.Equal(
                "[pb-and-j] assignment: #0 <- unit_a | #1 <- (none)",
                NetLog.Assignment(assignments));
        }

        [Fact]
        public void Assignment_WithNoAssignments_ComposesHeaderOnly()
        {
            Assert.Equal("[pb-and-j] assignment:", NetLog.Assignment(UnitAssignments.Empty));
        }

        [Fact]
        public void Assignment_WithNull_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => NetLog.Assignment(null!));
            Assert.Equal("assignments", ex.ParamName);
        }

        [Fact]
        public void AssignedUnits_ListsThem()
        {
            Assert.Equal(
                "[pb-and-j] you control: unit_a, unit_b",
                NetLog.AssignedUnits(new[] { "unit_a", "unit_b" }));
        }

        [Fact]
        public void AssignedUnits_WithOneUnit_OmitsTheSeparator()
        {
            Assert.Equal("[pb-and-j] you control: unit_a", NetLog.AssignedUnits(new[] { "unit_a" }));
        }

        [Fact]
        public void AssignedUnits_WithNone_SaysSo()
        {
            Assert.Equal("[pb-and-j] you control no units this combat", NetLog.AssignedUnits(new string[0]));
        }

        [Fact]
        public void AssignedUnits_WithNull_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => NetLog.AssignedUnits(null!));
            Assert.Equal("units", ex.ParamName);
        }

        // --- barrier ---

        [Fact]
        public void ReadyReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ready from #1 'ally' | turn 3 | 1 order",
                NetLog.ReadyReceived(1, "ally", 3, 1));
        }

        [Fact]
        public void ReadyReceived_PluralisesOrders()
        {
            Assert.EndsWith("| 0 orders", NetLog.ReadyReceived(1, "ally", 3, 0));
            Assert.EndsWith("| 2 orders", NetLog.ReadyReceived(1, "ally", 3, 2));
        }

        [Fact]
        public void ReadyReceived_WithMissingName_UsesPlaceholder()
        {
            Assert.Contains("#1 '?'", NetLog.ReadyReceived(1, null, 3, 1));
        }

        [Fact]
        public void BarrierWaiting_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] barrier 1/2 — waiting", NetLog.BarrierWaiting(1, 2));
        }

        [Fact]
        public void BarrierCommitting_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] barrier 2/2 — committing turn 3", NetLog.BarrierCommitting(2, 2, 3));
        }

        [Fact]
        public void ReadyIgnoredStale_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring stale ready from #1 for turn 2 (now on turn 3)",
                NetLog.ReadyIgnoredStale(1, 2, 3));
        }

        [Fact]
        public void ReadyNeedsResync_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] peer #1 is ahead (ready for turn 4, host on turn 3) — resyncing",
                NetLog.ReadyNeedsResync(1, 4, 3));
        }

        // --- orders and commit ---

        [Fact]
        public void OrdersApplied_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] applied 1 remote order, 0 rejected", NetLog.OrdersApplied(1, 0));
            Assert.Equal("[pb-and-j] applied 3 remote orders, 2 rejected", NetLog.OrdersApplied(3, 2));
        }

        [Fact]
        public void OrderResultSent_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] order result to #2: 3 accepted, 1 rejected",
                NetLog.OrderResultSent(2, 3, 1));
        }

        [Fact]
        public void OrderResultReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 orders: 3 accepted, 1 rejected by host",
                NetLog.OrderResultReceived(4, 3, 1));
        }

        [Fact]
        public void UnreadyReceived_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] un-ready from #2 'ally' for turn 3", NetLog.UnreadyReceived(2, "ally", 3));
        }

        [Fact]
        public void UnreadyReceived_WithNoName_MarksItUnknown()
        {
            Assert.Equal("[pb-and-j] un-ready from #2 '?' for turn 3", NetLog.UnreadyReceived(2, null, 3));
        }

        [Fact]
        public void UnreadyIgnored_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring un-ready from #2 for turn 3 — already executing",
                NetLog.UnreadyIgnored(2, 3, "already executing"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void UnreadyIgnored_WithBlankReason_Throws(string? why)
        {
            Assert.Throws<ArgumentException>(() => NetLog.UnreadyIgnored(2, 3, why!));
        }

        [Fact]
        public void CombatStarted_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] combat started on turn 0 — announcing to 1 peer", NetLog.CombatStarted(0, 1));
            Assert.Equal("[pb-and-j] combat started on turn 4 — announcing to 2 peers", NetLog.CombatStarted(4, 2));
        }

        [Fact]
        public void CombatEnded_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] combat ended — unlocking 1 peer", NetLog.CombatEnded(1));
            Assert.Equal("[pb-and-j] combat ended — unlocking 0 peers", NetLog.CombatEnded(0));
        }

        [Fact]
        public void SendQueueBacklog_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] send queue backing up for #2: 40 frame(s), 262144 byte(s) — slow link",
                NetLog.SendQueueBacklog(2, 262144, 40));
        }

        [Fact]
        public void SendQueueOverflowed_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] send queue OVERFLOWED for #2 at 1024 frame(s), 4194304 byte(s) — dropping the peer",
                NetLog.SendQueueOverflowed(2, 4194304, 1024));
        }

        [Fact]
        public void SendFailed_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] send to #2 failed: IOException", NetLog.SendFailed(2, "IOException"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void SendFailed_WithBlankDetail_Throws(string? detail)
        {
            Assert.Throws<ArgumentException>(() => NetLog.SendFailed(2, detail!));
        }

        [Fact]
        public void SendAfterStop_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] dropping a frame for #0: the transport is stopped",
                NetLog.SendAfterStop(0));
        }

        [Fact]
        public void SnapshotUnitsSkipped_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] snapshot: 2 unit(s) not present locally, 1 local unit(s) not in the snapshot",
                NetLog.SnapshotUnitsSkipped(2, 1));
        }

        [Fact]
        public void SnapshotClamped_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] snapshot clamped: 128 units captured, only 128 fit — the rest are NOT corrected",
                NetLog.SnapshotClamped(128, 128));
        }

        [Fact]
        public void PeerHeldForReconnect_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] holding #2 'ally' units for 120s in case they reconnect",
                NetLog.PeerHeldForReconnect(2, "ally", 120.0));
        }

        [Fact]
        public void PeerRejoined_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] 'ally' rejoined as #4 (was #2) — units rebound",
                NetLog.PeerRejoined(2, 4, "ally"));
        }

        [Fact]
        public void ReconnectExpired_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] #2 'ally' did not return — releasing their units",
                NetLog.ReconnectExpired(2, "ally"));
        }

        [Fact]
        public void Rejoining_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] rejoining session 7f3a91 as peer #1", NetLog.Rejoining("7f3a91", 1));
        }

        [Fact]
        public void PeerTimedOut_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] peer #2 'ally' silent for 20s — dropping", NetLog.PeerTimedOut(2, "ally", 20.4));
        }

        [Fact]
        public void HostTimedOut_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] host silent for 31s — connection lost, continuing single-player",
                NetLog.HostTimedOut(30.6));
        }

        [Fact]
        public void CombatStartedByHost_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] host started combat on turn 0", NetLog.CombatStartedByHost(0));
        }

        [Fact]
        public void CombatEndedByHost_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] host's combat ended — back to the lobby", NetLog.CombatEndedByHost());
        }

        [Fact]
        public void CombatStateObserved_ComposesBothDirections()
        {
            Assert.Equal("[pb-and-j] host reports combat started", NetLog.CombatStateObserved(true));
            Assert.Equal("[pb-and-j] host reports combat ended", NetLog.CombatStateObserved(false));
        }

        [Fact]
        public void OrderRejectedUnowned_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] order REJECTED from #1: unit_a is not assigned to that peer",
                NetLog.OrderRejectedUnowned(1, "unit_a"));
        }

        [Fact]
        public void OrderRejectedUnowned_WithMissingUnit_UsesPlaceholder()
        {
            Assert.Contains(": ? is not assigned", NetLog.OrderRejectedUnowned(1, null));
        }

        [Fact]
        public void OrderRejectedByGame_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] order REJECTED from #1: unit_a 'move_run' — OutOfWindow",
                NetLog.OrderRejectedByGame(1, "unit_a", "move_run", OrderApplyResult.OutOfWindow));
        }

        [Fact]
        public void OrderRejectedByGame_WithMissingFields_UsesPlaceholders()
        {
            Assert.Equal(
                "[pb-and-j] order REJECTED from #2: ? '?' — Invalid",
                NetLog.OrderRejectedByGame(2, null, null, OrderApplyResult.Invalid));
        }

        [Fact]
        public void TurnCommitted_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] turn 3 committed", NetLog.TurnCommitted(3));
        }

        [Fact]
        public void CommitRefused_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] commit REFUSED for turn 3 — staying in planning, peers unlocked",
                NetLog.CommitRefused(3));
        }

        [Fact]
        public void TurnCompleted_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 3 complete | digest 3f9c1a04 | broadcast to 1 peer",
                NetLog.TurnCompleted(3, "3f9c1a04", 1));
        }

        [Fact]
        public void TurnCompleted_PluralisesPeers()
        {
            Assert.EndsWith("broadcast to 2 peers", NetLog.TurnCompleted(3, "d", 2));
        }

        [Fact]
        public void DigestMatched_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] turn 3 digest 3f9c1a04 OK", NetLog.DigestMatched(3, "3f9c1a04"));
        }

        [Fact]
        public void DigestDiverged_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 3 DIVERGED | host aaaa1111 | local bbbb2222",
                NetLog.DigestDiverged(3, "aaaa1111", "bbbb2222"));
        }

        [Fact]
        public void DigestDiverged_WithMissingValues_UsesPlaceholders()
        {
            Assert.Equal("[pb-and-j] turn 3 DIVERGED | host ? | local ?", NetLog.DigestDiverged(3, null, null));
        }

        [Fact]
        public void MailboxOverflowed_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] mailbox overflowed — dropped 1 event", NetLog.MailboxOverflowed(1));
            Assert.Equal("[pb-and-j] mailbox overflowed — dropped 5 events", NetLog.MailboxOverflowed(5));
        }

        // --- status ---

        [Fact]
        public void Status_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] session HOST | state Planning | turn 3 | participants 2 | ready 0/2",
                NetLog.Status("HOST", "Planning", 3, 2, 0));
        }

        [Fact]
        public void Status_WithBlankRole_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.Status(" ", "Planning", 0, 0, 0));
            Assert.Equal("role", ex.ParamName);
        }

        [Fact]
        public void Status_WithBlankState_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.Status("HOST", "", 0, 0, 0));
            Assert.Equal("state", ex.ParamName);
        }

        [Fact]
        public void NoSession_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] no session — use pbj.host or pbj.join", NetLog.NoSession());
        }

        // --- culture ---

        [Fact]
        public void Lines_AreCultureIndependent()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(
                    "[pb-and-j] host listening on 127.0.0.1:27600 | protocol v1 | slots 3",
                    NetLog.HostListening("127.0.0.1", 27600, 1, 3));
                Assert.Equal("[pb-and-j] turn 1000 committed", NetLog.TurnCommitted(1000));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    }
}
