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

        public static string AssignedUnits(IReadOnlyList<string> units)
        {
            if (units == null)
            {
                throw new ArgumentNullException(nameof(units));
            }
            if (units.Count == 0)
            {
                return Prefix + "you control no units this combat";
            }
            var sb = new StringBuilder();
            sb.Append(Prefix).Append("you control: ");
            for (var i = 0; i < units.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(units[i]);
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

        // --- snapshots ---

        public static string SnapshotSent(int turn, int unitCount, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} snapshot | {1} unit{2} | broadcast to {3} peer{4}",
                turn, unitCount, Plural(unitCount), peerCount, Plural(peerCount));
        }

        public static string SnapshotVerified(int turn, int unitCount, string? digest)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} corrected | {1} unit{2} | digest {3} OK",
                turn, unitCount, Plural(unitCount), Describe(digest));
        }

        public static string SnapshotStillDiverged(int turn, string? expected, string? actual)
        {
            // Loud on purpose: correction landing and the result still not
            // matching means the two sides disagree about which units exist,
            // which no amount of position-setting can fix.
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} STILL DIVERGED after correction | host {1} | local {2}",
                turn, Describe(expected), Describe(actual));
        }

        public static string SnapshotUnitsSkipped(int missingLocally, int missingRemotely)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "snapshot: {0} unit(s) not present locally, {1} local unit(s) not in the snapshot",
                missingLocally, missingRemotely);
        }

        public static string SnapshotClamped(int captured, int cap)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "snapshot clamped: {0} units captured, only {1} fit — the rest are NOT corrected",
                captured, cap);
        }

        /// <summary>
        /// Announces that the listener is reachable from off this machine.
        /// </summary>
        /// <remarks>
        /// A warning, not an info line, and worded so it cannot be mistaken for
        /// routine. Everything before M7 bound loopback only; nobody should
        /// discover after the fact that their game was accepting connections
        /// from the network.
        /// </remarks>
        public static string HostListeningOpenly(string bindAddress, int port)
        {
            RequireText(bindAddress, nameof(bindAddress));
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "OPEN LISTENER on {0}:{1} — reachable from outside this machine. "
                + "A passphrase is required, but it travels in the clear over plain TCP. "
                + "Stop it with pbj.net-stop when you are done.",
                bindAddress, port);
        }

        public static string HandshakeTimedOut(int peerId, double seconds)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "socket #{0} connected but never handshook within {1:F0}s — dropping", peerId, seconds);
        }

        // --- keyframes (M6) ---

        public static string KeyframesSent(
            int turn, int trackCount, int keyCount, float windowStart, float windowEnd, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} keyframes | {1} tracks, {2} keys | {3:F2}s-{4:F2}s | broadcast to {5} peer{6}",
                turn, trackCount, keyCount, windowStart, windowEnd, peerCount, Plural(peerCount));
        }

        public static string KeyframesReceived(
            int turn, int trackCount, int keyCount, float windowStart, float windowEnd)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} keyframes received | {1} tracks, {2} keys | {3:F2}s of motion",
                turn, trackCount, keyCount, windowEnd - windowStart);
        }

        /// <summary>
        /// The recorder had nothing. Informational, not a warning.
        /// </summary>
        /// <remarks>
        /// Expected whenever the scenario runs with prediction disabled, since
        /// the game only starts its replay recorder when prediction is on. The
        /// turn still completes and snapshot correction still lands.
        /// </remarks>
        public static string KeyframesUnavailable()
        {
            return Prefix + "no keyframes recorded this turn — snapshot correction only";
        }

        public static string KeyframesClamped(int captured, int cap, int thinned)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "keyframes clamped: {0} tracks captured, only {1} fit; {2} track(s) thinned",
                captured, cap, thinned);
        }

        /// <summary>
        /// The correction changed which units this machine is drawing.
        /// </summary>
        /// <remarks>
        /// Logged only on the edge, never every turn, because the steady state
        /// is "nothing changed" and a line per turn would bury the one that
        /// matters. The counts are what make it a diagnosis rather than a
        /// notice: a client that quietly diverges on visibility shows a
        /// different battlefield from the host while every digest still reports
        /// OK, which is precisely how this went unnoticed until somebody looked
        /// at two screens at once.
        /// </remarks>
        public static string VisibilityCorrected(int revealed, int hidden)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "visibility corrected | {0} unit{1} revealed, {2} hidden",
                revealed, Plural(revealed), hidden);
        }

        // --- poses (M8) ---

        public static string PosesSent(int turn, int partCount, int peerCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} poses | {1} unit track{2} | broadcast to {3} peer{4}",
                turn, partCount, Plural(partCount), peerCount, Plural(peerCount));
        }

        /// <summary>
        /// The turn plays with skeletal animation.
        /// </summary>
        public static string PosesReceived(int turn, int trackCount)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} poses complete | {1} unit track{2} | playing the battle",
                turn, trackCount, Plural(trackCount));
        }

        /// <summary>
        /// The turn falls back to M6's transform-only playback.
        /// </summary>
        /// <remarks>
        /// Deliberately logged every time rather than only on the interesting
        /// arm. A turn that slides instead of walking is the one symptom a
        /// player can see and cannot explain, and "poses 3 of 8" is the
        /// difference between a bug report and a diagnosis. The wording says
        /// what the player is looking at, not what the code did.
        /// </remarks>
        public static string PosesIncomplete(int turn, int held, int expected)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} poses incomplete — {1} of {2} arrived | units will slide, not walk",
                turn, held, expected);
        }

        /// <summary>
        /// The recorder held pose data the host could not turn into tracks.
        /// </summary>
        /// <remarks>
        /// Two losses that look identical from the outside and have different
        /// causes, so both are named. A unit with no recorded bones is not
        /// posed by the host's own replay either, and a key whose joint array
        /// no longer matches the current skeleton belongs to a rebuild that
        /// happened mid-turn. Neither is fatal and neither is visible in the
        /// track counts alone, which is exactly why they are said out loud —
        /// a unit that slides while its neighbours walk is otherwise a symptom
        /// with no explanation anywhere in the log.
        /// </remarks>
        public static string PosesNotCaptured(int unitsWithoutBones, int keysDropped)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "poses partly uncaptured: {0} unit{1} without recorded bones, "
                    + "{2} key{3} whose skeleton no longer matches",
                unitsWithoutBones, Plural(unitsWithoutBones),
                keysDropped, Plural(keysDropped));
        }

        /// <summary>
        /// The host could not put this turn's poses on the wire at all.
        /// </summary>
        /// <remarks>
        /// Names the whole turn, because that is the unit of the decision: one
        /// unrepairable track demotes every unit to sliding rather than leaving
        /// one statue among walkers.
        /// </remarks>
        public static string PosesUnsendable(int turn, PoseTrackFault fault, string? unit)
        {
            return Prefix + string.Format(
                CultureInfo.InvariantCulture,
                "turn {0} poses dropped: {1} on '{2}' — the whole turn plays transform-only",
                turn, fault, unit ?? "(unnamed)");
        }

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

        private static string Describe(LoadOutcome outcome)
        {
            switch (outcome)
            {
                case LoadOutcome.Loaded:
                    return "OK";
                case LoadOutcome.Refused:
                    return "REFUSED (the game would not start it)";
                case LoadOutcome.Unavailable:
                    return "UNAVAILABLE (no such save, or a different one)";
                default:
                    // A peer can put any byte on the wire; the decoder casts it
                    // unvalidated, exactly as it does for RejectReason.
                    return "UNKNOWN (" + (int)outcome + ")";
            }
        }

        public static string LoadIgnoredStale(int instructed, int held) =>
            Prefix + ("ignoring a load for selection " + instructed + " — we hold " + held);

        public static string LoadAlreadyBegun(int selectionVersion) =>
            Prefix + ("already loading selection " + selectionVersion + " — ignoring the repeat");

        public static string LobbySelectIsHostOnly()
        {
            return Prefix + "only the host picks the lobby save";
        }

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

        public static string CombatEndedByHost()
        {
            return Prefix + "host's combat ended — back to the lobby";
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
