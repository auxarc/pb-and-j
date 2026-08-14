using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class LobbyBarrierTests
    {
        private static LobbyBarrier Barrier(int selection = 0, params int[] participants)
        {
            var barrier = new LobbyBarrier(selection);
            foreach (var p in participants)
            {
                barrier.AddParticipant(p);
            }
            return barrier;
        }

        private static LobbyBarrier HostAndOneClient(int selection = 0) => Barrier(selection, 0, 1);

        [Fact]
        public void IsSatisfied_WithNoParticipants_ReturnsFalse()
        {
            // Nobody is in the lobby, so there is nothing to be ready for.
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
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(0, 0));
            Assert.False(barrier.IsSatisfied);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 0));
            Assert.True(barrier.IsSatisfied);
        }

        [Fact]
        public void SetReady_Twice_IsIdempotent()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(1, 0);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 0));
            Assert.Equal(1, barrier.ReadyCount);
        }

        [Fact]
        public void SetReady_ForStaleSelection_ReturnsStale()
        {
            // The host changed the save after this peer readied for the old one.
            // Accepting it would start a save the peer never agreed to.
            Assert.Equal(ReadyOutcome.Stale, HostAndOneClient(selection: 4).SetReady(1, 3));
        }

        [Fact]
        public void SetReady_ForStaleSelection_DoesNotMarkReady()
        {
            var barrier = HostAndOneClient(selection: 4);
            barrier.SetReady(1, 3);
            Assert.Equal(0, barrier.ReadyCount);
        }

        [Fact]
        public void SetReady_ForFutureSelection_ReturnsNeedsResync()
        {
            // Unlike TurnBarrier's, this arm has NO honest counterpart: only the
            // host mints a selection version, it never rewinds, and TCP ordering
            // means a client cannot have seen one the host has not reached. It is
            // reachable only from a forged or buggy LobbyReady — which is real
            // enough to keep, since messages do not validate. Do not read it as a
            // mirror of the turn barrier's force-execute resync.
            Assert.Equal(ReadyOutcome.NeedsResync, HostAndOneClient(selection: 3).SetReady(1, 4));
        }

        [Fact]
        public void SetReady_ForUnknownParticipant_ReturnsUnknownParticipant()
        {
            Assert.Equal(ReadyOutcome.UnknownParticipant, HostAndOneClient().SetReady(99, 0));
        }

        [Fact]
        public void Unready_ClearsReadiness()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 0);
            barrier.SetReady(1, 0);
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
            // The case the host session has to check for explicitly: a departing
            // peer can fill the barrier without anyone calling SetReady.
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 0);
            Assert.False(barrier.IsSatisfied);
            Assert.True(barrier.RemoveParticipant(1));
            Assert.True(barrier.IsSatisfied);
        }

        [Fact]
        public void RemoveParticipant_LastRemaining_LeavesBarrierUnsatisfied()
        {
            var barrier = Barrier(0, 0);
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
            var barrier = Barrier(0, 0, 1, 2);
            barrier.SetReady(1, 0);
            Assert.Equal(1, barrier.ReadyCount);
            barrier.RemoveParticipant(1);
            Assert.Equal(0, barrier.ReadyCount);
        }

        [Fact]
        public void AddParticipant_Twice_IsIdempotent()
        {
            var barrier = Barrier(0, 0);
            barrier.AddParticipant(0);
            Assert.Equal(1, barrier.ParticipantCount);
        }

        [Fact]
        public void AddParticipant_AfterOthersReadied_BlocksTheBarrier()
        {
            // Someone joining the lobby un-fills it, which is the point: they
            // have not agreed to the save yet.
            var barrier = Barrier(0, 0);
            barrier.SetReady(0, 0);
            Assert.True(barrier.IsSatisfied);
            barrier.AddParticipant(1);
            Assert.False(barrier.IsSatisfied);
        }

        [Fact]
        public void AdvanceTo_ClearsAllReadiness()
        {
            // Changing the selected save must un-ready everyone, or a peer that
            // agreed to save A is counted as agreeing to save B.
            var barrier = HostAndOneClient();
            barrier.SetReady(0, 0);
            barrier.SetReady(1, 0);
            barrier.AdvanceTo(1);
            Assert.Equal(1, barrier.Selection);
            Assert.Equal(0, barrier.ReadyCount);
            Assert.False(barrier.IsSatisfied);
        }

        [Fact]
        public void AdvanceTo_LetsPreviouslyFutureSelectionBecomeCurrent()
        {
            var barrier = HostAndOneClient();
            barrier.AdvanceTo(1);
            Assert.Equal(ReadyOutcome.Accepted, barrier.SetReady(1, 1));
        }

        [Fact]
        public void ReadyCount_And_ParticipantCount_ReportProgress()
        {
            var barrier = Barrier(0, 0, 1, 2);
            barrier.SetReady(2, 0);
            Assert.Equal(1, barrier.ReadyCount);
            Assert.Equal(3, barrier.ParticipantCount);
        }

        [Fact]
        public void IsReady_ReportsPerParticipantState()
        {
            var barrier = HostAndOneClient();
            barrier.SetReady(1, 0);
            Assert.False(barrier.IsReady(0));
            Assert.True(barrier.IsReady(1));
            Assert.False(barrier.IsReady(99));
        }

        [Fact]
        public void Selection_StartsAtConstructedValue()
        {
            Assert.Equal(7, new LobbyBarrier(7).Selection);
        }

        [Fact]
        public void Selection_DefaultsToTheSameZeroAsLobbySelectionNone()
        {
            // Pinned: a drift between these two would make the very first honest
            // ready arrive as Stale, which is invisible until someone wonders why
            // the lobby never fills.
            Assert.Equal(LobbySelection.None.Version, new LobbyBarrier(LobbySelection.None.Version).Selection);
            Assert.Equal(0, LobbySelection.None.Version);
        }

        // --- ReadyParticipants: the accessor TurnBarrier has no need for, and
        // --- the reason this is a separate class rather than a second instance.

        [Fact]
        public void ReadyParticipants_IsEmptyWhenNobodyIsReady()
        {
            Assert.Empty(HostAndOneClient().ReadyParticipants);
        }

        [Fact]
        public void ReadyParticipants_ListsOnlyTheReadyOnes()
        {
            var barrier = Barrier(0, 0, 1, 2);
            barrier.SetReady(2, 0);
            barrier.SetReady(0, 0);
            Assert.Equal(new[] { 0, 2 }, barrier.ReadyParticipants);
        }

        [Fact]
        public void ReadyParticipants_IsInRosterOrderNotReadyOrder()
        {
            // M11c renders this next to the roster, so it must not reshuffle as
            // people ready up.
            var barrier = Barrier(0, 5, 3, 9);
            barrier.SetReady(9, 0);
            barrier.SetReady(3, 0);
            barrier.SetReady(5, 0);
            Assert.Equal(new[] { 5, 3, 9 }, barrier.ReadyParticipants);
        }

        [Fact]
        public void ReadyParticipants_DropsAParticipantThatLeft()
        {
            var barrier = Barrier(0, 0, 1);
            barrier.SetReady(1, 0);
            barrier.RemoveParticipant(1);
            Assert.Empty(barrier.ReadyParticipants);
        }

        [Fact]
        public void Participants_ListsEveryoneInAdditionOrder()
        {
            Assert.Equal(new[] { 5, 3, 9 }, Barrier(0, 5, 3, 9).Participants);
        }
    }

    public class LobbySelectionTests
    {
        [Fact]
        public void None_ChoosesNothing()
        {
            Assert.Equal(0, LobbySelection.None.Version);
            Assert.Null(LobbySelection.None.SaveKey);
            Assert.Null(LobbySelection.None.SaveDigest);
            Assert.False(LobbySelection.None.HasSave);
        }

        [Fact]
        public void Constructor_KeepsWhatItWasGiven()
        {
            var selection = new LobbySelection(4, "pbj_campaign", "abc123");
            Assert.Equal(4, selection.Version);
            Assert.Equal("pbj_campaign", selection.SaveKey);
            Assert.Equal("abc123", selection.SaveDigest);
        }

        [Fact]
        public void HasSave_IsTrueOnlyForARealKey()
        {
            Assert.True(new LobbySelection(1, "pbj_campaign", null).HasSave);
            Assert.False(new LobbySelection(1, null, null).HasSave);
            // Empty is as good as absent — a peer cannot load "".
            Assert.False(new LobbySelection(1, string.Empty, null).HasSave);
        }

        [Fact]
        public void HasSave_DoesNotDependOnTheDigest()
        {
            // A save this machine has not hashed is still a save.
            Assert.True(new LobbySelection(1, "pbj_campaign", null).HasSave);
        }

        [Fact]
        public void Next_AdvancesTheVersionAndTakesTheNewSave()
        {
            var next = LobbySelection.None.Next("pbj_campaign", "abc123");
            Assert.Equal(1, next.Version);
            Assert.Equal("pbj_campaign", next.SaveKey);
            Assert.Equal("abc123", next.SaveDigest);
        }

        [Fact]
        public void Next_AdvancesEvenWhenNothingChanged()
        {
            // Deliberate: re-choosing the same save still clears every ready.
            // The cost is one re-click; the alternative is starting a save a peer
            // never confirmed.
            var first = LobbySelection.None.Next("pbj_campaign", "abc123");
            var again = first.Next("pbj_campaign", "abc123");
            Assert.Equal(2, again.Version);
            Assert.Equal(first.SaveKey, again.SaveKey);
        }

        [Fact]
        public void Next_CanClearTheSelection()
        {
            var chosen = LobbySelection.None.Next("pbj_campaign", "abc123");
            var cleared = chosen.Next(null, null);
            Assert.Equal(2, cleared.Version);
            Assert.False(cleared.HasSave);
        }
    }
}
