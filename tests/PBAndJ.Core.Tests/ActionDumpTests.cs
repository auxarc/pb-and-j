using System;
using System.Collections.Generic;
using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class ActionDumpTests
    {
        private static ActionSnapshot Move(int owner = 1, string key = "move_run", float start = 0f, float dur = 2.5f, bool locked = false) =>
            new ActionSnapshot(owner, "unit_" + owner, key, start, dur, locked);

        // --- ActionSnapshot value semantics ---

        [Fact]
        public void Snapshot_RetainsAllFields()
        {
            var s = new ActionSnapshot(7, "unit_7", "attack_main", 1.5f, 0.75f, locked: true);
            Assert.Equal(7, s.OwnerId);
            Assert.Equal("unit_7", s.OwnerName);
            Assert.Equal("attack_main", s.DataKey);
            Assert.Equal(1.5f, s.StartTime);
            Assert.Equal(0.75f, s.Duration);
            Assert.True(s.Locked);
        }

        [Fact]
        public void Snapshot_AllowsNullOwnerName_NormalizedToUnknown()
        {
            var s = new ActionSnapshot(3, null, "wait", 0f, 1f, false);
            Assert.Equal("?", s.OwnerName);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Snapshot_WithMissingDataKey_Throws(string? key)
        {
            var ex = Assert.Throws<ArgumentException>(() => new ActionSnapshot(1, "u", key!, 0f, 1f, false));
            Assert.Equal("dataKey", ex.ParamName);
        }

        // --- Formatter ---

        [Fact]
        public void Format_EmptyList_ReportsNoActions()
        {
            var result = ActionDumpFormatter.Format(3, new List<ActionSnapshot>());
            Assert.Equal("[pb-and-j] action dump | turn 3 | 0 actions", result);
        }

        [Fact]
        public void Format_NullList_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => ActionDumpFormatter.Format(0, null!));
            Assert.Equal("actions", ex.ParamName);
        }

        [Fact]
        public void Format_SingleAction_HeaderPlusOneLine()
        {
            var result = ActionDumpFormatter.Format(1, new[] { Move(owner: 4, key: "move_run", start: 5f, dur: 2.25f) });
            var lines = result.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.Equal("[pb-and-j] action dump | turn 1 | 1 action", lines[0]);
            Assert.Equal("  unit_4 (#4): move_run @5.00s +2.25s", lines[1]);
        }

        [Fact]
        public void Format_LockedActionsAreMarked()
        {
            var result = ActionDumpFormatter.Format(1, new[] { Move(locked: true) });
            Assert.EndsWith("[locked]", result.Split('\n')[1]);
        }

        [Fact]
        public void Format_SortsByOwnerThenStartTime()
        {
            var actions = new[]
            {
                Move(owner: 2, key: "b_second", start: 1f),
                Move(owner: 1, key: "a_second", start: 3f),
                Move(owner: 2, key: "b_first", start: 0f),
                Move(owner: 1, key: "a_first", start: 0f),
            };
            var lines = ActionDumpFormatter.Format(9, actions).Split('\n');
            Assert.Equal(5, lines.Length);
            Assert.Contains("a_first", lines[1]);
            Assert.Contains("a_second", lines[2]);
            Assert.Contains("b_first", lines[3]);
            Assert.Contains("b_second", lines[4]);
        }

        [Fact]
        public void Format_UsesInvariantCultureForTimes()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var result = ActionDumpFormatter.Format(1, new[] { Move(start: 1.5f, dur: 2.25f) });
                Assert.Contains("@1.50s +2.25s", result);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }

        [Fact]
        public void Format_PluralizesActionCount()
        {
            var two = ActionDumpFormatter.Format(0, new[] { Move(), Move(owner: 2) });
            Assert.StartsWith("[pb-and-j] action dump | turn 0 | 2 actions", two);
        }
    }
}
