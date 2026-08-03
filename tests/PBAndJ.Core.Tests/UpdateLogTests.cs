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

        // --- the prompt ---
        //
        // These land on an NGUI label inside the game's confirmation dialog, not
        // in Player.log, which is why none of them carry the log prefix. The
        // dialog is the only place a second player ever learns they are stale, so
        // it has to be complete on its own — they will not be reading the log.

        private static UpdateResult Available => UpdateCheck.Compare("0.5.0", "0.6.0");

        [Fact]
        public void PromptHeader_NamesTheModSoThePlayerKnowsWhatIsAsking()
        {
            // A bare "Update available" modal in Phantom Brigade's own styling
            // would read as the game asking, not a mod.
            Assert.Contains("pb-and-j", UpdateLog.PromptHeader());
        }

        [Fact]
        public void PromptHeader_DoesNotCarryTheLogPrefix_BecauseALabelIsNotALogLine()
        {
            Assert.DoesNotContain("[pb-and-j]", UpdateLog.PromptHeader());
        }

        [Fact]
        public void PromptBody_NamesBothVersionsSoTheGapIsVisible()
        {
            var body = UpdateLog.PromptBody(Available);
            Assert.Contains("0.5.0", body);
            Assert.Contains("0.6.0", body);
        }

        [Fact]
        public void PromptBody_SaysItOpensAWebPage_SoConfirmingIsNotASurprise()
        {
            // Confirm sends them out of the game to a browser. Saying so is the
            // difference between a button and a trapdoor.
            Assert.Contains("browser", UpdateLog.PromptBody(Available));
        }

        [Fact]
        public void PromptBody_SaysTheGameMustRestart_BecauseModsLoadOnceAtStartup()
        {
            Assert.Contains("restart", UpdateLog.PromptBody(Available));
        }

        [Fact]
        public void PromptBody_DoesNotPromiseToInstallAnything()
        {
            // The mod cannot install: the game loads mod assemblies once, at
            // startup, into an AppDomain it cannot unload. Wording that implies
            // otherwise turns a manual folder swap into a bug report.
            var body = UpdateLog.PromptBody(Available);
            Assert.DoesNotContain("install", body, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("automatic", body, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void PromptBody_DoesNotCarryTheLogPrefix()
        {
            Assert.DoesNotContain("[pb-and-j]", UpdateLog.PromptBody(Available));
        }
    }
}
