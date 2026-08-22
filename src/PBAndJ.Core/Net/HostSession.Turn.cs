using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The turn barrier.
    //
    // Ready and unready from either side, the commit that fires when it fills, and the
    // order-apply results that commit produces.
    //
    // One part of HostSession, a single class split across files.
    // Class-level prose lives ONLY in HostSession.cs: this file uses //
    // rather than /// so the compiler cannot concatenate summaries from
    // eleven parts into one type entry in PBAndJ.Core.xml.
    public sealed partial class HostSession
    {
        private void HandleReady(int peerId, ReadyMessage ready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "ready before hello"));
                KickPeer(peerId, effects);
                return;
            }
            if (State == HostSessionState.Executing)
            {
                effects.Add(new LogEffect(NetLog.ReadyIgnoredStale(peerId, ready.Turn, barrier.Turn)));
                return;
            }

            // Not a switch: ReadyOutcome.UnknownParticipant cannot occur here,
            // because a registered peer is always a barrier participant and
            // registration was verified above. A case for it would be dead code.
            var outcome = barrier.SetReady(peerId, ready.Turn);
            if (outcome == ReadyOutcome.Stale)
            {
                effects.Add(new LogEffect(NetLog.ReadyIgnoredStale(peerId, ready.Turn, barrier.Turn)));
                return;
            }
            if (outcome == ReadyOutcome.NeedsResync)
            {
                // A scenario force-execute can advance the host's turn outside
                // the barrier, so being ahead is not the peer's fault and must
                // not disconnect it.
                effects.Add(new LogEffect(NetLog.ReadyNeedsResync(peerId, ready.Turn, barrier.Turn)));
                effects.Add(new SendEffect(peerId, new TurnCommitMessage(barrier.Turn)));
                return;
            }

            submitted[peerId] = new List<OrderPayload>(ready.Orders);
            effects.Add(new LogEffect(NetLog.ReadyReceived(peerId, NameOf(peerId), ready.Turn, ready.Orders.Count)));
            TryCommit(effects);
        }

        private void HandleUnready(int peerId, UnreadyMessage unready, List<PbjEffect> effects)
        {
            if (!registry.TryGet(peerId, out _))
            {
                effects.Add(new DisconnectEffect(peerId, "unready before hello"));
                KickPeer(peerId, effects);
                return;
            }
            if (State == HostSessionState.Executing)
            {
                effects.Add(new LogEffect(NetLog.UnreadyIgnored(peerId, unready.Turn, "already executing")));
                return;
            }
            if (unready.Turn != barrier.Turn)
            {
                effects.Add(new LogEffect(NetLog.UnreadyIgnored(peerId, unready.Turn, "not the current turn")));
                return;
            }

            // Idempotent by construction: TurnBarrier.Unready tolerates a peer
            // that was never ready, so a duplicate is a no-op rather than a fault.
            barrier.Unready(peerId);
            submitted.Remove(peerId);
            effects.Add(new LogEffect(NetLog.UnreadyReceived(peerId, NameOf(peerId), unready.Turn)));
        }

        private void HandleLocalUnready(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning)
            {
                return;
            }
            if (barrier.Unready(PbjPeerRegistry.HostPeerId))
            {
                effects.Add(new LogEffect(NetLog.UnreadyReceived(
                    PbjPeerRegistry.HostPeerId, HostName, barrier.Turn)));
                effects.Add(new SetExecutionLockEffect(false));
            }
        }

        private void HandleOrderApplied(OrderAppliedEvent applied)
        {
            if (applied.Result == OrderApplyResult.Applied)
            {
                if (pendingAccepted.TryGetValue(applied.PeerId, out var count))
                {
                    pendingAccepted[applied.PeerId] = count + 1;
                }
                return;
            }
            Reject(applied.PeerId, applied.BatchIndex, applied.Result);
        }

        private void Reject(int peerId, int batchIndex, OrderApplyResult reason)
        {
            if (pendingRejections.TryGetValue(peerId, out var rejections))
            {
                rejections.Add(new RejectedOrder(batchIndex, reason));
            }
        }

        private void ClearPendingResults()
        {
            pendingAccepted.Clear();
            pendingRejections.Clear();
            pendingResultOrder.Clear();
        }

        private void HandleLocalReady(List<PbjEffect> effects)
        {
            if (State != HostSessionState.Planning)
            {
                return;
            }
            barrier.SetReady(PbjPeerRegistry.HostPeerId, barrier.Turn);
            TryCommit(effects);
        }

        private void TryCommit(List<PbjEffect> effects)
        {
            if (!barrier.IsSatisfied)
            {
                effects.Add(new LogEffect(NetLog.BarrierWaiting(barrier.ReadyCount, barrier.ParticipantCount)));
                return;
            }

            effects.Add(new LogEffect(NetLog.BarrierCommitting(
                barrier.ReadyCount, barrier.ParticipantCount, barrier.Turn)));

            // Apply every remote order, then commit, then VERIFY, then broadcast.
            // Broadcasting first would leave peers locked forever whenever the
            // game silently refuses the commit.
            ClearPendingResults();
            var applied = 0;
            var refused = 0;
            foreach (var peerId in SubmittingPeers())
            {
                // Every submitting peer gets a result, even one whose batch was
                // empty or entirely refused.
                pendingResultOrder.Add(peerId);
                pendingAccepted[peerId] = 0;
                pendingRejections[peerId] = new List<RejectedOrder>();

                var orders = submitted[peerId];
                for (var i = 0; i < orders.Count; i++)
                {
                    var order = orders[i];
                    if (!assignments.IsOwnedBy(peerId, order.OwnerName))
                    {
                        effects.Add(new LogEffect(NetLog.OrderRejectedUnowned(peerId, order.OwnerName)));
                        Reject(peerId, i, OrderApplyResult.NotOwned);
                        refused++;
                        continue;
                    }
                    effects.Add(new ApplyOrderEffect(peerId, i, order));
                    applied++;
                }
            }

            effects.Add(new LogEffect(NetLog.OrdersApplied(applied, refused)));

            // M12c: THE MOMENT. Every peer's order is in this host's ECS and the
            // turn has not advanced, so this is the only instant at which a save
            // holds a complete, not-yet-executed plan.
            //
            // Ordering is by construction rather than by frame timing. PbjRuntime
            // .Run is a queue and effects an effect produces go to the BACK, so
            // every ApplyOrderEffect above is carried out before this is dequeued;
            // HandleOrderApplied returns no effects, so nothing an apply produces
            // can land between here and the commit below.
            //
            // Deliberately NOT after CommitTurnEffect, although CanSave would
            // still say yes there: Simulating flips a frame later, but
            // ConfirmExecution has already run ReplaceCurrentTurn, so a save taken
            // then is stamped with the next turn while holding this turn's plan.
            //
            // Host-only, and that falls out of where this line sits: TryCommit is
            // the host's. A client's ECS never receives a peer's orders, so its
            // own checkpoint would reload into a half-planned turn.
            if (barrier.Turn % checkpointEveryNTurns == 0)
            {
                effects.Add(new WriteCheckpointEffect(barrier.Turn));
            }

            committedTurn = barrier.Turn;
            effects.Add(new CommitTurnEffect(committedTurn));
        }

        private void HandleCommitOutcome(CommitOutcomeEvent outcome, List<PbjEffect> effects)
        {
            if (!outcome.Committed)
            {
                effects.Add(new LogEffect(NetLog.CommitRefused(outcome.Turn)));
                barrier.Unready(PbjPeerRegistry.HostPeerId);
                foreach (var peer in registry.Peers)
                {
                    barrier.Unready(peer.PeerId);
                }
                submitted.Clear();
                // No OrderResult: planning re-opens, so these orders have no
                // outcome to report yet. Discarding them also stops a stale
                // rejection attaching itself to the next commit.
                ClearPendingResults();
                effects.Add(new SetExecutionLockEffect(false));
                return;
            }

            State = HostSessionState.Executing;
            submitted.Clear();

            // Results before TurnCommit: a peer should know what became of its
            // orders before it is told execution has begun.
            foreach (var peerId in pendingResultOrder)
            {
                var accepted = pendingAccepted[peerId];
                var rejections = pendingRejections[peerId];
                effects.Add(new SendEffect(peerId, new OrderResultMessage(outcome.Turn, accepted, rejections)));
                effects.Add(new LogEffect(NetLog.OrderResultSent(peerId, accepted, rejections.Count)));
            }
            ClearPendingResults();

            effects.Add(new LogEffect(NetLog.TurnCommitted(outcome.Turn)));
            effects.Add(new BroadcastEffect(new TurnCommitMessage(outcome.Turn)));
            effects.Add(new SetExecutionLockEffect(true));
        }
    }
}
