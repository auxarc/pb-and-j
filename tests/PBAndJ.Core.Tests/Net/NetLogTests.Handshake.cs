using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The handshake and the roster it settles: a socket connecting, a peer
    // admitted, rejected or timed out, the welcome, the session summary, the
    // per-peer assignment and the assigned-units line, and a peer leaving.
    // HandshakeTimedOut was filed under the orders banner in the original; the
    // split moved it to the subject it names.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // This class has no helpers and no fields -- every member is a test -- so
    // unlike the other split test classes there is no shared fixture in
    // NetLogTests.cs to look for.
    public partial class NetLogTests
    {
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

        [Fact]
        public void HandshakeTimedOut_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] socket #4 connected but never handshook within 10s — dropping",
                NetLog.HandshakeTimedOut(4, 10.0));
        }
    }
}
