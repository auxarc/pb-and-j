using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Keyframes (M6): the exact bytes, every key of every track, the visibility
    // stamps and their absence, the empty message, the two caps, and the
    // frame-limit bound those caps are sized against.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
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
    }
}
