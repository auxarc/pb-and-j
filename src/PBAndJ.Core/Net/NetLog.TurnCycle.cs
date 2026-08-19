using System.Globalization;

namespace PBAndJ.Core.Net
{
    // The turn barrier and the orders that cross it: who is ready, whose orders were
    // taken or refused, and the commit and digest that close the turn.
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
        // --- the turn barrier ---

        public static string ReadyReceived(int peerId, string? name, int turn, int orderCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "ready from #{0} '{1}' | turn {2} | {3} order{4}",
                peerId, Describe(name), turn, orderCount, Plural(orderCount));
        }

        /// <summary>
        /// What the Ready batch dropped before sending. Never silent: this filter
        /// is the only thing between a genuine order and never being submitted,
        /// so the count has to be visible when it is wrong.
        /// </summary>
        public static string OrdersNotOurs(int dropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "held back {0} order{1} for units that are not ours", dropped, Plural(dropped));
        }

        public static string BarrierWaiting(int readyCount, int participantCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "barrier {0}/{1} — waiting", readyCount, participantCount);
        }

        public static string BarrierCommitting(int readyCount, int participantCount, int turn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "barrier {0}/{1} — committing turn {2}", readyCount, participantCount, turn);
        }

        public static string ReadyIgnoredStale(int peerId, int turn, int currentTurn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "ignoring stale ready from #{0} for turn {1} (now on turn {2})", peerId, turn, currentTurn);
        }

        public static string ReadyNeedsResync(int peerId, int turn, int currentTurn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "peer #{0} is ahead (ready for turn {1}, host on turn {2}) — resyncing",
                peerId, turn, currentTurn);
        }

        // --- orders and commit ---

        public static string OrdersApplied(int applied, int rejected)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "applied {0} remote order{1}, {2} rejected", applied, Plural(applied), rejected);
        }

        public static string OrderRejectedUnowned(int peerId, string? unitName)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "order REJECTED from #{0}: {1} is not assigned to that peer", peerId, Describe(unitName));
        }

        public static string OrderRejectedByGame(int peerId, string? unitName, string? blueprint, OrderApplyResult result)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "order REJECTED from #{0}: {1} '{2}' — {3}",
                peerId, Describe(unitName), Describe(blueprint), result);
        }

        public static string OrderResultSent(int peerId, int accepted, int rejected)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "order result to #{0}: {1} accepted, {2} rejected", peerId, accepted, rejected);
        }

        public static string OrderResultReceived(int turn, int accepted, int rejected)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} orders: {1} accepted, {2} rejected by host", turn, accepted, rejected);
        }

        public static string UnreadyReceived(int peerId, string? name, int turn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "un-ready from #{0} '{1}' for turn {2}", peerId, Describe(name), turn);
        }

        public static string UnreadyIgnored(int peerId, int turn, string why)
        {
            RequireText(why, nameof(why));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "ignoring un-ready from #{0} for turn {1} — {2}", peerId, turn, why);
        }

        public static string TurnCommitted(int turn)
        {
            return Prefix + string.Format(CultureInfo.InvariantCulture, "turn {0} committed", turn);
        }

        public static string CommitRefused(int turn)
        {
            // ConfirmExecution fails silently; without this line a wedged
            // session would be invisible.
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "commit REFUSED for turn {0} — staying in planning, peers unlocked", turn);
        }

        public static string TurnCompleted(int turn, string? digest, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} complete | digest {1} | broadcast to {2} peer{3}",
                turn, Describe(digest), peerCount, Plural(peerCount));
        }

        public static string DigestMatched(int turn, string? digest)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture, "turn {0} digest {1} OK", turn, Describe(digest));
        }

        public static string DigestDiverged(int turn, string? expected, string? actual)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} DIVERGED | host {1} | local {2}", turn, Describe(expected), Describe(actual));
        }
    }
}
