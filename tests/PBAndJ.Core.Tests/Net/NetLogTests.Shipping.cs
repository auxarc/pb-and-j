using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Shipping the fight (M12b): writing the scenario out to a peer, and every way
    // that can fail or be unnecessary.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // This class has no helpers and no fields -- every member is a test -- so
    // unlike the other split test classes there is no shared fixture in
    // NetLogTests.cs to look for.
    public partial class NetLogTests
    {
        // --- shipping the fight (M12b) ---

        [Fact]
        public void CombatShipping_SaysTheFightIsBeingWritten()
        {
            Assert.Contains("writing the fight", NetLog.CombatShipping(3, 2));
        }

        [Fact]
        public void CombatShipFailed_SaysTheHostIsCarryingOnAlone()
        {
            Assert.Contains("starting alone", NetLog.CombatShipFailed());
        }

        [Fact]
        public void CombatNobodyToWaitFor_IsSaidOutLoudRatherThanSkippedSilently()
        {
            // "The fight was never offered" and "everyone arrived instantly" look
            // identical in a log otherwise.
            Assert.Contains("nobody else is here", NetLog.CombatNobodyToWaitFor());
        }

        [Fact]
        public void CombatOffered_NamesTheFightAndItsDigest()
        {
            var line = NetLog.CombatOffered("pbj_combat_test", "d1", 2);
            Assert.Contains("pbj_combat_test", line);
            Assert.Contains("d1", line);
        }

        [Fact]
        public void CombatOffered_WithNothingToName_StillReads()
        {
            Assert.Contains("?", NetLog.CombatOffered(null, null, 1));
        }

        [Fact]
        public void CombatEntryReported_NamesWhoAndHow()
        {
            Assert.Contains("ally", NetLog.CombatEntryReported(1, "ally", LoadOutcome.Loaded));
            Assert.Contains("?", NetLog.CombatEntryReported(1, null, LoadOutcome.Refused));
        }

        [Fact]
        public void CombatEntryTimedOut_SaysTheFightStartsWithoutThem()
        {
            Assert.Contains("starting without it", NetLog.CombatEntryTimedOut(2));
        }

        [Fact]
        public void CombatEntryAbandoned_SaysHowManyWereStillComingIn()
        {
            Assert.Contains("2 machines", NetLog.CombatEntryAbandoned(2));
            Assert.Contains("1 machine ", NetLog.CombatEntryAbandoned(1));
        }

        [Fact]
        public void CombatShipTooLate_SaysTheFightIsOver()
        {
            Assert.Contains("no longer in it", NetLog.CombatShipTooLate());
        }

        [Fact]
        public void CombatShipNotOurs_ExplainsAFileThatAppearedForNoReason()
        {
            Assert.Contains("not hosting", NetLog.CombatShipNotOurs());
        }

        [Fact]
        public void CombatAlreadyHeld_AndFetching_NameTheFight()
        {
            Assert.Contains("pbj_combat_test", NetLog.CombatAlreadyHeld("pbj_combat_test"));
            Assert.Contains("pbj_combat_test", NetLog.CombatFetching("pbj_combat_test"));
            Assert.Contains("?", NetLog.CombatAlreadyHeld(null));
            Assert.Contains("?", NetLog.CombatFetching(null));
        }
    }
}
