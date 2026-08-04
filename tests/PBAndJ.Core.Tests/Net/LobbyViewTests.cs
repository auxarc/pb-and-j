using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class LobbyViewTests
    {
        private static LobbyPeerState Peer(int id, string? name, bool ready) =>
            new LobbyPeerState(id, name, ready);

        private static LobbyView Host(params LobbyPeerState[] roster) =>
            new LobbyView(true, 0, "pbj_firstrun", roster, false);

        private static LobbyView Client(params LobbyPeerState[] roster) =>
            new LobbyView(false, 1, "pbj_firstrun", roster, false);

        // --- the save line ---

        [Fact]
        public void SaveLine_ShowsTheDisplayNameNotTheKey()
        {
            // The pbj_ prefix is storage, not vocabulary. A player never typed it
            // and should never have to read it.
            Assert.Equal("Save · firstrun", Host().SaveLine);
        }

        [Fact]
        public void SaveLine_WithNoSaveChosen_SaysSoRatherThanBeingBlank()
        {
            var view = new LobbyView(true, 0, null, new LobbyPeerState[0], false);
            Assert.Equal("Save · none chosen yet", view.SaveLine);
        }

        // --- who may do what ---

        [Fact]
        public void CanChooseSave_ForTheHost_IsTrue()
        {
            Assert.True(Host().CanChooseSave);
        }

        [Fact]
        public void CanChooseSave_ForAClient_IsFalse()
        {
            // The host drives the overworld and therefore owns the save choice.
            // A client sees the selection read-only.
            Assert.False(Client().CanChooseSave);
        }

        [Fact]
        public void CanStart_ForTheHostWithEveryoneReady_IsTrue()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", true));
            Assert.True(view.CanStart);
        }

        [Fact]
        public void CanStart_WithSomeoneNotReady_IsFalse()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", false));
            Assert.False(view.CanStart);
        }

        [Fact]
        public void CanStart_ForAClientWithEveryoneReady_IsFalse()
        {
            // Only the host starts. A client with a live Start button would be a
            // button that does nothing, which is the M6 failure in miniature.
            var view = Client(Peer(0, "host", true), Peer(1, "ally", true));
            Assert.False(view.CanStart);
        }

        [Fact]
        public void CanStart_WithNoSaveChosen_IsFalse()
        {
            // Everyone can be ready for nothing at all — LobbyBarrier counts
            // agreement, not agreement about something.
            var view = new LobbyView(true, 0, null, new[] { Peer(0, "host", true) }, false);
            Assert.False(view.CanStart);
        }

        [Fact]
        public void CanStart_WithAnEmptyRoster_IsFalse()
        {
            Assert.False(Host().CanStart);
        }

        // --- the roster ---

        [Fact]
        public void RosterLines_MarkReadyAndWaiting()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", false));
            Assert.Equal(
                new[] { "host (you) — ready", "ally — not ready" },
                view.RosterLines);
        }

        [Fact]
        public void RosterLines_MarkWhichOneIsYou()
        {
            // Two players called "ally" is not a case the protocol prevents, and
            // a roster you cannot find yourself in is a roster you cannot act on.
            var view = Client(Peer(0, "ally", true), Peer(1, "ally", false));
            Assert.Equal(
                new[] { "ally — ready", "ally (you) — not ready" },
                view.RosterLines);
        }

        [Fact]
        public void RosterLines_ForAPeerWithNoName_SaysSomething()
        {
            // Name is nullable on the wire. A blank line reads as a rendering bug.
            var view = Host(Peer(7, null, false));
            Assert.Equal(new[] { "peer 7 — not ready" }, view.RosterLines);
        }

        [Fact]
        public void RosterLines_ForAnEmptyRoster_IsEmpty()
        {
            Assert.Empty(Host().RosterLines);
        }

        [Fact]
        public void RosterLines_AreCappedWithAnOverflowLine()
        {
            // The screen has a fixed stack of labels — there is no scroll view in
            // this UI and none is known to exist. The wire roster caps at 16, so
            // the overflow line is reachable rather than theoretical.
            var count = LobbyView.MaxRosterLines + 5;
            var many = Enumerable.Range(0, count).Select(i => Peer(i, "p" + i, false)).ToArray();
            var lines = new LobbyView(true, 0, "pbj_firstrun", many, false).RosterLines;

            // One slot of the cap goes to the overflow line itself, so the hidden
            // count is 5 + 1, not 5.
            Assert.Equal(LobbyView.MaxRosterLines, lines.Count);
            Assert.Equal("…and 6 more", lines[lines.Count - 1]);
        }

        [Fact]
        public void RosterLines_AtExactlyTheCap_AreNotTruncated()
        {
            var exact = Enumerable.Range(0, LobbyView.MaxRosterLines)
                .Select(i => Peer(i, "p" + i, false)).ToArray();
            var lines = new LobbyView(true, 0, "pbj_firstrun", exact, false).RosterLines;

            Assert.Equal(LobbyView.MaxRosterLines, lines.Count);
            Assert.Equal("p" + (LobbyView.MaxRosterLines - 1) + " — not ready", lines[lines.Count - 1]);
        }

        // --- the status line ---

        [Fact]
        public void StatusLine_CountsWhoIsStillMissing()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", false), Peer(2, "third", false));
            Assert.Equal("1 of 3 ready · waiting for 2", view.StatusLine);
        }

        [Fact]
        public void StatusLine_ForOneMissing_CountsThatOne()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", false));
            Assert.Equal("1 of 2 ready · waiting for 1", view.StatusLine);
        }

        [Fact]
        public void StatusLine_WhenEveryoneIsReady_SaysSo()
        {
            var view = Host(Peer(0, "host", true), Peer(1, "ally", true));
            Assert.Equal("Everyone is ready.", view.StatusLine);
        }

        [Fact]
        public void StatusLine_WithNoSaveChosen_AsksTheHostToChoose()
        {
            // Most fundamental first: readiness is meaningless before there is
            // something to be ready for, so that is what the line reports.
            var view = new LobbyView(true, 0, null, new[] { Peer(0, "host", false) }, false);
            Assert.Equal("Choose a save to begin.", view.StatusLine);
        }

        [Fact]
        public void StatusLine_ForAClientWithNoSaveChosen_WaitsOnTheHost()
        {
            // A client cannot act on "choose a save" — telling it to would be an
            // instruction it has no button for.
            var view = new LobbyView(false, 1, null, new[] { Peer(0, "host", false) }, false);
            Assert.Equal("Waiting for the host to choose a save.", view.StatusLine);
        }

        [Fact]
        public void StatusLine_WithAnEmptyRoster_ReportsNoSession()
        {
            // The screen is open before anyone connects. It must say something.
            Assert.Equal("Not connected.", Host().StatusLine);
        }

        // --- the ready button ---

        [Fact]
        public void ReadyLabel_WhenNotReady_OffersToReady()
        {
            Assert.Equal("Ready", Host(Peer(0, "host", false)).ReadyLabel);
        }

        [Fact]
        public void ReadyLabel_WhenReady_OffersToWithdraw()
        {
            // The action's name stays the same from button to status line, and a
            // toggle that reads "Ready" while you are ready is ambiguous about
            // which way it goes.
            Assert.Equal("Not ready", Host(Peer(0, "host", true)).ReadyLabel);
        }

        [Fact]
        public void CanReady_WithNoSaveChosen_IsFalse()
        {
            // Nothing to agree to. The host's own barrier refuses this too
            // (HandleLocalLobbyReady guards on selection.HasSave), so a live
            // button here would be one whose click is silently swallowed — the
            // exact class of bug the connect screen already paid for.
            var view = new LobbyView(true, 0, null, new[] { Peer(0, "host", false) }, false);
            Assert.False(view.CanReady);
        }

        [Fact]
        public void CanReady_WithASaveChosen_IsTrue()
        {
            Assert.True(Host(Peer(0, "host", false)).CanReady);
        }

        [Fact]
        public void CanReady_WithAnEmptyRoster_IsFalse()
        {
            Assert.False(Host().CanReady);
        }

        // --- the local member ---

        [Fact]
        public void IsLocallyReady_ReadsTheRosterNotTheSentFlag()
        {
            // The roster is the host's answer and the sent flag is only what we
            // asked for. Rendering the request rather than the answer is how a
            // screen ends up showing success that never happened — the silent
            // success bug M10c was rebuilt around.
            var view = new LobbyView(false, 1, "pbj_firstrun",
                new[] { Peer(0, "host", true), Peer(1, "ally", false) }, true);
            Assert.False(view.IsLocallyReady);
        }

        [Fact]
        public void IsLocallyReady_WhenTheHostAgrees_IsTrue()
        {
            var view = new LobbyView(false, 1, "pbj_firstrun",
                new[] { Peer(0, "host", true), Peer(1, "ally", true) }, false);
            Assert.True(view.IsLocallyReady);
        }

        [Fact]
        public void IsLocallyReady_WhenWeAreNotInTheRoster_IsFalse()
        {
            var view = new LobbyView(false, 9, "pbj_firstrun", new[] { Peer(0, "host", true) }, false);
            Assert.False(view.IsLocallyReady);
        }

        [Fact]
        public void ReadyPending_WhenAskedButNotYetConfirmed_IsTrue()
        {
            // A click that never reached the host must not look like one that
            // did. This is the only thing that tells those two apart.
            var view = new LobbyView(false, 1, "pbj_firstrun",
                new[] { Peer(0, "host", true), Peer(1, "ally", false) }, true);
            Assert.True(view.ReadyPending);
            Assert.True(view.ReadySent);
        }

        [Fact]
        public void ReadyPending_OnceTheHostConfirms_IsFalse()
        {
            var view = new LobbyView(false, 1, "pbj_firstrun",
                new[] { Peer(0, "host", true), Peer(1, "ally", true) }, true);
            Assert.False(view.ReadyPending);
        }

        [Fact]
        public void ReadyPending_WhenWeNeverAsked_IsFalse()
        {
            var view = new LobbyView(false, 1, "pbj_firstrun",
                new[] { Peer(0, "host", true), Peer(1, "ally", false) }, false);
            Assert.False(view.ReadyPending);
            Assert.False(view.ReadySent);
        }

        [Fact]
        public void Members_CountsTheRoster()
        {
            // The glue sizes its label stack from this.
            Assert.Equal(2, Host(Peer(0, "host", true), Peer(1, "ally", false)).Members);
        }

        [Fact]
        public void Roster_Null_IsTreatedAsEmpty()
        {
            var view = new LobbyView(true, 0, "pbj_firstrun", null, false);
            Assert.Empty(view.RosterLines);
            Assert.Equal("Not connected.", view.StatusLine);
        }
    }

    public class LobbyTextTests
    {
        [Fact]
        public void Title_NamesTheRoomNotTheDoor()
        {
            // The connect screen is "Multiplayer" — the door. This is the room
            // behind it, and calling both the same thing would make Back
            // ambiguous.
            Assert.Equal("Lobby", LobbyText.Title());
        }

        [Fact]
        public void ChooseSaveLabel_IsTheSameWordTheStatusLineUses()
        {
            Assert.Equal("Choose save", LobbyText.ChooseSaveLabel());
        }

        [Fact]
        public void StartLabel_IsPlain()
        {
            Assert.Equal("Start", LobbyText.StartLabel());
        }

        [Fact]
        public void ReadyLabel_IsTheRestingWording()
        {
            // What the button is built with. Which of Ready / Not ready it shows
            // at any moment is LobbyView.ReadyLabel's answer.
            Assert.Equal("Ready", LobbyText.ReadyLabel());
        }

        [Fact]
        public void NoSession_SaysWhatToDoFirst()
        {
            Assert.Equal(
                "Host or join a session first — the lobby is what you do once you are connected.",
                LobbyText.NoSession());
        }

        [Fact]
        public void CouldNotBuild_NamesTheWayThrough()
        {
            // Direction rather than an apology: the console can do everything the
            // screen can, so a failed screen is an inconvenience and not a wall.
            Assert.Equal(
                "The lobby screen could not be built. Use pbj.saves and pbj.lobby-select from the console.",
                LobbyText.CouldNotBuild());
        }

        [Fact]
        public void SaveNotUsable_RefusesOutLoud()
        {
            // The picker should never offer one, but appearing to accept a save
            // the lobby then never uses is worse than saying no.
            Assert.Equal("That is not a multiplayer save.", LobbyText.SaveNotUsable());
        }

        [Fact]
        public void NothingToAgreeTo_ExplainsTheDeadButton()
        {
            Assert.Equal("There is no save chosen yet to agree to.", LobbyText.NothingToAgreeTo());
        }

        [Fact]
        public void ReadyPendingSuffix_DistinguishesAskedFromConfirmed()
        {
            Assert.Equal(" · waiting for the host to confirm you", LobbyText.ReadyPendingSuffix());
        }

        [Fact]
        public void Opened_AndNotOpened_AreDistinct()
        {
            Assert.Equal("Lobby open.", LobbyText.Opened());
            Assert.Equal("The lobby did not open — see the log.", LobbyText.NotOpened());
        }

        [Fact]
        public void StartUnavailable_SaysWhatIsMissingRatherThanApologising()
        {
            Assert.Equal(
                "The synchronised load arrives in the next update — everyone is ready, but nothing loads yet.",
                LobbyText.StartNotImplemented());
        }

        [Theory]
        [InlineData(LobbySaveProblem.Empty, "Type a name for the new save.")]
        [InlineData(LobbySaveProblem.TooLong, "That name is too long.")]
        [InlineData(LobbySaveProblem.BadCharacter, "Use only letters, numbers, dots, dashes and underscores.")]
        [InlineData(LobbySaveProblem.AlreadyPrefixed, "Leave off the pbj_ prefix — it is added for you.")]
        [InlineData(LobbySaveProblem.Reserved, "That name is reserved by the game. Pick another.")]
        [InlineData(LobbySaveProblem.Duplicate, "A multiplayer save already has that name.")]
        public void DescribeProblem_ExplainsEveryProblem(LobbySaveProblem problem, string expected)
        {
            Assert.Equal(expected, LobbyText.DescribeProblem(problem));
        }

        [Fact]
        public void DescribeProblem_ForNoProblem_IsEmpty()
        {
            // One element, one job: the warning line is blank when there is
            // nothing to warn about rather than saying "OK".
            Assert.Equal(string.Empty, LobbyText.DescribeProblem(LobbySaveProblem.None));
        }

        [Fact]
        public void DescribeProblem_ForAValueItHasNeverHeardOf_StillSaysSomething()
        {
            // Reached from a cast, and a screen that renders nothing for an
            // unknown problem is a screen that silently accepts a refused name.
            Assert.Equal("That name cannot be used.", LobbyText.DescribeProblem((LobbySaveProblem)200));
        }
    }
}
