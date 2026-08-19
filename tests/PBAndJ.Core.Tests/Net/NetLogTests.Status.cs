using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // What `pbj.status` prints, and what it prints when there is no session.
    //
    // One part of NetLogTests, a single class split across 9 files.
    // This class has no helpers and no fields -- every member is a test -- so
    // unlike the other split test classes there is no shared fixture in
    // NetLogTests.cs to look for.
    public partial class NetLogTests
    {
        // --- status ---

        [Fact]
        public void Status_ComposesTheLine()
        {
            Assert.Equal(
                "[pb-and-j] session HOST | state Planning | turn 3 | participants 2 | ready 0/2",
                NetLog.Status("HOST", "Planning", 3, 2, 0));
        }

        [Fact]
        public void Status_WithBlankRole_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.Status(" ", "Planning", 0, 0, 0));
            Assert.Equal("role", ex.ParamName);
        }

        [Fact]
        public void Status_WithBlankState_Throws()
        {
            var ex = Assert.Throws<ArgumentException>(() => NetLog.Status("HOST", "", 0, 0, 0));
            Assert.Equal("state", ex.ParamName);
        }

        [Fact]
        public void NoSession_ComposesTheLine()
        {
            Assert.Equal("[pb-and-j] no session — use pbj.host or pbj.join", NetLog.NoSession());
        }
    }
}
