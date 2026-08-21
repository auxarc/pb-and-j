using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    // The turn cycle: submit, withdraw, and be told how it went.
    //
    // `OnlyOursOf` is here rather than in the shared part because HandleLocalReady is
    // its only caller. Its own remark is worth reading before touching it: it is a
    // courtesy that saves wire, not the ownership check -- the host makes that rule
    // true.
    //
    // One part of ClientSession, a single class split across files. Class-level prose
    // lives ONLY in ClientSession.cs: this file uses // rather than /// so the
    // compiler cannot concatenate summaries from every part into one type entry in
    // PBAndJ.Core.xml.
    public sealed partial class ClientSession
    {
        private void HandleLocalReady(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Planning)
            {
                return;
            }

            var captured = bridge.CaptureLocalOrders();
            var orders = OnlyOursOf(captured);

            submittedThisTurn = true;
            effects.Add(new SendEffect(HostConnectionId, new ReadyMessage(Turn, orders)));
            effects.Add(new LogEffect(NetLog.ReadyReceived(PeerId, playerName, Turn, orders.Count)));
            if (orders.Count != captured.Count)
            {
                effects.Add(new LogEffect(NetLog.OrdersNotOurs(captured.Count - orders.Count)));
            }
            effects.Add(new SetExecutionLockEffect(true));
        }

        /// <summary>
        /// Narrows a captured batch to the units this peer was dealt.
        /// </summary>
        /// <remarks>
        /// A client's local ECS holds the <em>enemy AI's</em> planned actions as
        /// well as the player's, and on a client they do not carry the
        /// <c>AIAction</c> tag the bridge filters on — whatever applies it runs at
        /// execution time, which a client never reaches. The first two-game turn
        /// therefore submitted 13 enemy orders and 3 of the host's alongside its
        /// own 2. All were correctly rejected, but they waste the wire, spend the
        /// per-message order cap and bury genuine rejections in noise.
        /// <para>
        /// This is a <b>courtesy, not an enforcement point</b>. The host checks
        /// ownership on every order regardless, and that check is what makes the
        /// rule true. So when this peer has not been told what it owns — no
        /// <c>Assignments</c> yet — it sends everything and defers, rather than
        /// silently withholding a real order on incomplete information.
        /// </para>
        /// </remarks>
        private IReadOnlyList<OrderPayload> OnlyOursOf(IReadOnlyList<OrderPayload> captured)
        {
            if (OwnedUnits.Count == 0)
            {
                return captured;
            }

            var ours = new List<OrderPayload>(captured.Count);
            for (var i = 0; i < captured.Count; i++)
            {
                var order = captured[i];
                for (var u = 0; u < OwnedUnits.Count; u++)
                {
                    // Ordinal: nameInternal is the join key every unit is
                    // addressed by, and a loose match here would turn a clean
                    // rejection into a confusing one.
                    if (string.Equals(order.OwnerName, OwnedUnits[u], StringComparison.Ordinal))
                    {
                        ours.Add(order);
                        break;
                    }
                }
            }
            return ours;
        }
        private void HandleLocalUnready(List<PbjEffect> effects)
        {
            if (State != ClientSessionState.Planning || !submittedThisTurn)
            {
                return;
            }

            submittedThisTurn = false;
            effects.Add(new SendEffect(HostConnectionId, new UnreadyMessage(Turn)));
            effects.Add(new LogEffect(NetLog.UnreadyReceived(PeerId, playerName, Turn)));
            effects.Add(new SetExecutionLockEffect(false));
        }

        private void HandleTurnCommit(TurnCommitMessage commit, List<PbjEffect> effects)
        {
            // Also the resync path: if a scenario force-execute moved the host
            // on, this is how we learn the real turn.
            Turn = commit.Turn;
            State = ClientSessionState.Watching;
            submittedThisTurn = false;
            effects.Add(new LogEffect(NetLog.TurnCommitted(commit.Turn)));
            effects.Add(new SetExecutionLockEffect(true));
        }

        private void HandleTurnComplete(TurnCompleteMessage complete, List<PbjEffect> effects)
        {
            var local = bridge.ComputeStateDigest();
            effects.Add(string.Equals(local, complete.Digest, StringComparison.Ordinal)
                ? new LogEffect(NetLog.DigestMatched(complete.Turn, complete.Digest))
                : new LogEffect(NetLog.DigestDiverged(complete.Turn, complete.Digest, local)));

            Turn = complete.Turn + 1;
            State = ClientSessionState.Planning;
            submittedThisTurn = false;
            effects.Add(new SetExecutionLockEffect(false));
        }
    }
}
