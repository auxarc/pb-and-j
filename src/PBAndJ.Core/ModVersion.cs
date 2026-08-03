using System;
using System.Globalization;

namespace PBAndJ.Core
{
    /// <summary>
    /// A three-part mod version, comparable numerically.
    /// </summary>
    /// <remarks>
    /// Exists so the update check cannot compare versions as strings. "0.9.0"
    /// sorts after "0.10.0" lexically, so a string comparison would tell
    /// everyone on 0.9.0 that they were current — silently, and precisely at the
    /// moment the release they need exists. The version is also the handshake's
    /// real compatibility gate (see <c>PbjProtocol.ModVersion</c>), so getting
    /// this wrong reads as a netcode bug on someone else's machine.
    /// </remarks>
    public readonly struct ModVersion : IEquatable<ModVersion>, IComparable<ModVersion>
    {
        public ModVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        /// <summary>
        /// Reads <c>MAJOR.MINOR.PATCH</c>, tolerating a leading <c>v</c>.
        /// </summary>
        /// <remarks>
        /// The <c>v</c> is not cosmetic tolerance: GitHub release tags are
        /// conventionally <c>v0.5.0</c> while <c>mod/metadata.yaml</c> carries a
        /// bare <c>0.5.0</c>, and both name the same build.
        /// <para>
        /// Deliberately strict about everything else. A pre-release suffix or a
        /// fourth part is refused rather than guessed at, because the caller
        /// reports <see cref="UpdateStatus.Unknown"/> for a version it cannot
        /// read, and that is a far better outcome than confidently comparing
        /// something it misunderstood.
        /// </para>
        /// </remarks>
        public static bool TryParse(string? text, out ModVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text!.Trim();
            if (trimmed[0] == 'v' || trimmed[0] == 'V')
            {
                trimmed = trimmed.Substring(1);
            }

            var parts = trimmed.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            if (!TryPart(parts[0], out var major)
                || !TryPart(parts[1], out var minor)
                || !TryPart(parts[2], out var patch))
            {
                return false;
            }

            version = new ModVersion(major, minor, patch);
            return true;
        }

        /// <summary>
        /// One component: digits only, non-empty, and inside <c>int</c>.
        /// </summary>
        /// <remarks>
        /// <c>int.TryParse</c> alone would accept a leading sign and surrounding
        /// whitespace, so "-1.0.0" would parse as a version that sorts below
        /// everything.
        /// </remarks>
        private static bool TryPart(string text, out int value)
        {
            value = 0;
            if (text.Length == 0)
            {
                return false;
            }
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        public int CompareTo(ModVersion other)
        {
            if (Major != other.Major)
            {
                return Major < other.Major ? -1 : 1;
            }
            if (Minor != other.Minor)
            {
                return Minor < other.Minor ? -1 : 1;
            }
            if (Patch != other.Patch)
            {
                return Patch < other.Patch ? -1 : 1;
            }
            return 0;
        }

        public bool Equals(ModVersion other) => CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is ModVersion other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return ((Major * 397) ^ Minor) * 397 ^ Patch;
            }
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);
        }
    }

    /// <summary>How this build compares with the newest published release.</summary>
    public enum UpdateStatus : byte
    {
        /// <summary>One or both versions could not be read. Never assume current.</summary>
        Unknown = 0,

        /// <summary>Running the newest release.</summary>
        Current = 1,

        /// <summary>A newer release exists.</summary>
        UpdateAvailable = 2,

        /// <summary>Running something newer than any release — a dev build.</summary>
        LocalAhead = 3,
    }

    /// <summary>The outcome of comparing this build with the newest release.</summary>
    public readonly struct UpdateResult
    {
        public UpdateResult(UpdateStatus status, ModVersion local, ModVersion latest)
        {
            Status = status;
            Local = local;
            Latest = latest;
        }

        public UpdateStatus Status { get; }
        public ModVersion Local { get; }
        public ModVersion Latest { get; }
    }

    /// <summary>
    /// Decides whether this build is out of date.
    /// </summary>
    /// <remarks>
    /// Pure, and separate from the fetch on purpose. Everything network- and
    /// JSON-shaped lives in the mod glue where it can use the game's own
    /// <c>UnityWebRequest</c> and the vendored Newtonsoft; what arrives here is
    /// two strings. That keeps the part that can be quietly wrong — the
    /// comparison — under the coverage gate, and keeps Core dependency-free.
    /// </remarks>
    public static class UpdateCheck
    {
        public static UpdateResult Compare(string? localVersion, string? latestVersion)
        {
            // Both parsed unconditionally rather than short-circuited, so the
            // result always carries whatever each side did yield — a caller
            // reporting Unknown can still say which of the two it failed to read.
            var localReadable = ModVersion.TryParse(localVersion, out var local);
            var latestReadable = ModVersion.TryParse(latestVersion, out var latest);

            if (!localReadable || !latestReadable)
            {
                return new UpdateResult(UpdateStatus.Unknown, local, latest);
            }

            var order = local.CompareTo(latest);
            var status = order < 0
                ? UpdateStatus.UpdateAvailable
                : order > 0
                    ? UpdateStatus.LocalAhead
                    : UpdateStatus.Current;

            return new UpdateResult(status, local, latest);
        }
    }
}
