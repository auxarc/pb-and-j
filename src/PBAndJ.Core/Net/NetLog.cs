using System;

namespace PBAndJ.Core.Net
{
    // Where to start reading. The prefix every line carries and the few formatters the
    // parts share: the plural 's', the '?' for a missing string, the argument check, and
    // the LoadOutcome wording.
    //
    // Describe(LoadOutcome) sat inside the lobby section in the original. Its two call
    // sites are in different parts -- the lobby's load report and combat entry -- so it
    // is shared fixture, not lobby's.
    //
    // NetLog is one class split across this file and its siblings; this one holds what
    // they share.
    /// <summary>
    /// Composes every human-readable networking line the glue logs. The glue
    /// itself contains no formatting — it calls here and hands the result to
    /// Debug.Log, exactly as <c>LoadBanner</c> does for the load banner.
    /// </summary>
    /// <remarks>
    /// These strings are the in-game smoke checklist's assertions, so they are
    /// pinned by exact-string tests. Changing one means changing its test.
    /// </remarks>
    public static partial class NetLog
    {
        private const string Prefix = "[pb-and-j] ";

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
