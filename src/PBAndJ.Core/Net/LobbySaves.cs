using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>Why a proposed multiplayer save name was refused, or <see cref="None"/>.</summary>
    public enum LobbySaveProblem : byte
    {
        None = 0,
        Empty = 1,
        TooLong = 2,
        BadCharacter = 3,
        AlreadyPrefixed = 4,
        Reserved = 5,
        Duplicate = 6,
    }

    /// <summary>
    /// The <c>pbj_</c> save namespace: what belongs to it, and how a display name
    /// and a save key convert between each other.
    /// </summary>
    /// <remarks>
    /// Multiplayer saves are ordinary campaign saves living in the ordinary save
    /// folder under a name prefix. <c>SaveLocation</c> is a fixed five-value enum
    /// switch-mapped to hardcoded paths, so a custom save location is impossible
    /// and a subdirectory would mean intercepting every load and save path; the
    /// prefix leaves the game's own paths completely unmodified. See
    /// docs/design/campaign-coop.md, "Save storage".
    /// <para>
    /// Everything here is case-insensitive, and that is not tidiness. The game
    /// compares save names <c>OrdinalIgnoreCase</c> and lowercases before checking
    /// reserved names, while the filesystem under Proton is case-sensitive. A
    /// case-sensitive check here would let <c>PBJ_foo</c> exist as a save hidden
    /// from nothing, which the game's own duplicate check would nonetheless read
    /// as an overwrite of <c>pbj_foo</c>.
    /// </para>
    /// </remarks>
    public static class LobbySaveNames
    {
        /// <summary>The name prefix that makes a save a multiplayer one.</summary>
        public const string Prefix = "pbj_";

        /// <summary>
        /// M9's combat-scenario transfer slot, which is inside <see cref="Prefix"/>
        /// and is <b>not</b> a campaign.
        /// </summary>
        /// <remarks>
        /// Named here rather than in the mod so that one place owns the namespace.
        /// <c>WriteScenario</c> deletes and rewrites this directory on every
        /// scenario transfer, so a lobby that selected it would have peers ready
        /// onto a save the next transfer destroys. <see cref="LobbyCatalogue"/>
        /// excludes it and <see cref="LobbySaveRules"/> reserves it.
        /// </remarks>
        public const string ScenarioSlot = Prefix + "combat_test";

        /// <summary>
        /// Longest accepted display name. The game imposes no limit of its own —
        /// this exists so a pathological name cannot reach a log line or a screen,
        /// not because any real name approaches it.
        /// </summary>
        public const int MaxNameLength = 64;

        /// <summary>True if <paramref name="key"/> names a multiplayer save.</summary>
        public static bool IsMultiplayerKey(string? key)
        {
            return key != null && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The save key a display name is stored under.</summary>
        public static string KeyFor(string? displayName) => Prefix + displayName;

        /// <summary>
        /// What to show a player for a save key. An unprefixed key is returned
        /// unchanged, so a mixed list renders without branching.
        /// </summary>
        public static string? DisplayFor(string? key)
        {
            return IsMultiplayerKey(key) ? key!.Substring(Prefix.Length) : key;
        }
    }

    /// <summary>
    /// Whether a display name may become a multiplayer save. Pure, so it is the
    /// same answer in the lobby screen and on the console.
    /// </summary>
    public static class LobbySaveRules
    {
        /// <summary>
        /// The eight names <c>AutosaveFilenames</c> declares, plus the timed-slot
        /// prefix, mirroring <c>DataPathHelper.IsReservedFilename</c>.
        /// </summary>
        /// <remarks>
        /// Copied rather than derived because Core cannot see a game type. Note
        /// what this deliberately is <b>not</b>: a blanket <c>autosave_*</c> rule.
        /// The game refuses only the timed prefix and these exact eight, so
        /// <c>autosave_myrun</c> is a name it permits — and diverging from the game
        /// by refusing more would be a rule with no reason behind it.
        /// </remarks>
        private static readonly string[] ReservedNames =
        {
            "autosave_quicksave",
            "autosave_before_combat",
            "autosave_after_combat",
            "autosave_before_travel",
            "autosave_after_stop",
            "autosave_campaign_end",
            "autosave_game_exit",
        };

        private const string TimedPrefix = "autosave_timed_";

        /// <summary>
        /// Everything wrong with <paramref name="displayName"/> as a new
        /// multiplayer save, or <see cref="LobbySaveProblem.None"/>.
        /// </summary>
        /// <remarks>
        /// Most-fundamental-first, the same ordering <c>ConnectForm</c> uses:
        /// renaming to dodge a duplicate cannot help while the name is reserved,
        /// so reserved is reported first. One problem rather than all of them —
        /// the caller refuses either way, and one reason is what fits on a screen.
        /// <para>
        /// <paramref name="existingKeys"/> are save <em>keys</em> as the game lists
        /// them, prefixed and unprefixed alike; only multiplayer ones can collide,
        /// because a singleplayer <c>firstrun</c> and <c>pbj_firstrun</c> are
        /// different directories and converting one into the other is the point.
        /// </para>
        /// </remarks>
        public static LobbySaveProblem CheckNewName(string? displayName, IEnumerable<string?>? existingKeys)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return LobbySaveProblem.Empty;
            }
            if (displayName!.Length > LobbySaveNames.MaxNameLength)
            {
                return LobbySaveProblem.TooLong;
            }
            if (!IsSafeName(displayName))
            {
                return LobbySaveProblem.BadCharacter;
            }
            if (LobbySaveNames.IsMultiplayerKey(displayName))
            {
                return LobbySaveProblem.AlreadyPrefixed;
            }
            if (IsReserved(displayName))
            {
                return LobbySaveProblem.Reserved;
            }
            if (Collides(LobbySaveNames.KeyFor(displayName), existingKeys))
            {
                return LobbySaveProblem.Duplicate;
            }
            return LobbySaveProblem.None;
        }

        /// <summary>
        /// Structural safety: could this name address anything other than a plain
        /// directory directly inside the save folder?
        /// </summary>
        /// <remarks>
        /// The same allowlist <see cref="ScenarioPayload.IsSafeName"/> uses, for
        /// the same reason: a separator this code has never heard of is rejected by
        /// default. That one rule subsumes the traversal cases worth naming —
        /// <c>..</c> cannot escape without a separator and <c>C:</c> cannot anchor
        /// without a colon — so there is deliberately no explicit traversal check
        /// to fall out of step with it. The leading-dot rule keeps hidden
        /// directories out; the trailing-dot rule keeps Windows from stripping it
        /// and aliasing two names onto one directory.
        /// </remarks>
        private static bool IsSafeName(string name)
        {
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
        /// True if the <em>game</em> chose this name, rather than a player.
        /// </summary>
        /// <remarks>
        /// The autosaves the game writes on its own schedule, and the only names a
        /// load may safely be redirected into the <c>pbj_</c> namespace: a name a
        /// player typed or picked from a grid is theirs, and steering it elsewhere
        /// would load a save they did not choose. See
        /// <see cref="LobbySaveWrites.NameForRead"/>.
        /// <para>
        /// Deliberately <b>not</b> the scenario slot, which is M9's and not the
        /// game's, and deliberately not a blanket <c>autosave_*</c> rule — the game
        /// itself claims only the timed prefix and these exact names, so
        /// <c>autosave_myrun</c> belongs to whoever typed it.
        /// </para>
        /// </remarks>
        public static bool IsGameGeneratedSaveName(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            if (name!.StartsWith(TimedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            for (var i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(name, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsReserved(string displayName)
        {
            // The scenario slot is reserved without being the game's own: M9's
            // WriteScenario deletes and rewrites it wholesale, so a campaign must
            // never be created there.
            return string.Equals(
                       LobbySaveNames.KeyFor(displayName),
                       LobbySaveNames.ScenarioSlot,
                       StringComparison.OrdinalIgnoreCase)
                   || IsGameGeneratedSaveName(displayName);
        }

        private static bool Collides(string key, IEnumerable<string?>? existingKeys)
        {
            if (existingKeys == null)
            {
                return false;
            }

            foreach (var existing in existingKeys)
            {
                if (existing != null && string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>One save as the catalogue sees it.</summary>
    /// <remarks>
    /// Handed in from outside — the sessions read no disk, exactly as they read no
    /// clock, so enumerating saves and hashing them belongs to the glue and only
    /// the rules live here.
    /// </remarks>
    public readonly struct LobbySaveEntry
    {
        public LobbySaveEntry(string? key, long timeInSystem, string? digest)
        {
            Key = key;
            TimeInSystem = timeInSystem;
            Digest = digest;
        }

        /// <summary>The save's directory name, which is its whole identity.</summary>
        public string? Key { get; }

        /// <summary>When the save was written, as the game records it.</summary>
        public long TimeInSystem { get; }

        /// <summary>
        /// Contents hash, or null when nobody has needed one yet — listing a
        /// catalogue does not require hashing every save on disk.
        /// </summary>
        public string? Digest { get; }

        /// <summary>What to show a player.</summary>
        public string? DisplayName => LobbySaveNames.DisplayFor(Key);
    }

    /// <summary>Which saves a lobby may offer, and in what order.</summary>
    public static class LobbyCatalogue
    {
        private static readonly LobbySaveEntry[] Nothing = new LobbySaveEntry[0];

        /// <summary>
        /// The multiplayer saves among <paramref name="all"/>, newest first.
        /// </summary>
        /// <remarks>
        /// Sorted the way the game's own load grid sorts — <c>timeInSystem</c>
        /// descending — because a lobby list ordered differently from the screen
        /// beside it reads as a bug. <see cref="LobbySaveNames.ScenarioSlot"/> is
        /// excluded: it is inside the prefix but it is not a campaign.
        /// </remarks>
        public static IReadOnlyList<LobbySaveEntry> Multiplayer(IEnumerable<LobbySaveEntry>? all)
        {
            if (all == null)
            {
                return Nothing;
            }

            var kept = new List<LobbySaveEntry>();
            foreach (var entry in all)
            {
                if (IsOffered(entry.Key))
                {
                    kept.Add(entry);
                }
            }

            kept.Sort((x, y) => y.TimeInSystem.CompareTo(x.TimeInSystem));
            return kept;
        }

        /// <summary>
        /// True if <paramref name="key"/> is a save this catalogue would offer.
        /// </summary>
        /// <remarks>
        /// What lets the console refuse <c>pbj.lobby-select pbj_combat_test</c>.
        /// <c>HostSession</c> accepts any key by design — the session reads no disk
        /// and cannot know what exists — so the guard belongs at the edge that
        /// does.
        /// </remarks>
        public static bool Contains(IEnumerable<LobbySaveEntry>? all, string? key)
        {
            if (all == null || string.IsNullOrEmpty(key) || !IsOffered(key))
            {
                return false;
            }

            foreach (var entry in all)
            {
                if (entry.Key != null && string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsOffered(string? key)
        {
            return LobbySaveNames.IsMultiplayerKey(key)
                && !string.Equals(key, LobbySaveNames.ScenarioSlot, StringComparison.OrdinalIgnoreCase);
        }
    }
}
