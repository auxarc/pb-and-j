using System;
using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PoseDigestTests
    {
        private static PoseBoneEntry Bone(
            string? unit, int index, float p = 1f, float r = 0.5f)
        {
            return new PoseBoneEntry(
                unit, index, new Vec3(p, p + 1f, p + 2f), new Vec4(r, r, r, 1f));
        }

        private static string Digest(params PoseBoneEntry[] bones)
        {
            return PoseDigest.Compute(bones).Digest;
        }

        [Fact]
        public void Compute_OfNothing_CountsNothing()
        {
            var (count, digest) = PoseDigest.Compute(new PoseBoneEntry[0]);

            Assert.Equal(0, count);
            Assert.Equal(16, digest.Length);
        }

        [Fact]
        public void Compute_IsIndependentOfEnumerationOrder()
        {
            // Two machines have no reason to agree on the order a group
            // enumerates in, and never did.
            var a = Digest(Bone("a", 0), Bone("a", 1), Bone("b", 0));
            var b = Digest(Bone("b", 0), Bone("a", 1), Bone("a", 0));

            Assert.Equal(a, b);
        }

        [Fact]
        public void Compute_OfTwoJointsThatSwapped_Differs()
        {
            // 🔑 THE DEFECT THIS EXISTS FOR. A remap that transposed two joints
            // puts a mech's elbow on its knee while every counter stays
            // identical. A digest over an unordered bag of bone values would be
            // the same across this swap, which is why the bone index is part of
            // the key rather than only a tiebreak.
            var straight = Digest(Bone("a", 0, p: 1f), Bone("a", 1, p: 9f));
            var swapped = Digest(Bone("a", 0, p: 9f), Bone("a", 1, p: 1f));

            Assert.NotEqual(straight, swapped);
        }

        [Fact]
        public void Compute_OfADifferentRotation_Differs()
        {
            // Rotation is where a posing defect actually shows: a transposed
            // joint keeps its position and changes its orientation.
            Assert.NotEqual(Digest(Bone("a", 0, r: 0.5f)), Digest(Bone("a", 0, r: 0.6f)));
        }

        [Fact]
        public void Compute_OfADifferentPosition_Differs()
        {
            Assert.NotEqual(Digest(Bone("a", 0, p: 1f)), Digest(Bone("a", 0, p: 2f)));
        }

        [Fact]
        public void Compute_OfADifferentUnitName_Differs()
        {
            Assert.NotEqual(Digest(Bone("a", 0)), Digest(Bone("b", 0)));
        }

        [Fact]
        public void Compute_OfADifferentBoneIndex_Differs()
        {
            Assert.NotEqual(Digest(Bone("a", 0)), Digest(Bone("a", 1)));
        }

        [Fact]
        public void Compute_OfTheSameSkeletonTwice_Matches()
        {
            // The other half of every difference test above: a digest that
            // changed on identical input would report divergence for ever.
            Assert.Equal(Digest(Bone("a", 0), Bone("a", 1)),
                         Digest(Bone("a", 0), Bone("a", 1)));
        }

        [Fact]
        public void Compute_SkipsNamelessUnits_FromTheCountAsWellAsTheDigest()
        {
            // The count is what a reader checks to know the reading is not
            // vacuous, so a nameless unit must not be able to inflate it.
            var (count, digest) = PoseDigest.Compute(
                new[] { Bone("a", 0), Bone(null, 1), Bone(string.Empty, 2) });

            Assert.Equal(1, count);
            Assert.Equal(Digest(Bone("a", 0)), digest);
        }

        [Fact]
        public void Compute_OfAMovedBoundary_Differs()
        {
            // The separator's job. Without it the fields form one flat byte
            // stream and a set that merely moves a boundary reads as identical.
            Assert.NotEqual(Digest(Bone("ab", 0)), Digest(Bone("a", 0)));
        }

        [Fact]
        public void Compute_OfSubMillimetreMovement_IsUnchanged()
        {
            // The quantum, asserted rather than implied: below it, two readings
            // of the same pose must not disagree over float noise.
            Assert.Equal(Digest(Bone("a", 0, p: 1f)),
                         Digest(Bone("a", 0, p: 1.0000001f)));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Compute_OfANonFiniteValue_DigestsRatherThanThrows(float bad)
        {
            // Two machines in the same broken state have to agree that they are.
            var (count, digest) = PoseDigest.Compute(
                new[] { new PoseBoneEntry("a", 0, new Vec3(bad, 0f, 0f), default) });

            Assert.Equal(1, count);
            Assert.Equal(16, digest.Length);
        }

        [Fact]
        public void Compute_OfValuesPastTheIntegerRange_Saturates()
        {
            // Both clamps, and they must not collapse into each other.
            var high = Digest(new PoseBoneEntry("a", 0, new Vec3(1e9f, 0f, 0f), default));
            var low = Digest(new PoseBoneEntry("a", 0, new Vec3(-1e9f, 0f, 0f), default));

            Assert.NotEqual(high, low);
        }

        [Fact]
        public void Scales_AreTheOnesTheRemarksClaim()
        {
            // The remarks argue from these numbers, so a change to them has to
            // break a test rather than quietly invalidate the reasoning.
            Assert.Equal(1000f, PoseDigest.PositionScale);
            Assert.Equal(10000f, PoseDigest.RotationScale);
        }

        [Fact]
        public void Entry_KeepsWhatItWasGiven()
        {
            var e = new PoseBoneEntry("a", 3, new Vec3(1f, 2f, 3f), new Vec4(4f, 5f, 6f, 7f));

            Assert.Equal("a", e.Unit);
            Assert.Equal(3, e.Bone);
            Assert.Equal(2f, e.Position.Y);
            Assert.Equal(7f, e.Rotation.W);
        }
    }
}
