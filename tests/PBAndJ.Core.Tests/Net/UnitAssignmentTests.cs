using System;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class UnitAssignmentTests
    {
        private static string[] Units(params string[] names) => names;

        // --- planner ---

        [Fact]
        public void Plan_WithHostOnly_GivesAllUnitsToHost()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0 }, Units("unit_b", "unit_a"));
            Assert.Equal(new[] { "unit_a", "unit_b" }, assignments.UnitsFor(0));
        }

        [Fact]
        public void Plan_WithTwoPeers_DealsRoundRobinInNameOrder()
        {
            var assignments = UnitAssignmentPlanner.Plan(
                new[] { 0, 1 }, Units("unit_c", "unit_a", "unit_d", "unit_b"));
            Assert.Equal(new[] { "unit_a", "unit_c" }, assignments.UnitsFor(0));
            Assert.Equal(new[] { "unit_b", "unit_d" }, assignments.UnitsFor(1));
        }

        [Fact]
        public void Plan_WithMoreUnitsThanPeers_GivesRemainderToEarlierPeers()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units("a", "b", "c"));
            Assert.Equal(new[] { "a", "c" }, assignments.UnitsFor(0));
            Assert.Equal(new[] { "b" }, assignments.UnitsFor(1));
        }

        [Fact]
        public void Plan_WithMorePeersThanUnits_GivesEmptyListsToLatePeers()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1, 2 }, Units("only_one"));
            Assert.Equal(new[] { "only_one" }, assignments.UnitsFor(0));
            Assert.Empty(assignments.UnitsFor(1));
            Assert.Empty(assignments.UnitsFor(2));
        }

        [Fact]
        public void Plan_IsStableAcrossInputOrdering()
        {
            var forward = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units("a", "b", "c", "d"));
            var shuffled = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units("d", "b", "a", "c"));
            Assert.Equal(forward.UnitsFor(0), shuffled.UnitsFor(0));
            Assert.Equal(forward.UnitsFor(1), shuffled.UnitsFor(1));
        }

        [Fact]
        public void Plan_OrdersPeersById_RegardlessOfInputOrder()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 2, 0, 1 }, Units("a", "b", "c"));
            Assert.Equal(new[] { "a" }, assignments.UnitsFor(0));
            Assert.Equal(new[] { "b" }, assignments.UnitsFor(1));
            Assert.Equal(new[] { "c" }, assignments.UnitsFor(2));
        }

        [Fact]
        public void Plan_SortsUnitNamesOrdinal()
        {
            // Ordinal, not culture-aware: uppercase sorts before lowercase.
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0 }, Units("b", "A", "a", "B"));
            Assert.Equal(new[] { "A", "B", "a", "b" }, assignments.UnitsFor(0));
        }

        [Fact]
        public void Plan_WithNoUnits_ReturnsEmptyAssignmentsForEveryPeer()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units());
            Assert.Empty(assignments.UnitsFor(0));
            Assert.Empty(assignments.UnitsFor(1));
        }

        [Fact]
        public void Plan_SkipsBlankUnitNames()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0 }, new[] { "a", null!, "  ", "b" });
            Assert.Equal(new[] { "a", "b" }, assignments.UnitsFor(0));
        }

        [Fact]
        public void Plan_DeduplicatesUnitNames()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0 }, Units("a", "a", "b"));
            Assert.Equal(new[] { "a", "b" }, assignments.UnitsFor(0));
        }

        [Fact]
        public void Plan_WithNullPeerIds_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => UnitAssignmentPlanner.Plan(null!, Units("a")));
            Assert.Equal("peerIds", ex.ParamName);
        }

        [Fact]
        public void Plan_WithNoPeers_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => UnitAssignmentPlanner.Plan(new int[0], Units("a")));
            Assert.Equal("peerIds", ex.ParamName);
        }

        [Fact]
        public void Plan_WithNullUnitNames_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => UnitAssignmentPlanner.Plan(new[] { 0 }, null!));
            Assert.Equal("unitNames", ex.ParamName);
        }

        // --- assignments lookup: the host-side security boundary ---

        [Fact]
        public void IsOwnedBy_AssignedUnit_ReturnsTrue()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units("a", "b"));
            Assert.True(assignments.IsOwnedBy(0, "a"));
            Assert.True(assignments.IsOwnedBy(1, "b"));
        }

        [Fact]
        public void IsOwnedBy_UnitOfAnotherPeer_ReturnsFalse()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0, 1 }, Units("a", "b"));
            Assert.False(assignments.IsOwnedBy(1, "a"));
        }

        [Fact]
        public void IsOwnedBy_UnassignedUnit_ReturnsFalse()
        {
            Assert.False(UnitAssignmentPlanner.Plan(new[] { 0 }, Units("a")).IsOwnedBy(0, "ghost"));
        }

        [Fact]
        public void IsOwnedBy_UnknownPeer_ReturnsFalse()
        {
            Assert.False(UnitAssignmentPlanner.Plan(new[] { 0 }, Units("a")).IsOwnedBy(99, "a"));
        }

        [Fact]
        public void IsOwnedBy_IsOrdinalCaseSensitive()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 0 }, Units("unit_a"));
            Assert.True(assignments.IsOwnedBy(0, "unit_a"));
            Assert.False(assignments.IsOwnedBy(0, "UNIT_A"));
        }

        [Fact]
        public void IsOwnedBy_WithNullUnitName_ReturnsFalse()
        {
            Assert.False(UnitAssignmentPlanner.Plan(new[] { 0 }, Units("a")).IsOwnedBy(0, null));
        }

        [Fact]
        public void UnitsFor_UnknownPeer_ReturnsEmpty()
        {
            Assert.Empty(UnitAssignmentPlanner.Plan(new[] { 0 }, Units("a")).UnitsFor(99));
        }

        [Fact]
        public void PeerIds_AreOrderedAndComplete()
        {
            var assignments = UnitAssignmentPlanner.Plan(new[] { 2, 0, 1 }, Units("a"));
            Assert.Equal(new[] { 0, 1, 2 }, assignments.PeerIds.ToArray());
        }

        [Fact]
        public void Empty_HasNoPeersAndOwnsNothing()
        {
            Assert.Empty(UnitAssignments.Empty.PeerIds);
            Assert.False(UnitAssignments.Empty.IsOwnedBy(0, "a"));
            Assert.Empty(UnitAssignments.Empty.UnitsFor(0));
        }
    }
}
