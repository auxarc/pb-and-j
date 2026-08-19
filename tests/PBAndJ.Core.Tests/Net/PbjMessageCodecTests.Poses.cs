using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Poses (M8): every field of every key, the two equipment flags that a single
    // combined field could not tell apart, a null track, and the weapon lights
    // that travel alongside the keys.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
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
    }
}
