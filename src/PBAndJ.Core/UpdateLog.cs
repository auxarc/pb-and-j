using System.Globalization;

namespace PBAndJ.Core
{
    /// <summary>
    /// The lines the update check emits.
    /// </summary>
    /// <remarks>
    /// Here rather than in the glue for the same reason <c>NetLog</c> is: the
    /// wording is the whole user-facing surface of this feature, and a line that
    /// says "up to date" when it means "could not tell" is the exact failure the
    /// check exists to prevent — someone sitting on a stale build wondering why
    /// the handshake keeps refusing them.
    /// </remarks>
    public static class UpdateLog
    {
        private const string Prefix = "[pb-and-j] ";

        public static string Describe(UpdateResult result, string? downloadUrl)
        {
            switch (result.Status)
            {
                case UpdateStatus.UpdateAvailable:
                    return Prefix + string.Format(
                        CultureInfo.InvariantCulture,
                        "a newer build exists: {0} (you have {1}){2}",
                        result.Latest, result.Local,
                        string.IsNullOrWhiteSpace(downloadUrl) ? string.Empty : " — " + downloadUrl);

                case UpdateStatus.Current:
                    return Prefix + string.Format(
                        CultureInfo.InvariantCulture,
                        "mod is up to date ({0})", result.Local);

                case UpdateStatus.LocalAhead:
                    // Never phrased as an update. Somebody running a dev build
                    // must not be told to install something older than their own
                    // work, and a forgotten release should look like one.
                    return Prefix + string.Format(
                        CultureInfo.InvariantCulture,
                        "running {0}, newer than the latest release ({1}) — a dev build, or a release was never cut",
                        result.Local, result.Latest);

                default:
                    return Prefix + "could not tell whether this build is current — version strings unreadable";
            }
        }

        /// <summary>
        /// Reaching GitHub failed. Informational, not a warning: the session
        /// works perfectly well without ever knowing.
        /// </summary>
        public static string CheckFailed(string? reason)
        {
            return Prefix + "update check skipped — "
                + (string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason!);
        }

        /// <summary>
        /// The repo has never published a release.
        /// </summary>
        /// <remarks>
        /// A distinct line rather than a failed request, because GitHub answers
        /// <c>releases/latest</c> with a bare 404 in this case and it is a
        /// perfectly ordinary state — one that would otherwise put an alarming
        /// line in the log on every session start until the first release is cut.
        /// </remarks>
        public static string NoReleases()
        {
            return Prefix + "no releases published yet — nothing to compare this build against";
        }

        /// <summary>
        /// Announces the one outbound request the mod makes on its own account.
        /// </summary>
        /// <remarks>
        /// The mod's standing promise is that nothing leaves the machine unless a
        /// session is started. This request is made only once a session is being
        /// started, and it says so out loud rather than being discovered in a
        /// packet capture.
        /// </remarks>
        public static string Checking(string host)
        {
            return Prefix + "checking for a newer mod build (" + host + ")";
        }

        // --- the prompt ---
        //
        // Everything below lands on a label inside the game's own confirmation
        // dialog, so none of it carries <see cref="Prefix"/>: that marker exists
        // to pick this mod's lines out of a shared Player.log, and on a label it
        // is just noise. Sentence case and plain verbs, to sit alongside the
        // game's own UI rather than shouting over it.

        /// <summary>
        /// The dialog's title. Names the mod, because the dialog wears Phantom
        /// Brigade's styling and would otherwise read as the game asking.
        /// </summary>
        public static string PromptHeader()
        {
            return "pb-and-j — newer build available";
        }

        /// <summary>
        /// The dialog's body: what the gap is, what confirming does, and what
        /// the player has to do themselves.
        /// </summary>
        /// <remarks>
        /// This is the only place a second player ever finds out they are on a
        /// stale build — they are not reading Player.log — so it has to stand
        /// alone. It promises nothing the mod cannot do: the game loads mod
        /// assemblies once, during startup, into an AppDomain it cannot unload,
        /// so replacing the folder by hand and restarting is the whole procedure
        /// and the wording says so rather than implying an updater.
        /// </remarks>
        public static string PromptBody(UpdateResult result)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "You have {0}. The newest release is {1}.\n\n"
                + "Confirm to open the releases page in your browser. Download the mod zip, "
                + "replace your pb-and-j folder with the one inside it, then restart the game — "
                + "mods are loaded once at startup, so this cannot be swapped in while you play.",
                result.Local, result.Latest);
        }
    }
}
