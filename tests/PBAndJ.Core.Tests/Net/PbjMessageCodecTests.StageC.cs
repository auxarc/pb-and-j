using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The stage C additions -- reaction pings and melee trajectories -- and, kept
    // with them because the author's banner draws the line here, every cap the
    // poses message enforces on decode: pings, melees, weapon lights, parts,
    // joints and keys.
    // The banner names only the additions. Its section had grown past it long
    // before this split, and moving the boundary would have been a judgement
    // about the tests rather than about the file's length.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
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
        // the names are at their cap too, which the M6 sibling does NOT do:
        // Encode_KeyframesAtBothCaps_StaysUnderTheFrameLimit, in .Keyframes.cs.
        // Its "pb_mech_00" names hide the fact that PbjWriter would accept a
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
    }
}
