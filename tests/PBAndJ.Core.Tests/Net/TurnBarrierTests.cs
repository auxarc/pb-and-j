using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class TurnBarrierTests
    {
        private static TurnBarrier Barrier(int turn = 3, params int[] participants)
        {
            var barrier = new TurnBarrier(turn);
            foreach (var p in participants)
            {
                barrier.AddParticipant(p);
            }
            return barrier;
        }

        private static TurnBarrier HostAndOneClient(int turn = 3) => Barrier(turn, 0, 1);

        [Fact]
        public void IsSatisfied_WithNoParticipants_ReturnsFalse()
        {
            // Never commit an empty session.
            Assert.False(Barrier().IsSatisfied);
        }

        [Fact]
        public void IsSatisfied_WithParticipantsButNoneReady_ReturnsFalse()
        {
            Assert.False(HostAndOneClient().IsSatisfied);
        }

        [Fact]
        public void SetReady_ForAllParticipants_SatisfiesBarrier()
        {
            var barrier = HostAndOneClient();
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(0, 3));
            Assert.False(barrier.IsSatisfied);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 3));
            Assert.True(barrier.IsSatisfied);
        }

        [Fact]
        public void SetReady_Twice_IsIdempotent()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(1, 3);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 3));
            Assert.Equal(1, barrier.ReadyCount);
        }

        [Fact]
        public void SetReady_ForStaleTurn_ReturnsStale()
        {
            // A late duplicate arriving after the host already committed.
            Assert.Equal(ReadyOutcome.Stale, HostAndOneClient().SetReady(1, 2));
        }

        [Fact]
        public void SetReady_ForStaleTurn_DoesNotMarkReady()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(1, 2);
            Assert.Equal(0, barrier.ReadyCount);
        }

        [Fact]
        public void SetReady_ForFutureTurn_ReturnsNeedsResync()
        {
            // NOT a protocol violation: a scenario force-execute can advance the
            // host's turn behind the barrier's back, so a peer can legitimately
            // be behind. Disconnecting here would kick an innocent peer.
            Assert.Equal(ReadyOutcome.NeedsResync, HostAndOneClient().SetReady(1, 4));
        }

        [Fact]
        public void SetReady_ForUnknownParticipant_ReturnsUnknownParticipant()
        {
            Assert.Equal(ReadyOutcome.UnknownParticipant, HostAndOneClient().SetReady(99, 3));
        }

        [Fact]
        public void Unready_ClearsReadiness()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 3);
            barrier.SetReady(1, 3);
            Assert.True(barrier.IsSatisfied);
            Assert.True(barrier.Unready(1));
            Assert.False(barrier.IsSatisfied);
        }

        [Fact]
        public void Unready_ForNotReadyParticipant_ReturnsFalse()
        {
            Assert.False(HostAndOneClient().Unready(1));
        }

        [Fact]
        public void Unready_ForUnknownParticipant_ReturnsFalse()
        {
            Assert.False(HostAndOneClient().Unready(99));
        }

        [Fact]
        public void RemoveParticipant_WhenOthersAreReady_SatisfiesBarrier()
        {
            // A dead peer must never wedge the session.
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 3);
            Assert.False(barrier.IsSatisfied);
            Assert.True(barrier.RemoveParticipant(1));
            Assert.True(barrier.IsSatisfied);
        }

        [Fact]
        public void RemoveParticipant_LastRemaining_LeavesBarrierUnsatisfied()
        {
            var barrier = Barrier(3, 0);
            barrier.RemoveParticipant(0);
            Assert.False(barrier.IsSatisfied);
            Assert.Equal(0, barrier.ParticipantCount);
        }

        [Fact]
        public void RemoveParticipant_Unknown_ReturnsFalse()
        {
            Assert.False(HostAndOneClient().RemoveParticipant(99));
        }

        [Fact]
        public void RemoveParticipant_DropsTheirReadiness()
        {
            var barrier = Barrier(3, 0, 1, 2);
            barrier.SetReady(1, 3);
            Assert.Equal(1, barrier.ReadyCount);
            barrier.RemoveParticipant(1);
            Assert.Equal(0, barrier.ReadyCount);
        }

        [Fact]
        public void AddParticipant_Twice_IsIdempotent()
        {
            var barrier = Barrier(3, 0);
            barrier.AddParticipant(0);
            Assert.Equal(1, barrier.ParticipantCount);
        }

        [Fact]
        public void AddParticipant_MidTurn_BlocksTheBarrierUntilTheyReady()
        {
            var barrier = Barrier(3, 0);
            barrier.SetReady(0, 3);
            Assert.True(barrier.IsSatisfied);
            barrier.AddParticipant(1);
            Assert.False(barrier.IsSatisfied);
        }

        [Fact]
        public void AdvanceTo_ClearsAllReadiness()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 3);
            barrier.SetReady(1, 3);
            barrier.AdvanceTo(4);
            Assert.Equal(4, barrier.Turn);
            Assert.Equal(0, barrier.ReadyCount);
            Assert.False(barrier.IsSatisfied);
        }

        [Fact]
        public void AdvanceTo_LetsPreviouslyStaleTurnBecomeCurrent()
        {
            var barrier = HostAndOneClient();
            barrier.AdvanceTo(4);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 4));
        }

        [Fact]
        public void ReadyCount_And_ParticipantCount_ReportProgress()
        {
            var barrier = Barrier(3, 0, 1, 2);
            barrier.SetReady(2, 3);
            Assert.Equal(1, barrier.ReadyCount);
            Assert.Equal(3, barrier.ParticipantCount);
        }

        [Fact]
        public void IsReady_ReportsPerParticipantState()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(1, 3);
            Assert.False(barrier.IsReady(0));
            Assert.True(barrier.IsReady(1));
            Assert.False(barrier.IsReady(99));
        }

        [Fact]
        public void Turn_StartsAtConstructedValue()
        {
            Assert.Equal(7, new TurnBarrier(7).Turn);
        }
    }
}
