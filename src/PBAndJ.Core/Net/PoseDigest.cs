using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One bone as a digest reads it: whose it is, which one, and where.
    /// </summary>
    /// <remarks>
    /// Local space, because that is what <c>KeyframePlayer.ApplyPose</c> writes.
    /// A world-space reading would fold in the root transform and report a
    /// difference every time a unit merely stood somewhere else.
    /// </remarks>
    public readonly struct PoseBoneEntry
    {
        public PoseBoneEntry(string? unit, int bone, Vec3 position, Vec4 rotation)
        {
            Unit = unit;
            Bone = bone;
            Position = position;
            Rotation = rotation;
        }

        /// <summary>The persistent entity's internal name — the join key.</summary>
        public string? Unit { get; }

        /// <summary>Index into the unit's own recorded bone list.</summary>
        public int Bone { get; }

        public Vec3 Position { get; }
        public Vec4 Rotation { get; }
    }

    /// <summary>
    /// A stable fingerprint of the skeleton this machine is currently drawing.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>It exists because nothing measured the one thing playback is for.</b>
    /// The drive state reports thirty-eight counters from the player —
    /// effects shown, lights fired, wrecks played, units posed — and every one of
    /// them can be identical while every mech on screen holds the wrong pose.
    /// <c>PosedUnits</c> counts units it wrote bones for, never whether the bones
    /// were right. So a refactor of the playback path could be "verified" against
    /// a full set of matching numbers and be wrong, which is the failure this
    /// project has shipped instruments for before.
    /// <para>
    /// ⚠️ <b>Only meaningful at a pinned cursor.</b> Playback advances with real
    /// time, so two runs sample different moments and a bare reading differs for
    /// reasons that are nobody's defect. <c>pbj.fx-hold t</c> clamps the cursor
    /// and holds it — its own remark says it exists so frames can be diffed
    /// rather than judged — and that is what makes this comparable. Read it held,
    /// at the same <c>t</c>, or do not read it.
    /// </para>
    /// <para>
    /// ⚠️ <b>And it must be read beside a non-zero count.</b> A machine posing
    /// nothing digests to the empty basis and matches another machine posing
    /// nothing, perfectly, with the whole player unwired. <see cref="Compute"/>
    /// returns the count for that reason and callers are expected to print it.
    /// </para>
    /// <para>
    /// Deliberately NOT on the wire and not folded into <see cref="StateDigest"/>.
    /// That digest is compared automatically every turn, and a client
    /// legitimately holds a different pose from the host for the whole of a
    /// window; carrying this there would report permanent false divergence. This
    /// is a reading a human asks for at a moment they chose.
    /// </para>
    /// </remarks>
    public static class PoseDigest
    {
        // FNV-1a 64, matching PartStateDigest and AssetPoolDigest for the reason
        // that governs all three: GetHashCode is not stable across processes, and
        // a number whose only purpose is comparison has to be stable by
        // construction.
        private const ulong Basis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // Hashed after the unit name and after every entry, so a set that merely
        // moves a boundary between fields cannot read as identical.
        private const byte Separator = 0x0A;

        /// <summary>Position quantum: 1mm, in a skeleton's local space.</summary>
        /// <remarks>
        /// Finer than <see cref="StateDigest.PositionScale"/>'s 0.1 world units,
        /// which is chosen for where a mech stands. Bone offsets are centimetres,
        /// so that quantum would round most of a skeleton to the same number and
        /// the digest would be blind to exactly what it is watching.
        /// </remarks>
        public const float PositionScale = 1000f;

        /// <summary>Rotation quantum: 1e-4 per quaternion component.</summary>
        /// <remarks>
        /// Rotation is where a posing defect actually shows — a transposed joint
        /// keeps its position and changes its orientation — and components live
        /// in [-1, 1], so a coarse quantum would collapse most of the range.
        /// </remarks>
        public const float RotationScale = 10000f;

        /// <summary>
        /// How many bones were counted, and the digest over them.
        /// </summary>
        /// <remarks>
        /// Sorted by unit ordinal then by bone index, so the answer is a property
        /// of the skeleton rather than of the order a group happened to enumerate
        /// in. <b>Bone index is part of the key rather than merely a tiebreak:</b>
        /// two joints that swapped their transforms are the defect this is for,
        /// and a digest over an unordered bag of bone values would be identical
        /// across that swap.
        /// <para>
        /// Entries with no unit name are skipped, and skipped from the count as
        /// well, so a nameless unit cannot inflate a reading that is being
        /// checked for being non-zero.
        /// </para>
        /// </remarks>
        public static (int Count, string Digest) Compute(IEnumerable<PoseBoneEntry> bones)
        {
            var kept = new List<PoseBoneEntry>();
            foreach (var bone in bones)
            {
                if (!string.IsNullOrEmpty(bone.Unit))
                {
                    kept.Add(bone);
                }
            }

            kept.Sort(Order);

            var hash = Basis;
            for (var i = 0; i < kept.Count; i++)
            {
                hash = MixText(hash, kept[i].Unit!);
                hash = MixByte(hash, Separator);
                hash = MixInt(hash, kept[i].Bone);
                hash = MixInt(hash, Quantise(kept[i].Position.X, PositionScale));
                hash = MixInt(hash, Quantise(kept[i].Position.Y, PositionScale));
                hash = MixInt(hash, Quantise(kept[i].Position.Z, PositionScale));
                hash = MixInt(hash, Quantise(kept[i].Rotation.X, RotationScale));
                hash = MixInt(hash, Quantise(kept[i].Rotation.Y, RotationScale));
                hash = MixInt(hash, Quantise(kept[i].Rotation.Z, RotationScale));
                hash = MixInt(hash, Quantise(kept[i].Rotation.W, RotationScale));
                hash = MixByte(hash, Separator);
            }

            return (kept.Count, hash.ToString("x16", CultureInfo.InvariantCulture));
        }

        private static int Order(PoseBoneEntry left, PoseBoneEntry right)
        {
            var byUnit = string.CompareOrdinal(left.Unit, right.Unit);
            return byUnit != 0 ? byUnit : left.Bone.CompareTo(right.Bone);
        }

        private static ulong MixText(ulong hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            for (var i = 0; i < bytes.Length; i++)
            {
                hash = MixByte(hash, bytes[i]);
            }
            return hash;
        }

        private static ulong MixInt(ulong hash, int value)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                hash = MixByte(hash, (byte)((value >> shift) & 0xFF));
            }
            return hash;
        }

        private static ulong MixByte(ulong hash, byte value)
        {
            unchecked
            {
                return (hash ^ value) * Prime;
            }
        }

        // Its own copy of the quantiser, like PartStateDigest's and for the same
        // reason: StateDigest's scales define a wire digest two peers compare
        // automatically, and coupling a local reading to it would mean a change
        // here could only be made by moving the protocol. The sentinel is stated
        // rather than implied — a NaN reaching a comparison has to digest, and
        // two machines in the same broken state have to agree that they are.
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
