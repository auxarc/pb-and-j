using System;

namespace PBAndJ.Core
{
    /// <summary>
    /// Composes the log lines emitted by the game-glue layer. The glue itself
    /// (ModLink subclass, Harmony patches) contains no logic — it delegates here.
    /// </summary>
    public static class LoadBanner
    {
        public static string Compose(string modId, string version)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                throw new ArgumentException("Mod id must be a non-empty string.", nameof(modId));
            }
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException("Version must be a non-empty string.", nameof(version));
            }
            return $"[{modId.Trim()}] v{version.Trim()} — core loaded";
        }

        public static string PatchFired(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new ArgumentException("Patch target must be a non-empty string.", nameof(target));
            }
            return $"[pb-and-j] patch fired: {target.Trim()}";
        }
    }
}
