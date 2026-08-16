using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class UnitLightKeyTests
    {
        internal static UnitLightKey Sample(string? socket = "arm_left", float time = 1f)
        {
            return new UnitLightKey(
                time,
                socket,
                new Vec3(10f, 20f, 30f),
                new Vec4(0.1f, 0.2f, 0.3f, 1f),
                6f,
                0.05f,
                0.1f,
                0.2f);
        }

        // Read every getter, and read them against distinct values. The three
        // durations are the trap: they are the same type, they arrive adjacent,
        // and swapping stable with fade changes how long a flash lasts without
        // changing anything a count could see.
        [Fact]
        public void Constructor_KeepsEveryFieldInItsOwnSlot()
        {
            var key = Sample();

            Assert.Equal(1f, key.Time);
            Assert.Equal("arm_left", key.Socket);
            Assert.Equal(new Vec3(10f, 20f, 30f), key.Position);
            Assert.Equal(new Vec4(0.1f, 0.2f, 0.3f, 1f), key.Colour);
            Assert.Equal(6f, key.Intensity);
            Assert.Equal(0.05f, key.DurationBuildup);
            Assert.Equal(0.1f, key.DurationStable);
            Assert.Equal(0.2f, key.DurationFade);
        }
    }

    public class UnitPoseTrackLightsTests
    {
        private static UnitPoseTrack Track(IReadOnlyList<UnitLightKey>? lights)
        {
            return new UnitPoseTrack("unit_a", new[] { "j" }, null, lights);
        }

        // Same contract as the trail list, for the same reason: absence and
        // emptiness are one instruction, so the client never branches on null.
        [Fact]
        public void Lights_WhenNotSupplied_IsEmptyRatherThanNull()
        {
            var track = new UnitPoseTrack("unit_a", null, null);

            Assert.NotNull(track.Lights);
            Assert.Empty(track.Lights);
        }

        [Fact]
        public void Lights_AreKeptInOrder()
        {
            var track = Track(new[]
            {
                UnitLightKeyTests.Sample(time: 1f),
                UnitLightKeyTests.Sample(time: 2f),
            });

            Assert.Equal(2, track.Lights.Count);
            Assert.Equal(1f, track.Lights[0].Time);
            Assert.Equal(2f, track.Lights[1].Time);
        }
    }
}
