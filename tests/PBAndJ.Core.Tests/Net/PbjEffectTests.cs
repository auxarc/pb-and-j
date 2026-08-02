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
            var effect = new ApplyOrderEffect(1, order);
            Assert.Equal(PbjEffectKind.ApplyOrder, effect.Kind);
            Assert.Equal(1, effect.PeerId);
            Assert.Same(order, effect.Order);
        }

        [Fact]
        public void ApplyOrder_WithNullOrder_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new ApplyOrderEffect(1, null!));
            Assert.Equal("order", ex.ParamName);
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
        public void Log_WithNullLine_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new LogEffect(null!));
            Assert.Equal("line", ex.ParamName);
        }
    }
}
