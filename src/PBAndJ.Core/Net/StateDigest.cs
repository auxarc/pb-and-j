using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>One unit's post-execution state, as far as the digest cares.</summary>
    public readonly struct UnitState
    {
        public UnitState(string? name, Vec3 position, float integrity)
        {
            Name = name;
            Position = position;
            Integrity = integrity;
        }

        public string? Name { get; }
        public Vec3 Position { get; }
        public float Integrity { get; }
    }

    /// <summary>
    /// Order-independent fingerprint of post-turn combat state, carried on
    /// <see cref="TurnCompleteMessage"/> so a client can detect divergence from
    /// the host before any state sync exists.
    /// </summary>
    /// <remarks>
    /// Built entirely from <b>integer quantisations</b>, never from formatted
    /// floats. The host runs Mono/net472 under Wine and the harness runs .NET 9
    /// on Linux; float-to-string differs between those runtimes, so a
    /// string-shaped digest would report permanent false divergence.
    /// <para>
    /// Per-unit hashes are combined by unchecked addition rather than XOR: XOR
    /// makes an identical pair cancel to zero, which would hide real state.
    /// </para>
    /// </remarks>
    public static class StateDigest
    {
        /// <summary>Position quantum: 0.1 world units.</summary>
        public const float PositionScale = 10f;

        /// <summary>Integrity quantum: 0.001.</summary>
        public const float IntegrityScale = 1000f;

        private const uint FnvOffsetBasis = 2166136261;
        private const uint FnvPrime = 16777619;

        public static string Compute(IReadOnlyList<UnitState> units)
        {
            if (units == null)
            {
                throw new ArgumentNullException(nameof(units));
            }

            unchecked
            {
                var total = FnvOffsetBasis;
                for (var i = 0; i < units.Count; i++)
                {
                    total += HashUnit(units[i]);
                }
                return total.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static uint HashUnit(UnitState unit)
        {
            unchecked
            {
                var hash = FnvOffsetBasis;
                var name = unit.Name ?? string.Empty;
                for (var i = 0; i < name.Length; i++)
                {
                    hash = (hash ^ name[i]) * FnvPrime;
                }
                hash = MixInt(hash, Quantise(unit.Position.X, PositionScale));
                hash = MixInt(hash, Quantise(unit.Position.Y, PositionScale));
                hash = MixInt(hash, Quantise(unit.Position.Z, PositionScale));
                hash = MixInt(hash, Quantise(unit.Integrity, IntegrityScale));
                return hash;
            }
        }

        private static uint MixInt(uint hash, int value)
        {
            unchecked
            {
                hash = (hash ^ (uint)(value & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((value >> 8) & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((value >> 16) & 0xFF)) * FnvPrime;
                hash = (hash ^ (uint)((value >> 24) & 0xFF)) * FnvPrime;
                return hash;
            }
        }

        /// <summary>
        /// Snaps a float to an integer multiple of 1/scale. Non-finite values
        /// collapse to a fixed sentinel: a wrecked unit can carry a NaN
        /// transform, and that must digest rather than throw.
        /// </summary>
        private static int Quantise(float value, float scale)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return int.MinValue;
            }

            var scaled = value * scale;
            if (scaled >= int.MaxValue)
            {
                return int.MaxValue;
            }
            if (scaled <= int.MinValue + 1)
            {
                return int.MinValue + 1;
            }
            return (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }
    }
}
