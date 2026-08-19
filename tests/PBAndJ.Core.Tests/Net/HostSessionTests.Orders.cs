using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Order results: what each submitting peer is told about its own batch after a commit.
    // One section of the original, moved whole.
    //
    // One part of HostSessionTests, a single class split across seventeen files.
    // Helpers used by more than one part live in HostSessionTests.cs; a helper
    // lives here only because this part is effectively its sole user.
    public partial class HostSessionTests
    {
        // --- order results ---

        [Fact]
        public void Commit_SendsEachSubmittingPeerAnOrderResult()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());

            // The apply effects fold their outcomes back before the commit runs;
            // the runtime does that, so drive it by hand here.
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }
            var outcome = host.Handle(new CommitOutcomeEvent(3, true));

            var result = (OrderResultMessage)Single<SendEffect>(outcome).Message;
            Assert.Equal(3, result.Turn);
            Assert.Equal(1, result.Accepted);
            Assert.Empty(result.Rejected);
        }

        [Fact]
        public void Commit_ReportsAnUnownedOrderByItsBatchIndex()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_a") }));
            var effects = host.Handle(new LocalReadyEvent());
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }
            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;

            Assert.Equal(1, result.Accepted);
            var rejected = Assert.Single(result.Rejected);
            Assert.Equal(1, rejected.Index);
            Assert.Equal(OrderApplyResult.NotOwned, rejected.Reason);
        }

        [Fact]
        public void Commit_ReportsAGameRejectionByItsBatchIndex()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b"), Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            var applies = All<ApplyOrderEffect>(effects).ToList();
            host.Handle(new OrderAppliedEvent(1, applies[0].BatchIndex, OrderApplyResult.Applied));
            host.Handle(new OrderAppliedEvent(1, applies[1].BatchIndex, OrderApplyResult.Invalid));

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Equal(1, result.Accepted);
            var rejected = Assert.Single(result.Rejected);
            Assert.Equal(1, rejected.Index);
            Assert.Equal(OrderApplyResult.Invalid, rejected.Reason);
        }

        [Fact]
        public void Commit_SendsAnOrderResultEvenToAPeerThatSubmittedNothing()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, null));
            host.Handle(new LocalReadyEvent());

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Equal(0, result.Accepted);
            Assert.Empty(result.Rejected);
        }

        [Fact]
        public void Commit_SendsOrderResultsBeforeBroadcastingTurnCommit()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());
            var effects = host.Handle(new CommitOutcomeEvent(3, true)).ToList();

            var resultAt = effects.FindIndex(e => e is SendEffect send && send.Message is OrderResultMessage);
            var commitAt = effects.FindIndex(e => e is BroadcastEffect b && b.Message is TurnCommitMessage);
            Assert.True(resultAt >= 0 && commitAt >= 0);
            Assert.True(resultAt < commitAt, "OrderResult must reach the peer before it is told execution began.");
        }

        [Fact]
        public void RefusedCommit_SendsNoOrderResult()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            host.Handle(new LocalReadyEvent());

            var effects = host.Handle(new CommitOutcomeEvent(3, false));
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void RefusedCommit_DiscardsAccumulatedResultsSoTheyDoNotLeakIntoTheNextCommit()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            host.Handle(new LocalReadyEvent());
            host.Handle(new CommitOutcomeEvent(3, false));

            // Re-ready with a clean, fully-owned batch: the earlier unowned
            // rejection must not still be attached to this peer.
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_b") }));
            var effects = host.Handle(new LocalReadyEvent());
            foreach (var apply in All<ApplyOrderEffect>(effects))
            {
                host.Handle(new OrderAppliedEvent(apply.PeerId, apply.BatchIndex, OrderApplyResult.Applied));
            }

            var result = (OrderResultMessage)Single<SendEffect>(host.Handle(new CommitOutcomeEvent(3, true))).Message;
            Assert.Empty(result.Rejected);
            Assert.Equal(1, result.Accepted);
        }

        [Fact]
        public void Disconnect_DiscardsThatPeersAccumulatedResults()
        {
            var host = WithPeer();
            host.HandleMessage(1, new ReadyMessage(3, new[] { Order("unit_a") }));
            host.Handle(new PeerDisconnectedEvent(1, "closed"));

            // The departed peer is gone, so the host commits alone and there is
            // nobody left to send a result to.
            var effects = host.Handle(new CommitOutcomeEvent(3, true));
            Assert.Empty(All<SendEffect>(effects));
        }

        [Fact]
        public void OrderApplied_ProducesNoEffectsOfItsOwn()
        {
            var host = WithPeer();
            Assert.Empty(host.Handle(new OrderAppliedEvent(1, 0, OrderApplyResult.Applied)));
        }
    }
}
