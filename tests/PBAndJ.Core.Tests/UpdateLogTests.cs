using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    public class UpdateLogTests
    {
        private const string Url = "https://github.com/auxarc/pb-and-j/releases/latest";

        [Fact]
        public void Describe_UpdateAvailable_NamesBothVersionsAndWhereToGetIt()
        {
            var line = UpdateLog.Describe(UpdateCheck.Compare("0.4.0", "0.5.0"), Url);
            Assert.Contains("0.4.0", line);
            Assert.Contains("0.5.0", line);
            Assert.Contains(Url, line);
        }

        [Fact]
        public void Describe_UpdateAvailable_WithNoUrl_StillNamesTheVersions()
        {
            // A release with no downloadable asset is still worth reporting.
            var line = UpdateLog.Describe(UpdateCheck.Compare("0.4.0", "0.5.0"), null);
            Assert.Contains("0.5.0", line);
            Assert.DoesNotContain("—  ", line);
        }

        [Fact]
        public void Describe_Current_SaysSoWithoutShouting()
        {
            var line = UpdateLog.Describe(UpdateCheck.Compare("0.4.0", "0.4.0"), Url);
            Assert.Contains("0.4.0", line);
            Assert.DoesNotContain(Url, line);
        }

        [Fact]
        public void Describe_LocalAhead_DoesNotTellAnyoneToDowngrade()
        {
            var line = UpdateLog.Describe(UpdateCheck.Compare("0.5.0", "0.4.0"), Url);
            Assert.Contains("0.5.0", line);
            Assert.Contains("0.4.0", line);
            Assert.DoesNotContain(Url, line);
        }

        [Fact]
        public void Describe_Unknown_SaysItCouldNotTellRatherThanGuessing()
        {
            var line = UpdateLog.Describe(UpdateCheck.Compare("0.4.0", "wat"), Url);
            Assert.Contains("could not", line);
            Assert.DoesNotContain("up to date", line);
        }

        [Fact]
        public void Describe_EveryStatus_CarriesThePrefix()
        {
            foreach (var (local, remote) in new[]
                     {
                         ("0.4.0", "0.5.0"), ("0.4.0", "0.4.0"),
                         ("0.5.0", "0.4.0"), ("0.4.0", "wat"),
                     })
            {
                Assert.StartsWith("[pb-and-j] ", UpdateLog.Describe(UpdateCheck.Compare(local, remote), Url));
            }
        }

        [Fact]
        public void CheckFailed_NamesTheReasonAndIsNotAlarming()
        {
            // Being unable to reach GitHub is not an error in the mod, and must
            // not read like one — the session works fine without it.
            var line = UpdateLog.CheckFailed("connection timed out");
            Assert.StartsWith("[pb-and-j] ", line);
            Assert.Contains("connection timed out", line);
        }

        [Fact]
        public void CheckFailed_WithNoReason_StillReads()
        {
            Assert.StartsWith("[pb-and-j] ", UpdateLog.CheckFailed(null));
        }

        [Fact]
        public void NoReleases_ReadsAsAStateNotAFailure()
        {
            // GitHub answers releases/latest with a bare 404 when a repo has
            // never published one. Reporting that as a failed request would put
            // an alarming line in the log on every session start for as long as
            // no release exists — which is the repo's normal state today.
            var line = UpdateLog.NoReleases();
            Assert.StartsWith("[pb-and-j] ", line);
            Assert.Contains("no releases", line);
            Assert.DoesNotContain("failed", line);
        }

        [Fact]
        public void Checking_NamesTheSource()
        {
            // The mod promises no outbound traffic without opt-in, so the one
            // call it does make announces itself.
            Assert.Contains("api.github.com", UpdateLog.Checking("api.github.com"));
        }
    }
}
