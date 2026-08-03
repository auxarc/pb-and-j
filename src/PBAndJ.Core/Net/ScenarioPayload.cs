using System;
using System.Collections.Generic;
using System.Globalization;

namespace PBAndJ.Core.Net
{
    /// <summary>Why a scenario was refused, or <see cref="None"/> if it was not.</summary>
    public enum ScenarioRejection : byte
    {
        None = 0,
        NoFiles = 1,
        TooManyFiles = 2,
        TooLarge = 3,
        DisallowedName = 4,
        DuplicateName = 5,
        MissingRequiredFile = 6,
    }

    /// <summary>One file of a transferred save.</summary>
    /// <remarks>
    /// The name is carried so the receiver can check it against an allowlist and
    /// so the digest can tell two different files apart — <b>never</b> so it can
    /// be used to build a path. See <see cref="ScenarioPayload"/>.
    /// </remarks>
    public sealed class ScenarioFile
    {
        private static readonly byte[] NoContent = new byte[0];

        public ScenarioFile(string? name, byte[]? content)
        {
            Name = name;
            Content = content ?? NoContent;
        }

        public string? Name { get; }

        public byte[] Content { get; }
    }

    /// <summary>
    /// A combat save, in transit. M9's answer to the manual folder copy stage 2
    /// used to need.
    /// </summary>
    /// <remarks>
    /// This is the only place the mod turns bytes off the wire into bytes on
    /// disk, so it is the only place where a peer that got through the passphrase
    /// can reach past the protocol and into the filesystem. The passphrase is a
    /// door lock and not an envelope (docs/design/networking.md, "Opt-in and
    /// privacy"), so the guards here are the real boundary, and they are
    /// deliberately paranoid in three independent ways:
    /// <list type="bullet">
    /// <item>the receiver takes the save <em>directory</em> name from its own
    /// constant, never from the wire — <see cref="SaveName"/> is logged and
    /// compared, and is not a path component;</item>
    /// <item><see cref="IsSafeName"/> rejects anything structurally capable of
    /// escaping a directory, independently of what is on the allowlist;</item>
    /// <item><see cref="IsAllowedName"/> then narrows to the two files the game's
    /// own <c>DoSave</c> writes.</item>
    /// </list>
    /// Each would be sufficient today. They are all here because the allowlist is
    /// the one most likely to be widened later, and widening it should not
    /// silently re-open traversal.
    /// </remarks>
    public sealed class ScenarioPayload
    {
        /// <summary>The zipped combat state, as <c>DataManagerSave.DoSave</c> writes it.</summary>
        public const string ContentFileName = "content.zip";

        /// <summary>The save's descriptor, written beside <see cref="ContentFileName"/>.</summary>
        public const string MetadataFileName = "metadata.yaml";

        /// <summary>
        /// Longest accepted file name. Far above the two real names, because the
        /// bound exists to stop a pathological name reaching the log, not to
        /// validate the ones we expect.
        /// </summary>
        public const int MaxNameLength = 64;

        /// <summary>
        /// Most files one transfer may carry. A save has two; the headroom is for
        /// a future save format, not for a caller to fill.
        /// </summary>
        public const int MaxFiles = 4;

        /// <summary>
        /// Largest transfer, summed across files. The real save is ~124 KB
        /// against <see cref="PbjRuntime.MaxFrameLength"/> of 1 MiB, so this
        /// leaves a factor of four of headroom and still cannot fill a frame.
        /// Over it, the transfer is <b>refused rather than truncated</b> — half a
        /// save is worse than none, because <c>pbj.combat-load</c> would try to
        /// load it.
        /// </summary>
        public const int MaxTotalBytes = 1 << 19;

        private static readonly ScenarioFile[] NoFilesAtAll = new ScenarioFile[0];

        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime = 16777619;

        /// <summary>
        /// No save to offer. What a host that has never run
        /// <c>pbj.combat-save</c> hands back — not an error, just nothing to say.
        /// </summary>
        public static readonly ScenarioPayload None = new ScenarioPayload(null, null);

        public ScenarioPayload(string? saveName, IReadOnlyList<ScenarioFile>? files)
        {
            SaveName = saveName;
            Files = files ?? NoFilesAtAll;

            var total = 0L;
            for (var i = 0; i < Files.Count; i++)
            {
                total += Files[i].Content.Length;
            }

            TotalBytes = total;
            Digest = ComputeDigest(Files);
        }

        /// <summary>
        /// What the <em>host</em> calls this save. Informational: the receiver
        /// writes to its own save name regardless, so this never composes a path.
        /// </summary>
        public string? SaveName { get; }

        public IReadOnlyList<ScenarioFile> Files { get; }

        /// <summary>
        /// Summed across <see cref="Files"/>. A <c>long</c> so that summing
        /// several large files cannot overflow into a small positive number that
        /// then passes <see cref="Inspect"/> — the check has to be able to see
        /// the real total to refuse it.
        /// </summary>
        public long TotalBytes { get; }

        /// <summary>
        /// Eight hex digits identifying these file contents, order-independent.
        /// </summary>
        /// <remarks>
        /// FNV-1a, the same hash <see cref="StateDigest"/> uses, for the same
        /// reason: one hash function in Core rather than two. It answers "is this
        /// the save I already have" and "did it arrive intact"; it is <b>not</b> a
        /// cryptographic checksum and must not be relied on as one. A peer that
        /// is already past the passphrase and can choose the bytes could choose
        /// colliding ones — which is why the name guards, not the digest, are what
        /// keep the write safe.
        /// </remarks>
        public string Digest { get; }

        /// <summary>
        /// True if <paramref name="digest"/> names these same contents, so the
        /// transfer can be skipped.
        /// </summary>
        public bool Matches(string? digest)
        {
            return digest != null && string.Equals(Digest, digest, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Structural safety: could this name, whatever it is, address anything
        /// other than a plain file directly inside a directory?
        /// </summary>
        /// <remarks>
        /// An allowlist of permitted characters rather than a denylist of
        /// forbidden ones, so a separator this code has never heard of is
        /// rejected by default. That single rule subsumes the traversal cases
        /// worth naming: <c>..</c> cannot escape without a separator, and
        /// <c>C:</c> cannot anchor without a colon. The leading-dot rule also
        /// keeps hidden files out, and the trailing-dot rule keeps Windows from
        /// silently stripping it and aliasing two names to one file.
        /// </remarks>
        public static bool IsSafeName(string? name)
        {
            if (string.IsNullOrEmpty(name) || name!.Length > MaxNameLength)
            {
                return false;
            }
            if (name[0] == '.' || name[name.Length - 1] == '.')
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '.' || c == '-' || c == '_';
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Structurally safe <em>and</em> one of the two files a save actually
        /// has. Case-sensitive on purpose: the game writes exactly these names,
        /// and accepting <c>Content.Zip</c> would mean two spellings of one file
        /// on a case-sensitive filesystem.
        /// </summary>
        public static bool IsAllowedName(string? name)
        {
            return IsSafeName(name)
                && (string.Equals(name, ContentFileName, StringComparison.Ordinal)
                    || string.Equals(name, MetadataFileName, StringComparison.Ordinal));
        }

        /// <summary>
        /// Everything wrong with this payload, or <see cref="ScenarioRejection.None"/>.
        /// </summary>
        /// <remarks>
        /// Cheap checks first, so a peer cannot make us do per-file work before
        /// we notice the whole thing is oversized. Reports the first problem
        /// rather than all of them: the caller refuses the transfer either way,
        /// and one reason is what fits in a log line.
        /// </remarks>
        public ScenarioRejection Inspect()
        {
            if (Files.Count == 0)
            {
                return ScenarioRejection.NoFiles;
            }
            if (Files.Count > MaxFiles)
            {
                return ScenarioRejection.TooManyFiles;
            }
            if (TotalBytes > MaxTotalBytes)
            {
                return ScenarioRejection.TooLarge;
            }

            var seenContent = false;
            var seenMetadata = false;
            for (var i = 0; i < Files.Count; i++)
            {
                var name = Files[i].Name;
                if (!IsAllowedName(name))
                {
                    return ScenarioRejection.DisallowedName;
                }

                if (string.Equals(name, ContentFileName, StringComparison.Ordinal))
                {
                    if (seenContent)
                    {
                        return ScenarioRejection.DuplicateName;
                    }
                    seenContent = true;
                }
                else
                {
                    if (seenMetadata)
                    {
                        return ScenarioRejection.DuplicateName;
                    }
                    seenMetadata = true;
                }
            }

            return seenContent && seenMetadata
                ? ScenarioRejection.None
                : ScenarioRejection.MissingRequiredFile;
        }

        private static string ComputeDigest(IReadOnlyList<ScenarioFile> files)
        {
            unchecked
            {
                // Summed rather than folded in sequence, so directory
                // enumeration order — which no filesystem guarantees — cannot
                // change a save's identity. Addition rather than XOR for the
                // reason StateDigest gives: XOR makes an identical pair cancel.
                var total = FnvOffsetBasis;
                for (var i = 0; i < files.Count; i++)
                {
                    total += HashFile(files[i]);
                }
                return total.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private static uint HashFile(ScenarioFile file)
        {
            unchecked
            {
                var hash = FnvOffsetBasis;
                var name = file.Name ?? string.Empty;
                for (var i = 0; i < name.Length; i++)
                {
                    hash = (hash ^ name[i]) * FnvPrime;
                }

                // The length is mixed in separately so an empty file is
                // distinguishable from an absent one: without it both would
                // contribute only their name.
                var length = file.Content.Length;
                hash = (hash ^ (uint)(length & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((length >> 8) & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((length >> 16) & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((length >> 24) & 0xFF)) * FnvPrime;

                var content = file.Content;
                for (var i = 0; i < content.Length; i++)
                {
                    hash = (hash ^ content[i]) * FnvPrime;
                }
                return hash;
            }
        }
    }
}
