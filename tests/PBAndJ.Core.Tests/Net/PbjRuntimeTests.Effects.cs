using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // One effect at a time: what the runtime does to the transport, the game bridge
    // and the log for the effects a host session hands back, and what it does
    // instead when the game refuses. The runner has more effect kinds than this
    // file exercises: those driving a client's own view are covered in .Playback.cs,
    // and the one that ships a fight in .CombatEdges.cs.
    //
    // One part of PbjRuntimeTests; the shared fixture is in PbjRuntimeTests.cs.
    public partial class PbjRuntimeTests
    {
        // --- effects ---

        [Fact]
        public void Pump_SendEffect_WritesAnEncodedFrame()
        {
            WithHandshakenPeer();
            var sent = transport.Sent[0];
            Assert.Equal(1, sent.PeerId);
            Assert.Equal(FrameEncoder.HeaderLength + PbjMessageCodec.Encode(
                (WelcomeMessage)transport.MessagesTo(1)[0]).Length, sent.Frame.Length);
        }

        [Fact]
        public void Pump_WriteCheckpointEffect_AsksTheBridgeForTheCommittingTurn()
        {
            // Driven through the real byte path, so the arm is reached the way the
            // game reaches it -- a session that produced the effect, a queue that
            // dequeued it, and the runner's TYPE-pattern switch that dispatched it.
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal(new[] { 3 }, bridge.Checkpoints);
        }

        [Fact]
        public void Pump_WriteCheckpointEffect_LandsAfterEveryOrderAndBeforeTheCommit()
        {
            // The queue is what makes the ordering true, not frame timing: an
            // order-apply feeds its result straight back into the session, and if
            // that could produce effects they would land between the checkpoint and
            // the commit. This asserts what the runner actually did, in order.
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Single(bridge.Applied);
            Assert.Single(bridge.Checkpoints);
            Assert.Equal(1, bridge.CommitCalls);
            Assert.Equal(new[] { "apply", "checkpoint", "commit" }, bridge.CallOrder);
        }

        [Fact]
        public void Pump_BroadcastEffect_WritesToEveryPeerExceptTheExcluded()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, Frame(GoodHello("a"))));
            mailbox.Post(new PeerBytesEvent(2, Frame(GoodHello("b"))));
            runtime.Pump(0);

            // Peer 2's join is broadcast to peer 1 but not back to peer 2.
            Assert.Contains(transport.MessagesTo(1), m => m is PeerJoinedMessage);
            Assert.DoesNotContain(transport.MessagesTo(2), m => m is PeerJoinedMessage);
        }

        [Fact]
        public void Pump_DisconnectEffect_CallsTransportDisconnect()
        {
            var runtime = HostRuntime();
            mailbox.Post(new PeerBytesEvent(1, Frame(new HelloMessage(0xDEAD, 1, "v", "ally", null, null))));
            runtime.Pump(0);
            Assert.Equal(1, transport.Disconnected.Single().PeerId);
        }

        [Fact]
        public void Pump_ApplyOrderEffect_CallsGameBridge()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal("unit_b", bridge.Applied.Single().OwnerName);
        }

        [Fact]
        public void Pump_ApplyOrderEffect_WhenGameRefuses_LogsRejection()
        {
            bridge.ApplyResults["unit_b"] = OrderApplyResult.OutOfWindow;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3,
                new[] { new OrderPayload("move_run", "unit_b", 0f, 2f) }))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Contains(log.Lines, l => l.Contains("order REJECTED from #1: unit_b 'move_run' — OutOfWindow"));
        }

        [Fact]
        public void Pump_CommitTurnEffect_CommitsThenBroadcastsInTheSamePump()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal(1, bridge.CommitCalls);
            Assert.Contains(transport.MessagesTo(1), m => m is TurnCommitMessage);
            Assert.Equal(HostSessionState.Executing, host.State);
        }

        [Fact]
        public void Pump_BeginLoadEffect_StartsTheLoadAndWaits()
        {
            // A started load reports later, from the game's own completion
            // callback — so the pump that starts it must produce no outcome.
            bridge.InCombat = false;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new LocalLobbySelectEvent("pbj_campaign", "abc"));
            mailbox.Post(new LocalLobbyReadyEvent());
            mailbox.Post(new PeerBytesEvent(1, Frame(new LobbyReadyMessage(1))));
            runtime.Pump(0);

            Assert.Equal(new[] { "pbj_campaign" }, bridge.LoadsBegun);
            Assert.True(host.LoadInFlight);
        }

        [Fact]
        public void Pump_BeginLoadEffect_WhenTheLoadCannotStart_ReportsAtOnce()
        {
            // The reason BeginLoad returns an outcome rather than a bool: a
            // machine that already knows it has not got the save must say so,
            // not cost the host a two-minute timeout waiting for silence.
            bridge.InCombat = false;
            bridge.LoadRefusal = LoadOutcome.Unavailable;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new LocalLobbySelectEvent("pbj_campaign", "abc"));
            mailbox.Post(new LocalLobbyReadyEvent());
            mailbox.Post(new PeerBytesEvent(1, Frame(new LobbyReadyMessage(1))));
            runtime.Pump(0);

            // The host's own load failed, so the whole thing is abandoned.
            Assert.False(host.LoadInFlight);
            Assert.Contains(log.Lines, l => l.Contains("abandoning"));
        }

        [Fact]
        public void Pump_CommitTurnEffect_WhenGameRefuses_BroadcastsNothing()
        {
            // The reason CommitTurnEffect feeds its outcome back rather than
            // being fire-and-forget.
            bridge.CommitSucceeds = false;
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);

            Assert.Equal(1, bridge.CommitCalls);
            Assert.DoesNotContain(transport.MessagesTo(1), m => m is TurnCommitMessage);
            Assert.Contains(log.Lines, l => l.Contains("commit REFUSED"));
            Assert.Equal(HostSessionState.Planning, host.State);
            Assert.Contains(false, bridge.LockCalls);
        }

        [Fact]
        public void Pump_SetExecutionLockEffect_CallsGameBridge()
        {
            var runtime = WithHandshakenPeer();
            mailbox.Post(new PeerBytesEvent(1, Frame(new ReadyMessage(3, null))));
            mailbox.Post(new LocalReadyEvent());
            runtime.Pump(0);
            Assert.Contains(true, bridge.LockCalls);
        }

        [Fact]
        public void Pump_LogEffect_WritesToLog()
        {
            WithHandshakenPeer();
            Assert.Contains(log.Lines, l => l.Contains("handshake ok: #1 'ally'"));
        }
    }
}
