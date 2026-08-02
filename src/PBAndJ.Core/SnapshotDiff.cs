using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PBAndJ.Core
{
    /// <summary>
    /// Compares the planned actions captured before a mid-combat save against
    /// those present after the save is reloaded. Identity is the persistent
    /// owner name + blueprint key + timing (combat entity ids are regenerated
    /// on load and must be ignored). Multiset semantics with a small float
    /// tolerance for serialization round-trip drift.
    /// </summary>
    public static class SnapshotDiff
    {
        private const float TimeTolerance = 0.01f;

        public static string Compare(IReadOnlyList<ActionSnapshot> before, IReadOnlyList<ActionSnapshot> after)
        {
            if (before == null)
            {
                throw new ArgumentNullException(nameof(before));
            }
            if (after == null)
            {
                throw new ArgumentNullException(nameof(after));
            }

            var remaining = after.ToList();
            var lost = new List<ActionSnapshot>();
            foreach (var b in before)
            {
                var matchIndex = remaining.FindIndex(a => Matches(b, a));
                if (matchIndex >= 0)
                {
                    remaining.RemoveAt(matchIndex);
                }
                else
                {
                    lost.Add(b);
                }
            }

            var verdict = lost.Count == 0 && remaining.Count == 0 ? "MATCH" : "DIFF";
            var sb = new StringBuilder();
            sb.Append($"[pb-and-j] save/load diff | before {before.Count} | after {after.Count} | {verdict}");
            foreach (var a in Ordered(lost))
            {
                sb.Append('\n').Append("  - lost: ").Append(Describe(a));
            }
            foreach (var a in Ordered(remaining))
            {
                sb.Append('\n').Append("  + gained: ").Append(Describe(a));
            }
            return sb.ToString();
        }

        private static bool Matches(ActionSnapshot b, ActionSnapshot a) =>
            string.Equals(b.OwnerName, a.OwnerName, StringComparison.Ordinal)
            && string.Equals(b.DataKey, a.DataKey, StringComparison.Ordinal)
            && Math.Abs(b.StartTime - a.StartTime) <= TimeTolerance
            && Math.Abs(b.Duration - a.Duration) <= TimeTolerance;

        private static IEnumerable<ActionSnapshot> Ordered(IEnumerable<ActionSnapshot> actions) =>
            actions.OrderBy(a => a.OwnerName, StringComparer.Ordinal)
                   .ThenBy(a => a.DataKey, StringComparer.Ordinal)
                   .ThenBy(a => a.StartTime);

        private static string Describe(ActionSnapshot a) =>
            string.Format(CultureInfo.InvariantCulture, "{0}: {1} @{2:F2}s +{3:F2}s", a.OwnerName, a.DataKey, a.StartTime, a.Duration);
    }
}
