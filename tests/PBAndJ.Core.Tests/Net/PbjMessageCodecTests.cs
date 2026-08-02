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
            yield return new object[] { new WelcomeMessage(1, "s", 1, "host", new[] { new PeerInfo(0, "host") }, 3, "tok") };
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
            yield return new object[] { new UnreadyMessage(3) };
            yield return new object[]
            {
                new OrderResultMessage(3, 2, new[] { new RejectedOrder(1, OrderApplyResult.NotOwned) }),
            };
            yield return new object[] { new CombatStartMessage(0) };
            yield return new object[] { new CombatEndMessage() };
            yield return new object[] { new PingMessage(1) };
            yield return new object[] { new PongMessage(1) };
            yield return new object[] { new SnapshotMessage(3, "3f9c1a04", new[] { Snapshot("unit_a") }) };
            yield return new object[]
            {
                new RejoinMessage(PbjProtocol.Magic, 2, "0.2.0", "ally", "7f3a91", 1, "tok"),
            };
        }

        private static UnitSnapshot Snapshot(string name) =>
            new UnitSnapshot(name, new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 0.75f, false, 0f);

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
            var m = RoundTrip(new WelcomeMessage(1, "7f3a91", 1, "host", peers, 3, "tok"));
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
            Assert.Empty(RoundTrip(new WelcomeMessage(1, "s", 0, "host", null, 0, "tok")).Peers);
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

        [Fact]
        public void RoundTrip_Unready_PreservesTurn()
        {
            Assert.Equal(9, RoundTrip(new UnreadyMessage(9)).Turn);
        }

        [Fact]
        public void RoundTrip_OrderResult_PreservesCountsAndEveryRejection()
        {
            var m = RoundTrip(new OrderResultMessage(4, 2, new[]
            {
                new RejectedOrder(0, OrderApplyResult.NotOwned),
                new RejectedOrder(3, OrderApplyResult.OutOfWindow),
            }));

            Assert.Equal(4, m.Turn);
            Assert.Equal(2, m.Accepted);
            Assert.Equal(2, m.Rejected.Count);
            Assert.Equal(0, m.Rejected[0].Index);
            Assert.Equal(OrderApplyResult.NotOwned, m.Rejected[0].Reason);
            Assert.Equal(3, m.Rejected[1].Index);
            Assert.Equal(OrderApplyResult.OutOfWindow, m.Rejected[1].Reason);
        }

        [Fact]
        public void RoundTrip_OrderResult_WithNullRejections_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new OrderResultMessage(4, 0, null)).Rejected);
        }

        [Fact]
        public void Decode_OrderResultWithTooManyRejections_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.OrderResult);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxOrdersPerReady + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void RoundTrip_CombatStart_PreservesTurn()
        {
            Assert.Equal(0, RoundTrip(new CombatStartMessage(0)).Turn);
        }

        [Fact]
        public void Encode_CombatEnd_IsTypeByteOnly()
        {
            // The type byte is the whole message; adding a body later is a wire break.
            Assert.Equal(new byte[] { (byte)PbjMessageType.CombatEnd }, PbjMessageCodec.Encode(new CombatEndMessage()));
        }

        [Fact]
        public void RoundTrip_PingAndPong_PreserveTheNonce()
        {
            Assert.Equal(int.MaxValue, RoundTrip(new PingMessage(int.MaxValue)).Nonce);
            Assert.Equal(-7, RoundTrip(new PongMessage(-7)).Nonce);
        }

        [Fact]
        public void RoundTrip_Snapshot_PreservesEveryFieldOfEveryUnit()
        {
            var m = RoundTrip(new SnapshotMessage(4, "abc123", new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(1.5f, -2.25f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                    new Vec3(0f, 0f, -1f), 0.625f, false, 0f),
                new UnitSnapshot("pb_mech_02", new Vec3(-9f, 0f, 0.125f), new Vec4(1f, 0f, 0f, 0f),
                    new Vec3(1f, 0f, 0f), 0f, true, 2.5f),
            }));

            Assert.Equal(4, m.Turn);
            Assert.Equal("abc123", m.Digest);
            Assert.Equal(2, m.Units.Count);

            var alive = m.Units[0];
            Assert.Equal("pb_mech_01", alive.Name);
            Assert.Equal(1.5f, alive.Position.X);
            Assert.Equal(-2.25f, alive.Position.Y);
            Assert.Equal(3f, alive.Position.Z);
            Assert.Equal(0.1f, alive.Rotation.X);
            Assert.Equal(0.4f, alive.Rotation.W);
            Assert.Equal(-1f, alive.Facing.Z);
            Assert.Equal(0.625f, alive.Integrity);
            Assert.False(alive.IsDead);

            var dead = m.Units[1];
            Assert.True(dead.IsDead);
            Assert.Equal(2.5f, dead.DeathTime);
            Assert.Equal(0.125f, dead.Position.Z);
        }

        [Fact]
        public void RoundTrip_Snapshot_WithNoUnits_PreservesEmpty()
        {
            Assert.Empty(RoundTrip(new SnapshotMessage(1, null, null)).Units);
        }

        [Fact]
        public void RoundTrip_Snapshot_PreservesNonFiniteFloatsExactly()
        {
            // Raw IEEE-754 bits, not quantised and never formatted — a wrecked
            // unit can carry a NaN transform and it must survive the wire
            // identically on Mono-under-Wine and .NET.
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("u", new Vec3(float.NaN, float.PositiveInfinity, float.NegativeInfinity),
                    new Vec4(float.Epsilon, 0f, 0f, 1f), new Vec3(0f, 0f, 0f), float.NaN, false, 0f),
            }));

            Assert.True(float.IsNaN(m.Units[0].Position.X));
            Assert.True(float.IsPositiveInfinity(m.Units[0].Position.Y));
            Assert.True(float.IsNegativeInfinity(m.Units[0].Position.Z));
            Assert.Equal(float.Epsilon, m.Units[0].Rotation.X);
            Assert.True(float.IsNaN(m.Units[0].Integrity));
        }

        [Fact]
        public void Decode_SnapshotWithTooManyUnits_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Snapshot);
            writer.WriteInt32(1);
            writer.WriteString("d");
            writer.WriteInt32(PbjMessageCodec.MaxUnitsPerSnapshot + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Encode_SnapshotAtTheCap_StaysWellUnderTheFrameLimit()
        {
            // The size claim the whole "the writer thread is not a snapshot
            // prerequisite" argument rests on.
            var units = new UnitSnapshot[PbjMessageCodec.MaxUnitsPerSnapshot];
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = Snapshot("pb_mech_" + i.ToString("00"));
            }

            var bytes = PbjMessageCodec.Encode(new SnapshotMessage(1, "3f9c1a04", units));
            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength / 16,
                $"a full snapshot was {bytes.Length} bytes, more than 1/16th of the frame limit");
        }

        [Fact]
        public void RoundTrip_Welcome_PreservesTheResumeToken()
        {
            Assert.Equal("3f9c1a04",
                RoundTrip(new WelcomeMessage(2, "s", 1, "h", null, 0, "3f9c1a04")).ResumeToken);
        }

        [Fact]
        public void RoundTrip_Rejoin_PreservesEveryField()
        {
            var m = RoundTrip(new RejoinMessage(PbjProtocol.Magic, 2, "0.2.0", "ally", "7f3a91", 4, "tok"));
            Assert.Equal(PbjProtocol.Magic, m.Magic);
            Assert.Equal(2, m.ProtocolVersion);
            Assert.Equal("0.2.0", m.ModVersion);
            Assert.Equal("ally", m.PlayerName);
            Assert.Equal("7f3a91", m.SessionId);
            Assert.Equal(4, m.ClaimedPeerId);
            Assert.Equal("tok", m.ResumeToken);
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
