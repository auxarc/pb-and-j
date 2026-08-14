using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class KeyframesTests
    {
        [Fact]
        public void TransformKey_KeepsEveryFieldItWasGiven()
        {
            var key = new TransformKey(1.5f, new Vec3(1f, 2f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f));

            Assert.Equal(1.5f, key.Time);
            Assert.Equal(1f, key.Position.X);
            Assert.Equal(2f, key.Position.Y);
            Assert.Equal(3f, key.Position.Z);
            Assert.Equal(0.1f, key.Rotation.X);
            Assert.Equal(0.2f, key.Rotation.Y);
            Assert.Equal(0.3f, key.Rotation.Z);
            Assert.Equal(0.4f, key.Rotation.W);
        }

        [Fact]
        public void UnitTrack_KeepsItsNameAndKeys()
        {
            var track = new UnitTrack("pb_mech_01", new[]
            {
                new TransformKey(0f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
            });

            Assert.Equal("pb_mech_01", track.Name);
            Assert.Single(track.Transforms);
        }

        // Mirrors ReadyMessage.Orders and SnapshotMessage.Units: a null list is
        // an empty one, so no caller has to null-check a collection.
        [Fact]
        public void UnitTrack_NullKeys_BecomeAnEmptyList()
        {
            Assert.Empty(new UnitTrack("pb_mech_01", null).Transforms);
        }

        [Fact]
        public void KeyframesMessage_NullTracks_BecomeAnEmptyList()
        {
            Assert.Empty(new KeyframesMessage(1, 0f, 5f, null).Tracks);
        }

        [Fact]
        public void KeyframesMessage_ReportsItsOwnType()
        {
            Assert.Equal(PbjMessageType.Keyframes,
                new KeyframesMessage(1, 0f, 5f, null).Type);
        }

        [Fact]
        public void KeyframesMessage_KeepsTheTurnAndTheWindow()
        {
            var message = new KeyframesMessage(7, 30f, 35f, new[]
            {
                new UnitTrack("pb_mech_01", null),
            });

            Assert.Equal(7, message.Turn);
            Assert.Equal(30f, message.WindowStart);
            Assert.Equal(35f, message.WindowEnd);
            Assert.Single(message.Tracks);
        }

        [Fact]
        public void KeyframeCapture_KeepsTheWindowAndTheTracks()
        {
            var capture = new KeyframeCapture(30f, 35f, new[] { new UnitTrack("pb_mech_01", null) });

            Assert.Equal(30f, capture.WindowStart);
            Assert.Equal(35f, capture.WindowEnd);
            Assert.Single(capture.Tracks);
        }

        [Fact]
        public void KeyframeCapture_NullTracks_BecomeAnEmptyList()
        {
            Assert.Empty(new KeyframeCapture(0f, 1f, null).Tracks);
        }

        // The shape a bridge hands back when the recorder had nothing — a turn
        // with prediction disabled, or a bridge that is a client and never
        // captures at all.
        [Fact]
        public void KeyframeCapture_None_IsAnEmptyZeroWidthWindow()
        {
            Assert.Empty(KeyframeCapture.None.Tracks);
            Assert.Equal(0f, KeyframeCapture.None.WindowStart);
            Assert.Equal(0f, KeyframeCapture.None.WindowEnd);
        }

        // The window travels explicitly rather than being inferred from the
        // first and last key: a unit that died early has a short track, and
        // every track must be played against the same time base.
        [Fact]
        public void KeyframesMessage_WindowIsIndependentOfTheKeysItCarries()
        {
            var message = new KeyframesMessage(7, 30f, 35f, new[]
            {
                new UnitTrack("dies_early", new[]
                {
                    new TransformKey(30f, new Vec3(0f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                    new TransformKey(31f, new Vec3(1f, 0f, 0f), new Vec4(0f, 0f, 0f, 1f)),
                }),
            });

            Assert.Equal(35f, message.WindowEnd);
            Assert.Equal(31f, message.Tracks[0].Transforms[1].Time);
        }

        // --- poses (M8) ---

        [Fact]
        public void JointPose_CarriesItsLocalPlacement()
        {
            var pose = new JointPose(new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f));

            Assert.Equal(2f, pose.Position.Y);
            Assert.Equal(1f, pose.Rotation.W);
        }

        [Fact]
        public void PoseKey_CarriesTheTwoEquipmentFlagsSeparately()
        {
            var key = new PoseKey(2.5f, true, false, new[] { new JointPose(default, default) });

            Assert.Equal(2.5f, key.Time);
            Assert.True(key.SyncLeftEquipment);
            Assert.False(key.SyncRightEquipment);
            Assert.Single(key.Joints);
        }

        [Fact]
        public void PoseKey_WithNoJoints_IsEmptyRatherThanNull()
        {
            Assert.Empty(new PoseKey(0f, false, false, null).Joints);
        }

        [Fact]
        public void UnitPoseTrack_CarriesTheHostsJointNamesInHostOrder()
        {
            var track = new UnitPoseTrack("pb_mech_01", new[] { "hip", "knee" }, null);

            Assert.Equal("pb_mech_01", track.Name);
            Assert.Equal(new[] { "hip", "knee" }, track.Joints);
            Assert.Empty(track.Keys);
        }

        [Fact]
        public void UnitPoseTrack_WithNothingAtAll_IsEmptyRatherThanNull()
        {
            var track = new UnitPoseTrack(null, null, null);

            Assert.Empty(track.Joints);
            Assert.Empty(track.Keys);
        }

        // Empty poses is a first-class outcome, not a failure: it is exactly
        // M6's behaviour, and it is the only fallback that keeps every unit
        // moving the same way as every other one.
        [Fact]
        public void KeyframeCapture_WithoutPoses_CarriesAnEmptySetRatherThanNull()
        {
            Assert.Empty(KeyframeCapture.None.Poses);
            Assert.Empty(new KeyframeCapture(0f, 5f, null).Poses);
        }

        [Fact]
        public void KeyframeCapture_CarriesPosesBesideTheTransformTracks()
        {
            var capture = new KeyframeCapture(
                15f, 20f,
                new[] { new UnitTrack("unit_a", null) },
                new[] { new UnitPoseTrack("unit_a", null, null) });

            Assert.Equal("unit_a", Assert.Single(capture.Tracks).Name);
            Assert.Equal("unit_a", Assert.Single(capture.Poses).Name);
        }

        // Both turn-dependent statics the client needs to seed the game's own
        // scrubber are already here: WindowStart IS the host's turnStartTime,
        // and previewTimeLimit is only WindowEnd rounded. The first design of
        // this stage put both on the wire a second time.
        [Fact]
        public void KeyframeCapture_WindowStartIsTheHostsTurnStartTime()
        {
            Assert.Equal(15f, new KeyframeCapture(15f, 20f, null).WindowStart);
        }
    }
}
