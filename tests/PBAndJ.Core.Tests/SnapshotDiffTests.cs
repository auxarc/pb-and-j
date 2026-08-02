using System;
using System.Collections.Generic;
using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class SnapshotDiffTests
    {
        private static ActionSnapshot A(string owner, string key = "move_run", float start = 0f, float dur = 2f, int id = 1) =>
            new ActionSnapshot(id, owner, key, start, dur, locked: false);

        [Fact]
        public void Compare_NullBefore_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => SnapshotDiff.Compare(null!, new List<ActionSnapshot>()));
            Assert.Equal("before", ex.ParamName);
        }

        [Fact]
        public void Compare_NullAfter_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => SnapshotDiff.Compare(new List<ActionSnapshot>(), null!));
            Assert.Equal("after", ex.ParamName);
        }

        [Fact]
        public void Compare_IdenticalSets_ReportsMatch()
        {
            var before = new[] { A("unit_a"), A("unit_b", "attack_primary", 1.5f, 0.75f) };
            var after = new[] { A("unit_b", "attack_primary", 1.5f, 0.75f), A("unit_a") };
            var result = SnapshotDiff.Compare(before, after);
            Assert.Equal("[pb-and-j] save/load diff | before 2 | after 2 | MATCH", result);
        }

        [Fact]
        public void Compare_IgnoresOwnerIdDifferences_MatchesByName()
        {
            // Combat entity ids are regenerated on load; identity is the persistent unit name.
            var before = new[] { A("unit_a", id: 51) };
            var after = new[] { A("unit_a", id: 87) };
            Assert.EndsWith("MATCH", SnapshotDiff.Compare(before, after));
        }

        [Fact]
        public void Compare_ToleratesTinyFloatDrift()
        {
            var before = new[] { A("unit_a", start: 1.000f, dur: 2.000f) };
            var after = new[] { A("unit_a", start: 1.004f, dur: 1.996f) };
            Assert.EndsWith("MATCH", SnapshotDiff.Compare(before, after));
        }

        [Fact]
        public void Compare_ReportsLostActions()
        {
            var before = new[] { A("unit_a"), A("unit_b", "attack_primary", 1f, 0.5f) };
            var after = new[] { A("unit_a") };
            var lines = SnapshotDiff.Compare(before, after).Split('\n');
            Assert.Equal("[pb-and-j] save/load diff | before 2 | after 1 | DIFF", lines[0]);
            Assert.Equal("  - lost: unit_b: attack_primary @1.00s +0.50s", lines[1]);
        }

        [Fact]
        public void Compare_ReportsGainedActions()
        {
            var before = new[] { A("unit_a") };
            var after = new[] { A("unit_a"), A("unit_c", "wait", 3f, 1f) };
            var lines = SnapshotDiff.Compare(before, after).Split('\n');
            Assert.Equal("[pb-and-j] save/load diff | before 1 | after 2 | DIFF", lines[0]);
            Assert.Equal("  + gained: unit_c: wait @3.00s +1.00s", lines[1]);
        }

        [Fact]
        public void Compare_MatchesDuplicateActionsByMultiplicity()
        {
            // Two identical attacks before, one after -> one lost (multiset semantics).
            var before = new[] { A("unit_a", "attack_primary", 1f, 0.5f), A("unit_a", "attack_primary", 1f, 0.5f) };
            var after = new[] { A("unit_a", "attack_primary", 1f, 0.5f) };
            var lines = SnapshotDiff.Compare(before, after).Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("  - lost: unit_a: attack_primary", lines[1]);
        }

        [Fact]
        public void Compare_UsesInvariantCultureInReport()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var lines = SnapshotDiff.Compare(new[] { A("u", "wait", 1.5f, 2.25f) }, Array.Empty<ActionSnapshot>()).Split('\n');
                Assert.Equal("  - lost: u: wait @1.50s +2.25s", lines[1]);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    }
}
