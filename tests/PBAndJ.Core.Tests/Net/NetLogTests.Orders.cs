using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Under the author's `// --- orders and commit ---` banner, which is 667 lines
    // long and covers rather more than its name: this part is the front of it --
    // orders applied and their results, un-ready, the combat edges, the send queue's
    // complaints, and the two snapshot losses.
    // The rest of that banner's span is .Playback.cs and .Commit.cs.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // Helpers used by more than one part live in NetLogTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class NetLogTests
    {
        // --- orders and commit ---

        [Fact]
        public void OrdersApplied_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] applied 1 remote order, 0 rejected", NetLog.OrdersApplied(1, 0));
            Assert.Equal("[pb-and-j] applied 3 remote orders, 2 rejected", NetLog.OrdersApplied(3, 2));
        }

        [Fact]
        public void OrderResultSent_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] order result to #2: 3 accepted, 1 rejected",
                NetLog.OrderResultSent(2, 3, 1));
        }

        [Fact]
        public void OrderResultReceived_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] turn 4 orders: 3 accepted, 1 rejected by host",
                NetLog.OrderResultReceived(4, 3, 1));
        }

        [Fact]
        public void UnreadyReceived_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] un-ready from #2 'ally' for turn 3", NetLog.UnreadyReceived(2, "ally", 3));
        }

        [Fact]
        public void UnreadyReceived_WithNoName_MarksItUnknown()
        {
            Assert.Equal("[pb-and-j] un-ready from #2 '?' for turn 3", NetLog.UnreadyReceived(2, null, 3));
        }

        [Fact]
        public void UnreadyIgnored_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] ignoring un-ready from #2 for turn 3 — already executing",
                NetLog.UnreadyIgnored(2, 3, "already executing"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void UnreadyIgnored_WithBlankReason_Throws(string? why)
        {
            Assert.Throws<ArgumentException>(() => NetLog.UnreadyIgnored(2, 3, why!));
        }

        [Fact]
        public void CombatStarted_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] combat started on turn 0 — announcing to 1 peer", NetLog.CombatStarted(0, 1));
            Assert.Equal("[pb-and-j] combat started on turn 4 — announcing to 2 peers", NetLog.CombatStarted(4, 2));
        }

        [Fact]
        public void CombatEnded_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] combat ended — unlocking 1 peer", NetLog.CombatEnded(1));
            Assert.Equal("[pb-and-j] combat ended — unlocking 0 peers", NetLog.CombatEnded(0));
        }

        [Fact]
        public void SendQueueBacklog_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] send queue backing up for #2: 40 frame(s), 262144 byte(s) — slow link",
                NetLog.SendQueueBacklog(2, 262144, 40));
        }

        [Fact]
        public void SendQueueOverflowed_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] send queue OVERFLOWED for #2 at 1024 frame(s), 4194304 byte(s) — dropping the peer",
                NetLog.SendQueueOverflowed(2, 4194304, 1024));
        }

        [Fact]
        public void SendFailed_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] send to #2 failed: IOException", NetLog.SendFailed(2, "IOException"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("  ")]
        public void SendFailed_WithBlankDetail_Throws(string? detail)
        {
            Assert.Throws<ArgumentException>(() => NetLog.SendFailed(2, detail!));
        }

        [Fact]
        public void SendAfterStop_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] dropping a frame for #0: the transport is stopped",
                NetLog.SendAfterStop(0));
        }

        [Fact]
        public void SnapshotUnitsSkipped_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] snapshot: 2 unit(s) not present locally, 1 local unit(s) not in the snapshot",
                NetLog.SnapshotUnitsSkipped(2, 1));
        }

        [Fact]
        public void SnapshotClamped_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] snapshot clamped: 128 units captured, only 128 fit — the rest are NOT corrected",
                NetLog.SnapshotClamped(128, 128));
        }
    }
}
