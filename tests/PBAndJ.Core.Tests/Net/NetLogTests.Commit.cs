using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The back of the `// --- orders and commit ---` banner: reconnecting and
    // timing out, combat as the host announces it, the two ways an order is
    // rejected, and the commit / completion / digest lines the banner is named
    // for. That is all twenty of them.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // This class has no helpers and no fields -- every member is a test -- so
    // unlike the other split test classes there is no shared fixture in
    // NetLogTests.cs to look for.
    public partial class NetLogTests
    {
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
            Assert.Equal(
                "[pb-and-j] host's combat ended — back to the lobby, holding execute until they return",
                NetLog.CombatEndedByHost());
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
    }
}
