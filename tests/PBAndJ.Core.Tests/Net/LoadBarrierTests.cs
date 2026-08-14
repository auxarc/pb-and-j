using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class LoadBarrierTests
    {
        private static LoadBarrier Started(int version = 1, params int[] participants)
        {
            var barrier = new LoadBarrier();
            barrier.Start(version, participants.Length == 0 ? new[] { 0, 1 } : participants);
            return barrier;
        }

        [Fact]
        public void ANewBarrier_IsNotRunning()
        {
            var barrier = new LoadBarrier();
            Assert.False(barrier.InFlight);
            Assert.False(barrier.IsComplete);
            Assert.Empty(barrier.Waiting);
        }

        [Fact]
        public void Start_TakesTheParticipantsAndTheVersion()
        {
            var barrier = Started(7, 0, 1, 2);
            Assert.True(barrier.InFlight);
            Assert.Equal(7, barrier.Version);
            Assert.Equal(new[] { 0, 1, 2 }, barrier.Waiting);
            Assert.False(barrier.IsComplete);
        }

        [Fact]
        public void Report_ForTheRightVersion_IsAccepted()
        {
            var barrier = Started();
            Assert.True(barrier.Report(1, 1, LoadOutcome.Loaded));
            Assert.Equal(new[] { 0 }, barrier.Waiting);
        }

        [Fact]
        public void Report_ForAStaleVersion_IsIgnored()
        {
            // A callback can outlive the load that asked for it — a campaign load
            // is seconds long and the lobby can move on. Counting a report from a
            // load nobody is waiting for would complete the wrong barrier.
            var barrier = Started(2);
            Assert.False(barrier.Report(1, 1, LoadOutcome.Loaded));
            Assert.Equal(new[] { 0, 1 }, barrier.Waiting);
        }

        [Fact]
        public void Report_FromSomeoneNotInTheLoad_IsIgnored()
        {
            var barrier = Started(1, 0, 1);
            Assert.False(barrier.Report(9, 1, LoadOutcome.Loaded));
        }

        [Fact]
        public void Report_Twice_IsIgnoredTheSecondTime()
        {
            var barrier = Started();
            Assert.True(barrier.Report(1, 1, LoadOutcome.Loaded));
            Assert.False(barrier.Report(1, 1, LoadOutcome.Loaded));
        }

        [Fact]
        public void Report_WhenNothingIsInFlight_IsIgnored()
        {
            Assert.False(new LoadBarrier().Report(1, 1, LoadOutcome.Loaded));
        }

        [Fact]
        public void IsComplete_OnceEveryoneHasAnswered()
        {
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Loaded);
            Assert.False(barrier.IsComplete);
            barrier.Report(1, 1, LoadOutcome.Loaded);
            Assert.True(barrier.IsComplete);
            Assert.Empty(barrier.Waiting);
        }

        [Fact]
        public void IsComplete_CountsFailuresAsAnswers()
        {
            // The barrier is waiting for news, not for success. A peer that says
            // "I could not" has stopped being a reason to wait.
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Loaded);
            barrier.Report(1, 1, LoadOutcome.Unavailable);
            Assert.True(barrier.IsComplete);
        }

        [Fact]
        public void Outcomes_AreRecordedPerParticipant()
        {
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Loaded);
            barrier.Report(1, 1, LoadOutcome.Refused);
            Assert.Equal(LoadOutcome.Loaded, barrier.OutcomeFor(0));
            Assert.Equal(LoadOutcome.Refused, barrier.OutcomeFor(1));
        }

        [Fact]
        public void OutcomeFor_SomeoneWhoHasNotAnswered_IsNull()
        {
            Assert.Null(Started().OutcomeFor(1));
        }

        [Fact]
        public void Loaded_ListsOnlyThoseWhoActuallyGotIn()
        {
            var barrier = Started(1, 0, 1, 2);
            barrier.Report(0, 1, LoadOutcome.Loaded);
            barrier.Report(1, 1, LoadOutcome.Unavailable);
            barrier.Report(2, 1, LoadOutcome.Loaded);
            Assert.Equal(new[] { 0, 2 }, barrier.Loaded);
        }

        [Fact]
        public void Loaded_WhenNobodyGotIn_IsEmpty()
        {
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Refused);
            barrier.Report(1, 1, LoadOutcome.Unavailable);
            Assert.True(barrier.IsComplete);
            Assert.Empty(barrier.Loaded);
        }

        [Fact]
        public void Drop_RemovesSomeoneStillBeingWaitedFor()
        {
            // What a timeout does, and what a disconnect mid-load does. The load
            // completes on the rest rather than hanging on someone who has gone.
            var barrier = Started();
            Assert.True(barrier.Drop(1));
            Assert.Equal(new[] { 0 }, barrier.Waiting);
        }

        [Fact]
        public void Drop_CanCompleteTheBarrier()
        {
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Loaded);
            Assert.False(barrier.IsComplete);
            barrier.Drop(1);
            Assert.True(barrier.IsComplete);
        }

        [Fact]
        public void Drop_ForSomeoneWhoAlreadyAnswered_ChangesNothing()
        {
            var barrier = Started();
            barrier.Report(1, 1, LoadOutcome.Loaded);
            Assert.False(barrier.Drop(1));
            Assert.Equal(LoadOutcome.Loaded, barrier.OutcomeFor(1));
        }

        [Fact]
        public void Finish_EndsTheFlightAndForgetsIt()
        {
            var barrier = Started();
            barrier.Report(0, 1, LoadOutcome.Loaded);
            barrier.Finish();

            Assert.False(barrier.InFlight);
            Assert.False(barrier.IsComplete);
            Assert.Empty(barrier.Waiting);

            // And a late report for the finished load cannot restart it.
            Assert.False(barrier.Report(1, 1, LoadOutcome.Loaded));
        }

        [Fact]
        public void Start_WhileAlreadyInFlight_ReplacesTheOldLoad()
        {
            // Should not happen — the host guards on InFlight — but a barrier
            // that silently kept the first load's participants would wait on
            // people who were never told.
            var barrier = Started(1, 0, 1);
            barrier.Start(2, new[] { 5 });
            Assert.Equal(2, barrier.Version);
            Assert.Equal(new[] { 5 }, barrier.Waiting);
        }

        // --- deadlines ---

        [Fact]
        public void Deadlines_AreNotMintedByStart()
        {
            // Start runs in message-handler context, where the session's clock
            // may still be zero — a deadline of 0 + 120 judged against process
            // uptime expires everyone on the first tick. Minting waits for a
            // real clock.
            var barrier = Started();
            Assert.Empty(barrier.Expired(1000.0, 120.0));
            Assert.Equal(new[] { 0, 1 }, barrier.Waiting);
        }

        [Fact]
        public void Expired_AfterSeeding_ReportsNobodyUntilTheDeadlinePasses()
        {
            var barrier = Started();
            barrier.Seed(1000.0);
            Assert.Empty(barrier.Expired(1119.0, 120.0));
        }

        [Fact]
        public void Expired_ReportsEveryoneStillWaitingPastTheDeadline()
        {
            var barrier = Started();
            barrier.Seed(1000.0);
            Assert.Equal(new[] { 0, 1 }, barrier.Expired(1121.0, 120.0));
        }

        [Fact]
        public void Expired_DoesNotReportSomeoneWhoAnswered()
        {
            var barrier = Started();
            barrier.Seed(1000.0);
            barrier.Report(1, 1, LoadOutcome.Loaded);
            Assert.Equal(new[] { 0 }, barrier.Expired(1121.0, 120.0));
        }

        [Fact]
        public void Seed_IsIgnoredForAnyoneAlreadySeeded()
        {
            // Ticks arrive four times a second; re-stamping would push the
            // deadline forward forever and the timeout would never fire.
            var barrier = Started();
            barrier.Seed(1000.0);
            barrier.Seed(1100.0);
            Assert.Equal(new[] { 0, 1 }, barrier.Expired(1121.0, 120.0));
        }

        [Fact]
        public void Seed_WhenNotInFlight_DoesNothing()
        {
            var barrier = new LoadBarrier();
            barrier.Seed(1000.0);
            Assert.Empty(barrier.Expired(9999.0, 120.0));
        }

        [Fact]
        public void Expired_WhenNotInFlight_IsEmpty()
        {
            Assert.Empty(new LoadBarrier().Expired(9999.0, 120.0));
        }

        [Fact]
        public void Seed_AfterAJoinerWasAdded_CoversTheJoinerToo()
        {
            // Not a real path today, but Seed walking only the pending set is
            // what makes it safe if one appears.
            var barrier = Started();
            barrier.Seed(1000.0);
            barrier.Report(0, 1, LoadOutcome.Loaded);
            Assert.Equal(new[] { 1 }, barrier.Expired(1121.0, 120.0));
        }
    }
}
