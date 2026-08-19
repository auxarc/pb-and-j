using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The barrier: who is ready, who is stale, and what makes it commit.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // Helpers used by more than one part live in NetLogTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class NetLogTests
    {
        // --- barrier ---

        [Fact]
        public void ReadyReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ready from #1 'ally' | turn 3 | 1 order",
                NetLog.ReadyReceived(1, "ally", 3, 1));
        }

        [Fact]
        public void ReadyReceived_PluralisesOrders()
        {
            Assert.EndsWith("| 0 orders", NetLog.ReadyReceived(1, "ally", 3, 0));
            Assert.EndsWith("| 2 orders", NetLog.ReadyReceived(1, "ally", 3, 2));
        }

        [Fact]
        public void ReadyReceived_WithMissingName_UsesPlaceholder()
        {
            Assert.Contains("#1 '?'", NetLog.ReadyReceived(1, null, 3, 1));
        }

        [Fact]
        public void BarrierWaiting_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] barrier 1/2 — waiting", NetLog.BarrierWaiting(1, 2));
        }

        [Fact]
        public void BarrierCommitting_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] barrier 2/2 — committing turn 3", NetLog.BarrierCommitting(2, 2, 3));
        }

        [Fact]
        public void ReadyIgnoredStale_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring stale ready from #1 for turn 2 (now on turn 3)",
                NetLog.ReadyIgnoredStale(1, 2, 3));
        }

        [Fact]
        public void ReadyNeedsResync_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] peer #1 is ahead (ready for turn 4, host on turn 3) — resyncing",
                NetLog.ReadyNeedsResync(1, 4, 3));
        }
    }
}
