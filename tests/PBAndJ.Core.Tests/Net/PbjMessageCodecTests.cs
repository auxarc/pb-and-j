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
            yield return new object[] { new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally", null, null) };
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
                new RejoinMessage(PbjProtocol.Magic, 2, "0.2.0", "ally", "7f3a91", 1, "tok", null, null),
            };
            yield return new object[]
            {
                new KeyframesMessage(3, 15f, 20f, new[] { Track("unit_a", 2) }),
            };
            yield return new object[]
            {
                new ScenarioOfferMessage("pbj_combat_test", 124000, "3f9c1a04"),
            };
            yield return new object[] { new ScenarioRequestMessage("3f9c1a04") };
            yield return new object[]
            {
                new ScenarioMessage("pbj_combat_test", "3f9c1a04", new[] { File("content.zip", 4) }),
            };
            yield return new object[]
            {
                new LobbyStateMessage(2, "pbj_campaign", "3f9c1a04", new[]
                {
                    new LobbyPeerState(0, "host", true),
                    new LobbyPeerState(1, "ally", false),
                }),
            };
            yield return new object[] { new LobbyReadyMessage(2) };
            yield return new object[] { new LobbyLoadMessage(2, "pbj_campaign", "abc123") };
            yield return new object[] { new LobbyLoadMessage(2, null, null) };
            yield return new object[] { new LobbyLoadedMessage(2, LoadOutcome.Loaded) };
            yield return new object[] { new LobbyUnreadyMessage(2) };
        }

        private static ScenarioFile File(string name, int bytes)
        {
            var content = new byte[bytes];
            for (var i = 0; i < bytes; i++)
            {
                content[i] = (byte)(i & 0xFF);
            }
            return new ScenarioFile(name, content);
        }

        private static UnitSnapshot Snapshot(string name) =>
            new UnitSnapshot(name, new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, 1f), 0.75f, false, 0f);

        private static UnitTrack Track(string name, int keys)
        {
            var frames = new TransformKey[keys];
            for (var i = 0; i < keys; i++)
            {
                frames[i] = new TransformKey(
                    i * 0.1f, new Vec3(i, 0f, 0f), new Vec4(0f, 0f, 0f, 1f));
            }
            return new UnitTrack(name, frames);
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
            var bytes = PbjMessageCodec.Encode(new HelloMessage(PbjProtocol.Magic, 1, "a", "b", "c", "d"));
            var expected = new byte[]
            {
                0x01,                               // type Hello
                0x31, 0x42, 0x4A, 0x50,             // magic 0x504A4231 little-endian
                0x01, 0x00, 0x00, 0x00,             // protocolVersion 1
                0x01, 0x00, 0x00, 0x00, 0x61,       // modVersion "a"
                0x01, 0x00, 0x00, 0x00, 0x62,       // playerName "b"
                0x01, 0x00, 0x00, 0x00, 0x63,       // gameBuild "c"
                0x01, 0x00, 0x00, 0x00, 0x64,       // passphrase "d"
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void RoundTrip_Hello_PreservesTheBuildAndPassphrase()
        {
            var m = RoundTrip(new HelloMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", "2.2.2-b8339", "hunter2"));
            Assert.Equal("2.2.2-b8339", m.GameBuild);
            Assert.Equal("hunter2", m.Passphrase);
        }

        // The harness has no game and sets no passphrase against a local host.
        [Fact]
        public void RoundTrip_Hello_WithNoBuildOrPassphrase_KeepsThemNull()
        {
            var m = RoundTrip(new HelloMessage(PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", null, null));
            Assert.Null(m.GameBuild);
            Assert.Null(m.Passphrase);
        }

        [Fact]
        public void RoundTrip_Rejoin_PreservesTheBuildAndPassphrase()
        {
            var m = RoundTrip(new RejoinMessage(
                PbjProtocol.Magic, PbjProtocol.Version, "0.3.0", "ally", "7f3a91", 1, "tok",
                "2.2.2-b8339", "hunter2"));
            Assert.Equal("2.2.2-b8339", m.GameBuild);
            Assert.Equal("hunter2", m.Passphrase);
        }

        [Fact]
        public void RoundTrip_Hello_PreservesFields()
        {
            var m = RoundTrip(new HelloMessage(PbjProtocol.Magic, 1, "0.2.0", "ally", null, null));
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

        // --- keyframes (M6) ---

        [Fact]
        public void Encode_Keyframes_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new KeyframesMessage(1, 0f, 2f, new[]
            {
                new UnitTrack("a", new[]
                {
                    new TransformKey(0f, new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            }));

            var expected = new byte[]
            {
                0x13,                               // type Keyframes (19)
                0x01, 0x00, 0x00, 0x00,             // turn 1
                0x00, 0x00, 0x00, 0x00,             // windowStart 0
                0x00, 0x00, 0x00, 0x40,             // windowEnd 2
                0x01, 0x00, 0x00, 0x00,             // one track
                0x01, 0x00, 0x00, 0x00, 0x61,       // name "a"
                0x01, 0x00, 0x00, 0x00,             // one key
                0x00, 0x00, 0x00, 0x00,             // time 0
                0x00, 0x00, 0x80, 0x3F,             // position.x 1
                0x00, 0x00, 0x00, 0x40,             // position.y 2
                0x00, 0x00, 0x40, 0x40,             // position.z 3
                0x00, 0x00, 0x00, 0x00,             // rotation.x 0
                0x00, 0x00, 0x00, 0x00,             // rotation.y 0
                0x00, 0x00, 0x00, 0x00,             // rotation.z 0
                0x00, 0x00, 0x80, 0x3F,             // rotation.w 1
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void RoundTrip_Keyframes_PreservesEveryKeyOfEveryTrack()
        {
            var m = RoundTrip(new KeyframesMessage(4, 15f, 20f, new[]
            {
                new UnitTrack("pb_mech_01", new[]
                {
                    new TransformKey(15f, new Vec3(1.5f, -2.25f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f)),
                    new TransformKey(15.1f, new Vec3(-9f, 0f, 0.125f), new Vec4(1f, 0f, 0f, 0f)),
                }),
                new UnitTrack("pb_mech_02", new TransformKey[0]),
            }));

            Assert.Equal(4, m.Turn);
            Assert.Equal(15f, m.WindowStart);
            Assert.Equal(20f, m.WindowEnd);
            Assert.Equal(2, m.Tracks.Count);

            var moving = m.Tracks[0];
            Assert.Equal("pb_mech_01", moving.Name);
            Assert.Equal(2, moving.Transforms.Count);
            Assert.Equal(15f, moving.Transforms[0].Time);
            Assert.Equal(1.5f, moving.Transforms[0].Position.X);
            Assert.Equal(-2.25f, moving.Transforms[0].Position.Y);
            Assert.Equal(3f, moving.Transforms[0].Position.Z);
            Assert.Equal(0.1f, moving.Transforms[0].Rotation.X);
            Assert.Equal(0.4f, moving.Transforms[0].Rotation.W);
            Assert.Equal(15.1f, moving.Transforms[1].Time);
            Assert.Equal(0.125f, moving.Transforms[1].Position.Z);
            Assert.Equal(1f, moving.Transforms[1].Rotation.X);

            // A unit with nothing recorded still travels, so the client can tell
            // "no motion" from "not in this combat".
            Assert.Equal("pb_mech_02", m.Tracks[1].Name);
            Assert.Empty(m.Tracks[1].Transforms);
        }

        [Fact]
        public void RoundTrip_KeyframesWithNoTracks_Survives()
        {
            Assert.Empty(RoundTrip(new KeyframesMessage(1, 0f, 5f, null)).Tracks);
        }

        [Fact]
        public void Decode_KeyframesWithTooManyTracks_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Keyframes);
            writer.WriteInt32(1);
            writer.WriteSingle(0f);
            writer.WriteSingle(5f);
            writer.WriteInt32(PbjMessageCodec.MaxTracksPerKeyframes + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_TrackWithTooManyKeys_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Keyframes);
            writer.WriteInt32(1);
            writer.WriteSingle(0f);
            writer.WriteSingle(5f);
            writer.WriteInt32(1);
            writer.WriteString("a");
            writer.WriteInt32(PbjMessageCodec.MaxKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        // The size claim the wire budget rests on: keyframes are two orders of
        // magnitude heavier than a snapshot, so the caps have to be shown to fit
        // rather than assumed to.
        [Fact]
        public void Encode_KeyframesAtBothCaps_StaysUnderTheFrameLimit()
        {
            var tracks = new UnitTrack[PbjMessageCodec.MaxTracksPerKeyframes];
            for (var i = 0; i < tracks.Length; i++)
            {
                tracks[i] = Track("pb_mech_" + i.ToString("00"), PbjMessageCodec.MaxKeysPerTrack);
            }

            var bytes = PbjMessageCodec.Encode(new KeyframesMessage(1, 0f, 5f, tracks));
            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a full keyframe message was {bytes.Length} bytes, over the frame limit");
        }

        // --- scenario transfer (M9) ---

        [Fact]
        public void Encode_ScenarioOffer_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new ScenarioOfferMessage("s", 2, "ab"));

            var expected = new byte[]
            {
                0x14,                               // type ScenarioOffer (20)
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveName "s"
                0x02, 0x00, 0x00, 0x00,             // totalBytes 2
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // digest "ab"
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void Encode_Scenario_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new ScenarioMessage("s", "ab", new[]
            {
                new ScenarioFile("f", new byte[] { 0xDE, 0xAD }),
            }));

            var expected = new byte[]
            {
                0x16,                               // type Scenario (22)
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveName "s"
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // digest "ab"
                0x01, 0x00, 0x00, 0x00,             // one file
                0x01, 0x00, 0x00, 0x00, 0x66,       // name "f"
                0x02, 0x00, 0x00, 0x00, 0xDE, 0xAD, // content
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void RoundTrip_ScenarioOffer_PreservesEveryField()
        {
            var m = RoundTrip(new ScenarioOfferMessage("pbj_combat_test", 124546, "3f9c1a04"));
            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal(124546, m.TotalBytes);
            Assert.Equal("3f9c1a04", m.Digest);
        }

        [Fact]
        public void RoundTrip_ScenarioRequest_PreservesTheDigest()
        {
            Assert.Equal("3f9c1a04", RoundTrip(new ScenarioRequestMessage("3f9c1a04")).Digest);
        }

        [Fact]
        public void RoundTrip_ScenarioRequest_WithNoDigest_KeepsItNull()
        {
            // A peer that holds nothing asks with no digest at all, rather than
            // inventing one that could accidentally match.
            Assert.Null(RoundTrip(new ScenarioRequestMessage(null)).Digest);
        }

        [Fact]
        public void RoundTrip_Scenario_PreservesEveryFileByteForByte()
        {
            var m = RoundTrip(new ScenarioMessage("pbj_combat_test", "3f9c1a04", new[]
            {
                File("content.zip", 3000),
                new ScenarioFile("metadata.yaml", new byte[] { 0x00, 0xFF, 0x7F, 0x80 }),
            }));

            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal("3f9c1a04", m.Digest);
            Assert.Equal(2, m.Files.Count);

            Assert.Equal("content.zip", m.Files[0].Name);
            Assert.Equal(File("content.zip", 3000).Content, m.Files[0].Content);

            Assert.Equal("metadata.yaml", m.Files[1].Name);
            Assert.Equal(new byte[] { 0x00, 0xFF, 0x7F, 0x80 }, m.Files[1].Content);
        }

        [Fact]
        public void RoundTrip_Scenario_PreservesAnEmptyFile()
        {
            // Zero-length is a real state on disk and must not decode as null,
            // or the digest the sender computed stops matching.
            var m = RoundTrip(new ScenarioMessage("s", "d", new[] { new ScenarioFile("f", new byte[0]) }));
            Assert.Empty(m.Files[0].Content);
        }

        [Fact]
        public void RoundTrip_ScenarioWithNoFiles_Survives()
        {
            Assert.Empty(RoundTrip(new ScenarioMessage("s", "d", null)).Files);
        }

        [Fact]
        public void Decode_ScenarioWithTooManyFiles_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Scenario);
            writer.WriteString("s");
            writer.WriteString("d");
            writer.WriteInt32(ScenarioPayload.MaxFiles + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_ScenarioWithANullFileBlob_YieldsEmptyContent()
        {
            // -1 is the writer's null sentinel. It is not a shape we ever send,
            // but a peer can, and it must land as empty rather than as a null
            // that surfaces three layers away at the point of writing to disk.
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Scenario);
            writer.WriteString("s");
            writer.WriteString("d");
            writer.WriteInt32(1);
            writer.WriteString("f");
            writer.WriteInt32(-1);

            var m = Assert.IsType<ScenarioMessage>(PbjMessageCodec.Decode(writer.ToArray()));
            Assert.Empty(m.Files[0].Content);
        }

        // The size claim M9 rests on: the real save is ~124 KB, the cap is 512 KB,
        // and the frame limit is 1 MiB. Pinned rather than assumed, because
        // exceeding it would fail only on a real transfer.
        [Fact]
        public void Encode_ScenarioAtTheSizeCap_StaysUnderTheFrameLimit()
        {
            var half = (int)(ScenarioPayload.MaxTotalBytes / 2);
            var bytes = PbjMessageCodec.Encode(new ScenarioMessage("pbj_combat_test", "3f9c1a04", new[]
            {
                File("content.zip", half),
                File("metadata.yaml", half),
            }));

            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a scenario at the size cap was {bytes.Length} bytes, over the frame limit");
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
            var m = RoundTrip(new RejoinMessage(PbjProtocol.Magic, 2, "0.2.0", "ally", "7f3a91", 4, "tok", null, null));
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

        // --- lobby (M11a) ---

        [Fact]
        public void Encode_LobbyState_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new LobbyStateMessage(2, "s", "ab", new[]
            {
                new LobbyPeerState(0, "h", true),
            }));

            var expected = new byte[]
            {
                0x17,                               // type LobbyState (23)
                0x02, 0x00, 0x00, 0x00,             // selectionVersion 2
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveKey "s"
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // saveDigest "ab"
                0x01, 0x00, 0x00, 0x00,             // one peer
                0x00, 0x00, 0x00, 0x00,             // peerId 0
                0x01, 0x00, 0x00, 0x00, 0x68,       // name "h"
                0x01,                               // ready
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void Encode_LobbyReady_ProducesExactBytes()
        {
            Assert.Equal(
                new byte[] { 0x18, 0x02, 0x00, 0x00, 0x00 },
                PbjMessageCodec.Encode(new LobbyReadyMessage(2)));
        }

        [Fact]
        public void Encode_LobbyUnready_ProducesExactBytes()
        {
            Assert.Equal(
                new byte[] { 0x19, 0x02, 0x00, 0x00, 0x00 },
                PbjMessageCodec.Encode(new LobbyUnreadyMessage(2)));
        }

        [Fact]
        public void RoundTrip_LobbyState_PreservesEveryField()
        {
            var m = RoundTrip(new LobbyStateMessage(4, "pbj_campaign", "3f9c1a04", new[]
            {
                new LobbyPeerState(0, "host", true),
                new LobbyPeerState(1, "ally", false),
            }));

            Assert.Equal(4, m.SelectionVersion);
            Assert.Equal("pbj_campaign", m.SaveKey);
            Assert.Equal("3f9c1a04", m.SaveDigest);
            Assert.Equal(2, m.Peers.Count);
            Assert.Equal(0, m.Peers[0].PeerId);
            Assert.Equal("host", m.Peers[0].Name);
            Assert.True(m.Peers[0].Ready);
            Assert.Equal("ally", m.Peers[1].Name);
            Assert.False(m.Peers[1].Ready);
        }

        [Fact]
        public void RoundTrip_LobbyState_WithNothingSelected_KeepsTheNulls()
        {
            // "No save chosen yet" is a real lobby state, not a malformed one.
            var m = RoundTrip(new LobbyStateMessage(0, null, null, null));
            Assert.Equal(0, m.SelectionVersion);
            Assert.Null(m.SaveKey);
            Assert.Null(m.SaveDigest);
            Assert.Empty(m.Peers);
        }

        [Fact]
        public void RoundTrip_LobbyReadyAndUnready_PreserveTheSelection()
        {
            Assert.Equal(9, RoundTrip(new LobbyReadyMessage(9)).SelectionVersion);
        }

        [Fact]
        public void LobbyLoad_CarriesTheSelectionAndTheSave()
        {
            var round = RoundTrip(new LobbyLoadMessage(4, "pbj_campaign", "abc123"));
            Assert.Equal(4, round.SelectionVersion);
            Assert.Equal("pbj_campaign", round.SaveKey);
            Assert.Equal("abc123", round.SaveDigest);
        }

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void LobbyLoaded_CarriesEveryOutcome(LoadOutcome outcome)
        {
            var round = RoundTrip(new LobbyLoadedMessage(4, outcome));
            Assert.Equal(4, round.SelectionVersion);
            Assert.Equal(outcome, round.Outcome);
        }

        [Fact]
        public void LobbyLoaded_WithAnOutcomeWeDoNotKnow_DecodesRatherThanThrowing()
        {
            // The cast is unvalidated, exactly as RejectReason's is. A peer can
            // put any byte on the wire and the host must survive reading it —
            // faulting the session over an unknown enum value would let a peer
            // hang up on us by sending one.
            var round = RoundTrip(new LobbyLoadedMessage(1, (LoadOutcome)200));
            Assert.Equal((LoadOutcome)200, round.Outcome);
            Assert.Equal(9, RoundTrip(new LobbyUnreadyMessage(9)).SelectionVersion);
        }

        [Fact]
        public void Decode_LobbyStateOverThePeerCap_Throws()
        {
            // The roster shares Welcome's cap, since it is the same roster.
            var peers = new LobbyPeerState[PbjMessageCodec.MaxPeersPerWelcome + 1];
            for (var i = 0; i < peers.Length; i++)
            {
                peers[i] = new LobbyPeerState(i, "p" + i, false);
            }
            var encoded = PbjMessageCodec.Encode(new LobbyStateMessage(1, "s", null, peers));
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(encoded));
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
