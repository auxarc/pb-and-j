using PBAndJ.Core;
using Xunit;

namespace PBAndJ.Core.Tests
{
    /// <summary>
    /// The decision to put a modal in front of somebody. Separate from
    /// <c>UpdateCheck</c> — knowing a build is stale and interrupting a player
    /// over it are different questions, and only the second one can be annoying.
    /// </summary>
    public class UpdateOfferTests
    {
        private static UpdateResult Result(UpdateStatus status, string local, string latest)
        {
            ModVersion.TryParse(local, out var l);
            ModVersion.TryParse(latest, out var r);
            return new UpdateResult(status, l, r);
        }

        // --- who gets asked ---

        [Fact]
        public void Decide_WhenAnUpdateExists_OffersToOpenTheReleasesPage()
        {
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.UpdateAvailable, "0.5.0", "0.6.0"),
                dialogAvailable: true,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.PointAtReleasePage, offer);
        }

        [Fact]
        public void Decide_WhenCurrent_SaysNothing()
        {
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.Current, "0.5.0", "0.5.0"),
                dialogAvailable: true,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }

        [Fact]
        public void Decide_WhenLocalIsAhead_NeverSendsADevBuildBackwards()
        {
            // The dev machine is permanently ahead of the newest release between
            // cuts. A prompt here would offer to replace somebody's own work with
            // something older, once per session, forever.
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.LocalAhead, "0.6.0", "0.5.0"),
                dialogAvailable: true,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }

        [Fact]
        public void Decide_WhenTheVersionsAreUnreadable_DoesNotGuessThatAnUpdateExists()
        {
            // Unknown is the zero value precisely so that a failure to read never
            // degrades into a claim. It must not degrade into a prompt either.
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.Unknown, "0.5.0", "not-a-version"),
                dialogAvailable: true,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }

        // --- when asking is impossible or rude ---

        [Fact]
        public void Decide_WhenNoDialogIsAvailable_StaysSilentRatherThanFailing()
        {
            // The game's confirmation view is a scene singleton. If it is not up
            // yet there is nothing to open, and the log line already carries the
            // URL — so this is a quiet no-op, not an error.
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.UpdateAvailable, "0.5.0", "0.6.0"),
                dialogAvailable: false,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }

        [Fact]
        public void Decide_WhenAlreadyOffered_DoesNotAskASecondTimeInOneSession()
        {
            // The check runs on every session start, and an evening involves
            // several. Being asked once and ignoring it must mean ignored.
            var offer = UpdatePrompt.Decide(
                Result(UpdateStatus.UpdateAvailable, "0.5.0", "0.6.0"),
                dialogAvailable: true,
                alreadyOffered: true);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }

        [Theory]
        [InlineData(UpdateStatus.Current)]
        [InlineData(UpdateStatus.LocalAhead)]
        [InlineData(UpdateStatus.Unknown)]
        public void Decide_ForEveryNonUpdateStatus_StaysSilentWhateverElseIsTrue(UpdateStatus status)
        {
            // Guards the ordering of the checks: a future edit that tests
            // alreadyOffered before the status must not start prompting people
            // who are already current.
            var offer = UpdatePrompt.Decide(
                Result(status, "0.5.0", "0.5.0"),
                dialogAvailable: true,
                alreadyOffered: false);

            Assert.Equal(UpdateOffer.Nothing, offer);
        }
    }
}
