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
            yield return new object[] { new BasePositionMessage(1024.5f, -37.25f) };
            yield return new object[] { new CombatOfferMessage("pbj_combat_test", "d1", 4) };
            yield return new object[] { new CombatEnteredMessage(4, LoadOutcome.Loaded) };
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
            yield return new object[] { new PosesMessage(3, 0, 1, PoseTrack("unit_a", 4, 3)) };

            // A part carrying no track. Decode cannot produce this — it always
            // builds a track — but encode has to survive it, because the host
            // assembles parts from captured data and a null there must not
            // throw inside SendTo, which encodes outside its own try block.
            yield return new object[] { new PosesMessage(3, 0, 1, null) };

            yield return new object[]
            {
                new ReplayAssetsMessage(3, 0, 2, new AssetCapture(
                    new[] { StandaloneAsset(1) },
                    new[] { ProjectileAsset(2, 3) },
                    new[] { BeamAsset(3, 3) })),
            };

            // A part carrying nothing. The host never sends one — Split emits
            // no parts at all for an empty turn — but a null capture must not
            // throw inside SendTo, which encodes outside its own try block.
            yield return new object[] { new ReplayAssetsMessage(3, 0, 1, null) };
        }

        private static AssetTrackHead AssetHead(string key, float? hue = null, AssetColour? colour = null) =>
            new AssetTrackHead(key, 1.5f, 3.25f, hue, colour);

        private static StandaloneAssetTrack StandaloneAsset(
            int id, float? hue = null, AssetColour? colour = null) =>
            new StandaloneAssetTrack(
                id, AssetHead("fx_impact_" + id, hue, colour),
                new Vec3(1f, 2f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                new Vec3(1.5f, 1.5f, 1.5f), new Vec4(4f, 5f, 6f, 0.75f),
                new Vec3(7f, 8f, 9f));

        private static ProjectileAssetTrack ProjectileAsset(int id, int keys)
        {
            var frames = new TransformKey[keys];
            for (var i = 0; i < keys; i++)
            {
                frames[i] = new TransformKey(
                    i * 0.05f, new Vec3(i, i + 1, i + 2), new Vec4(0.1f, 0.2f, 0.3f, 0.4f));
            }
            return new ProjectileAssetTrack(
                id, AssetHead("fx_bullet_" + id), new Vec3(2f, 2f, 2f), frames);
        }

        private static ProjectileAssetTrack TrailedProjectile(int id, int keys, int points)
        {
            var plain = ProjectileAsset(id, keys);
            var trail = new TrailKey[points];
            for (var i = 0; i < points; i++)
            {
                // Every field a different value, and none of them equal to any
                // other field's. Five Vec3s in a row are interchangeable to the
                // compiler; a tangent that arrives in the normal's slot is a
                // trail lit from the wrong side.
                trail[i] = new TrailKey(
                    i * 0.1f,
                    i * 0.1f + 0.5f,
                    new Vec3(i + 1, i + 2, i + 3),
                    new Vec3(i + 10, i + 11, i + 12),
                    new Vec3(i + 20, i + 21, i + 22),
                    new Vec3(i + 30, i + 31, i + 32),
                    new Vec3(i + 40, i + 41, i + 42),
                    new Vec4(0.11f, 0.22f, 0.33f, 0.44f),
                    0.9f + i,
                    0.05f + i);
            }

            return new ProjectileAssetTrack(
                plain.Id, plain.Head, plain.Scale, plain.Keys, trail);
        }

        private static UnitLightKey LightKey(int index)
        {
            return new UnitLightKey(
                index * 0.25f,
                "socket_" + index,
                new Vec3(index + 1, index + 2, index + 3),
                new Vec4(0.15f, 0.25f, 0.35f, 1f),
                6f + index,
                0.02f + index,
                0.03f + index,
                0.04f + index);
        }

        private static BeamAssetTrack BeamAsset(int id, int keys)
        {
            var frames = new BeamKey[keys];
            for (var i = 0; i < keys; i++)
            {
                frames[i] = new BeamKey(
                    i * 0.05f, new Vec3(i, i + 1, i + 2), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                    new Vec3(0.5f, 0.25f, i * 3f));
            }
            return new BeamAssetTrack(id, AssetHead("fx_beam_" + id), frames);
        }

        private static UnitPoseTrack PoseTrack(string name, int joints, int keys)
        {
            var names = new string[joints];
            for (var i = 0; i < joints; i++)
            {
                names[i] = "joint_" + i;
            }

            var poseKeys = new PoseKey[keys];
            for (var k = 0; k < keys; k++)
            {
                var values = new JointPose[joints];
                for (var j = 0; j < joints; j++)
                {
                    values[j] = new JointPose(
                        new Vec3(j, k, 0f), new Vec4(0f, 0f, 0f, 1f));
                }
                poseKeys[k] = new PoseKey(k * 0.1f, k % 2 == 0, k % 3 == 0, values);
            }

            return new UnitPoseTrack(name, names, poseKeys);
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
                new Vec3(0f, 0f, 1f), 0.75f);

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

        [Theory]
        [InlineData(LoadOutcome.Loaded)]
        [InlineData(LoadOutcome.Refused)]
        [InlineData(LoadOutcome.Unavailable)]
        public void RoundTrip_CombatEntered_PreservesTheTurnAndOutcome(LoadOutcome outcome)
        {
            var m = RoundTrip(new CombatEnteredMessage(4, outcome));
            Assert.Equal(4, m.Turn);
            Assert.Equal(outcome, m.Outcome);
        }

        [Fact]
        public void RoundTrip_CombatOffer_PreservesTheFightsIdentity()
        {
            // The digest is the load-bearing field: the scenario slot is
            // rewritten every mission, so the name alone cannot tell this fight
            // from the last one.
            var m = RoundTrip(new CombatOfferMessage("pbj_combat_test", "d1", 4));
            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal("d1", m.Digest);
            Assert.Equal(4, m.Turn);
        }

        [Fact]
        public void RoundTrip_BasePosition_PreservesBothCoordinates()
        {
            var m = RoundTrip(new BasePositionMessage(1024.5f, -37.25f));

            Assert.Equal(1024.5f, m.X);
            Assert.Equal(-37.25f, m.Z);
        }

        [Fact]
        public void Encode_BasePosition_IsNineBytes_SoNoHeightCanBeHidingInIt()
        {
            // A type byte and two floats, and nothing else. This is the guard on
            // the design decision rather than on the arithmetic: the receiving
            // machine snaps to its own ground, and a third coordinate appearing
            // here later would mean somebody had started sending the host's idea
            // of a surface the client renders for itself.
            Assert.Equal(1 + 4 + 4, PbjMessageCodec.Encode(new BasePositionMessage(1f, 2f)).Length);
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
                    new Vec3(0f, 0f, -1f), 0.625f),
                new UnitSnapshot("pb_mech_02", new Vec3(-9f, 0f, 0.125f), new Vec4(1f, 0f, 0f, 0f),
                    new Vec3(1f, 0f, 0f), 0f),
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

            var second = m.Units[1];
            Assert.Equal(0.125f, second.Position.Z);
        }

        // Three booleans that all default to a different value from the one
        // being asserted, per unit, so a decoder that read them in the wrong
        // order or dropped one cannot pass. They are the last fields in the
        // record, which is exactly where an off-by-one in the reader lands.
        [Fact]
        public void RoundTrip_Snapshot_PreservesVisibilityPerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isHidden: true, isHiddenDetectable: false, isDeployed: false),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isHidden: false, isHiddenDetectable: true, isDeployed: true),
            }));

            Assert.True(m.Units[0].IsHidden);
            Assert.False(m.Units[0].IsHiddenDetectable);
            Assert.False(m.Units[0].IsDeployed);

            Assert.False(m.Units[1].IsHidden);
            Assert.True(m.Units[1].IsHiddenDetectable);
            Assert.True(m.Units[1].IsDeployed);
        }

        // Per unit, and with a different count on each, so a decoder that read
        // one unit's list into the next unit's record cannot pass. The list is
        // the LAST thing in the record, which is exactly where a reader that
        // dropped a field lands — and where the two removed death fields used to
        // sit, so this leg is also what pins their removal.
        [Fact]
        public void RoundTrip_Snapshot_PreservesWreckedPartsPerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[] { new PartDestruction("equipment_left", 4.25f) }),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[]
                    {
                        new PartDestruction("core", 0f),
                        // The spawn sentinel. A codec that quantised or clamped
                        // the stamp would erase the sign that tells a
                        // pre-battle wreck from one this turn produced.
                        new PartDestruction("leg_right", -100f),
                    }),
            }));

            Assert.Empty(m.Units[0].WreckedParts);

            var one = Assert.Single(m.Units[1].WreckedParts);
            Assert.Equal("equipment_left", one.Socket);
            Assert.Equal(4.25f, one.Time);

            Assert.Equal(2, m.Units[2].WreckedParts.Count);
            Assert.Equal("core", m.Units[2].WreckedParts[0].Socket);
            Assert.Equal(0f, m.Units[2].WreckedParts[0].Time);
            Assert.Equal("leg_right", m.Units[2].WreckedParts[1].Socket);
            Assert.Equal(-100f, m.Units[2].WreckedParts[1].Time);
        }

        // The unit's own wreck travels beside its parts and is a SEPARATE fact,
        // so the units here are chosen to disagree in both directions: one
        // wrecked with no parts recorded, one with parts and not wrecked. A
        // decoder that inferred either from the other cannot pass.
        [Fact]
        public void RoundTrip_Snapshot_PreservesTheUnitWreckIndependentlyOfItsParts()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isWrecked: true, wreckedAt: 7.5f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    wreckedParts: new[] { new PartDestruction("core", 2f) }),
                // Negative is the "no moment to wait for" convention, shared
                // with PartDestruction.Time, and a codec that clamped or
                // quantised the stamp would erase it.
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    isWrecked: true, wreckedAt: -100f),
            }));

            Assert.True(m.Units[0].IsWrecked);
            Assert.Equal(7.5f, m.Units[0].WreckedAt);
            Assert.Empty(m.Units[0].WreckedParts);

            Assert.False(m.Units[1].IsWrecked);
            Assert.Single(m.Units[1].WreckedParts);

            Assert.True(m.Units[2].IsWrecked);
            Assert.Equal(-100f, m.Units[2].WreckedAt);
        }

        // Truncation rather than a fault, and the asymmetry is the point: a
        // snapshot is a correction, so refusing to send one over a part list
        // would cost that unit its position and visibility too.
        [Fact]
        public void RoundTrip_Snapshot_TruncatesAnOversizeWreckedPartList()
        {
            var parts = new PartDestruction[PbjMessageCodec.MaxWreckedPartsPerUnit + 5];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = new PartDestruction("socket_" + i, i);
            }

            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, wreckedParts: parts),
            }));

            Assert.Equal(PbjMessageCodec.MaxWreckedPartsPerUnit, m.Units[0].WreckedParts.Count);
            Assert.Equal("socket_0", m.Units[0].WreckedParts[0].Socket);
        }

        // M16. Per unit and with a different count on each, so a decoder that
        // read one unit's list into the next unit's record cannot pass. This is
        // now the LAST list in the record, which is where a reader that dropped a
        // field lands.
        [Fact]
        public void RoundTrip_Snapshot_PreservesPartStatePerUnit()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    parts: new[] { new PartState("core", 0.375f, 0.5f) }),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    parts: new[]
                    {
                        // Integrity and barrier are independent, so the pair here
                        // is deliberately asymmetric in both directions: a decoder
                        // that read one into the other cannot pass.
                        new PartState("equipment_left", 0f, 1f),
                        new PartState("equipment_right", 1f, 0f),
                    }),
            }));

            Assert.Empty(m.Units[0].Parts);

            var one = Assert.Single(m.Units[1].Parts);
            Assert.Equal("core", one.Socket);
            Assert.Equal(0.375f, one.Integrity);
            Assert.Equal(0.5f, one.Barrier);

            Assert.Equal(2, m.Units[2].Parts.Count);
            Assert.Equal(0f, m.Units[2].Parts[0].Integrity);
            Assert.Equal(1f, m.Units[2].Parts[0].Barrier);
            Assert.Equal(1f, m.Units[2].Parts[1].Integrity);
            Assert.Equal(0f, m.Units[2].Parts[1].Barrier);
        }

        // M16, and the pairing this exists for: the two states a single float
        // could not tell apart are "absent on the host" — which is the whole
        // player squad, mid-combat — and "present and zero", which is a real
        // value the game writes for a wrecked unit. Before M16 both travelled as
        // a bare 0f and the client wrote a component the host did not have.
        [Fact]
        public void RoundTrip_Snapshot_PreservesFrameIntegrityPresenceAndValue()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0f, hasFrameIntegrity: true),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 0.625f, hasFrameIntegrity: true),
            }));

            Assert.False(m.Units[0].HasFrameIntegrity);
            Assert.True(m.Units[1].HasFrameIntegrity);
            Assert.Equal(0f, m.Units[1].Integrity);
            Assert.True(m.Units[2].HasFrameIntegrity);
            Assert.Equal(0.625f, m.Units[2].Integrity);
        }

        [Fact]
        public void RoundTrip_Snapshot_TruncatesAnOversizePartStateList()
        {
            var parts = new PartState[PbjMessageCodec.MaxPartsPerUnit + 5];
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = new PartState("socket_" + i, 1f, 1f);
            }

            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f, parts: parts),
            }));

            Assert.Equal(PbjMessageCodec.MaxPartsPerUnit, m.Units[0].Parts.Count);
            Assert.Equal("socket_0", m.Units[0].Parts[0].Socket);
        }

        // Presence and value travel separately, so the pairs chosen here are the
        // two a single combined field could not tell apart: absent (which a host
        // reports for its whole player squad) and present-but-negative (which a
        // client manufactures for the same units, because the save writer stamps
        // -1 for an absent component and the loader adds it back to everything
        // deployed). Collapsing them would make the correction a no-op.
        [Fact]
        public void RoundTrip_Snapshot_PreservesArrivalTimePresenceAndValue()
        {
            var m = RoundTrip(new SnapshotMessage(1, null, new[]
            {
                new UnitSnapshot("pb_mech_01", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f),
                new UnitSnapshot("pb_mech_02", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    hasArrivalTime: true, arrivalTime: -1f),
                new UnitSnapshot("pb_mech_03", new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(0f, 0f, 1f), 1f,
                    hasArrivalTime: true, arrivalTime: 10.13f),
            }));

            Assert.False(m.Units[0].HasArrivalTime);
            Assert.Equal(0f, m.Units[0].ArrivalTime);

            Assert.True(m.Units[1].HasArrivalTime);
            Assert.Equal(-1f, m.Units[1].ArrivalTime);

            Assert.True(m.Units[2].HasArrivalTime);
            Assert.Equal(10.13f, m.Units[2].ArrivalTime);
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
                    new Vec4(float.Epsilon, 0f, 0f, 1f), new Vec3(0f, 0f, 0f), float.NaN),
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
                0x00, 0x00, 0x80, 0xFF,             // revealTime -inf (none)
                0x00, 0x00, 0x80, 0xFF,             // hideTime -inf (none)
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

        // The sentinel has to survive as a sentinel. WriteSingle emits raw bits,
        // so negative infinity round-trips exactly — but "exactly" is the whole
        // claim, and a decoder that clamped or normalised it would turn "this
        // unit never changed visibility" into "it was revealed a very long time
        // ago", which reads as correct in every log and is wrong on screen.
        [Fact]
        public void RoundTrip_Keyframes_PreservesVisibilityStampsAndTheirAbsence()
        {
            var m = RoundTrip(new KeyframesMessage(1, 10f, 15f, new[]
            {
                new UnitTrack("never_changed", null),
                new UnitTrack("revealed", null, revealTime: 12.5f),
                new UnitTrack("retreated", null, hideTime: 13.25f),
                new UnitTrack("both", null, revealTime: 11f, hideTime: 14f),
            }));

            Assert.True(float.IsNegativeInfinity(m.Tracks[0].RevealTime));
            Assert.True(float.IsNegativeInfinity(m.Tracks[0].HideTime));

            Assert.Equal(12.5f, m.Tracks[1].RevealTime);
            Assert.True(float.IsNegativeInfinity(m.Tracks[1].HideTime));

            Assert.True(float.IsNegativeInfinity(m.Tracks[2].RevealTime));
            Assert.Equal(13.25f, m.Tracks[2].HideTime);

            Assert.Equal(11f, m.Tracks[3].RevealTime);
            Assert.Equal(14f, m.Tracks[3].HideTime);
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

        // --- poses (M8) ---

        [Fact]
        public void RoundTrip_Poses_PreservesEveryFieldOfEveryKey()
        {
            var decoded = RoundTrip(new PosesMessage(9, 2, 5, PoseTrack("pb_mech_02", 3, 4)));

            Assert.Equal(9, decoded.Turn);
            Assert.Equal(2, decoded.PartIndex);
            Assert.Equal(5, decoded.PartCount);
            Assert.Equal("pb_mech_02", decoded.Track!.Name);
            Assert.Equal(new[] { "joint_0", "joint_1", "joint_2" }, decoded.Track.Joints);
            Assert.Equal(4, decoded.Track.Keys.Count);

            var key = decoded.Track.Keys[2];
            Assert.Equal(0.2f, key.Time);
            Assert.True(key.SyncLeftEquipment);
            Assert.False(key.SyncRightEquipment);
            Assert.Equal(3, key.Joints.Count);
            Assert.Equal(1f, key.Joints[1].Position.X);
            Assert.Equal(2f, key.Joints[1].Position.Y);
            Assert.Equal(1f, key.Joints[1].Rotation.W);
        }

        // The equipment flags are the reason a pose is not just "a time and some
        // joints": they pin the weapon to the palm for that frame. A codec that
        // dropped or transposed them would detach the rifle from the hand mid-
        // burst, and nothing else in the suite would notice.
        [Fact]
        public void RoundTrip_Poses_KeepsTheTwoEquipmentFlagsApart()
        {
            var key = new PoseKey(1f, true, false, new[] { new JointPose(default, default) });
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1, new UnitPoseTrack("u", new[] { "j" }, new[] { key }))).Track!.Keys[0];

            Assert.True(decoded.SyncLeftEquipment);
            Assert.False(decoded.SyncRightEquipment);
        }

        [Fact]
        public void RoundTrip_PosesWithNullTrack_ReadsBackAsAnEmptyTrack()
        {
            var decoded = RoundTrip(new PosesMessage(1, 0, 1, null));

            Assert.Null(decoded.Track!.Name);
            Assert.Empty(decoded.Track.Joints);
            Assert.Empty(decoded.Track.Keys);

            // A null track must reproduce as the empty-light shape too, or
            // encode can no longer round-trip everything decode can produce.
            Assert.Empty(decoded.Track.Lights);
        }

        // Every field asserted against a distinct value. The three durations are
        // the trap here: same type, adjacent on the wire, and transposing stable
        // with fade changes how a flash decays without changing any count.
        [Fact]
        public void RoundTrip_Poses_PreservesEveryFieldOfAWeaponLight()
        {
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack("u", new[] { "j" }, null, new[] { LightKey(1) })));

            var light = Assert.Single(decoded.Track!.Lights);
            Assert.Equal(0.25f, light.Time);
            Assert.Equal("socket_1", light.Socket);
            Assert.Equal(new[] { 2f, 3f, 4f }, new[] { light.Position.X, light.Position.Y, light.Position.Z });
            Assert.Equal(
                new[] { 0.15f, 0.25f, 0.35f, 1f },
                new[] { light.Colour.X, light.Colour.Y, light.Colour.Z, light.Colour.W });
            Assert.Equal(7f, light.Intensity);
            Assert.Equal(1.02f, light.DurationBuildup);
            Assert.Equal(1.03f, light.DurationStable);
            Assert.Equal(1.04f, light.DurationFade);
        }

        // Lights travel beside the poses, not instead of them: a track must
        // arrive carrying both, or a firing mech loses either its walk or its
        // muzzle flash depending on which the codec dropped.
        [Fact]
        public void RoundTrip_Poses_CarriesLightsAlongsideTheKeys()
        {
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack(
                    "u",
                    new[] { "joint_0" },
                    new[] { new PoseKey(0f, false, false, new[] { new JointPose(default, default) }) },
                    new[] { LightKey(0), LightKey(1), LightKey(2) })));

            Assert.Single(decoded.Track!.Keys);
            Assert.Equal(3, decoded.Track.Lights.Count);
            Assert.Equal(
                new[] { "socket_0", "socket_1", "socket_2" },
                new[]
                {
                    decoded.Track.Lights[0].Socket,
                    decoded.Track.Lights[1].Socket,
                    decoded.Track.Lights[2].Socket,
                });
        }

        // A pose track with no lights is the ordinary case — a unit that walked
        // without firing — and must stay free.
        [Fact]
        public void RoundTrip_Poses_AUnitThatDidNotFireCarriesNoLights()
        {
            var decoded = RoundTrip(new PosesMessage(3, 0, 1, PoseTrack("unit_a", 4, 3)));

            Assert.Empty(decoded.Track!.Lights);
        }

        // --- stage C: reaction pings and melee trajectories ---

        private static MeleeTrajectory Melee(int seed) => new MeleeTrajectory(
            seed + 0.5f,
            seed + 1.5f,
            seed % 2 == 0,
            "shockwave_" + seed,
            new Vec3(seed, seed + 1, seed + 2),
            new Vec3(seed + 3, seed + 4, seed + 5));

        [Fact]
        public void RoundTrip_Poses_PreservesEveryFieldOfAMeleeTrajectory()
        {
            // Distinct values in every slot. The trap is the position pair:
            // same type, adjacent on the wire, and transposing them drags the
            // shockwave backwards along the swing without moving any count.
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack("u", new[] { "j" }, null, null, null, new[] { Melee(1) })));

            var melee = Assert.Single(decoded.Track!.Melees);
            Assert.Equal(1.5f, melee.TimeStart);
            Assert.Equal(2.5f, melee.TimeEnd);
            Assert.False(melee.PartUsed);
            Assert.Equal("shockwave_1", melee.ShockwaveKey);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { melee.PosStart.X, melee.PosStart.Y, melee.PosStart.Z });
            Assert.Equal(new[] { 4f, 5f, 6f }, new[] { melee.PosEnd.X, melee.PosEnd.Y, melee.PosEnd.Z });
        }

        [Fact]
        public void RoundTrip_Poses_PreservesReactionPingsInOrder()
        {
            // Order is a precondition, not a nicety: "the latest ping" is read
            // as "the last one at or before the cursor", which is only the
            // newest while the list stays ascending.
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack("u", new[] { "j" }, null, null, new[] { 0.5f, 1.5f, 2.5f })));

            Assert.Equal(new[] { 0.5f, 1.5f, 2.5f }, decoded.Track!.Reactions);
        }

        [Fact]
        public void RoundTrip_Poses_CarriesPingsAndMeleesAlongsideKeysAndLights()
        {
            // All four collections at once. Each is length-prefixed and read in
            // a fixed order, so a misplaced count is not a corrupt field but a
            // misparse of everything after it.
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack(
                    "u",
                    new[] { "joint_0" },
                    new[] { new PoseKey(0f, false, false, new[] { new JointPose(default, default) }) },
                    new[] { LightKey(0), LightKey(1) },
                    new[] { 4f },
                    new[] { Melee(2), Melee(3) })));

            Assert.Single(decoded.Track!.Keys);
            Assert.Equal(2, decoded.Track.Lights.Count);
            Assert.Equal(new[] { 4f }, decoded.Track.Reactions);
            Assert.Equal(2, decoded.Track.Melees.Count);
            Assert.Equal(
                new[] { "shockwave_2", "shockwave_3" },
                new[] { decoded.Track.Melees[0].ShockwaveKey, decoded.Track.Melees[1].ShockwaveKey });
        }

        [Fact]
        public void RoundTrip_Poses_AUnitThatNeitherPingedNorSwungCarriesNeither()
        {
            var decoded = RoundTrip(new PosesMessage(3, 0, 1, PoseTrack("unit_a", 4, 3)));

            Assert.Empty(decoded.Track!.Reactions);
            Assert.Empty(decoded.Track.Melees);
        }

        [Fact]
        public void RoundTrip_PosesWithNullTrack_ReadsBackWithNeitherPingsNorMelees()
        {
            var decoded = RoundTrip(new PosesMessage(1, 0, 1, null));

            Assert.Empty(decoded.Track!.Reactions);
            Assert.Empty(decoded.Track.Melees);
        }

        [Fact]
        public void RoundTrip_Poses_PreservesANullShockwaveKey()
        {
            var decoded = RoundTrip(new PosesMessage(
                1, 0, 1,
                new UnitPoseTrack(
                    "u", new[] { "j" }, null, null, null,
                    new[] { new MeleeTrajectory(0f, 1f, true, null, default, default) })));

            Assert.Null(Assert.Single(decoded.Track!.Melees).ShockwaveKey);
        }

        [Fact]
        public void Decode_PosesWithTooManyReactionPings_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Poses);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteString("u");
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxReactionPingsPerUnit + 1);

            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_PosesWithTooManyMelees_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Poses);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteString("u");
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxMeleesPerUnit + 1);

            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_PosesWithTooManyWeaponLights_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Poses);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteString("u");
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxLightKeysPerUnit + 1);

            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_PosesWithTooManyParts_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Poses);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxPosePartsPerTurn + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_PoseTrackWithTooManyJoints_Throws()
        {
            var writer = PosesHeader();
            writer.WriteString("u");
            writer.WriteInt32(PbjMessageCodec.MaxJointsPerPose + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_PoseTrackWithTooManyKeys_Throws()
        {
            var writer = PosesHeader();
            writer.WriteString("u");
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxPoseKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        // A key claiming more joints than the cap, even though the track's own
        // name list is empty. Bounding the key against the cap rather than
        // against that list is what keeps a disagreeing sender from choosing our
        // allocation size.
        [Fact]
        public void Decode_PoseKeyWithTooManyJoints_Throws()
        {
            var writer = PosesHeader();
            writer.WriteString("u");
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            writer.WriteSingle(0f);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteInt32(PbjMessageCodec.MaxJointsPerPose + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        private static PbjWriter PosesHeader()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Poses);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            return writer;
        }

        // The size claim the one-track-per-message decision rests on — and note
        // the names are at their cap too, which the M6 sibling above does NOT
        // do. Its "pb_mech_00" names hide the fact that PbjWriter would accept a
        // 4096-byte one; with names at that limit the analogous keyframe bound
        // does not actually hold. An oversize frame is not a local failure, it
        // is a PbjProtocolException on the RECEIVER, which drops the sender as
        // malformed — so this has to be shown at the caps, not near them.
        [Fact]
        public void Encode_PosesAtEveryCapIncludingNames_StaysUnderTheFrameLimit()
        {
            var longName = new string('j', PbjMessageCodec.MaxPoseNameLength);
            var joints = new string[PbjMessageCodec.MaxJointsPerPose];
            for (var i = 0; i < joints.Length; i++)
            {
                joints[i] = longName;
            }

            var keys = new PoseKey[PbjMessageCodec.MaxPoseKeysPerTrack];
            for (var k = 0; k < keys.Length; k++)
            {
                var values = new JointPose[PbjMessageCodec.MaxJointsPerPose];
                for (var j = 0; j < values.Length; j++)
                {
                    values[j] = new JointPose(new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f));
                }
                keys[k] = new PoseKey(k, true, true, values);
            }

            var bytes = PbjMessageCodec.Encode(new PosesMessage(
                1, 0, PbjMessageCodec.MaxPosePartsPerTurn,
                new UnitPoseTrack(longName, joints, keys)));

            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a fully capped pose part was {bytes.Length} bytes, over the frame limit");
        }

        // --- replayed effects (M14) ---

        // Exhaustive on purpose. A swapped position and rotation is a
        // projectile flying sideways and a swapped scale is an effect nobody
        // can see, and no count, log line or round-trip-the-type test would
        // show either.
        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfAStandaloneTrack()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(9, 2, 5, new AssetCapture(
                new[] { StandaloneAsset(11) }, null, null)));

            Assert.Equal(9, decoded.Turn);
            Assert.Equal(2, decoded.PartIndex);
            Assert.Equal(5, decoded.PartCount);

            var track = Assert.Single(decoded.Assets.Standalone);
            Assert.Equal(11, track.Id);
            Assert.Equal("fx_impact_11", track.Head.AssetKey);
            Assert.Equal(1.5f, track.Head.TimeStart);
            Assert.Equal(3.25f, track.Head.TimeEnd);
            Assert.Null(track.Head.Hue);
            Assert.Null(track.Head.Colour);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { track.Position.X, track.Position.Y, track.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { track.Rotation.X, track.Rotation.Y, track.Rotation.Z, track.Rotation.W });
            Assert.Equal(new[] { 1.5f, 1.5f, 1.5f }, new[] { track.Scale.X, track.Scale.Y, track.Scale.Z });
            Assert.Equal(
                new[] { 4f, 5f, 6f, 0.75f },
                new[]
                {
                    track.VelocityAndDecay.X, track.VelocityAndDecay.Y,
                    track.VelocityAndDecay.Z, track.VelocityAndDecay.W,
                });
            Assert.Equal(
                new[] { 7f, 8f, 9f },
                new[] { track.PositionLocal.X, track.PositionLocal.Y, track.PositionLocal.Z });
        }

        // Ten fields, each read back into a rebuilt AraTrail.Point by the game's
        // own ApplyTime. Asserted one at a time and against distinct values,
        // because the five Vec3s are the same type in the same run and the codec
        // would happily swap two of them forever.
        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfATrailPoint()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(4, 0, 1, new AssetCapture(
                null, new[] { TrailedProjectile(7, 2, 1) }, null)));

            var point = Assert.Single(Assert.Single(decoded.Assets.Projectiles).Trail);
            Assert.Equal(0f, point.Time);
            Assert.Equal(0.5f, point.TimeEnd);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { point.Position.X, point.Position.Y, point.Position.Z });
            Assert.Equal(new[] { 10f, 11f, 12f }, new[] { point.Velocity.X, point.Velocity.Y, point.Velocity.Z });
            Assert.Equal(
                new[] { 20f, 21f, 22f },
                new[] { point.PerlinDirection.X, point.PerlinDirection.Y, point.PerlinDirection.Z });
            Assert.Equal(new[] { 30f, 31f, 32f }, new[] { point.Tangent.X, point.Tangent.Y, point.Tangent.Z });
            Assert.Equal(new[] { 40f, 41f, 42f }, new[] { point.Normal.X, point.Normal.Y, point.Normal.Z });
            Assert.Equal(
                new[] { 0.11f, 0.22f, 0.33f, 0.44f },
                new[] { point.Colour.X, point.Colour.Y, point.Colour.Z, point.Colour.W });
            Assert.Equal(0.9f, point.Thickness);
            Assert.Equal(0.05f, point.Texcoord);
        }

        // Emission order is the ribbon's geometry: SetPoints treats the last
        // point as the head. A codec that reversed the list would pass every
        // per-field assertion above and still turn every trail inside out.
        [Fact]
        public void RoundTrip_ReplayAssets_KeepsTrailPointsInEmissionOrder()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(4, 0, 1, new AssetCapture(
                null, new[] { TrailedProjectile(7, 2, 4) }, null)));

            var trail = Assert.Single(decoded.Assets.Projectiles).Trail;
            Assert.Equal(4, trail.Count);
            Assert.Equal(new[] { 0f, 0.1f, 0.2f, 0.3f }, new[]
            {
                trail[0].Time, trail[1].Time, trail[2].Time, trail[3].Time,
            });
        }

        // The common case by a wide margin — 106 of 109 measured projectiles —
        // and the one that must not cost anything or break stage A's shape.
        [Fact]
        public void RoundTrip_ReplayAssets_AProjectileWithoutATrailStaysEmpty()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, new[] { ProjectileAsset(7, 2) }, null)));

            Assert.Empty(Assert.Single(decoded.Assets.Projectiles).Trail);
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfAProjectileKey()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, new[] { ProjectileAsset(7, 2) }, null)));

            var track = Assert.Single(decoded.Assets.Projectiles);
            Assert.Equal(7, track.Id);
            Assert.Equal("fx_bullet_7", track.Head.AssetKey);
            Assert.Equal(new[] { 2f, 2f, 2f }, new[] { track.Scale.X, track.Scale.Y, track.Scale.Z });
            Assert.Equal(2, track.Keys.Count);

            var key = track.Keys[1];
            Assert.Equal(0.05f, key.Time);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { key.Position.X, key.Position.Y, key.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W });
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesEveryFieldOfABeamKey()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                null, null, new[] { BeamAsset(8, 2) })));

            var track = Assert.Single(decoded.Assets.Beams);
            Assert.Equal(8, track.Id);
            Assert.Equal("fx_beam_8", track.Head.AssetKey);
            Assert.Equal(2, track.Keys.Count);

            var key = track.Keys[1];
            Assert.Equal(0.05f, key.Time);
            Assert.Equal(new[] { 1f, 2f, 3f }, new[] { key.Position.X, key.Position.Y, key.Position.Z });
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 0.4f },
                new[] { key.Rotation.X, key.Rotation.Y, key.Rotation.Z, key.Rotation.W });
            Assert.Equal(
                new[] { 0.5f, 0.25f, 3f },
                new[] { key.Parameters.X, key.Parameters.Y, key.Parameters.Z });
        }

        // Absence and zero are different instructions: an absent hue leaves the
        // prefab's own alone, a hue of zero flattens it. A sentinel float could
        // not tell them apart inside a 0..1 block, which is why both blocks are
        // present-flagged.
        [Fact]
        public void RoundTrip_ReplayAssets_KeepsAnAbsentHueApartFromAZeroOne()
        {
            var zero = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1, hue: 0f) }, null, null)));
            var absent = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1) }, null, null)));

            Assert.Equal(0f, zero.Assets.Standalone[0].Head.Hue);
            Assert.Null(absent.Assets.Standalone[0].Head.Hue);
        }

        [Fact]
        public void RoundTrip_ReplayAssets_PreservesBothEndsOfAColour()
        {
            var colour = new AssetColour(
                new Vec4(0.1f, 0.2f, 0.3f, 1f), new Vec4(0.9f, 0.8f, 0.7f, 0.5f));

            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, new AssetCapture(
                new[] { StandaloneAsset(1, hue: 0.25f, colour: colour) }, null, null)));

            var head = decoded.Assets.Standalone[0].Head;
            Assert.Equal(0.25f, head.Hue);
            Assert.NotNull(head.Colour);
            Assert.Equal(
                new[] { 0.1f, 0.2f, 0.3f, 1f },
                new[]
                {
                    head.Colour!.Value.From.X, head.Colour.Value.From.Y,
                    head.Colour.Value.From.Z, head.Colour.Value.From.W,
                });
            Assert.Equal(
                new[] { 0.9f, 0.8f, 0.7f, 0.5f },
                new[]
                {
                    head.Colour.Value.To.X, head.Colour.Value.To.Y,
                    head.Colour.Value.To.Z, head.Colour.Value.To.W,
                });
        }

        [Fact]
        public void RoundTrip_ReplayAssetsWithNoCapture_ReadsBackAsAnEmptyOne()
        {
            var decoded = RoundTrip(new ReplayAssetsMessage(1, 0, 1, null));

            Assert.True(decoded.Assets.IsEmpty);
        }

        [Fact]
        public void Decode_ReplayAssetsWithTooManyParts_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.ReplayAssets);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(PbjMessageCodec.MaxAssetPartsPerTurn + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Decode_ReplayAssetsWithTooManyTracksOfOneKind_Throws(int kind)
        {
            var writer = ReplayAssetsHeader();
            for (var i = 0; i < kind; i++)
            {
                writer.WriteInt32(0);
            }
            writer.WriteInt32(PbjMessageCodec.MaxAssetsPerPart + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_AProjectileWithTooManyKeys_Throws()
        {
            var writer = ReplayAssetsHeader();
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            WriteAssetTrackHeadForTest(writer);
            writer.WriteSingle(1f);
            writer.WriteSingle(1f);
            writer.WriteSingle(1f);
            writer.WriteInt32(PbjMessageCodec.MaxAssetKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_ABeamWithTooManyKeys_Throws()
        {
            var writer = ReplayAssetsHeader();
            writer.WriteInt32(0);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            WriteAssetTrackHeadForTest(writer);
            writer.WriteInt32(PbjMessageCodec.MaxAssetKeysPerTrack + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        private static PbjWriter ReplayAssetsHeader()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.ReplayAssets);
            writer.WriteInt32(1);
            writer.WriteInt32(0);
            writer.WriteInt32(1);
            return writer;
        }

        // id, then the head: key, start, end, and both present-flags clear.
        private static void WriteAssetTrackHeadForTest(PbjWriter writer)
        {
            writer.WriteInt32(1);
            writer.WriteString("fx");
            writer.WriteSingle(0f);
            writer.WriteSingle(1f);
            writer.WriteBool(false);
            writer.WriteBool(false);
        }

        // The size claim the counted-parts decision rests on, proved at three
        // full lists rather than at the one a sender packs — a decoder cannot
        // assume the sender packed the way we do, so the bound has to hold for
        // anything it will accept. Names are at their cap too, for the reason
        // the pose sibling above spells out.
        [Fact]
        public void Encode_ReplayAssetsAtEveryCap_StaysUnderTheFrameLimit()
        {
            var key = new string('k', PbjMessageCodec.MaxAssetKeyLength);
            var colour = new AssetColour(
                new Vec4(1f, 1f, 1f, 1f), new Vec4(1f, 1f, 1f, 1f));
            var head = new AssetTrackHead(key, 0f, 5f, 0.5f, colour);

            var standalone = new StandaloneAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < standalone.Length; i++)
            {
                standalone[i] = new StandaloneAssetTrack(
                    i, head, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f),
                    new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 0f), new Vec3(0f, 0f, 0f));
            }

            var transforms = new TransformKey[PbjMessageCodec.MaxAssetKeysPerTrack];
            for (var i = 0; i < transforms.Length; i++)
            {
                transforms[i] = new TransformKey(i, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f));
            }
            // Trails at their cap too, and this is the term that actually
            // decides the bound: a trail point is 92 bytes against a transform
            // key's 32, so MaxTrailPointsPerTrack is what stands between a full
            // part and the 1 MiB frame limit. Stage A's version of this test
            // predates trails, and leaving it unchanged would have gone on
            // "proving" a bound for a message shape we no longer send.
            var trail = new TrailKey[PbjMessageCodec.MaxTrailPointsPerTrack];
            for (var i = 0; i < trail.Length; i++)
            {
                trail[i] = new TrailKey(
                    i, i + 1,
                    new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f),
                    new Vec3(1f, 1f, 1f), new Vec3(1f, 1f, 1f),
                    new Vec4(1f, 1f, 1f, 1f), 1f, 1f);
            }
            var projectiles = new ProjectileAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < projectiles.Length; i++)
            {
                projectiles[i] = new ProjectileAssetTrack(
                    i, head, new Vec3(1f, 1f, 1f), transforms, trail);
            }

            var beamKeys = new BeamKey[PbjMessageCodec.MaxAssetKeysPerTrack];
            for (var i = 0; i < beamKeys.Length; i++)
            {
                beamKeys[i] = new BeamKey(
                    i, new Vec3(1f, 1f, 1f), new Vec4(0f, 0f, 0f, 1f), new Vec3(1f, 1f, 1f));
            }
            var beams = new BeamAssetTrack[PbjMessageCodec.MaxAssetsPerPart];
            for (var i = 0; i < beams.Length; i++)
            {
                beams[i] = new BeamAssetTrack(i, head, beamKeys);
            }

            var bytes = PbjMessageCodec.Encode(new ReplayAssetsMessage(
                1, 0, PbjMessageCodec.MaxAssetPartsPerTurn,
                new AssetCapture(standalone, projectiles, beams)));

            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a fully capped asset part was {bytes.Length} bytes, over the frame limit");

            // Pinned, not merely bounded. A fully capped part measures ~712 KiB
            // of the 1 MiB limit, so the real headroom is about 1.44x and the
            // trail term alone is half the message — raising
            // MaxTrailPointsPerTrack past ~112 breaches the frame. "Under the
            // limit" would still pass at 99% full and tell nobody the next cap
            // bump is the one that breaks decode.
            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength * 3 / 4,
                $"a fully capped asset part was {bytes.Length} bytes, which has eaten "
                    + "the headroom the trail cap was sized to leave");
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
