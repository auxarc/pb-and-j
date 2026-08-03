using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class ModVersionTests
    {
        // --- parsing ---

        [Theory]
        [InlineData("0.4.0", 0, 4, 0)]
        [InlineData("1.0.0", 1, 0, 0)]
        [InlineData("0.10.2", 0, 10, 2)]
        [InlineData("12.34.56", 12, 34, 56)]
        public void TryParse_ReadsThreeParts(string text, int major, int minor, int patch)
        {
            Assert.True(ModVersion.TryParse(text, out var version));
            Assert.Equal(major, version.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(patch, version.Patch);
        }

        [Theory]
        [InlineData("v0.4.0")]
        [InlineData("V0.4.0")]
        public void TryParse_ToleratesATagPrefix(string text)
        {
            // GitHub release tags are conventionally v-prefixed; the mod's own
            // version string is not. Both name the same build.
            Assert.True(ModVersion.TryParse(text, out var version));
            Assert.Equal(new ModVersion(0, 4, 0), version);
        }

        [Theory]
        [InlineData("0.4")]
        [InlineData("0.4.0.1")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        [InlineData("v")]
        [InlineData("a.b.c")]
        [InlineData("0.4.x")]
        [InlineData("-1.0.0")]
        [InlineData("0.4.0-beta")]
        [InlineData("0..0")]
        [InlineData("999999999999.0.0")]
        public void TryParse_RejectsAnythingElse(string? text)
        {
            Assert.False(ModVersion.TryParse(text, out _));
        }

        [Fact]
        public void TryParse_OnFailure_YieldsZero()
        {
            ModVersion.TryParse("nonsense", out var version);
            Assert.Equal(default, version);
        }

        // --- ordering ---

        [Theory]
        [InlineData("0.4.0", "0.4.0", 0)]
        [InlineData("0.4.0", "0.5.0", -1)]
        [InlineData("0.5.0", "0.4.0", 1)]
        [InlineData("0.9.0", "0.10.0", -1)]
        [InlineData("0.10.0", "0.9.0", 1)]
        [InlineData("1.0.0", "0.99.99", 1)]
        [InlineData("0.99.99", "1.0.0", -1)]
        [InlineData("0.4.1", "0.4.2", -1)]
        [InlineData("0.4.2", "0.4.1", 1)]
        public void CompareTo_OrdersNumericallyNotLexically(string left, string right, int expected)
        {
            // 0.9.0 vs 0.10.0 is the case a string comparison gets wrong, and it
            // would get it wrong silently, telling everyone they are up to date.
            ModVersion.TryParse(left, out var a);
            ModVersion.TryParse(right, out var b);
            Assert.Equal(expected, a.CompareTo(b));
        }

        [Fact]
        public void Equals_AndHashCode_AgreeForTheSameVersion()
        {
            var a = new ModVersion(1, 2, 3);
            var b = new ModVersion(1, 2, 3);
            Assert.True(a.Equals(b));
            Assert.True(a.Equals((object)b));
            Assert.False(a.Equals("1.2.3"));
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void ToString_RoundTrips()
        {
            Assert.Equal("0.4.0", new ModVersion(0, 4, 0).ToString());
        }

        // --- the decision ---

        [Fact]
        public void Compare_WhenRemoteIsNewer_ReportsAnUpdate()
        {
            var result = UpdateCheck.Compare("0.4.0", "v0.5.0");
            Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
            Assert.Equal("0.5.0", result.Latest.ToString());
        }

        [Fact]
        public void Compare_WhenEqual_ReportsCurrent()
        {
            Assert.Equal(UpdateStatus.Current, UpdateCheck.Compare("0.4.0", "0.4.0").Status);
        }

        [Fact]
        public void Compare_WhenLocalIsNewer_SaysSoRatherThanClaimingCurrent()
        {
            // The dev machine is routinely ahead of the newest release. Calling
            // that "up to date" would hide a forgotten release; calling it an
            // update would send someone downgrading over their own work.
            Assert.Equal(UpdateStatus.LocalAhead, UpdateCheck.Compare("0.5.0", "0.4.0").Status);
        }

        [Theory]
        [InlineData("0.4.0", "not-a-version")]
        [InlineData("not-a-version", "0.4.0")]
        [InlineData(null, "0.4.0")]
        [InlineData("0.4.0", null)]
        public void Compare_WhenEitherSideIsUnreadable_ReportsUnknown(string? local, string? remote)
        {
            // Never guess. An unreadable version must not silently read as
            // "current", which is the failure that leaves someone on a stale
            // build wondering why the handshake is refused.
            Assert.Equal(UpdateStatus.Unknown, UpdateCheck.Compare(local, remote).Status);
        }

        [Fact]
        public void Compare_RetainsBothVersionsForTheMessage()
        {
            var result = UpdateCheck.Compare("0.4.0", "0.6.1");
            Assert.Equal("0.4.0", result.Local.ToString());
            Assert.Equal("0.6.1", result.Latest.ToString());
        }
    }
}
