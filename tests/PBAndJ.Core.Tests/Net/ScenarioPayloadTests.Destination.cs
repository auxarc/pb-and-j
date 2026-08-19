using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Where a transfer is allowed to land. A payload names its own destination, so
    // this is what stops a peer already past the passphrase from writing outside the
    // pbj_ namespace.
    //
    // IsAllowedDestination_AcceptsADotInsideTheName was filed among the Inspect tests
    // in the original; it is a destination test and sits with its siblings here.
    public partial class ScenarioPayloadTests
    {
        // --- destination (M11e) ---

        [Theory]
        [InlineData("pbj_combat_test")]
        [InlineData("pbj_firstrun")]
        [InlineData("pbj_x")]
        public void IsAllowedDestination_AcceptsSavesInsideTheNamespace(string key)
        {
            // M9's slot and a campaign key are both legitimate destinations: one
            // mechanism carries both, so the rule is "inside the namespace and
            // structurally safe" rather than a list of special cases.
            Assert.True(ScenarioPayload.IsAllowedDestination(key));
        }

        [Fact]
        public void IsAllowedDestination_AcceptsADotInsideTheName()
        {
            // pbj_.hidden looks like a hidden file and is not one: the prefix means
            // the directory name starts with 'p'. The leading-dot rule is about what
            // the name actually begins with, so this is legitimate and the guard
            // must not over-reach and refuse a save someone could really own.
            Assert.True(ScenarioPayload.IsAllowedDestination("pbj_.hidden"));
        }

        [Theory]
        [InlineData("pbj_../../.bashrc")]
        [InlineData("pbj_a/b")]
        [InlineData("pbj_a\\b")]
        [InlineData("pbj_C:")]
        [InlineData("pbj_trailing.")]
        public void IsAllowedDestination_RejectsAnythingThatCouldEscapeTheSaveFolder(string key)
        {
            // The reason structural safety is an explicit conjunct rather than
            // borrowed from LobbyCatalogue.IsOffered, which checks only the prefix
            // and not-the-scenario-slot — every one of these passes that.
            Assert.False(ScenarioPayload.IsAllowedDestination(key));
        }

        [Theory]
        [InlineData("firstrun")]
        [InlineData("autosave_timed_0")]
        [InlineData(null)]
        [InlineData("")]
        public void IsAllowedDestination_RejectsAnythingOutsideTheNamespace(string? key)
        {
            // A transfer may only ever land inside pbj_. Writing outside it would
            // let a peer past the passphrase overwrite a singleplayer campaign.
            Assert.False(ScenarioPayload.IsAllowedDestination(key));
        }

        [Fact]
        public void IsAllowedDestination_AcceptsAKeyAtTheLengthLimit()
        {
            // A legal display name is 64 characters and the key carries the prefix
            // on top, so the key bound is 68 and not 64. Reusing the file-name
            // bound here would make legal saves untransferable.
            Assert.True(ScenarioPayload.IsAllowedDestination(
                LobbySaveNames.Prefix + new string('a', LobbySaveNames.MaxNameLength)));
            Assert.False(ScenarioPayload.IsAllowedDestination(
                LobbySaveNames.Prefix + new string('a', LobbySaveNames.MaxNameLength + 1)));
        }

        [Fact]
        public void Inspect_RejectsADisallowedDestination()
        {
            var payload = new ScenarioPayload("../../elsewhere", new[] { Content(), Metadata() });
            Assert.Equal(ScenarioRejection.DisallowedDestination, payload.Inspect());
        }

        [Fact]
        public void Inspect_ChecksTheDestinationBeforeNames_SoAFloodIsStillCheapToRefuse()
        {
            // Same doctrine as the size check above: refuse on the one cheap string
            // before doing per-file work.
            var payload = new ScenarioPayload("nope", new[]
            {
                new ScenarioFile("../../.bashrc", Bytes("rm -rf")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.DisallowedDestination, payload.Inspect());
        }

        [Fact]
        public void None_StillReportsNoFiles_NotABadDestination()
        {
            // Load-bearing ordering. HostSession.OfferScenario treats NoFiles as the
            // benign "nothing to offer" case and logs nothing; every other rejection
            // is reported as a fault. A host that has never taken a combat save must
            // not start warning about its destination.
            Assert.Equal(ScenarioRejection.NoFiles, ScenarioPayload.None.Inspect());
        }
    }
}
