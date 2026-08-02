using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PBAndJ.Core
{
    /// <summary>
    /// Formats a turn's planned actions into the multi-line log message emitted
    /// at the planning→execution commit point.
    /// </summary>
    public static class ActionDumpFormatter
    {
        public static string Format(int turn, IEnumerable<ActionSnapshot> actions)
        {
            if (actions == null)
            {
                throw new ArgumentNullException(nameof(actions));
            }

            var sorted = actions.OrderBy(a => a.OwnerId).ThenBy(a => a.StartTime).ToList();
            var noun = sorted.Count == 1 ? "action" : "actions";
            var sb = new StringBuilder();
            sb.Append($"[pb-and-j] action dump | turn {turn} | {sorted.Count} {noun}");

            foreach (var a in sorted)
            {
                sb.Append('\n');
                sb.Append(string.Format(
                    CultureInfo.InvariantCulture,
                    "  {0} (#{1}): {2} @{3:F2}s +{4:F2}s",
                    a.OwnerName, a.OwnerId, a.DataKey, a.StartTime, a.Duration));
                if (a.Locked)
                {
                    sb.Append(" [locked]");
                }
            }
            return sb.ToString();
        }
    }
}
