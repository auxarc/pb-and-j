using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class UnitSnapshotTests
    {
        private static UnitSnapshot Snapshot() =>
            new UnitSnapshot("pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0.1f, 0.2f, 0.3f, 0.4f),
                new Vec3(0f, 0f, -1f), 0.625f);

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
        }

        // Empty rather than null, so a client can walk it without a guard on
        // every frame of every window.
        [Fact]
        public void Constructor_DefaultsToAnIntactUnit()
        {
            Assert.Empty(Snapshot().WreckedParts);
        }

        [Fact]
        public void Constructor_DefaultsToAnUnwreckedUnit()
        {
            Assert.False(Snapshot().IsWrecked);
        }

        [Fact]
        public void Constructor_RetainsTheUnitWreck()
        {
            // A separate fact from the parts, and carried separately, because a
            // unit is wrecked while parts survive and can lose every part
            // without being wrecked itself. Only one of the two draws the
            // explosion.
            var unit = new UnitSnapshot(
                "pb_mech_01", default, default, default, 0.5f,
                isWrecked: true, wreckedAt: 7.5f);

            Assert.True(unit.IsWrecked);
            Assert.Equal(7.5f, unit.WreckedAt);
            Assert.Empty(unit.WreckedParts);
        }

        [Fact]
        public void Constructor_RetainsWreckedParts()
        {
            var unit = new UnitSnapshot(
                "pb_mech_01", default, default, default, 0.5f,
                wreckedParts: new[]
                {
                    new PartDestruction("equipment_left", 4.25f),
                    new PartDestruction("leg_right", -100f),
                });

            Assert.Equal(2, unit.WreckedParts.Count);
            Assert.Equal("equipment_left", unit.WreckedParts[0].Socket);
            Assert.Equal(4.25f, unit.WreckedParts[0].Time);
            // Negative is a real state, not an absence: it is the spawn sentinel
            // for a unit that arrived with the part already gone.
            Assert.Equal(-100f, unit.WreckedParts[1].Time);
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
                new Vec3(0f, 0f, -1f), 1f,
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
                new Vec3(0f, 0f, -1f), 1f);
            var hidden = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f,
                isHidden: true, isHiddenDetectable: true, isDeployed: false);

            Assert.Equal(
                StateDigest.Compute(new[] { visible.ToUnitState() }),
                StateDigest.Compute(new[] { hidden.ToUnitState() }));
        }

        // Absent, not zero. A host's player squad never gets an arrival time at
        // all (CombatScenarioSetupSystem deploys it without one), so "no arrival
        // time" is the majority case and has to be the default.
        [Fact]
        public void Constructor_DefaultsToNoArrivalTime()
        {
            var unit = Snapshot();
            Assert.False(unit.HasArrivalTime);
            Assert.Equal(0f, unit.ArrivalTime);
        }

        [Fact]
        public void Constructor_RetainsArrivalTime()
        {
            var unit = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f,
                hasArrivalTime: true, arrivalTime: 10.13f);

            Assert.True(unit.HasArrivalTime);
            Assert.Equal(10.13f, unit.ArrivalTime);
        }

        // Present-and-negative is a real state, not a malformed one, and it is
        // the one a client manufactures for itself. DataManagerSave adds an
        // arrival time to EVERY deployed unit on load, and the save writer
        // stamps -1 where the host had no component at all — so a client's whole
        // player squad reads has=true, value=-1 while the host reads absent.
        // Carrying presence separately from value is the only way to correct
        // that, which makes this pair worth pinning.
        [Fact]
        public void Constructor_RetainsAPresentNegativeArrivalTime()
        {
            var unit = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f,
                hasArrivalTime: true, arrivalTime: -1f);

            Assert.True(unit.HasArrivalTime);
            Assert.Equal(-1f, unit.ArrivalTime);
        }

        // Same argument as visibility above: the arrival time is presentational
        // and correction cannot repair it, so it must not move the digest.
        [Fact]
        public void ToUnitState_IgnoresArrivalTime()
        {
            var without = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f);
            var with = new UnitSnapshot(
                "pb_mech_01", new Vec3(1f, 2f, 3f), new Vec4(0f, 0f, 0f, 1f),
                new Vec3(0f, 0f, -1f), 1f,
                hasArrivalTime: true, arrivalTime: 10.13f);

            Assert.Equal(
                StateDigest.Compute(new[] { without.ToUnitState() }),
                StateDigest.Compute(new[] { with.ToUnitState() }));
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
