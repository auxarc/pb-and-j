using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public partial class PbjMessageCodecTests
    {
        // The shared fixture. A helper lives here when more than one part calls
        // it, or when its only callers are in this file; a helper whose sole
        // user is one OTHER part (>=90% of its call sites) lives with that part
        // instead. That is why UnsupportedMessage is in .Guards.cs and
        // TrailedProjectile is in .ReplayAssets.cs, and why Melee, PosesHeader,
        // ReplayAssetsHeader and WriteAssetTrackHeadForTest never moved: they
        // were already sole-user, in the parts the author had put them in.
        //
        // The two theories below are the cross-cutting ones -- every message
        // type, through AllMessages -- which is why AllMessages is here too even
        // though nothing outside this file names it.
        //
        // The nine other parts, in the order the original file had them:
        //
        //   .Messages.cs          per-type field fidelity for everything that
        //                         has no part of its own
        //   .Snapshots.cs         the snapshot message
        //   .Keyframes.cs         keyframes (M6)
        //   .Poses.cs             poses (M8)
        //   .StageC.cs            reaction pings, melee trajectories, pose caps
        //   .ReplayAssets.cs      replayed effects (M14)
        //   .ScenarioTransfer.cs  scenario transfer (M9)
        //   .Guards.cs            what the codec refuses
        //   .Lobby.cs             the lobby (M11a)
        //
        // The boundaries are the author's own `// --- name ---` banners. Only one
        // section was divided further: "per-type field fidelity" ran to 634 lines,
        // over the 500-line gate, and the snapshot family was the seam inside it.
        //
        // Two things sit where the banners left them rather than where their
        // subject would put them, and both are called out in the files that hold
        // them: EncodedMessage_SurvivesFramingRoundTrip is in .Lobby.cs, and the
        // Welcome/Rejoin resume-token pair is in .ScenarioTransfer.cs. Neither
        // was moved, because the split's rule was to follow the author's seams.

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

    }
}
