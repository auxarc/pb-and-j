using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The turn cycle: readying, the filter that cuts the Ready batch down to the
    // units this client owns, and un-readying.
    // The filter has its own banner because it was found in the stage 2 run
    // rather than designed in. Assigned is here because both of the parts that
    // would want it are this one.
    //
    // One part of ClientSessionTests, a single class split across 12 files.
    // Helpers used by more than one part live in ClientSessionTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class ClientSessionTests
    {
        // --- the turn cycle ---

        [Fact]
        public void Handle_LocalReady_SendsReadyWithCapturedOrders()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_c", 0f, 2f));
            var client = Welcomed();
            var effects = client.Handle(new LocalReadyEvent());

            var ready = Assert.IsType<ReadyMessage>(Single<SendEffect>(effects).Message);
            Assert.Equal(3, ready.Turn);
            Assert.Single(ready.Orders);
            Assert.Equal("unit_c", ready.Orders[0].OwnerName);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        // --- the Ready batch is filtered to what we own (found in the stage 2 run) ---
        //
        // A client's local ECS holds the enemy AI's planned actions too, and they
        // do not carry the AIAction tag there — the first two-game turn submitted
        // 13 enemy orders alongside 3 of the host's, all rejected. Harmless, but it
        // wastes the wire, eats the 256-order cap and buries genuine rejections.

        /// <summary>A welcomed client that has been dealt <paramref name="units"/>.</summary>
        private ClientSession Assigned(params string[] units)
        {
            var client = Welcomed();
            client.HandleMessage(ClientSession.HostConnectionId,
                new AssignmentsMessage(new[] { new PeerAssignment(1, units) }));
            return client;
        }

        [Fact]
        public void Handle_LocalReady_SendsOnlyOrdersForUnitsWeOwn()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_b", 0f, 2f));

            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Assigned("unit_a", "unit_b").Handle(new LocalReadyEvent())).Message);

            Assert.Equal(2, ready.Orders.Count);
            Assert.DoesNotContain(ready.Orders, o => o.OwnerName == "enemy_01");
        }

        [Fact]
        public void Handle_LocalReady_SaysWhatItDropped()
        {
            // No silent filtering: if a genuine order ever goes missing here, the
            // count is the only thing that would show it.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));

            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("1 order") && l.Line.Contains("not ours"));
        }

        [Fact]
        public void Handle_LocalReady_WithNothingToDrop_SaysNothingAboutIt()
        {
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_a", 0f, 2f));
            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            Assert.DoesNotContain(All<LogEffect>(effects), l => l.Line.Contains("not ours"));
        }

        [Fact]
        public void Handle_LocalReady_BeforeAnyAssignment_SendsEverythingAndLetsTheHostDecide()
        {
            // The filter is a courtesy over a host-authoritative check, not a
            // second enforcement point. A client that has not been told what it
            // owns must defer rather than silently withhold a real order.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "unit_c", 0f, 2f));
            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Welcomed().Handle(new LocalReadyEvent())).Message);
            Assert.Single(ready.Orders);
        }

        [Fact]
        public void Handle_LocalReady_WhenEveryOrderIsDropped_StillReadies()
        {
            // The barrier waits on every participant. Filtering everything away
            // must submit an empty batch, never skip the Ready — that would
            // deadlock the turn for both players.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "enemy_01", 0f, 2f));

            var effects = Assigned("unit_a").Handle(new LocalReadyEvent());
            var ready = Assert.IsType<ReadyMessage>(Single<SendEffect>(effects).Message);

            Assert.Empty(ready.Orders);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        // An order with no owner needs no arm here: OrderPayload's constructor
        // already refuses a null or empty ownerName, which is a stronger place
        // for that guarantee to live than this filter.

        [Fact]
        public void Handle_LocalReady_MatchesOwnerNamesExactly()
        {
            // nameInternal is the join key everything else is addressed by; a
            // case-insensitive match here would let a near-miss through and turn
            // a clean rejection into a confusing one.
            bridge.LocalOrders.Add(new OrderPayload("move_run", "Unit_A", 0f, 2f));
            var ready = Assert.IsType<ReadyMessage>(
                Single<SendEffect>(Assigned("unit_a").Handle(new LocalReadyEvent())).Message);
            Assert.Empty(ready.Orders);
        }

        [Fact]
        public void Handle_LocalReady_WhenNotPlanning_ProducesNoEffects()
        {
            var client = Client();
            Assert.Empty(client.Handle(new LocalReadyEvent()));
        }

        [Fact]
        public void Handle_LocalReady_Twice_SendsReadyAgain()
        {
            // Ready is idempotent by design; the host replaces the batch.
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));
            Assert.Single(All<SendEffect>(client.Handle(new LocalReadyEvent())));
        }

        [Fact]
        public void HandleMessage_TurnCommit_LocksExecutionAndWatches()
        {
            var client = Welcomed();
            var effects = client.HandleMessage(0, new TurnCommitMessage(3));
            Assert.Equal(ClientSessionState.Watching, client.State);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnCommit_ForUnexpectedTurn_ResyncsInsteadOfFaulting()
        {
            // The host can force-execute past us via scenario content.
            var client = Welcomed(turn: 3);
            client.HandleMessage(0, new TurnCommitMessage(7));
            Assert.Equal(7, client.Turn);
            Assert.Equal(ClientSessionState.Watching, client.State);
        }

        [Fact]
        public void HandleMessage_TurnComplete_UnlocksAndAdvancesToNextTurn()
        {
            var client = Welcomed();
            client.HandleMessage(0, new TurnCommitMessage(3));
            var effects = client.HandleMessage(0, new TurnCompleteMessage(3, bridge.Digest));

            Assert.Equal(ClientSessionState.Planning, client.State);
            Assert.Equal(4, client.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithMatchingDigest_LogsOk()
        {
            bridge.Digest = "3f9c1a04";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "3f9c1a04"));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("digest 3f9c1a04 OK"));
        }

        [Fact]
        public void HandleMessage_TurnComplete_WithDifferentDigest_LogsDiverged()
        {
            bridge.Digest = "bbbb2222";
            var effects = Welcomed().HandleMessage(0, new TurnCompleteMessage(3, "aaaa1111"));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("DIVERGED | host aaaa1111 | local bbbb2222"));
        }

        // --- un-ready ---

        [Fact]
        public void LocalUnready_AfterSubmitting_SendsUnreadyAndUnlocksExecution()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());

            var effects = client.Handle(new LocalUnreadyEvent());
            var unready = (UnreadyMessage)Single<SendEffect>(effects).Message;
            Assert.Equal(3, unready.Turn);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void LocalUnready_WithNothingSubmitted_SendsNothing()
        {
            var effects = Welcomed().Handle(new LocalUnreadyEvent());
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void LocalUnready_IsRefusedOnceTheHostHasCommitted()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.HandleMessage(ClientSession.HostConnectionId, new TurnCommitMessage(3));

            Assert.Empty(All<SendEffect>(client.Handle(new LocalUnreadyEvent())));
        }

        [Fact]
        public void LocalUnready_ThenReady_SubmitsAgain()
        {
            var client = Welcomed();
            client.Handle(new LocalReadyEvent());
            client.Handle(new LocalUnreadyEvent());

            Assert.IsType<ReadyMessage>(Single<SendEffect>(client.Handle(new LocalReadyEvent())).Message);
        }
    }
}
