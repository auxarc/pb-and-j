using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjMessageCodecTests
    {
        // Exists purely to reach PbjMessageCodec.Encode's default: arm. This is
        // why PbjMessage's ctor is protected and Type is abstract — do not
        // "tidy" either away.
        private sealed class UnsupportedMessage : PbjMessage
        {
            public override PbjMessageType Type => (PbjMessageType)200;
        }

        private static T RoundTrip<T>(T message) where T : PbjMessage
        {
            return Assert.IsType<T>(PbjMessageCodec.Decode(PbjMessageCodec.Encode(message)));
        }

        public static IEnumerable<object[]> AllMessages()
        {
            yield return new object[] { new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally") };
            yield return new object[] { new WelcomeMessage(1, "s", 1, "host", new[] { new PeerInfo(0, "host") }, 3) };
            yield return new object[] { new RejectMessage(RejectReason.SessionFull, "full") };
            yield return new object[] { new PeerJoinedMessage(2, "ally2") };
            yield return new object[] { new PeerLeftMessage(2, "ally2") };
            yield return new object[] { new ReadyMessage(3, new[] { new OrderPayload("move_run", "u", 0f, 2f) }) };
            yield return new object[] { new TurnCommitMessage(7) };
            yield return new object[] { new TurnCompleteMessage(7, "3f9c1a04") };
            yield return new object[] { new ByeMessage("bye") };
            yield return new object[]
            {
                new AssignmentsMessage(new[] { new PeerAssignment(0, new[] { "unit_a" }) }),
            };
        }

        [Theory]
        [MemberData(nameof(AllMessages))]
        public void Encode_EveryMessageType_StartsWithItsTypeByte(PbjMessage message)
        {
            Assert.Equal((byte)message.Type, PbjMessageCodec.Encode(message)[0]);
        }

        [Theory]
        [MemberData(nameof(AllMessages))]
        public void RoundTrip_EveryMessageType_PreservesItsType(PbjMessage message)
        {
            Assert.Equal(message.Type, PbjMessageCodec.Decode(PbjMessageCodec.Encode(message)).Type);
        }

        // --- per-type field fidelity ---

        [Fact]
        public void Encode_Hello_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new HelloMessage(PbjProtocol.Magic, 1, "a", "b"));
            var expected = new byte[]
            {
                0x01,                               // type Hello
                0x31, 0x42, 0x4A, 0x50,             // magic 0x504A4231 little-endian
                0x01, 0x00, 0x00, 0x00,             // protocolVersion 1
                0x01, 0x00, 0x00, 0x00, 0x61,       // modVersion "a"
                0x01, 0x00, 0x00, 0x00, 0x62,       // playerName "b"
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void RoundTrip_Hello_PreservesFields()
        {
            var m = RoundTrip(new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally"));
            Assert.Equal(PbjProtocol.Magic, m.Magic);
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("0.2.0", m.ModVersion);
            Assert.Equal("ally", m.PlayerName);
        }

        [Fact]
        public void RoundTrip_Welcome_PreservesFieldsAndPeerRoster()
        {
            var peers = new[] { new PeerInfo(0, "host"), new PeerInfo(1, "ally") };
            var m = RoundTrip(new WelcomeMessage(1, "7f3a91", 1, "host", peers, 3));
            Assert.Equal(1, m.ProtocolVersion);
            Assert.Equal("7f3a91", m.SessionId);
            Assert.Equal(1, m.AssignedPeerId);
            Assert.Equal("host", m.HostName);
            Assert.Equal(3, m.CurrentTurn);
            Assert.Equal(2, m.Peers.Count);
            Assert.Equal(1, m.Peers[1].PeerId);
            Assert.Equal("ally", m.Peers[1].Name);
        }

        [Fact]
        public void RoundTrip_Welcome_WithNoPeers_PreservesEmptyRoster()
        {
            Assert.Empty(RoundTrip(new WelcomeMessage(1, "s", 0, "host", null, 0)).Peers);
        }

        [Theory]
        [InlineData(RejectReason.BadMagic)]
        [InlineData(RejectReason.VersionMismatch)]
        [InlineData(RejectReason.SessionFull)]
        [InlineData(RejectReason.DuplicateName)]
        [InlineData(RejectReason.InvalidName)]
        [InlineData(RejectReason.NotAcceptingPeers)]
        public void RoundTrip_Reject_PreservesEveryReason(RejectReason reason)
        {
            var m = RoundTrip(new RejectMessage(reason, "detail"));
            Assert.Equal(reason, m.Reason);
            Assert.Equal("detail", m.Detail);
        }

        [Fact]
        public void RoundTrip_Reject_WithNullDetail_PreservesNull()
        {
            Assert.Null(RoundTrip(new RejectMessage(RejectReason.BadMagic, null)).Detail);
        }

        [Fact]
        public void RoundTrip_PeerJoined_PreservesFields()
        {
            var m = RoundTrip(new PeerJoinedMessage(2, "ally2"));
            Assert.Equal(2, m.PeerId);
            Assert.Equal("ally2", m.Name);
        }

        [Fact]
        public void RoundTrip_PeerLeft_PreservesFields()
        {
            var m = RoundTrip(new PeerLeftMessage(3, "ally3"));
            Assert.Equal(3, m.PeerId);
            Assert.Equal("ally3", m.Name);
        }

        [Fact]
        public void RoundTrip_Ready_PreservesTurnAndOrders()
        {
            var orders = new[]
            {
                new OrderPayload("move_run", "unit_a", 0f, 2f),
                new OrderPayload("attack_primary", "unit_b", 1.5f, 0.75f, targetedEntityName: "enemy_1"),
            };
            var m = RoundTrip(new ReadyMessage(3, orders));
            Assert.Equal(3, m.Turn);
            Assert.Equal(2, m.Orders.Count);
            Assert.Equal("move_run", m.Orders[0].Blueprint);
            Assert.Equal("enemy_1", m.Orders[1].TargetedEntityName);
        }

        [Fact]
        public void RoundTrip_Ready_WithNoOrders_PreservesEmptyBatch()
        {
            Assert.Empty(RoundTrip(new ReadyMessage(5, null)).Orders);
        }

        [Fact]
        public void RoundTrip_TurnCommit_PreservesTurn()
        {
            Assert.Equal(7, RoundTrip(new TurnCommitMessage(7)).Turn);
        }

        [Fact]
        public void RoundTrip_TurnComplete_PreservesTurnAndDigest()
        {
            var m = RoundTrip(new TurnCompleteMessage(7, "3f9c1a04"));
            Assert.Equal(7, m.Turn);
            Assert.Equal("3f9c1a04", m.Digest);
        }

        [Fact]
        public void RoundTrip_Assignments_PreservesEveryPeersUnits()
        {
            var m = RoundTrip(new AssignmentsMessage(new[]
            {
                new PeerAssignment(0, new[] { "unit_a", "unit_c" }),
                new PeerAssignment(1, new[] { "unit_b" }),
            }));
            Assert.Equal(2, m.Assignments.Count);
            Assert.Equal(0, m.Assignments[0].PeerId);
            Assert.Equal(new[] { "unit_a", "unit_c" }, m.Assignments[0].UnitNames);
            Assert.Equal(new[] { "unit_b" }, m.Assignments[1].UnitNames);
        }

        [Fact]
        public void RoundTrip_Assignments_WithPeerHoldingNoUnits_PreservesEmpty()
        {
            var m = RoundTrip(new AssignmentsMessage(new[] { new PeerAssignment(2, null) }));
            Assert.Empty(m.Assignments[0].UnitNames);
        }

        [Fact]
        public void RoundTrip_Assignments_WithNoEntries_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new AssignmentsMessage(null)).Assignments);
        }

        [Fact]
        public void Decode_AssignmentsWithTooManyUnits_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Assignments);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxUnitsPerPeer + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_AssignmentsWithNullUnitName_ReadsAsEmptyString()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Assignments);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteString(null);
            var m = Assert.IsType<AssignmentsMessage>(PbjMessageCodec.Decode(writer.ToArray()));
            Assert.Equal(string.Empty, m.Assignments[0].UnitNames[0]);
        }

        [Fact]
        public void RoundTrip_Bye_PreservesReason()
        {
            Assert.Equal("host shutting down", RoundTrip(new ByeMessage("host shutting down")).Reason);
        }

        [Fact]
        public void RoundTrip_Bye_WithNullReason_PreservesNull()
        {
            Assert.Null(RoundTrip(new ByeMessage(null)).Reason);
        }

        // --- guards ---

        [Fact]
        public void Encode_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => PbjMessageCodec.Encode(null!));
            Assert.Equal("message", ex.ParamName);
        }

        [Fact]
        public void Encode_WithUnsupportedMessageSubclass_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Encode(new UnsupportedMessage()));
        }

        [Fact]
        public void Decode_WithNullBuffer_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => PbjMessageCodec.Decode(null!));
            Assert.Equal("payload", ex.ParamName);
        }

        [Fact]
        public void Decode_WithEmptyBuffer_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(new byte[0]));
        }

        [Fact]
        public void Decode_WithUnknownTypeByte_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(new byte[] { 200 }));
        }

        [Fact]
        public void Decode_WithTrailingBytes_Throws()
        {
            var bytes = PbjMessageCodec.Encode(new TurnCommitMessage(1)).Concat(new byte[] { 0xFF }).ToArray();
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(bytes));
        }

        [Fact]
        public void Decode_ReadyWithTooManyOrders_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Ready);
            writer.WriteInt32(1);
            writer.WriteInt32(PbjMessageCodec.MaxOrdersPerReady + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_WelcomeWithTooManyPeers_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Welcome);
            writer.WriteInt32(1);
            writer.WriteString("s");
            writer.WriteInt32(1);
            writer.WriteString("host");
            writer.WriteInt32(PbjMessageCodec.MaxPeersPerWelcome + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_WithNegativeCollectionCount_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Ready);
            writer.WriteInt32(1);
            writer.WriteInt32(-3);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_TruncatedMessage_Throws()
        {
            var full = PbjMessageCodec.Encode(new TurnCompleteMessage(7, "digest"));
            var truncated = full.Take(full.Length - 3).ToArray();
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(truncated));
        }

        [Fact]
        public void EncodedMessage_SurvivesFramingRoundTrip()
        {
            // The two layers compose: framing carries whole encoded messages.
            var encoded = PbjMessageCodec.Encode(new TurnCommitMessage(42));
            var decoder = new FrameDecoder(4096);
            var frames = decoder.Feed(FrameEncoder.Encode(encoded), 0, FrameEncoder.HeaderLength + encoded.Length);
            Assert.Single(frames);
            Assert.Equal(42, Assert.IsType<TurnCommitMessage>(PbjMessageCodec.Decode(frames[0])).Turn);
        }
    }
}
