using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The combat lifecycle as the host drives it -- start, end, and what a client
    // does when it is told about a fight it is not in -- and the order results
    // that come back inside it.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- combat lifecycle from the host ---

        // --- M17 stage 2: which states own the combat outcome ---

        [Theory]
        [InlineData(ClientSessionState.Handshaking, true)]
        [InlineData(ClientSessionState.Lobby, true)]
        [InlineData(ClientSessionState.Planning, true)]
        [InlineData(ClientSessionState.Watching, true)]
        [InlineData(ClientSessionState.Closed, false)]
        [InlineData(ClientSessionState.Faulted, false)]
        public void ClientOwnsCombatOutcome_IsFalseOnceTheSessionIsOver(
            ClientSessionState state, bool owns)
        {
            // 🔴 The predicate M17 stage 2's EndCombatWithOutcome prefix is armed
            // by, and the two false rows are the correctness requirement rather
            // than tidiness. Fault's own comment says a lost host must never
            // leave the local execute button disabled -- the player continues
            // single-player from there, simulates locally, and reaches
            // CombatExecutionEndSystem normally. A prefix still armed in Closed
            // or Faulted would eat that outcome and make the fight unwinnable
            // AND unlosable for ever. Adding Faulted to the owning set is the
            // mutation this pins.
            Assert.Equal(owns, ClientSession.ClientOwnsCombatOutcome(state));
        }

        [Fact]
        public void ClientOwnsCombatOutcome_CoversEveryDeclaredState()
        {
            // The theory above is a hand-maintained list, which is a claim about
            // the world that nothing checks. This is what checks it: a seventh
            // state added to the enum fails here rather than silently arriving
            // untested.
            Assert.Equal(6, Enum.GetValues(typeof(ClientSessionState)).Length);
        }

        [Fact]
        public void CombatStart_MovesToPlanningAtTheHostsTurnAndUnlocks()
        {
            bridge.InCombat = false;
            var client = Welcomed(-1);
            Assert.Equal(ClientSessionState.Lobby, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));
            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(0, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatStart_ClearsAStaleSubmission()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new CombatStartMessage(0));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        // The combat-retry interregnum. A host retrying a fight leaves combat
        // first, so a client gets CombatEnd while still standing in the loaded
        // battle, and the host comes back moments later with a fresh CombatStart.
        //
        // Unlocking here was the defect: State goes to Lobby, HandleLocalReady
        // opens with `if (State != Planning) return;`, so the button the client
        // was just handed back does nothing at all and says nothing about why.
        // Holding it locked makes the refusal visible before the fact, which is
        // the standing rule — and CombatStart unlocks again on the host's return.

        [Fact]
        public void CombatEnd_ReturnsToLobbyDropsOwnedUnitsAndHoldsExecute()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new AssignmentsMessage(new[]
            {
                new PeerAssignment(1, new[] { "unit_b" }),
            }));
            Assert.NotEmpty(client.OwnedUnits);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.Equal(ClientSessionState.Lobby, client.State);
            Assert.Empty(client.OwnedUnits);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatEnd_WhileWatchingAlsoHoldsExecute()
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);

            var effects = client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void CombatEnd_ThenCombatStart_HandsExecuteBack()
        {
            // The interregnum has to actually end. This is the whole reason the
            // lock is safe: the same session that holds the button also releases
            // it, without the player doing anything.
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId, new CombatEndMessage());

            var effects = client.HandleMessage(
                ClientSession.HostConnectionId, new CombatStartMessage(0));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void OwnCombatEdges_AreLoggedButChangeNothing()
        {
            // A client's local InCombat is not authoritative — it learns combat
            // state from the host. These arms exist so the event does not throw.
            var client = Welcomed();

            foreach (var evt in new PbjInboundEvent[] { new CombatEnteredEvent(), new CombatExitedEvent() })
            {
                var effects = client.Handle(evt);
                Assert.Single(All<LogEffect>(effects));
                Assert.DoesNotContain(effects, e => !(e is LogEffect));
                Assert.Equal(ClientSessionState.Planning, client.State);
            }
        }

        [Fact]
        public void LocalCombatReady_IsLoggedRatherThanThrownOn()
        {
            // Only a host ships a fight, but the glue that writes one is armed by
            // an effect and answers frames later — long enough for the player to
            // have stopped hosting and joined someone else. Without this arm the
            // default case throws, and NetGlue.Pump turns a throw into
            // "networking stopped" for the rest of the process.
            var client = Welcomed();

            var effects = client.Handle(new LocalCombatReadyEvent("pbj_combat_test", "d1"));

            Assert.Single(All<LogEffect>(effects));
            Assert.DoesNotContain(effects, e => !(e is LogEffect));
        }

        [Fact]
        public void OrderApplied_IsIgnored()
        {
            // Clients never apply remote orders; the arm exists so the event,
            // which the shared runtime can produce, does not throw.
            Assert.Empty(Welcomed().Handle(new OrderAppliedEvent(1, 0, OrderApplyResult.Applied)));
        }

        // --- order results ---

        [Fact]
        public void OrderResult_IsReportedAndChangesNoState()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(ClientSession.HostConnectionId, new OrderResultMessage(3, 2, new[]
            {
                new RejectedOrder(1, OrderApplyResult.NotOwned),
            }));

            Assert.Single(All<LogEffect>(effects));
            Assert.DoesNotContain(effects, e => !(e is LogEffect));
            Assert.Equal(ClientSessionState.Planning, client.State);
        }
    }
}
