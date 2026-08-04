using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class LobbySaveWritesTests
    {
        [Fact]
        public void NameForWrite_InACoopCampaign_PrefixesTheName()
        {
            // The whole point of M11d part 4: the game picks its own save names on
            // its own schedule, and every one of them belongs to the namespace the
            // lobby selects from.
            Assert.Equal(
                "pbj_autosave_timed_0",
                LobbySaveWrites.NameForWrite("autosave_timed_0", true, true));
        }

        [Fact]
        public void NameForWrite_OutsideACoopCampaign_LeavesTheNameAlone()
        {
            // A singleplayer campaign writing prefixed saves is the failure that
            // makes a player's game look deleted: hidden from the load screen and
            // from Continue, both by M11b's own patches.
            Assert.Equal(
                "autosave_timed_0",
                LobbySaveWrites.NameForWrite("autosave_timed_0", false, true));
        }

        [Fact]
        public void NameForWrite_OnlyRedirectsTheNormalSaveLocation()
        {
            // BugTools writes the crash reporter's copy under SaveLocation.Reporter
            // (BugTools.cs:600). Renaming that would corrupt bug reports, and it is
            // not a campaign save in the first place. The same guard excludes the
            // dev-only InternalEditable path CIViewPauseSave takes at :701.
            Assert.Equal(
                "report_2026_08_04",
                LobbySaveWrites.NameForWrite("report_2026_08_04", true, false));
        }

        [Fact]
        public void NameForWrite_LeavesAnAlreadyPrefixedNameAlone()
        {
            // DoSave re-enters itself through DelayedSaveIE (DataManagerSave.cs:410
            // → :421), the path autosave_after_combat and autosave_after_stop both
            // take. Without this guard the second pass writes pbj_pbj_….
            Assert.Equal(
                "pbj_firstrun",
                LobbySaveWrites.NameForWrite("pbj_firstrun", true, true));
        }

        [Fact]
        public void NameForWrite_RecognisesAnAlreadyPrefixedNameWhateverItsCase()
        {
            // The filesystem under Proton is case-sensitive but the game compares
            // save names OrdinalIgnoreCase, so PBJ_firstrun and pbj_firstrun are one
            // save to the game and two directories on disk. Prefixing this again
            // would make a third.
            Assert.Equal(
                "PBJ_firstrun",
                LobbySaveWrites.NameForWrite("PBJ_firstrun", true, true));
        }

        [Fact]
        public void NameForWrite_Null_ReturnsNull()
        {
            // DoSave's own GetSavePath rejects a null name (DataManagerSave.cs:588).
            // Turning it into "pbj_" would invent a save directory out of a bug.
            Assert.Null(LobbySaveWrites.NameForWrite(null, true, true));
        }

        [Fact]
        public void NameForWrite_Empty_ReturnsItUnchanged()
        {
            Assert.Equal("", LobbySaveWrites.NameForWrite("", true, true));
        }

        [Fact]
        public void IsProtectedFromOverwrite_TheScenarioSlot_AlwaysIsEvenInACoopCampaign()
        {
            // M9's WriteScenario deletes and rewrites this directory on every
            // scenario transfer. It is inside the prefix and it is not a campaign,
            // so it is the one name the softening below must not open up.
            Assert.True(LobbySaveWrites.IsProtectedFromOverwrite(LobbySaveNames.ScenarioSlot, true));
            Assert.True(LobbySaveWrites.IsProtectedFromOverwrite(LobbySaveNames.ScenarioSlot, false));
        }

        [Fact]
        public void IsProtectedFromOverwrite_AMultiplayerSave_IsProtectedFromSingleplayer()
        {
            // M11b's rule, unchanged: multiplayer saves stay visible in the
            // singleplayer save grid and are made unwritable there instead of
            // hidden, because the duplicate check, save count and save limit all run
            // off the same call inside the same rebuild.
            Assert.True(LobbySaveWrites.IsProtectedFromOverwrite("pbj_firstrun", false));
        }

        [Fact]
        public void IsProtectedFromOverwrite_AMultiplayerSave_IsWritableInsideACoopCampaign()
        {
            // The softening. Inside a co-op campaign the namespace is the player's
            // own: clicking their save in the grid must offer an overwrite rather
            // than the game's "restricted" wording, which would otherwise leave
            // retyping the name as the only route — and that route skips the
            // overwrite confirmation entirely.
            Assert.False(LobbySaveWrites.IsProtectedFromOverwrite("pbj_firstrun", true));
        }

        [Fact]
        public void IsProtectedFromOverwrite_ASingleplayerSave_NeverIs()
        {
            // The game's own reserved-name rules still apply on top of this; this
            // answer is only ever "and also refuse ours".
            Assert.False(LobbySaveWrites.IsProtectedFromOverwrite("firstrun", true));
            Assert.False(LobbySaveWrites.IsProtectedFromOverwrite("firstrun", false));
        }

        [Fact]
        public void IsProtectedFromOverwrite_Null_IsNot()
        {
            Assert.False(LobbySaveWrites.IsProtectedFromOverwrite(null, false));
        }

        [Fact]
        public void IsProtectedFromOverwrite_TheScenarioSlot_IgnoresCase()
        {
            Assert.True(LobbySaveWrites.IsProtectedFromOverwrite("PBJ_COMBAT_TEST", true));
        }
    }
}
