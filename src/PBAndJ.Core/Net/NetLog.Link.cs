using System.Globalization;

namespace PBAndJ.Core.Net
{
    // The link itself: holding it open, losing a peer and taking them back, and the
    // outbound queue when it backs up or cannot send.
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
        // --- keepalive ---

        public static string PeerTimedOut(int peerId, string? name, double silentSeconds)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "peer #{0} '{1}' silent for {2:F0}s — dropping", peerId, Describe(name), silentSeconds);
        }

        public static string HostTimedOut(double silentSeconds)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "host silent for {0:F0}s — connection lost, continuing single-player", silentSeconds);
        }

        // --- reconnect ---

        public static string PeerHeldForReconnect(int peerId, string? name, double graceSeconds)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "holding #{0} '{1}' units for {2:F0}s in case they reconnect",
                peerId, Describe(name), graceSeconds);
        }

        public static string PeerRejoined(int oldPeerId, int newPeerId, string? name)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' rejoined as #{1} (was #{2}) — units rebound",
                Describe(name), newPeerId, oldPeerId);
        }

        public static string ReconnectExpired(int peerId, string? name)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "#{0} '{1}' did not return — releasing their units",
                peerId, Describe(name));
        }

        public static string Rejoining(string? sessionId, int claimedPeerId)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "rejoining session {0} as peer #{1}", Describe(sessionId), claimedPeerId);
        }

        // --- the outbound queue ---
        //
        // Composed here, called from the transports' writer threads, and posted
        // as a TransportLogEvent rather than logged directly: a background thread
        // must never touch the log sink. Composing a string touches nothing but
        // the string.

        public static string SendQueueBacklog(int peerId, int queuedBytes, int queuedFrames)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "send queue backing up for #{0}: {1} frame(s), {2} byte(s) — slow link",
                peerId, queuedFrames, queuedBytes);
        }

        public static string SendQueueOverflowed(int peerId, int queuedBytes, int queuedFrames)
        {
            // Dropping a frame is not an option — the protocol is a stateful
            // stream with no resend, so a lost TurnCommit would strand that peer
            // undetectably. The peer goes instead.
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "send queue OVERFLOWED for #{0} at {1} frame(s), {2} byte(s) — dropping the peer",
                peerId, queuedFrames, queuedBytes);
        }

        public static string SendFailed(int peerId, string detail)
        {
            RequireText(detail, nameof(detail));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture, "send to #{0} failed: {1}", peerId, detail);
        }

        public static string SendAfterStop(int peerId)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "dropping a frame for #{0}: the transport is stopped", peerId);
        }

        public static string MailboxOverflowed(int dropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "mailbox overflowed — dropped {0} event{1}", dropped, Plural(dropped));
        }
    }
}
