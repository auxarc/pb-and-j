using System;
using System.Globalization;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Every word the connect screen shows.
    /// </summary>
    /// <remarks>
    /// A second register, separate from <see cref="NetLog"/> on purpose. Every
    /// line NetLog composes begins with <c>"[pb-and-j] "</c>, which is how this
    /// mod's output is picked out of a shared Player.log — and which is pure
    /// noise on a label. NetLog also renders enum values verbatim
    /// (<c>rejected 'ally': BadPassphrase</c>), which is exactly right for
    /// somebody debugging and useless to somebody who just wants to play.
    /// <para>
    /// The rules followed here: name what the player controls rather than what
    /// the code calls it; keep an action's name the same from button to status
    /// line; give direction instead of apologising; and let one element do one
    /// job, so a label labels and the warning beside it warns.
    /// </para>
    /// <para>
    /// Exact-string tested, like NetLog's lines. Changing one means changing its
    /// test, which is the point — this wording is the feature's whole surface.
    /// </para>
    /// </remarks>
    public static class ConnectText
    {
        private const string LogPrefix = "[pb-and-j] ";

        // --- chrome ---

        public static string Title() => "Multiplayer";

        /// <remarks>
        /// The hint rides on the caption rather than sitting in the field as
        /// placeholder text. NGUI's UIInput renders a hardcoded em-dash for any
        /// empty unselected field and never consults its defaultText on that
        /// path, so a placeholder is not available to us — and one field doing
        /// two jobs (the host to reach, or the interface to bind) needs the hint
        /// somewhere.
        /// </remarks>
        public static string AddressLabel() => "Address · hostname, IP, or 0.0.0.0 to host openly";

        public static string PortLabel() => "Port";

        public static string PassphraseLabel() => "Passphrase · optional for local play";

        public static string RememberLabel() => "Remember these details";

        /// <remarks>
        /// Ticking the box is consent to convenience. It is not consent to
        /// having a shared secret written to disk in the clear — unless the
        /// screen says so, which is what this line is for. The protocol already
        /// sends the passphrase in the clear, so the file is not the weak link;
        /// that is a reason to be candid about it, not to keep quiet.
        /// </remarks>
        public static string RememberWarning() =>
            "The passphrase is saved to a file on this machine and is not encrypted.";

        // --- actions, and the statuses that echo them ---

        public static string HostButton() => "Host";

        public static string JoinButton() => "Join";

        public static string Hosting(string bind, int port) =>
            string.Format(CultureInfo.InvariantCulture, "Hosting on {0}:{1}. Waiting for a peer.", bind, port);

        public static string Joining(string address, int port) =>
            string.Format(CultureInfo.InvariantCulture, "Joining {0}:{1}…", address, port);

        // --- what is wrong with what was typed ---

        public static string DescribeProblem(ConnectProblem problem)
        {
            switch (problem)
            {
                case ConnectProblem.None:
                    return string.Empty;

                case ConnectProblem.AddressEmpty:
                    return "Enter the address of the machine you want to reach.";

                case ConnectProblem.AddressMalformed:
                    return "That does not look like an address. Use just the host — no http://, no :port.";

                case ConnectProblem.BindNotAnIpAddress:
                    return "To host, this must be an address on this machine: 127.0.0.1 to keep it local, "
                        + "or 0.0.0.0 to let someone else reach you.";

                case ConnectProblem.PortUnreadable:
                    return "The port must be a number. The usual one is 27600.";

                case ConnectProblem.PortOutOfRange:
                    return "The port must be between 1 and 65535. The usual one is 27600.";

                case ConnectProblem.OpenBindNeedsPassphrase:
                    return "Hosting on an address others can reach needs a passphrase — "
                        + "without one, anything that finds the port can join and give your units orders.";

                default:
                    return "Something about these details is not usable.";
            }
        }

        // --- what the host said when it refused ---

        /// <remarks>
        /// Each of these names the thing the player can act on. "Refused" with a
        /// code sends somebody to check their firewall when the answer is a typo
        /// in the passphrase.
        /// </remarks>
        public static string DescribeRejection(RejectReason reason)
        {
            switch (reason)
            {
                case RejectReason.None:
                    return "The host refused the connection without saying why.";

                case RejectReason.BadPassphrase:
                    return "The host did not accept that passphrase. Check it and try again.";

                case RejectReason.ModVersionMismatch:
                    return "You and the host are running different builds of pb-and-j. "
                        + "Both machines need the same one.";

                case RejectReason.GameBuildMismatch:
                    return "You and the host are running different builds of Phantom Brigade. "
                        + "Both machines need the same one.";

                case RejectReason.VersionMismatch:
                    return "The host speaks a different version of the network protocol. "
                        + "Updating pb-and-j on both machines should fix it.";

                case RejectReason.SessionFull:
                    return "That session is full.";

                case RejectReason.DuplicateName:
                    return "Someone in that session is already using your player name.";

                case RejectReason.InvalidName:
                    return "The host would not accept your player name.";

                case RejectReason.NotAcceptingPeers:
                    return "That session is not letting anyone in at the moment.";

                case RejectReason.UnknownSession:
                    return "That session has ended. Join afresh rather than reconnecting.";

                case RejectReason.BadResumeToken:
                    return "The host did not recognise this machine as one that had dropped out. "
                        + "Join afresh.";

                case RejectReason.BadMagic:
                    return "Whatever is listening on that port is not a pb-and-j host.";

                default:
                    // A newer host can send a reason this build has no name for.
                    return "The host refused the connection for a reason this build does not recognise.";
            }
        }

        // --- the empty state ---

        /// <remarks>
        /// An empty screen is an invitation to act, not a status report. Naming
        /// the command is the difference between a dead end and a first step —
        /// and this is the state a second player hits first.
        /// </remarks>
        public static string NothingRemembered() =>
            "No connection saved yet. Join once with  pbj.join <address> <port> <passphrase>  "
            + "in the console, and it will be offered here next time.";

        public static string ConfirmJoin(string address, int port) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "Join {0}:{1}?", address, port);

        // --- borrowing the log's own lines ---

        /// <summary>
        /// Strips the log marker so a line composed for Player.log can be shown
        /// on a label unchanged.
        /// </summary>
        /// <remarks>
        /// <c>NetGlue.Host</c> and <c>Join</c> already return sentences worth
        /// showing; they just arrive wearing the marker. Rewriting them here
        /// would be a second copy to keep in step.
        /// </remarks>
        public static string ForScreen(string? logLine)
        {
            if (string.IsNullOrEmpty(logLine))
            {
                return string.Empty;
            }

            // Leading only. A line that quotes the marker mid-sentence keeps it.
            return logLine!.StartsWith(LogPrefix, StringComparison.Ordinal)
                ? logLine.Substring(LogPrefix.Length)
                : logLine;
        }
    }
}
