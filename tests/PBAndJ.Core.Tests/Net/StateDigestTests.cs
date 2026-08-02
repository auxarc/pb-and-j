using System;
using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class StateDigestTests
    {
        private static UnitState Unit(string name = "unit_a", float x = 1f, float y = 2f, float z = 3f, float integrity = 1f) =>
            new UnitState(name, new Vec3(x, y, z), integrity);

        [Fact]
        public void Compute_WithSameUnitsInDifferentOrder_ProducesSameDigest()
        {
            // Group iteration order is not stable across processes, so the
            // digest must not depend on it.
            var a = new[] { Unit("unit_a"), Unit("unit_b", 4f), Unit("unit_c", 7f) };
            var b = new[] { Unit("unit_c", 7f), Unit("unit_a"), Unit("unit_b", 4f) };
            Assert.Equal(StateDigest.Compute(a), StateDigest.Compute(b));
        }

        [Fact]
        public void Compute_WithDifferentPosition_ProducesDifferentDigest()
        {
            Assert.NotEqual(
                StateDigest.Compute(new[] { Unit(x: 1f) }),
                StateDigest.Compute(new[] { Unit(x: 5f) }));
        }

        [Fact]
        public void Compute_WithDifferentIntegrity_ProducesDifferentDigest()
        {
            Assert.NotEqual(
                StateDigest.Compute(new[] { Unit(integrity: 1f) }),
                StateDigest.Compute(new[] { Unit(integrity: 0.5f) }));
        }

        [Fact]
        public void Compute_WithDifferentName_ProducesDifferentDigest()
        {
            Assert.NotEqual(
                StateDigest.Compute(new[] { Unit("unit_a") }),
                StateDigest.Compute(new[] { Unit("unit_b") }));
        }

        [Fact]
        public void Compute_WithinRoundingTolerance_ProducesSameDigest()
        {
            // Sub-quantum jitter must not read as divergence.
            Assert.Equal(
                StateDigest.Compute(new[] { Unit(x: 1.000f) }),
                StateDigest.Compute(new[] { Unit(x: 1.004f) }));
        }

        [Fact]
        public void Compute_BeyondRoundingTolerance_ProducesDifferentDigest()
        {
            Assert.NotEqual(
                StateDigest.Compute(new[] { Unit(x: 1.00f) }),
                StateDigest.Compute(new[] { Unit(x: 1.31f) }));
        }

        [Fact]
        public void Compute_WithDuplicateUnits_DoesNotCancelThemOut()
        {
            // Combining by XOR would make an identical pair vanish; it must not.
            Assert.NotEqual(
                StateDigest.Compute(new[] { Unit(), Unit() }),
                StateDigest.Compute(Array.Empty<UnitState>()));
        }

        [Fact]
        public void Compute_WithEmptyInput_ProducesStableConstant()
        {
            Assert.Equal(StateDigest.Compute(Array.Empty<UnitState>()), StateDigest.Compute(new UnitState[0]));
            Assert.Equal(8, StateDigest.Compute(Array.Empty<UnitState>()).Length);
        }

        [Fact]
        public void Compute_ProducesEightLowercaseHexCharacters()
        {
            var digest = StateDigest.Compute(new[] { Unit(), Unit("unit_b", 9f) });
            Assert.Equal(8, digest.Length);
            Assert.Matches("^[0-9a-f]{8}$", digest);
        }

        [Fact]
        public void Compute_WithNonFiniteValues_IsStableRatherThanThrowing()
        {
            // A wrecked unit can end up with a NaN transform; that must produce
            // a digest, not an exception.
            var digest = StateDigest.Compute(new[]
            {
                new UnitState("unit_a", new Vec3(float.NaN, float.PositiveInfinity, float.NegativeInfinity), float.NaN),
            });
            Assert.Matches("^[0-9a-f]{8}$", digest);
        }

        [Fact]
        public void Compute_WithExtremeFiniteValues_SaturatesRatherThanOverflowing()
        {
            // Finite but huge: scaling overflows the float and then the int.
            // Must clamp, not wrap into a nonsense quantum.
            var high = StateDigest.Compute(new[] { Unit(x: float.MaxValue) });
            var low = StateDigest.Compute(new[] { Unit(x: float.MinValue) });
            Assert.Matches("^[0-9a-f]{8}$", high);
            Assert.Matches("^[0-9a-f]{8}$", low);
            Assert.NotEqual(high, low);
        }

        [Fact]
        public void Compute_WithNullUnitName_IsTreatedAsEmpty()
        {
            var digest = StateDigest.Compute(new[] { new UnitState(null, new Vec3(0f, 0f, 0f), 1f) });
            Assert.Matches("^[0-9a-f]{8}$", digest);
        }

        [Fact]
        public void Compute_WithNullUnits_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => StateDigest.Compute(null!));
            Assert.Equal("units", ex.ParamName);
        }

        [Fact]
        public void Compute_IsCultureIndependent()
        {
            // The host runs Mono under Wine, the harness .NET on Linux. A digest
            // built from formatted floats would differ between them; this pins
            // that it is built from integer quantisation instead.
            var units = new[] { Unit(x: 1.5f, integrity: 0.25f) };
            var invariant = StateDigest.Compute(units);

            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal(invariant, StateDigest.Compute(units));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }

        [Fact]
        public void Mix_IsStableAndDistinguishesInputs()
        {
            Assert.Equal(StateDigest.Mix("a:1:ally"), StateDigest.Mix("a:1:ally"));
            Assert.NotEqual(StateDigest.Mix("a:1:ally"), StateDigest.Mix("b:1:ally"));
            Assert.Equal(8, StateDigest.Mix("anything").Length);
        }

        [Fact]
        public void Mix_WithNull_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => StateDigest.Mix(null!));
            Assert.Equal("value", ex.ParamName);
        }

        [Fact]
        public void UnitState_RetainsFields()
        {
            var unit = new UnitState("unit_a", new Vec3(1f, 2f, 3f), 0.75f);
            Assert.Equal("unit_a", unit.Name);
            Assert.Equal(new Vec3(1f, 2f, 3f), unit.Position);
            Assert.Equal(0.75f, unit.Integrity);
        }
    }
}
