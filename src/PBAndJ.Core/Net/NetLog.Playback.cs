using System.Globalization;

namespace PBAndJ.Core.Net
{
    // What a client is shown rather than simulates: snapshot corrections, the
    // keyframes it plays back, and the poses that carry a mech's limbs.
    //
    // Class-level XML doc lives only in NetLog.cs -- /// on a partial part is
    // concatenated by the compiler into one type entry.
    public static partial class NetLog
    {
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
    }
}
