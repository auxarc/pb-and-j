using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class LobbySaveNamesTests
    {
        [Fact]
        public void Prefix_IsTheOneTheModAlreadyWritesWith()
        {
            // M9's pbj.combat-save already writes SavedGames/pbj_combat_test/, and
            // the campaign-coop decision was to keep that namespace rather than
            // invent a second one. If this ever changes, every save on every
            // player's disk changes with it.
            Assert.Equal("pbj_", LobbySaveNames.Prefix);
        }

        [Fact]
        public void ScenarioSlot_IsInsideThePrefix()
        {
            // The trap this constant exists to name: M9's transfer slot is itself
            // pbj_-prefixed, so the catalogue would offer it as a campaign while
            // WriteScenario deletes and rewrites it on every scenario transfer.
            Assert.True(LobbySaveNames.IsMultiplayerKey(LobbySaveNames.ScenarioSlot));
        }

        [Fact]
        public void KeyFor_PrependsThePrefix()
        {
            Assert.Equal("pbj_firstrun", LobbySaveNames.KeyFor("firstrun"));
        }

        [Fact]
        public void DisplayFor_StripsThePrefix()
        {
            Assert.Equal("firstrun", LobbySaveNames.DisplayFor("pbj_firstrun"));
        }

        [Fact]
        public void DisplayFor_LeavesAnUnprefixedKeyAlone()
        {
            // A singleplayer key has no prefix to strip. Returning it unchanged is
            // what lets the catalogue render a mixed list without branching.
            Assert.Equal("TWICE SHY", LobbySaveNames.DisplayFor("TWICE SHY"));
        }

        [Fact]
        public void DisplayFor_Null_ReturnsNull()
        {
            Assert.Null(LobbySaveNames.DisplayFor(null));
        }

        [Fact]
        public void IsMultiplayerKey_IgnoresCase()
        {
            // The game compares save names OrdinalIgnoreCase (DoesSaveAlreadyExist)
            // and lowercases in IsReservedFilename, so a case-sensitive check here
            // would let PBJ_foo exist as a save that is hidden from nothing yet
            // reads as an overwrite of pbj_foo in the game's own duplicate check.
            Assert.True(LobbySaveNames.IsMultiplayerKey("PBJ_foo"));
            Assert.True(LobbySaveNames.IsMultiplayerKey("pbj_foo"));
        }

        [Fact]
        public void IsMultiplayerKey_ForAnOrdinarySave_ReturnsFalse()
        {
            Assert.False(LobbySaveNames.IsMultiplayerKey("TWICE SHY"));
        }

        [Fact]
        public void IsMultiplayerKey_Null_ReturnsFalse()
        {
            Assert.False(LobbySaveNames.IsMultiplayerKey(null));
        }
    }

    public class LobbySaveRulesTests
    {
        private static readonly string[] NoSaves = new string[0];

        [Fact]
        public void CheckNewName_ForAPlainName_IsAccepted()
        {
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName("firstrun", NoSaves));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CheckNewName_ForNothing_IsEmpty(string? name)
        {
            Assert.Equal(LobbySaveProblem.Empty, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Fact]
        public void CheckNewName_OverTheLengthLimit_IsTooLong()
        {
            var name = new string('a', LobbySaveNames.MaxNameLength + 1);
            Assert.Equal(LobbySaveProblem.TooLong, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Fact]
        public void CheckNewName_AtTheLengthLimit_IsAccepted()
        {
            var name = new string('a', LobbySaveNames.MaxNameLength);
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Theory]
        [InlineData("has space")]
        [InlineData("slash/es")]
        [InlineData("back\\slash")]
        [InlineData("C:drive")]
        [InlineData("..")]
        [InlineData("../escape")]
        [InlineData(".hidden")]
        [InlineData("trailing.")]
        public void CheckNewName_ForAnythingOffTheAllowlist_IsBadCharacter(string name)
        {
            // An allowlist, not a denylist: a separator this code has never heard
            // of is rejected by default. That single rule subsumes the traversal
            // cases — ".." cannot escape without a separator, "C:" cannot anchor
            // without a colon — which is why there is no explicit traversal check.
            Assert.Equal(LobbySaveProblem.BadCharacter, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Theory]
        [InlineData("run.1")]
        [InlineData("run-1")]
        [InlineData("run_1")]
        [InlineData("Run1")]
        public void CheckNewName_ForAllowlistedPunctuation_IsAccepted(string name)
        {
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Fact]
        public void CheckNewName_ForAnAlreadyPrefixedName_IsAlreadyPrefixed()
        {
            // The name is a display name; KeyFor adds the prefix. Accepting one
            // that already carries it would produce pbj_pbj_x.
            Assert.Equal(
                LobbySaveProblem.AlreadyPrefixed,
                LobbySaveRules.CheckNewName("pbj_firstrun", NoSaves));
        }

        [Fact]
        public void CheckNewName_ForAnAlreadyPrefixedName_IgnoresCase()
        {
            Assert.Equal(
                LobbySaveProblem.AlreadyPrefixed,
                LobbySaveRules.CheckNewName("PBJ_firstrun", NoSaves));
        }

        [Theory]
        [InlineData("autosave_timed_0")]
        [InlineData("autosave_timed_")]
        [InlineData("autosave_quicksave")]
        [InlineData("autosave_before_combat")]
        [InlineData("autosave_after_combat")]
        [InlineData("autosave_before_travel")]
        [InlineData("autosave_after_stop")]
        [InlineData("autosave_campaign_end")]
        [InlineData("autosave_game_exit")]
        public void CheckNewName_ForAGameReservedName_IsReserved(string name)
        {
            // Mirrors DataPathHelper.IsReservedFilename exactly: the
            // autosave_timed_ prefix plus exact matches of the eight
            // AutosaveFilenames constants.
            Assert.Equal(LobbySaveProblem.Reserved, LobbySaveRules.CheckNewName(name, NoSaves));
        }

        [Fact]
        public void CheckNewName_ForAGameReservedName_IgnoresCase()
        {
            // The game lowercases before comparing, so we must too.
            Assert.Equal(
                LobbySaveProblem.Reserved,
                LobbySaveRules.CheckNewName("AutoSave_Game_Exit", NoSaves));
        }

        [Fact]
        public void CheckNewName_ForANameTheGameItselfAllows_IsNotReserved()
        {
            // IsReservedFilename is NOT "autosave_*" — it is the timed prefix plus
            // eight exact names. autosave_myrun is a save name the game permits,
            // and over-rejecting would diverge from it for no reason.
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName("autosave_myrun", NoSaves));
        }

        [Fact]
        public void CheckNewName_ForTheScenarioSlot_IsReserved()
        {
            // Reserved on the UNPREFIXED name, because CheckNewName takes a display
            // name and AlreadyPrefixed rejects "pbj_combat_test" before any slot
            // comparison could see it. Get this backwards and the arm is
            // unreachable — which breaks the 100% gate — while the input that
            // actually collides sails through into M9's transfer slot, where
            // WriteScenario deletes it on the next scenario transfer.
            Assert.Equal(LobbySaveProblem.Reserved, LobbySaveRules.CheckNewName("combat_test", NoSaves));
        }

        [Fact]
        public void CheckNewName_ForTheScenarioSlot_IgnoresCase()
        {
            // Without this, Combat_Test slips through whenever the slot is absent
            // from disk, since the Duplicate check has nothing to compare against.
            Assert.Equal(LobbySaveProblem.Reserved, LobbySaveRules.CheckNewName("Combat_Test", NoSaves));
        }

        [Fact]
        public void CheckNewName_ForAnExistingKey_IsDuplicate()
        {
            var existing = new[] { "pbj_firstrun" };
            Assert.Equal(LobbySaveProblem.Duplicate, LobbySaveRules.CheckNewName("firstrun", existing));
        }

        [Fact]
        public void CheckNewName_ForAnExistingKey_IgnoresCase()
        {
            // The game's DoesSaveAlreadyExist compares OrdinalIgnoreCase while the
            // filesystem under Proton is case-sensitive, so MySave/mysave reads as
            // an overwrite in the game's UI and creates two directories on disk.
            // Matching the game's comparison is the conservative side of that.
            var existing = new[] { "pbj_FirstRun" };
            Assert.Equal(LobbySaveProblem.Duplicate, LobbySaveRules.CheckNewName("firstrun", existing));
        }

        [Fact]
        public void CheckNewName_IgnoresSingleplayerSavesWhenCheckingDuplicates()
        {
            // A singleplayer save called "firstrun" does not block pbj_firstrun —
            // they are different directories and convert exists precisely to make
            // one from the other.
            var existing = new[] { "firstrun" };
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName("firstrun", existing));
        }

        [Fact]
        public void CheckNewName_ToleratesANullInTheExistingKeys()
        {
            // Headers come from the game; a defensive null here costs one branch
            // and saves a crash in a screen.
            var existing = new string?[] { null, "pbj_other" };
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName("firstrun", existing));
        }

        [Fact]
        public void CheckNewName_WithNoExistingKeysAtAll_IsAccepted()
        {
            // A caller that has not listed the catalogue yet passes null rather
            // than being made to build an empty collection first.
            Assert.Equal(LobbySaveProblem.None, LobbySaveRules.CheckNewName("firstrun", null));
        }

        [Fact]
        public void CheckNewName_ReportsTheMostFundamentalProblemFirst()
        {
            // Same ordering doctrine as ConnectForm: a name that is both reserved
            // and a duplicate is reported as reserved, because renaming to dodge
            // the duplicate cannot help while the name is reserved.
            var existing = new[] { "pbj_autosave_game_exit" };
            Assert.Equal(
                LobbySaveProblem.Reserved,
                LobbySaveRules.CheckNewName("autosave_game_exit", existing));
        }
    }

    public class LobbySaveEntryTests
    {
        [Fact]
        public void DisplayName_StripsThePrefix()
        {
            var entry = new LobbySaveEntry("pbj_firstrun", 12L, "abcd1234");
            Assert.Equal("pbj_firstrun", entry.Key);
            Assert.Equal("firstrun", entry.DisplayName);
            Assert.Equal(12L, entry.TimeInSystem);
            Assert.Equal("abcd1234", entry.Digest);
        }

        [Fact]
        public void Digest_MayBeNull()
        {
            // Listing a catalogue does not require hashing every save on disk —
            // the digest is taken when a save is actually selected.
            Assert.Null(new LobbySaveEntry("pbj_firstrun", 0L, null).Digest);
        }
    }

    public class LobbyCatalogueTests
    {
        private static LobbySaveEntry Entry(string key, long time = 0L) => new LobbySaveEntry(key, time, null);

        [Fact]
        public void Multiplayer_KeepsOnlyPrefixedSaves()
        {
            var all = new[] { Entry("TWICE SHY"), Entry("pbj_firstrun"), Entry("autosave_timed_0") };
            var mp = LobbyCatalogue.Multiplayer(all);
            Assert.Equal(new[] { "pbj_firstrun" }, mp.Select(e => e.Key));
        }

        [Fact]
        public void Multiplayer_ExcludesTheScenarioSlot()
        {
            // The whole reason ScenarioSlot is a named constant: offering M9's
            // transfer slot as a campaign would let a lobby ready onto a directory
            // the next scenario transfer deletes.
            var all = new[] { Entry(LobbySaveNames.ScenarioSlot), Entry("pbj_firstrun") };
            Assert.Equal(new[] { "pbj_firstrun" }, LobbyCatalogue.Multiplayer(all).Select(e => e.Key));
        }

        [Fact]
        public void Multiplayer_SortsNewestFirst()
        {
            // Mirrors the game's own load grid, which sorts by timeInSystem
            // descending. A lobby that ordered saves differently from the screen
            // beside it would read as a bug.
            var all = new[] { Entry("pbj_old", 10L), Entry("pbj_new", 30L), Entry("pbj_mid", 20L) };
            Assert.Equal(
                new[] { "pbj_new", "pbj_mid", "pbj_old" },
                LobbyCatalogue.Multiplayer(all).Select(e => e.Key));
        }

        [Fact]
        public void Multiplayer_ForNothing_ReturnsEmpty()
        {
            Assert.Empty(LobbyCatalogue.Multiplayer(new LobbySaveEntry[0]));
        }

        [Fact]
        public void Multiplayer_Null_ReturnsEmpty()
        {
            Assert.Empty(LobbyCatalogue.Multiplayer(null));
        }

        [Fact]
        public void Contains_FindsAKeyInTheCatalogue()
        {
            var all = new[] { Entry("pbj_firstrun") };
            Assert.True(LobbyCatalogue.Contains(all, "pbj_firstrun"));
        }

        [Fact]
        public void Contains_IgnoresCase()
        {
            var all = new[] { Entry("pbj_firstrun") };
            Assert.True(LobbyCatalogue.Contains(all, "PBJ_FirstRun"));
        }

        [Fact]
        public void Contains_ForTheScenarioSlot_ReturnsFalse()
        {
            // This is what makes `pbj.lobby-select pbj_combat_test` refusable.
            // HostSession accepts any key by design — the session reads no disk —
            // so the console is where the guard has to live.
            var all = new[] { Entry(LobbySaveNames.ScenarioSlot), Entry("pbj_firstrun") };
            Assert.False(LobbyCatalogue.Contains(all, LobbySaveNames.ScenarioSlot));
        }

        [Fact]
        public void Contains_ForASingleplayerSave_ReturnsFalse()
        {
            var all = new[] { Entry("TWICE SHY"), Entry("pbj_firstrun") };
            Assert.False(LobbyCatalogue.Contains(all, "TWICE SHY"));
        }

        [Fact]
        public void Contains_ForAPrefixedSaveThatIsNotThere_ReturnsFalse()
        {
            // The key is one we would offer, so this runs the whole list rather
            // than short-circuiting on the shape of the name.
            var all = new[] { Entry("pbj_firstrun") };
            Assert.False(LobbyCatalogue.Contains(all, "pbj_missing"));
        }

        [Fact]
        public void Contains_ToleratesAnEntryWithNoKey()
        {
            // Entries come from the game's headers, and LoadSaveHeaders hands back
            // a default-constructed metadata for any directory it could not read.
            var all = new[] { Entry(null!), Entry("pbj_firstrun") };
            Assert.True(LobbyCatalogue.Contains(all, "pbj_firstrun"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Contains_ForNothing_ReturnsFalse(string? key)
        {
            Assert.False(LobbyCatalogue.Contains(new[] { Entry("pbj_firstrun") }, key));
        }

        [Fact]
        public void Contains_Null_ReturnsFalse()
        {
            Assert.False(LobbyCatalogue.Contains(null, "pbj_firstrun"));
        }
    }
}
