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

        /// <summary>The save it would be written to is not one we permit. M11e.</summary>
        DisallowedDestination = 7,

        /// <summary>
        /// One part is over <see cref="ScenarioPayload.MaxPartBytes"/>, which the
        /// writer would throw on rather than send. M11e.
        /// </summary>
        PartTooLarge = 8,

        /// <summary>
        /// Split content with a gap in it, or not starting at part zero — it would
        /// reassemble into a truncated save. M11e.
        /// </summary>
        PartsNotContiguous = 9,

        /// <summary>
        /// Whole and split content in the same payload, so which one is the save is
        /// ambiguous. M11e.
        /// </summary>
        MixedContentForm = 10,
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
        /// Largest transfer, summed across files.
        /// </summary>
        /// <remarks>
        /// Measured 2026-08-05 against a real save folder: campaign saves run
        /// 24–71 KB and M9's combat scenario — the largest thing there — is 119 KB.
        /// 768 KiB is therefore about six times the largest save anyone has, with
        /// room for the pilot files that make a long campaign grow.
        /// <para>
        /// Over it the transfer is <b>refused rather than truncated</b>: half a save
        /// is worse than none, because something would then try to load it.
        /// </para>
        /// </remarks>
        public const int MaxTotalBytes = 3 << 18;

        /// <summary>
        /// Largest single file, and the reason content is split at all.
        /// </summary>
        /// <remarks>
        /// <see cref="PbjWriter.MaxBytesLength"/> caps one blob at 512 KiB and
        /// <b>throws</b> over it (<c>PbjWriter</c>'s <c>WriteBytes</c>), while
        /// <c>PbjRuntime.SendTo</c> deliberately does not guard encoding — so an
        /// oversize blob does not fail politely, it escapes the effect pump and
        /// takes every effect queued behind it. Splitting keeps each part well
        /// under that cap so <see cref="MaxTotalBytes"/> can exceed it without ever
        /// putting the writer in that position, and without giving up the writer's
        /// own invariant that a single blob can never fill a frame.
        /// <para>
        /// 256 KiB × the three part slots <see cref="MaxFiles"/> leaves beside
        /// <see cref="MetadataFileName"/> is exactly <see cref="MaxTotalBytes"/>.
        /// </para>
        /// </remarks>
        public const int MaxPartBytes = 1 << 18;

        /// <summary>
        /// Longest accepted destination key: the <c>pbj_</c> prefix on top of a
        /// full-length display name.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="MaxNameLength"/>, which bounds <em>file</em>
        /// names. A legal save is <see cref="LobbySaveNames.MaxNameLength"/>
        /// characters and the key carries the prefix as well, so borrowing the file
        /// bound here would make perfectly legal saves untransferable.
        /// </remarks>
        public static readonly int MaxKeyLength =
            LobbySaveNames.Prefix.Length + LobbySaveNames.MaxNameLength;

        private static readonly ScenarioFile[] NoFilesAtAll = new ScenarioFile[0];

        private static readonly byte[] NoContentAtAll = new byte[0];

        /// <summary>
        /// How many numbered parts content may split into: everything
        /// <see cref="MaxFiles"/> allows beside <see cref="MetadataFileName"/>.
        /// </summary>
        public const int MaxParts = MaxFiles - 1;

        private static readonly string[] PartNames =
        {
            ContentFileName + ".0",
            ContentFileName + ".1",
            ContentFileName + ".2",
        };

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
            return !string.IsNullOrEmpty(name)
                && name!.Length <= MaxNameLength
                && HasOnlySafeCharacters(name);
        }

        /// <summary>
        /// Structurally safe <em>and</em> one of the files a save actually has:
        /// the metadata, and the content either whole or as a numbered part.
        /// </summary>
        /// <remarks>
        /// Case-sensitive on purpose: the game writes exactly these names, and
        /// accepting <c>Content.Zip</c> would mean two spellings of one file on a
        /// case-sensitive filesystem. The part names are an explicit short list
        /// rather than a parsed suffix, for the same reason the rest of this class
        /// prefers allowlists — a name shape nobody thought about is refused by
        /// default.
        /// </remarks>
        public static bool IsAllowedName(string? name)
        {
            return IsSafeName(name)
                && (string.Equals(name, ContentFileName, StringComparison.Ordinal)
                    || string.Equals(name, MetadataFileName, StringComparison.Ordinal)
                    || PartIndex(name) >= 0);
        }

        /// <summary>
        /// Which numbered content part this is, or -1 if it is not one.
        /// </summary>
        public static int PartIndex(string? name)
        {
            for (var i = 0; i < MaxParts; i++)
            {
                if (string.Equals(name, PartNames[i], StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Whether a transfer may be written to <paramref name="key"/>.
        /// </summary>
        /// <remarks>
        /// <b>The guard that replaces M9's fixed constant.</b> M9 never took a
        /// directory name from the wire at all; M11d's barrier makes every peer load
        /// the lobby's chosen key, so M11e has to — and this is the whole boundary
        /// that makes it safe.
        /// <para>
        /// Two conjuncts, and the structural one is <b>not</b> redundant.
        /// <see cref="LobbyCatalogue"/>'s own offer test checks the prefix and that
        /// the key is not the scenario slot; <c>pbj_../../x</c> satisfies both. Only
        /// <see cref="IsSafeKey"/> stops that, so it is stated here rather than
        /// borrowed from a rule written for a different question.
        /// </para>
        /// </remarks>
        public static bool IsAllowedDestination(string? key)
        {
            return IsSafeKey(key) && LobbySaveNames.IsMultiplayerKey(key);
        }

        /// <summary>
        /// Structural safety for a save <em>directory</em> name.
        /// </summary>
        /// <remarks>
        /// The same character rule as <see cref="IsSafeName"/> — one allowlist, so a
        /// separator neither has heard of is rejected by both — against the longer
        /// bound a prefixed key needs. See <see cref="MaxKeyLength"/>.
        /// </remarks>
        public static bool IsSafeKey(string? key)
        {
            return !string.IsNullOrEmpty(key)
                && key!.Length <= MaxKeyLength
                && HasOnlySafeCharacters(key);
        }

        /// <summary>
        /// Splits save content into parts small enough for the writer to encode.
        /// </summary>
        /// <remarks>
        /// Content that fits in one part keeps <see cref="ContentFileName"/>
        /// unchanged, so the common case — every save measured so far — is
        /// byte-identical on the wire to what M9 already sends.
        /// </remarks>
        public static IReadOnlyList<ScenarioFile> SplitContent(byte[]? content)
        {
            var bytes = content ?? NoContentAtAll;
            if (bytes.Length <= MaxPartBytes)
            {
                return new[] { new ScenarioFile(ContentFileName, bytes) };
            }

            var parts = new List<ScenarioFile>();
            var offset = 0;
            while (offset < bytes.Length && parts.Count < MaxParts)
            {
                var length = Math.Min(MaxPartBytes, bytes.Length - offset);
                var part = new byte[length];
                Array.Copy(bytes, offset, part, 0, length);
                parts.Add(new ScenarioFile(PartNames[parts.Count], part));
                offset += length;
            }
            return parts;
        }

        /// <summary>
        /// Reassembles the content of a payload <see cref="Inspect"/> has accepted.
        /// </summary>
        /// <remarks>
        /// Ordered by part index rather than by position, because the digest is
        /// deliberately order-independent and nothing else promises the wire keeps
        /// file order either. Reassembling in arrival order would corrupt a save in
        /// a way no check downstream would catch.
        /// </remarks>
        public static byte[] JoinContent(ScenarioPayload? payload)
        {
            if (payload == null)
            {
                return NoContentAtAll;
            }

            var files = payload.Files;
            for (var i = 0; i < files.Count; i++)
            {
                if (string.Equals(files[i].Name, ContentFileName, StringComparison.Ordinal))
                {
                    return files[i].Content;
                }
            }

            var ordered = new byte[MaxParts][];
            var total = 0;
            for (var i = 0; i < files.Count; i++)
            {
                var index = PartIndex(files[i].Name);
                if (index >= 0)
                {
                    ordered[index] = files[i].Content;
                    total += files[i].Content.Length;
                }
            }

            var joined = new byte[total];
            var offset = 0;
            for (var i = 0; i < MaxParts; i++)
            {
                if (ordered[i] != null)
                {
                    Array.Copy(ordered[i], 0, joined, offset, ordered[i].Length);
                    offset += ordered[i].Length;
                }
            }
            return joined;
        }

        private static bool HasOnlySafeCharacters(string value)
        {
            if (value[0] == '.' || value[value.Length - 1] == '.')
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
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

            // After the size checks, so a flood is still refused before any
            // per-file work — but before the names, because a payload aimed
            // somewhere we will never write is not worth inspecting further.
            // NoFiles deliberately still wins: ScenarioPayload.None carries no
            // destination and HostSession.OfferScenario reads NoFiles as the benign
            // "nothing to offer" rather than as a fault to warn about.
            if (!IsAllowedDestination(SaveName))
            {
                return ScenarioRejection.DisallowedDestination;
            }

            var seenContent = false;
            var seenMetadata = false;
            var seenParts = 0;
            var partSeen = new bool[MaxParts];
            for (var i = 0; i < Files.Count; i++)
            {
                var name = Files[i].Name;
                if (!IsAllowedName(name))
                {
                    return ScenarioRejection.DisallowedName;
                }
                if (Files[i].Content.Length > MaxPartBytes)
                {
                    return ScenarioRejection.PartTooLarge;
                }

                var part = PartIndex(name);
                if (part >= 0)
                {
                    if (partSeen[part])
                    {
                        return ScenarioRejection.DuplicateName;
                    }
                    partSeen[part] = true;
                    seenParts++;
                }
                else if (string.Equals(name, ContentFileName, StringComparison.Ordinal))
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

            if (seenContent && seenParts > 0)
            {
                return ScenarioRejection.MixedContentForm;
            }
            if (!seenMetadata || (!seenContent && seenParts == 0))
            {
                return ScenarioRejection.MissingRequiredFile;
            }

            // A contiguous run from zero. A gap would reassemble into a truncated
            // save that something would then try to load.
            for (var i = 0; i < seenParts; i++)
            {
                if (!partSeen[i])
                {
                    return ScenarioRejection.PartsNotContiguous;
                }
            }

            return ScenarioRejection.None;
        }

        /// <summary>
        /// The identity of the save, deliberately blind to how it was framed for
        /// the wire.
        /// </summary>
        /// <remarks>
        /// Numbered content parts are merged back into one logical
        /// <see cref="ContentFileName"/> before hashing. Without that, a save
        /// over <see cref="MaxPartBytes"/> could never match itself: the host
        /// offers <c>content.zip.0/.1/.2</c> while the client's own copy sits on
        /// disk as a single <c>content.zip</c>, and <see cref="HashFile"/> mixes
        /// both the name and the per-file length — so the two forms disagreed by
        /// construction and every large fight re-transferred on every offer, for
        /// ever.
        /// <para>
        /// A payload that never split is untouched by this: with no numbered
        /// part present the merge does not run, so every digest computed before
        /// this existed still holds. Only the previously-broken case moves.
        /// </para>
        /// <para>
        /// Merging is by part <em>index</em>, taken from the name, never by
        /// position in the list — arrival order is no more of a protocol
        /// guarantee than directory enumeration order is. Gaps are tolerated
        /// rather than rejected because this runs in the constructor, ahead of
        /// <see cref="Inspect"/>: a malformed payload must still produce a
        /// digest rather than throw on its way to being refused.
        /// </para>
        /// </remarks>
        private static string ComputeDigest(IReadOnlyList<ScenarioFile> files)
        {
            unchecked
            {
                // Summed rather than folded in sequence, so directory
                // enumeration order — which no filesystem guarantees — cannot
                // change a save's identity. Addition rather than XOR for the
                // reason StateDigest gives: XOR makes an identical pair cancel.
                var total = FnvOffsetBasis;
                byte[]?[]? parts = null;
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var part = PartIndexOf(file.Name);
                    if (part < 0)
                    {
                        total += HashFile(file);
                        continue;
                    }

                    if (parts == null)
                    {
                        parts = new byte[PartNames.Length][];
                    }
                    parts[part] = file.Content;
                }

                if (parts != null)
                {
                    total += HashFile(new ScenarioFile(ContentFileName, Join(parts)));
                }

                return total.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Which numbered content part this file is, or -1 for anything else.
        /// </summary>
        private static int PartIndexOf(string? name)
        {
            for (var i = 0; i < PartNames.Length; i++)
            {
                if (string.Equals(name, PartNames[i], StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Concatenates the parts present, in index order, skipping any gap.
        /// </summary>
        private static byte[] Join(byte[]?[] parts)
        {
            var length = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part != null)
                {
                    length += part.Length;
                }
            }

            var merged = new byte[length];
            var offset = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part == null)
                {
                    continue;
                }

                Array.Copy(part, 0, merged, offset, part.Length);
                offset += part.Length;
            }
            return merged;
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
