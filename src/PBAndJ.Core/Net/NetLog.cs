using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Composes every human-readable networking line the glue logs. The glue
    /// itself contains no formatting — it calls here and hands the result to
    /// Debug.Log, exactly as <c>LoadBanner</c> does for the load banner.
    /// </summary>
    /// <remarks>
    /// These strings are the in-game smoke checklist's assertions, so they are
    /// pinned by exact-string tests. Changing one means changing its test.
    /// </remarks>
    public static class NetLog
    {
        private const string Prefix = "[pb-and-j] ";

        // --- session lifecycle ---

        public static string HostListening(string bindAddress, int port, int protocolVersion, int slots)
        {
            RequireText(bindAddress, nameof(bindAddress));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "host listening on {0}:{1} | protocol v{2} | slots {3}",
                bindAddress, port, protocolVersion, slots);
        }

        public static string ClientConnecting(string hostAddress, int port, string playerName)
        {
            RequireText(hostAddress, nameof(hostAddress));
            RequireText(playerName, nameof(playerName));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "connecting to {0}:{1} as '{2}'", hostAddress, port, playerName);
        }

        public static string SessionClosed(int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "session closed | {0} peer{1} | listener stopped", peerCount, Plural(peerCount));
        }

        public static string PumpFailed(string detail)
        {
            RequireText(detail, nameof(detail));
            return Prefix + "networking stopped after an error — " + detail;
        }

        public static string TransportFailed(string detail)
        {
            RequireText(detail, nameof(detail));
            return Prefix + "transport failed — " + detail;
        }

        // --- handshake ---

        public static string PeerConnected(int peerId, string? remote)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "peer connected: #{0} from {1}", peerId, Describe(remote));
        }

        public static string HandshakeOk(int peerId, string name, int protocolVersion, string? modVersion)
        {
            RequireText(name, nameof(name));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "handshake ok: #{0} '{1}' | protocol v{2} | mod v{3}",
                peerId, name, protocolVersion, Describe(modVersion));
        }

        public static string HandshakeRejected(string? name, RejectReason reason, string? detail)
        {
            var line = Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "rejected '{0}': {1}", Describe(name), reason);
            return detail == null ? line : line + " (" + detail + ")";
        }

        public static string Welcomed(int peerId, string? sessionId, string? hostName, int turn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "welcome | peer #{0} | session {1} | host '{2}' | turn {3}",
                peerId, Describe(sessionId), Describe(hostName), turn);
        }

        public static string PeerLeft(int peerId, string? name, string? reason)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "peer left: #{0} '{1}' ({2})", peerId, Describe(name), Describe(reason));
        }

        public static string SessionSummary(IReadOnlyList<string> participants)
        {
            if (participants == null)
            {
                throw new ArgumentNullException(nameof(participants));
            }
            var sb = new StringBuilder();
            sb.Append(Prefix).Append("session: ").Append(participants.Count)
                .Append(" participant").Append(Plural(participants.Count));
            if (participants.Count > 0)
            {
                sb.Append(" (");
                for (var i = 0; i < participants.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(participants[i]);
                }
                sb.Append(')');
            }
            return sb.ToString();
        }

        public static string Assignment(UnitAssignments assignments)
        {
            if (assignments == null)
            {
                throw new ArgumentNullException(nameof(assignments));
            }
            var sb = new StringBuilder();
            sb.Append(Prefix).Append("assignment:");
            for (var i = 0; i < assignments.PeerIds.Count; i++)
            {
                var peerId = assignments.PeerIds[i];
                if (i > 0)
                {
                    sb.Append(" |");
                }
                sb.Append(" #").Append(peerId.ToString(CultureInfo.InvariantCulture)).Append(" <- ");
                var units = assignments.UnitsFor(peerId);
                if (units.Count == 0)
                {
                    sb.Append("(none)");
                }
                else
                {
                    for (var u = 0; u < units.Count; u++)
                    {
                        if (u > 0)
                        {
                            sb.Append(", ");
                        }
                        sb.Append(units[u]);
                    }
                }
            }
            return sb.ToString();
        }

        // --- the turn barrier ---

        public static string ReadyReceived(int peerId, string? name, int turn, int orderCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "ready from #{0} '{1}' | turn {2} | {3} order{4}",
                peerId, Describe(name), turn, orderCount, Plural(orderCount));
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

        public static string MailboxOverflowed(int dropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "mailbox overflowed — dropped {0} event{1}", dropped, Plural(dropped));
        }

        public static string Status(string role, string state, int turn, int participants, int ready)
        {
            RequireText(role, nameof(role));
            RequireText(state, nameof(state));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "session {0} | state {1} | turn {2} | participants {3} | ready {4}/{3}",
                role, state, turn, participants, ready);
        }

        public static string NoSession()
        {
            return Prefix + "no session — use pbj.host or pbj.join";
        }

        private static string Plural(int count) => count == 1 ? string.Empty : "s";

        private static string Describe(string? value) => string.IsNullOrEmpty(value) ? "?" : value!;

        private static void RequireText(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Value must be a non-empty string.", paramName);
            }
        }
    }
}
