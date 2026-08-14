using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class UnitSnapshotTests
    {
        private static UnitSnapshot Snapshot() =>
            new UnitSnapshot("pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                new Vec3(0f, 0f, -1f), 0.625f, true, 2.5f);

        [Fact]
        public void Constructor_RetainsEveryField()
        {
            var unit = Snapshot();
            Assert.Equal("pb_mech_01", unit.Name);
            Assert.Equal(2f, unit.Position.Y);
            Assert.Equal(0.3f, unit.Rotation.Z);
            Assert.Equal(0.4f, unit.Rotation.W);
            Assert.Equal(-1f, unit.Facing.Z);
            Assert.Equal(0.625f, unit.Integrity);
            Assert.True(unit.IsDead);
            Assert.Equal(2.5f, unit.DeathTime);
        }

        // The defaults describe a unit that is on the field and being drawn,
        // which is what almost every unit is. Stated as a test because they are
        // what a caller that forgets the new arguments will silently send.
        [Fact]
        public void Constructor_DefaultsToAVisibleDeployedUnit()
        {
            var unit = Snapshot();
            Assert.False(unit.IsHidden);
            Assert.False(unit.IsHiddenDetectable);
            Assert.True(unit.IsDeployed);
        }

        [Fact]
        public void Constructor_RetainsVisibility()
        {
            var unit = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f, false, 0f,
                isHidden: true, isHiddenDetectable: true, isDeployed: false);

            Assert.True(unit.IsHidden);
            Assert.True(unit.IsHiddenDetectable);
            Assert.False(unit.IsDeployed);
        }

        // Visibility is deliberately absent from the digest: it is presentational,
        // and correction cannot repair it, so including it would turn every
        // reveal into a permanent divergence report. Its absence is also exactly
        // why a client showing a different battlefield went unnoticed for weeks,
        // which is worth a test rather than a comment.
        [Fact]
        public void ToUnitState_IgnoresVisibility()
        {
            var visible = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f, false, 0f);
            var hidden = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f, false, 0f,
                isHidden: true, isHiddenDetectable: true, isDeployed: false);

            Assert.Equal(
                StateDigest.Compute(new[] { visible.ToUnitState() }),
                StateDigest.Compute(new[] { hidden.ToUnitState() }));
        }

        [Fact]
        public void Vec4_RetainsAllFourComponents()
        {
            var v = new Vec4(1f, 2f, 3f, 4f);
            Assert.Equal(1f, v.X);
            Assert.Equal(2f, v.Y);
            Assert.Equal(3f, v.Z);
            Assert.Equal(4f, v.W);
        }

        [Fact]
        public void ToUnitState_KeepsOnlyWhatTheDigestIsDefinedOver()
        {
            // Name, position and integrity — deliberately not rotation. A turret
            // mid-sweep would otherwise report a divergence that does not matter.
            var state = Snapshot().ToUnitState();
            Assert.Equal("pb_mech_01", state.Name);
            Assert.Equal(1f, state.Position.X);
            Assert.Equal(3f, state.Position.Z);
            Assert.Equal(0.625f, state.Integrity);
        }

        [Fact]
        public void ToUnitState_ProducesTheSameDigestAsAHandBuiltState()
        {
            // The projection is the only thing keeping a host's digest describing
            // exactly the units its snapshot carries.
            var viaSnapshot = StateDigest.Compute(new[] { Snapshot().ToUnitState() });
            var direct = StateDigest.Compute(new[]
            {
                new UnitState("pb_mech_01", new Vec3(1f, 2f, 3f), 0.625f),
            });

            Assert.Equal(direct, viaSnapshot);
        }
    }
}
