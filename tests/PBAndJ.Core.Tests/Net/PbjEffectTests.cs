using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjEffectTests
    {
        private static OrderPayload Order() => new OrderPayload("move_run", "unit_a", 0f, 2f);

        [Fact]
        public void Send_RetainsFields()
        {
            var message = new TurnCommitMessage(3);
            var effect = new SendEffect(2, message);
            Assert.Equal(PbjEffectKind.Send, effect.Kind);
            Assert.Equal(2, effect.PeerId);
            Assert.Same(message, effect.Message);
        }

        [Fact]
        public void Send_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new SendEffect(1, null!));
            Assert.Equal("message", ex.ParamName);
        }

        [Fact]
        public void Broadcast_RetainsFields()
        {
            var message = new PeerJoinedMessage(1, "ally");
            var effect = new BroadcastEffect(message, exceptPeerId: 1);
            Assert.Equal(PbjEffectKind.Broadcast, effect.Kind);
            Assert.Same(message, effect.Message);
            Assert.Equal(1, effect.ExceptPeerId);
        }

        [Fact]
        public void Broadcast_WithoutExclusion_HasNoExceptPeer()
        {
            Assert.Null(new BroadcastEffect(new TurnCommitMessage(1)).ExceptPeerId);
        }

        [Fact]
        public void Broadcast_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new BroadcastEffect(null!));
            Assert.Equal("message", ex.ParamName);
        }

        [Fact]
        public void Disconnect_RetainsFields()
        {
            var effect = new DisconnectEffect(3, "protocol violation");
            Assert.Equal(PbjEffectKind.Disconnect, effect.Kind);
            Assert.Equal(3, effect.PeerId);
            Assert.Equal("protocol violation", effect.Reason);
        }

        [Fact]
        public void Disconnect_WithNullReason_IsAccepted()
        {
            Assert.Null(new DisconnectEffect(3, null).Reason);
        }

        [Fact]
        public void ApplyOrder_RetainsFields()
        {
            var order = Order();
            var effect = new ApplyOrderEffect(1, 4, order);
            Assert.Equal(PbjEffectKind.ApplyOrder, effect.Kind);
            Assert.Equal(1, effect.PeerId);
            Assert.Equal(4, effect.BatchIndex);
            Assert.Same(order, effect.Order);
        }

        [Fact]
        public void ApplyOrder_WithNullOrder_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new ApplyOrderEffect(1, 0, null!));
            Assert.Equal("order", ex.ParamName);
        }

        [Fact]
        public void ApplySnapshot_RetainsFields()
        {
            var units = new[]
            {
                new UnitSnapshot("u", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0.5f, false, 0f),
            };
            var effect = new ApplySnapshotEffect(4, units, "abc");

            Assert.Equal(PbjEffectKind.ApplySnapshot, effect.Kind);
            Assert.Equal(4, effect.Turn);
            Assert.Equal("abc", effect.ExpectedDigest);
            Assert.Equal("u", Assert.Single(effect.Units).Name);
        }

        [Fact]
        public void ApplySnapshot_WithNullUnits_IsEmpty()
        {
            Assert.Empty(new ApplySnapshotEffect(1, null, null).Units);
        }

        [Fact]
        public void ClearLocalOrders_HasItsKind()
        {
            Assert.Equal(PbjEffectKind.ClearLocalOrders, new ClearLocalOrdersEffect().Kind);
        }

        [Fact]
        public void PlayKeyframes_RetainsTheTurnAndTheCapture()
        {
            var capture = new KeyframeCapture(15f, 20f, new[] { new UnitTrack("u", null) });
            var effect = new PlayKeyframesEffect(4, capture);

            Assert.Equal(PbjEffectKind.PlayKeyframes, effect.Kind);
            Assert.Equal(4, effect.Turn);
            Assert.Same(capture, effect.Capture);
        }

        // No null-capture convenience: an absent capture means "send nothing",
        // decided at the host, so one arriving here is a caller bug.
        [Fact]
        public void PlayKeyframes_WithNullCapture_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new PlayKeyframesEffect(1, null!));
            Assert.Equal("capture", ex.ParamName);
        }

        [Fact]
        public void StopKeyframes_HasItsKind()
        {
            Assert.Equal(PbjEffectKind.StopKeyframes, new StopKeyframesEffect().Kind);
        }

        [Fact]
        public void WriteScenario_RetainsThePayload()
        {
            var payload = new ScenarioPayload("pbj_combat_test", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[] { 1, 2 }),
            });
            var effect = new WriteScenarioEffect(payload);

            Assert.Equal(PbjEffectKind.WriteScenario, effect.Kind);
            Assert.Same(payload, effect.Payload);
        }

        [Fact]
        public void WriteScenario_WithNullPayload_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new WriteScenarioEffect(null!));
            Assert.Equal("payload", ex.ParamName);
        }

        [Fact]
        public void CommitTurn_RetainsTurn()
        {
            var effect = new CommitTurnEffect(7);
            Assert.Equal(PbjEffectKind.CommitTurn, effect.Kind);
            Assert.Equal(7, effect.Turn);
        }

        [Fact]
        public void SetExecutionLock_RetainsFlag()
        {
            Assert.Equal(PbjEffectKind.SetExecutionLock, new SetExecutionLockEffect(true).Kind);
            Assert.True(new SetExecutionLockEffect(true).Locked);
            Assert.False(new SetExecutionLockEffect(false).Locked);
        }

        [Fact]
        public void Log_RetainsLine()
        {
            var effect = new LogEffect("[pb-and-j] hello");
            Assert.Equal(PbjEffectKind.Log, effect.Kind);
            Assert.Equal("[pb-and-j] hello", effect.Line);
        }

        [Fact]
        public void BeginLoad_CarriesTheSaveAndTheVersion()
        {
            var effect = new BeginLoadEffect("pbj_campaign", 4);
            Assert.Equal(PbjEffectKind.BeginLoad, effect.Kind);
            Assert.Equal("pbj_campaign", effect.SaveKey);
            Assert.Equal(4, effect.SelectionVersion);
        }

        [Fact]
        public void BeginLoad_WithNoSave_IsAllowed()
        {
            // No throw: the session emits what the selection holds, and refusing
            // here would move a decision the glue is better placed to make into
            // a constructor that cannot explain itself.
            Assert.Null(new BeginLoadEffect(null, 0).SaveKey);
        }

        [Fact]
        public void Log_WithNullLine_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new LogEffect(null!));
            Assert.Equal("line", ex.ParamName);
        }
    }
}
