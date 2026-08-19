using System.Globalization;

namespace PBAndJ.Core.Net
{
    // Entering and leaving a fight: shipping it, offering it, who got in, and who was
    // still loading when it ended.
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
        // --- combat lifecycle ---

        /// <summary>The host is in a fight and has not shipped it yet. M12b.</summary>
        public static string CombatShipping(int turn, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "in combat on turn {0} — writing the fight for {1} peer{2}",
                turn, peerCount, Plural(peerCount));
        }

        public static string CombatShipFailed() =>
            Prefix + "could not write the fight to share — starting alone";

        public static string CombatNobodyToWaitFor() =>
            Prefix + "nobody else is here — starting combat without offering it";

        public static string CombatOffered(string? saveName, string? digest, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "offering the fight '{0}' ({1}) to {2} peer{3}",
                saveName ?? "?", digest ?? "?", peerCount, Plural(peerCount));
        }

        public static string CombatEntryReported(int peerId, string? name, LoadOutcome outcome) =>
            Prefix + ("into the fight " + Describe(outcome) + " from #" + peerId + " '" + (name ?? "?") + "'");

        /// <summary>The fight was abandoned while people were still loading it.</summary>
        public static string CombatEntryAbandoned(int waiting) =>
            Prefix + ("left the fight before " + waiting + " machine" + Plural(waiting)
                + " got into it — the entry is off");

        /// <summary>A fight was written after the host had already left it.</summary>
        public static string CombatShipTooLate() =>
            Prefix + "a fight was written, but we are no longer in it — not offering it";

        /// <summary>
        /// A fight was written on a machine that is not hosting one. M12b.
        /// </summary>
        /// <remarks>
        /// The glue is armed by an effect and answers frames later, which is long
        /// enough for the player to have stopped hosting. Logged rather than
        /// swallowed: the write really happened, and a file appearing with no
        /// explanation is worse than a line saying why it went nowhere.
        /// </remarks>
        public static string CombatShipNotOurs() =>
            Prefix + "a fight was written here, but this machine is not hosting one — ignoring it";

        public static string CombatEntryTimedOut(int peerId) =>
            Prefix + ("no word from #" + peerId + " about the fight after "
                + PbjProtocol.LoadTimeoutSeconds + "s — starting without it");

        /// <summary>A client was offered the fight it is already holding.</summary>
        public static string CombatAlreadyHeld(string? saveName) =>
            Prefix + ("already holding the fight '" + (saveName ?? "?") + "' — loading it");

        public static string CombatFetching(string? saveName) =>
            Prefix + ("fetching the fight '" + (saveName ?? "?") + "' from the host");

        public static string CombatStarted(int turn, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "combat started on turn {0} — announcing to {1} peer{2}", turn, peerCount, Plural(peerCount));
        }

        public static string CombatEnded(int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "combat ended — unlocking {0} peer{1}", peerCount, Plural(peerCount));
        }

        public static string CombatStateObserved(bool inCombat)
        {
            return Prefix + "host reports combat " + (inCombat ? "started" : "ended");
        }

        public static string CombatStartedByHost(int turn)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture, "host started combat on turn {0}", turn);
        }

        /// <summary>
        /// Says out loud that execute is being held, because the alternative is
        /// a button that looks live and silently does nothing.
        /// </summary>
        /// <remarks>
        /// A host retrying a fight leaves combat before re-entering it, so this
        /// is routinely an interregnum rather than an ending. The client keeps
        /// standing in the loaded battle throughout and gets a fresh
        /// <c>CombatStart</c> moments later.
        /// </remarks>
        public static string CombatEndedByHost()
        {
            return Prefix + "host's combat ended — back to the lobby, holding execute until they return";
        }
    }
}
