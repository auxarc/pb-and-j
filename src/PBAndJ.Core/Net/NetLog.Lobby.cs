using System;
using System.Globalization;

namespace PBAndJ.Core.Net
{
    // Getting everyone into the same game: offering and transferring a save (M9), and
    // the lobby that picks one and agrees to load it (M11a).
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
        // --- scenario transfer (M9) ---

        public static string ScenarioOffered(int peerId, string? saveName, int totalBytes, string? digest)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "offered scenario '{0}' ({1:N0} bytes, {2}) to peer #{3}",
                Describe(saveName), totalBytes, Describe(digest), peerId);
        }

        /// <summary>
        /// The host has a save directory but it is not something we would send.
        /// A warning, not silence: the alternative is a session where the
        /// transfer never happens and nobody can say why.
        /// </summary>
        public static string ScenarioNotOffered(string? saveName, ScenarioRejection reason)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "not offering scenario '{0}': {1}", Describe(saveName), reason);
        }

        /// <summary>
        /// A null digest is the manual pull — "whatever you have now" — and says
        /// so rather than rendering as a placeholder nobody can interpret.
        /// </summary>
        public static string ScenarioRequested(string? digest)
        {
            return Prefix + (digest == null
                ? "requesting the host's current scenario"
                : "requesting the host's scenario (" + digest + ")");
        }

        public static string ScenarioAlreadyHeld(string? digest)
        {
            return Prefix + "scenario " + Describe(digest) + " is already on disk — nothing to transfer";
        }

        public static string ScenarioSent(int peerId, string? saveName, int totalBytes)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "sent scenario '{0}' ({1:N0} bytes) to peer #{2}",
                Describe(saveName), totalBytes, peerId);
        }

        public static string ScenarioReceived(string? saveName, int fileCount, long totalBytes)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "scenario '{0}' received | {1} file{2}, {3:N0} bytes",
                Describe(saveName), fileCount, Plural(fileCount), totalBytes);
        }

        /// <summary>
        /// Refused before a byte reached the disk. Names the reason because this
        /// is the one path where a peer's bytes were about to become files.
        /// </summary>
        public static string ScenarioRefused(string? saveName, ScenarioRejection reason)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "refused scenario '{0}': {1}", Describe(saveName), reason);
        }

        public static string ScenarioDigestMismatch(string? claimed, string? actual)
        {
            return Prefix + "refused scenario: sender claimed " + Describe(claimed)
                + " but the bytes digest to " + Describe(actual);
        }

        /// <remarks>
        /// Two callers, two different truths. M9's scenario slot really does
        /// need a manual load — nothing else enters it. A lobby's campaign save
        /// needs no step at all: M11d loads it on every machine the moment the
        /// lobby agrees. Telling a player in a co-op campaign to run
        /// pbj.combat-load would have them drop a combat scenario on top of it.
        /// Observed on a running two-party session, 2026-08-07.
        /// </remarks>
        public static string ScenarioWritten(string? saveName)
        {
            var manual = string.Equals(saveName, LobbySaveNames.ScenarioSlot, StringComparison.Ordinal);
            return Prefix + (manual ? "scenario written to '" : "save written to '") + Describe(saveName)
                + (manual
                    ? "' — run pbj.combat-load to enter it"
                    : "' — the lobby will load it when everyone is ready");
        }

        public static string ScenarioWriteFailed(string? saveName)
        {
            return Prefix + "could not write scenario to '" + Describe(saveName) + "' — see the log above";
        }

        public static string ScenarioUnavailable()
        {
            return Prefix + "no combat save to send — run pbj.combat-save in a combat first";
        }

        // --- lobby (M11a) ---

        public static string LobbySelected(string? saveKey, string? digest, int selectionVersion)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby save is now '{0}' ({1}) | selection {2} — everyone must ready again",
                Describe(saveKey), Describe(digest), selectionVersion);
        }

        public static string LobbySelectionCleared(int selectionVersion)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby save cleared | selection {0}", selectionVersion);
        }

        public static string LobbySelectIgnored(string why)
        {
            RequireText(why, nameof(why));
            return Prefix + "ignoring lobby save selection — " + why;
        }

        public static string LobbyReadyReceived(int peerId, string? name, int selectionVersion)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby ready from #{0} '{1}' for selection {2}",
                peerId, Describe(name), selectionVersion);
        }

        public static string LobbyUnreadyReceived(int peerId, string? name, int selectionVersion)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby unready from #{0} '{1}' for selection {2}",
                peerId, Describe(name), selectionVersion);
        }

        public static string LobbyReadyIgnored(int peerId, int selectionVersion, string why)
        {
            RequireText(why, nameof(why));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "ignoring lobby ready from #{0} for selection {1} — {2}",
                peerId, selectionVersion, why);
        }

        /// <remarks>
        /// Says "misbehaving", not "resyncing", unlike its turn-barrier
        /// counterpart. Only the host mints a selection version and it never
        /// rewinds, so a peer claiming one we have not reached did not fall
        /// behind honestly — see <c>LobbyBarrier.SetReady</c>.
        /// </remarks>
        public static string LobbyReadyAhead(int peerId, int selectionVersion, int currentSelection)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "peer #{0} claims lobby selection {1} but the host is on {2} — resending the lobby state",
                peerId, selectionVersion, currentSelection);
        }

        public static string LobbyBarrierWaiting(int readyCount, int participantCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby {0}/{1} ready", readyCount, participantCount);
        }

        public static string LobbyBarrierSatisfied(int participantCount, string? saveKey)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby {0}/{0} ready for '{1}' — everyone has agreed",
                participantCount, Describe(saveKey));
        }

        public static string LobbyStateReceived(
            int selectionVersion, string? saveKey, int readyCount, int participantCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "lobby state | selection {0} | save '{1}' | {2}/{3} ready",
                selectionVersion, Describe(saveKey), readyCount, participantCount);
        }

        public static string LoadStarting(int participantCount, string? saveKey) =>
            Prefix + ("loading '" + (saveKey ?? "?") + "' on " + participantCount + " machine(s) — everyone agreed");

        public static string LoadReported(int peerId, string? name, LoadOutcome outcome) =>
            Prefix + ("load " + Describe(outcome) + " from #" + peerId + " '" + (name ?? "?") + "'");

        public static string LoadTimedOut(int peerId) =>
            Prefix + ("no word from #" + peerId + " after " + PbjProtocol.LoadTimeoutSeconds
                + "s — carrying on without it");

        public static string LoadComplete(int loadedCount, int participantCount) =>
            Prefix + ("load complete | " + loadedCount + " of " + participantCount + " machine(s) are in");

        /// <remarks>
        /// The host is not a peer that can be carried on without: it is the
        /// session. If its own load does not happen, nothing has happened.
        /// </remarks>
        public static string LoadAbandoned() =>
            Prefix + ("the host could not load — abandoning, the lobby is open again");

        public static string LoadIgnoredStale(int instructed, int held) =>
            Prefix + ("ignoring a load for selection " + instructed + " — we hold " + held);

        public static string LoadAlreadyBegun(int selectionVersion) =>
            Prefix + ("already loading selection " + selectionVersion + " — ignoring the repeat");

        public static string LobbySelectIsHostOnly()
        {
            return Prefix + "only the host picks the lobby save";
        }
    }
}
