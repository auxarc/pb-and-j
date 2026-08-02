using System;
using System.Globalization;

namespace PBAndJ.Core
{
    /// <summary>
    /// Composes the log line reporting the outcome of an order injection attempt.
    /// </summary>
    public static class InjectionReport
    {
        public static string Compose(string unitName, bool valid, int actionId, float startTime, float duration)
        {
            if (string.IsNullOrWhiteSpace(unitName))
            {
                throw new ArgumentException("Unit name must be a non-empty string.", nameof(unitName));
            }
            if (!valid)
            {
                return $"[pb-and-j] injection REJECTED for {unitName} (valid=False) — action was not accepted by the game";
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "[pb-and-j] injected move for {0}: action #{1} @{2:F2}s +{3:F2}s | valid=True",
                unitName, actionId, startTime, duration);
        }
    }
}
