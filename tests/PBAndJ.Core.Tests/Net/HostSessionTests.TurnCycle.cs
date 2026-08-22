using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Four sections of the original: the turn barrier, ownership enforcement, turn
    // completion, and un-ready. The name is TurnCycle rather than Barrier because
    // 'barrier' covered only the first of the four.
    //
    // Order() is called 13 times here and 10 times in .Orders.cs. Neither part is its
    // sole user, so it stays shared fixture in the primary rather than moving to
    // whichever side happens to call it more.
    //
    // One part of HostSessionTests, a single class split across nineteen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- the barrier ---

        [Fact]
        public void HandleMessage_Ready_FromOnlyClient_WaitsForHost()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            Assert.Empty(All<CommitTurnEffect>(effects));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("barrier 1/2"));
        }

        [Fact]
        public void Handle_LocalReady_WhenNoClientsReady_DoesNotCommit()
        {
            var host = WithPeer();
            Assert.Empty(All<CommitTurnEffect>(host.Handle(new LocalReadyEvent())));
        }

        [Fact]
        public void Handle_LocalReady_WhenAllReady_AppliesOrdersThenCommits()
        {
            // The money test: apply -> commit. Nothing is broadcast until the
            // commit is confirmed by a CommitOutcomeEvent.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());

            Assert.Collection(effects,
                e => Assert.Contains("committing turn 3", Assert.IsType<LogEffect>(e).Line),
                e => Assert.Equal("unit_b", Assert.IsType<ApplyOrderEffect>(e).Order.OwnerName),
                e => Assert.Contains("applied 1 remote order", Assert.IsType<LogEffect>(e).Line),
                e => Assert.Equal(3, Assert.IsType<WriteCheckpointEffect>(e).Turn),
                e => Assert.Equal(3, Assert.IsType<CommitTurnEffect>(e).Turn));
            Assert.Empty(All<BroadcastEffect>(effects));
        }

        // --- the combat checkpoint (M12c) ---

        [Fact]
        public void TryCommit_PutsTheCheckpointAfterEveryApplyAndBeforeTheCommit()
        {
            // THE correctness property of M12c, and the reason this is an ORDERING
            // assertion rather than an existence one. A checkpoint written before
            // the last apply holds a half-planned turn; one written after the
            // commit is stamped with the NEXT turn, because ConfirmExecution has
            // already run ReplaceCurrentTurn. Both would pass "the effect is
            // there".
            var host = WithPeer(maxPeers: 3);
            host.Handle(new PeerConnectedEvent(2, "127.0.0.1:2"));
            host.HandleMessage(2, GoodHello("second"));
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.HandleMessage(2, new ReadyMessage(3, new[] { Order("unit_c") }));

            var effects = host.Handle(new LocalReadyEvent());

            var lastApply = effects.ToList().FindLastIndex(e => e is ApplyOrderEffect);
            var checkpoint = effects.ToList().FindIndex(e => e is WriteCheckpointEffect);
            var commit = effects.ToList().FindIndex(e => e is CommitTurnEffect);

            Assert.Equal(2, All<ApplyOrderEffect>(effects).Count());
            Assert.True(lastApply < checkpoint, "the checkpoint must follow every apply");
            Assert.True(checkpoint < commit, "the checkpoint must precede the commit");
            Assert.Equal(3, Single<WriteCheckpointEffect>(effects).Turn);
        }

        [Fact]
        public void TryCommit_WithTheBarrierUnfilled_WritesNoCheckpoint()
        {
            // Nothing to checkpoint: the turn is still being planned, and a save
            // taken here would reload into a plan the other peers never sent.
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            Assert.Empty(All<WriteCheckpointEffect>(effects));
        }

        [Fact]
        public void Handle_CommitOutcome_WhenRefused_WritesNoCheckpoint()
        {
            // Planning re-opens, and the checkpoint for this turn was already
            // enqueued before the commit was tried. Emitting a second one on the
            // way back out would write the same turn twice per refusal.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, committed: false));
            Assert.Empty(All<WriteCheckpointEffect>(effects));
        }

        [Fact]
        public void TryCommit_WithACadenceOfTwo_SkipsTheOddTurnAndKeepsCommitting()
        {
            // The skip must not disturb anything else: the turn still commits, and
            // the ONLY difference is the missing checkpoint.
            bridge.CurrentTurn = 3;
            var session = new HostSession("host", "7f3a91", 3, bridge, "secret",
                SessionRequirements.None, checkpointEveryNTurns: 2);
            session.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            session.HandleMessage(1, GoodHello());
            session.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));

            var effects = session.Handle(new LocalReadyEvent());

            Assert.Empty(All<WriteCheckpointEffect>(effects));
            Assert.Equal(3, Single<CommitTurnEffect>(effects).Turn);
        }

        [Fact]
        public void TryCommit_WithACadenceOfTwo_WritesOnTheEvenTurn()
        {
            // The other half of the arithmetic, and it has to be a separate turn
            // rather than the same one twice: a test that only ever saw the skip
            // would pass against a cadence that never writes at all.
            bridge.CurrentTurn = 4;
            var session = new HostSession("host", "7f3a91", 3, bridge, "secret",
                SessionRequirements.None, checkpointEveryNTurns: 2);
            session.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));
            session.HandleMessage(1, GoodHello());
            session.HandleMessage(1, new ReadyMessage(4, new[] { Order("unit_b") }));

            var effects = session.Handle(new LocalReadyEvent());

            Assert.Equal(4, Single<WriteCheckpointEffect>(effects).Turn);
        }

        [Fact]
        public void Handle_CommitOutcome_WhenCommitted_BroadcastsTurnCommitAndLocks()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, committed: true));

            Assert.Equal(3, Assert.IsType<TurnCommitMessage>(Single<BroadcastEffect>(effects).Message).Turn);
            Assert.True(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void Handle_CommitOutcome_WhenRefused_UnlocksPeersAndStaysInPlanning()
        {
            // ConfirmExecution refuses silently in four normal situations; if we
            // had already broadcast TurnCommit, every peer would wait forever.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, committed: false));

            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("commit REFUSED for turn 3"));
            Assert.Empty(All<BroadcastEffect>(effects));
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void HandleMessage_Ready_Twice_ReplacesPreviousOrderSet()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_b") }));
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Single(All<ApplyOrderEffect>(effects));
        }

        [Fact]
        public void HandleMessage_Ready_ForStaleTurn_IsIgnored()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(2, null));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("stale ready"));
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void HandleMessage_Ready_ForFutureTurn_ResyncsInsteadOfDisconnecting()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new ReadyMessage(4, null));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(3, Messages<TurnCommitMessage>(effects).Single().Turn);
        }

        [Fact]
        public void HandleMessage_Ready_DuringExecution_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Contains(All<LogEffect>(effects), l => l.Line.Contains("stale ready"));
        }

        [Fact]
        public void Handle_LocalReady_WhenNotPlanning_ProducesNoEffects()
        {
            bridge.InCombat = false;
            Assert.Empty(Host().Handle(new LocalReadyEvent()));
        }

        [Fact]
        public void Handle_PeerDisconnected_WhileHostReady_CommitsImmediately()
        {
            // A dead peer must never wedge the session.
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new PeerDisconnectedEvent(1, "closed"));
            Assert.Equal(3, Single<CommitTurnEffect>(effects).Turn);
        }

        // --- ownership enforcement ---

        [Fact]
        public void HandleMessage_Ready_WithOrderForUnownedUnit_DropsThatOrder()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            var effects = host.Handle(new LocalReadyEvent());

            Assert.Empty(All<ApplyOrderEffect>(effects));
            Assert.Contains(All<LogEffect>(effects),
                l => l.Line.Contains("order REJECTED from #1: unit_a is not assigned"));
        }

        [Fact]
        public void HandleMessage_Ready_WithMixedOwnership_AppliesOnlyOwnedOrders()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a"), Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Equal("unit_b", Single<ApplyOrderEffect>(effects).Order.OwnerName);
        }

        [Fact]
        public void HandleMessage_Ready_WithAllOrdersUnowned_StillMarksPeerReady()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            Assert.Equal(1, host.ReadyCount);
        }

        // --- turn completion ---

        [Fact]
        public void Handle_LocalTurnComplete_BroadcastsTheCommittedTurnNotTheAdvancedOne()
        {
            // The ECS advances currentTurn before the sim runs, so reading it
            // back here would report turn+1.
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.CurrentTurn = 4;

            var effects = host.Handle(new LocalTurnCompleteEvent("3f9c1a04", null, null));
            var complete = (TurnCompleteMessage)All<BroadcastEffect>(effects)
                .Single(b => b.Message is TurnCompleteMessage).Message;
            Assert.Equal(3, complete.Turn);
            Assert.Equal("3f9c1a04", complete.Digest);
        }

        [Fact]
        public void Handle_LocalTurnComplete_ReturnsToPlanningAndUnlocks()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            bridge.CurrentTurn = 4;

            var effects = host.Handle(new LocalTurnCompleteEvent("d", null, null));
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Equal(4, host.Turn);
            Assert.Equal(0, host.ReadyCount);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void Handle_LocalTurnComplete_WhenNotExecuting_ProducesNoEffects()
        {
            Assert.Empty(WithPeer().Handle(new LocalTurnCompleteEvent("d", null, null)));
        }

        // --- un-ready ---

        [Fact]
        public void Unready_AfterReady_ClearsThatPeersReadiness()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            Assert.Equal(1, host.ReadyCount);

            host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Equal(0, host.ReadyCount);
        }

        [Fact]
        public void Unready_DiscardsTheSubmittedBatchSoItIsNotCommittedLater()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.HandleMessage(1, new UnreadyMessage(3));

            // Host readies alone; the barrier is satisfied because the peer is
            // no longer ready... it is, so nothing commits. Re-ready with nothing.
            host.HandleMessage(1, new ReadyMessage(3, null));
            var effects = host.Handle(new LocalReadyEvent());
            Assert.Empty(All<ApplyOrderEffect>(effects));
        }

        [Fact]
        public void Unready_WhenNotReady_IsANoOp()
        {
            var host = WithPeer();
            var effects = host.HandleMessage(1, new UnreadyMessage(3));

            Assert.Equal(0, host.ReadyCount);
            Assert.Empty(All<DisconnectEffect>(effects));
        }

        [Fact]
        public void Unready_IsIdempotent()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.HandleMessage(1, new UnreadyMessage(3));
            host.HandleMessage(1, new UnreadyMessage(3));

            Assert.Equal(0, host.ReadyCount);
            Assert.Equal(HostSessionState.Planning, host.State);
        }

        [Fact]
        public void Unready_ForAnotherTurn_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.HandleMessage(1, new UnreadyMessage(2));

            Assert.Equal(1, host.ReadyCount);
        }

        [Fact]
        public void Unready_WhileExecuting_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));
            Assert.Equal(HostSessionState.Executing, host.State);

            var effects = host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Empty(All<DisconnectEffect>(effects));
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void LocalUnready_AfterTheHostReadied_ClearsItAndUnlocks()
        {
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            Assert.Equal(1, host.ReadyCount);

            var effects = host.Handle(new LocalUnreadyEvent());
            Assert.Equal(0, host.ReadyCount);
            Assert.False(Single<SetExecutionLockEffect>(effects).Locked);
        }

        [Fact]
        public void LocalUnready_WhenTheHostWasNotReady_IsANoOp()
        {
            Assert.Empty(WithPeer().Handle(new LocalUnreadyEvent()));
        }

        [Fact]
        public void LocalUnready_WhileExecuting_IsIgnored()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, true));

            Assert.Empty(host.Handle(new LocalUnreadyEvent()));
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void LocalUnready_StopsATurnThatWouldOtherwiseHaveCommitted()
        {
            var host = WithPeer();
            host.Handle(new LocalReadyEvent());
            host.Handle(new LocalUnreadyEvent());

            // The peer readying should no longer fill the barrier.
            var effects = host.HandleMessage(1, new ReadyMessage(3, null));
            Assert.Empty(All<CommitTurnEffect>(effects));
        }

        [Fact]
        public void Unready_FromAnUnregisteredPeer_DisconnectsIt()
        {
            var host = Host();
            host.Handle(new PeerConnectedEvent(1, "127.0.0.1:1"));

            var effects = host.HandleMessage(1, new UnreadyMessage(3));
            Assert.Equal(1, Single<DisconnectEffect>(effects).PeerId);
        }
    }
}
