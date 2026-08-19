using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The lobby (M11a): selecting a save, readying, the synchronised load, and the
    // refusals on each.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // Helpers used by more than one part live in NetLogTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class NetLogTests
    {
        // --- lobby (M11a) ---

        [Fact]
        public void LobbySelected_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby save is now 'pbj_campaign' (3f9c1a04) | selection 2 — everyone must ready again",
                NetLog.LobbySelected("pbj_campaign", "3f9c1a04", 2));
        }

        [Fact]
        public void LobbySelected_WithNoDigest_RendersThePlaceholder()
        {
            // A save this machine has not hashed is still a save.
            Assert.Contains("(?)", NetLog.LobbySelected("pbj_campaign", null, 1));
        }

        [Fact]
        public void LobbySelectionCleared_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby save cleared | selection 3",
                NetLog.LobbySelectionCleared(3));
        }

        [Fact]
        public void LobbySelectIgnored_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring lobby save selection — not in the lobby",
                NetLog.LobbySelectIgnored("not in the lobby"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void LobbySelectIgnored_WithBlankReason_Throws(string? why)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.LobbySelectIgnored(why!));
            Assert.Equal("why", ex.ParamName);
        }

        [Fact]
        public void LobbyReadyReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby ready from #1 'ally' for selection 2",
                NetLog.LobbyReadyReceived(1, "ally", 2));
        }

        [Fact]
        public void LobbyUnreadyReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby unready from #1 'ally' for selection 2",
                NetLog.LobbyUnreadyReceived(1, "ally", 2));
        }

        [Fact]
        public void LobbyReadyIgnored_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring lobby ready from #1 for selection 2 — no save selected",
                NetLog.LobbyReadyIgnored(1, 2, "no save selected"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void LobbyReadyIgnored_WithBlankReason_Throws(string? why)
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.LobbyReadyIgnored(1, 2, why!));
            Assert.Equal("why", ex.ParamName);
        }

        [Fact]
        public void LobbyReadyAhead_SaysTheHostIsResendingRatherThanResyncing()
        {
            // Deliberately not worded like ReadyNeedsResync: nothing can put a
            // peer legitimately ahead of the host's selection, so this is a
            // misbehaving peer, not one that fell behind honestly.
            Assert.Equal(
                "[pb-and-j] peer #1 claims lobby selection 9 but the host is on 2 — resending the lobby state",
                NetLog.LobbyReadyAhead(1, 9, 2));
        }

        [Fact]
        public void LobbyBarrierWaiting_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] lobby 1/3 ready", NetLog.LobbyBarrierWaiting(1, 3));
        }

        [Fact]
        public void LobbyBarrierSatisfied_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby 3/3 ready for 'pbj_campaign' — everyone has agreed",
                NetLog.LobbyBarrierSatisfied(3, "pbj_campaign"));
        }

        [Fact]
        public void LobbyStateReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] lobby state | selection 2 | save 'pbj_campaign' | 1/3 ready",
                NetLog.LobbyStateReceived(2, "pbj_campaign", 1, 3));
        }

        [Fact]
        public void LobbyStateReceived_WithNothingSelected_RendersThePlaceholder()
        {
            Assert.Contains("save '?'", NetLog.LobbyStateReceived(0, null, 0, 2));
        }

        [Fact]
        public void LoadStarting_NamesTheSaveAndTheCount()
        {
            Assert.Equal(
                "[pb-and-j] loading 'pbj_campaign' on 2 machine(s) — everyone agreed",
                NetLog.LoadStarting(2, "pbj_campaign"));
        }

        [Fact]
        public void LoadStarting_WithNoSave_StillSaysSomething()
        {
            Assert.Contains("'?'", NetLog.LoadStarting(1, null), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded, "OK")]
        [InlineData(LoadOutcome.Refused, "REFUSED (the game would not start it)")]
        [InlineData(LoadOutcome.Unavailable, "UNAVAILABLE (no such save, or a different one)")]
        public void LoadReported_DescribesEveryOutcome(LoadOutcome outcome, string expected)
        {
            Assert.Equal(
                "[pb-and-j] load " + expected + " from #1 'ally'",
                NetLog.LoadReported(1, "ally", outcome));
        }

        [Fact]
        public void LoadReported_ForAnOutcomeWeDoNotKnow_SaysTheNumber()
        {
            // Reachable from the wire: the decoder casts the byte unvalidated.
            Assert.Equal(
                "[pb-and-j] load UNKNOWN (200) from #1 'ally'",
                NetLog.LoadReported(1, "ally", (LoadOutcome)200));
        }

        [Fact]
        public void LoadReported_WithNoName_StillNamesThePeer()
        {
            Assert.Contains("#1 '?'", NetLog.LoadReported(1, null, LoadOutcome.Loaded), StringComparison.Ordinal);
        }

        [Fact]
        public void LoadTimedOut_NamesTheWaitItGaveUpAfter()
        {
            Assert.Equal(
                "[pb-and-j] no word from #2 after 120s — carrying on without it",
                NetLog.LoadTimedOut(2));
        }

        [Fact]
        public void LoadComplete_CountsWhoActuallyGotIn()
        {
            // Not "2 of 2 loaded" — a participant that failed still completed the
            // barrier, and the line has to be able to say 1 of 2.
            Assert.Equal(
                "[pb-and-j] load complete | 1 of 2 machine(s) are in",
                NetLog.LoadComplete(1, 2));
        }

        [Fact]
        public void LoadAbandoned_SaysTheLobbyIsUsableAgain()
        {
            Assert.Equal(
                "[pb-and-j] the host could not load — abandoning, the lobby is open again",
                NetLog.LoadAbandoned());
        }

        [Fact]
        public void LoadIgnoredStale_NamesBothVersions()
        {
            Assert.Equal(
                "[pb-and-j] ignoring a load for selection 5 — we hold 4",
                NetLog.LoadIgnoredStale(5, 4));
        }

        [Fact]
        public void LoadAlreadyBegun_NamesTheVersion()
        {
            Assert.Equal(
                "[pb-and-j] already loading selection 5 — ignoring the repeat",
                NetLog.LoadAlreadyBegun(5));
        }

        [Fact]
        public void LobbySelectIsHostOnly_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] only the host picks the lobby save", NetLog.LobbySelectIsHostOnly());
        }
    }
}
