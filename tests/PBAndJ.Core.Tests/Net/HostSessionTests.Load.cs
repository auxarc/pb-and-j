using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The synchronised load (M11d): everyone agreeing, then loading in unison, and every
    // way that can fail. One section of the original. Loading() is used only here.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- the synchronised load (M11d) ---

        /// <summary>A host and one peer, both agreed, so the load has just fired.</summary>
        private HostSession Loading(out IReadOnlyList<PbjEffect> fired)
        {
            var host = LobbyHost();
            host.Handle(Select().Event);
            host.Handle(new LocalLobbyReadyEvent());
            fired = host.HandleMessage(1, new LobbyReadyMessage(1));
            return host;
        }

        [Fact]
        public void Load_FiresWhenEveryoneHasAgreed()
        {
            var host = Loading(out var effects);

            Assert.True(host.LoadInFlight);
            var load = All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>().Single();
            Assert.Equal("pbj_campaign", load.SaveKey);
            Assert.Single(All<BeginLoadEffect>(effects));
        }

        [Fact]
        public void Load_AdvancesTheSelectionSoTheAgreementCannotBeSpentTwice()
        {
            // The heart of the design. IsSatisfied is a predicate nothing
            // consumes and the host stays in Lobby for the whole campaign, so a
            // level-triggered load would re-fire from every later barrier check
            // — including the disconnect path — and reload the original save on
            // every machine mid-play.
            var host = Loading(out _);

            Assert.Equal(2, host.Selection.Version);
            Assert.Equal(0, host.LobbyReadyCount);
            Assert.False(host.LobbyIsSatisfied);
        }

        [Fact]
        public void Load_BroadcastsTheNewLobbyStateBeforeTheLoadInstruction()
        {
            // Firing puts the host a version ahead of every client, and a client
            // validates LobbyLoad against the version it last heard. Reverse
            // these two and every client refuses while the host loads alone.
            var host = Loading(out var effects);

            var broadcasts = All<BroadcastEffect>(effects).Select(b => b.Message).ToList();
            var stateAt = broadcasts.FindIndex(m => m is LobbyStateMessage s && s.SelectionVersion == 2);
            var loadAt = broadcasts.FindIndex(m => m is LobbyLoadMessage);

            Assert.True(stateAt >= 0, "the advanced LobbyState must be broadcast");
            Assert.True(loadAt > stateAt, "LobbyLoad must follow the LobbyState carrying its version");
            Assert.Equal(2, Assert.IsType<LobbyLoadMessage>(broadcasts[loadAt]).SelectionVersion);
        }

        [Fact]
        public void Load_DoesNotFireASecondTimeWhileOneIsRunning()
        {
            var host = Loading(out _);
            // A peer leaving re-checks the barrier — the path that would have
            // been catastrophic.
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Empty(All<BroadcastEffect>(effects).Select(b => b.Message).OfType<LobbyLoadMessage>());
        }

        [Fact]
        public void Load_WithNoSaveChosen_DoesNotFire()
        {
            bridge.InCombat = false;
            var host = Host();
            host.Handle(new LocalLobbyReadyEvent());
            Assert.False(host.LoadInFlight);
        }

        [Fact]
        public void Load_CompletesWhenEveryoneHasReported()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            Assert.True(host.LoadInFlight);

            var effects = host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("2 of 2 machine(s) are in"));
        }

        [Fact]
        public void Load_CompletesEvenWhenAPeerFailed()
        {
            // The barrier waits for news, not for success.
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            var effects = host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Unavailable));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("1 of 2 machine(s) are in"));
        }

        [Fact]
        public void Load_ReportForAStaleVersion_IsIgnored()
        {
            var host = Loading(out _);
            var effects = host.HandleMessage(1, new LobbyLoadedMessage(1, LoadOutcome.Loaded));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("load OK"));
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void Load_HostReportForALoadThatIsNotRunning_IsIgnored()
        {
            // A callback outliving the load that asked for it. Acting on it would
            // complete a barrier nobody is waiting on.
            var host = LobbyHost();
            var effects = host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("load OK"));
        }

        [Fact]
        public void Load_HostFailure_AbandonsTheWholeLoad()
        {
            // The host is not a peer that can be carried on without: it is the
            // session. Dropping it would leave the others in a campaign it is
            // not in.
            var host = Loading(out _);
            var effects = host.Handle(new LoadFinishedEvent(2, LoadOutcome.Refused));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("abandoning"));
        }

        [Fact]
        public void Load_APeerLeavingMidLoad_StopsBeingWaitedFor()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));

            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("machine(s) are in"));
        }

        [Fact]
        public void Load_TimesOutAPeerThatNeverReports()
        {
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));

            // Seed-don't-judge: the first tick mints the deadline rather than
            // measuring against a clock that was never stamped.
            host.Handle(new TickEvent(1000.0));
            Assert.True(host.LoadInFlight);

            var effects = host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds + 1.0));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("no word from #1"));
        }

        [Fact]
        public void Load_DoesNotTimeOutBeforeTheDeadline()
        {
            var host = Loading(out _);
            host.Handle(new TickEvent(1000.0));
            host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds - 1.0));
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void Load_HostTimingOutAbandonsRatherThanDropping()
        {
            var host = Loading(out _);
            host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));
            host.Handle(new TickEvent(1000.0));

            var effects = host.Handle(new TickEvent(1000.0 + PbjProtocol.LoadTimeoutSeconds + 1.0));

            Assert.False(host.LoadInFlight);
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("abandoning"));
        }

        [Fact]
        public void Load_TicksWithNothingRunning_DoNothing()
        {
            var host = LobbyHost();
            host.Handle(new TickEvent(1000.0));
            Assert.False(host.LoadInFlight);
        }

        [Fact]
        public void Load_AfterCompleting_TheLobbyCanFillAgain()
        {
            // Deliberate: unanimous agreement a second time is a deliberate
            // reload. The alternative — a barrier that can never fire again —
            // is M11a's do-nothing barrier reintroduced.
            var host = Loading(out _);
            host.Handle(new LoadFinishedEvent(2, LoadOutcome.Loaded));
            host.HandleMessage(1, new LobbyLoadedMessage(2, LoadOutcome.Loaded));
            Assert.False(host.LoadInFlight);

            host.Handle(new LocalLobbyReadyEvent());
            var effects = host.HandleMessage(1, new LobbyReadyMessage(2));

            Assert.True(host.LoadInFlight);
            Assert.Single(All<BeginLoadEffect>(effects));
        }
    }
}
