using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// One part's damage as a digest reads it: the join key and the two values.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="PartState"/>. That type is a wire record
    /// belonging to one unit, so it carries no unit name; this one is a flat
    /// entry in a set that spans every unit on the machine, and is built from a
    /// live ECS on <b>both</b> machines rather than from anything received.
    /// </remarks>
    public readonly struct PartStateEntry
    {
        public PartStateEntry(string? unit, string? socket, float integrity, float barrier)
        {
            Unit = unit;
            Socket = socket;
            Integrity = integrity;
            Barrier = barrier;
        }

        public string? Unit { get; }
        public string? Socket { get; }
        public float Integrity { get; }
        public float Barrier { get; }
    }

    /// <summary>
    /// A stable fingerprint of every part's damage, for comparing two running
    /// games against each other. M16.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The point is that it is computed identically on both machines from
    /// each machine's OWN live ECS</b>, so it verifies the sync end to end rather
    /// than verifying our belief about it. Nothing on the wire is consulted. Host
    /// <c>partState</c> must equal client <c>partState</c>, in the way
    /// <see cref="AssetPoolDigest"/>'s reading was compared for M14.
    /// <para>
    /// ⚠️ <b>It is only meaningful at a stated moment, and a reader who does not
    /// know that will be told the sync is broken when it is working.</b> The
    /// client holds a turn's damage until its window settles, so it legitimately
    /// disagrees during a replay, throughout a held frame
    /// (<c>pbj.fx-hold</c> pins the cursor and the settle never fires), and for
    /// the whole of a turn whose keyframes never arrived. Compare between turns,
    /// with no hold active.
    /// </para>
    /// <para>
    /// ⚠️ <b>And it must be read beside a non-zero applied count.</b> Two machines
    /// with no damage at all agree perfectly with the sync entirely unwired,
    /// which is the vacuous pass this project has shipped instruments for before.
    /// A <see cref="Compute"/> <c>Count</c> difference is a roster difference
    /// rather than this defect; the bridge already logs those separately.
    /// </para>
    /// <para>
    /// Deliberately NOT folded into <see cref="StateDigest"/>. That digest is
    /// carried on the wire and compared automatically, so a socket the sync
    /// legitimately skipped would become a permanent false mismatch reported
    /// every turn. This one is a reading a human asks for at a moment they chose.
    /// </para>
    /// </remarks>
    public static class PartStateDigest
    {
        // FNV-1a 64, matching AssetPoolDigest rather than StateDigest's 32-bit
        // form, for the reason that governs both: GetHashCode is not stable
        // across processes, and a number whose entire purpose is comparison
        // between two machines has to be stable by construction.
        private const ulong Basis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // Hashed between the two halves of the join key and after every entry.
        // Without it the strings form one flat byte stream and any set that
        // merely moves a boundary reads as identical.
        private const byte Separator = 0x0A;

        /// <summary>
        /// The quantum, matching <see cref="StateDigest.IntegrityScale"/>.
        /// </summary>
        /// <remarks>
        /// 0.001, which is also the game's own <c>RoughlyEqual</c> threshold for
        /// deciding one of these values moved at all
        /// (<c>DestructionRamp.ChangeEpsilon</c> records the same number). So the
        /// digest is insensitive to exactly the differences the game itself calls
        /// no difference.
        /// </remarks>
        public const float Scale = 1000f;

        /// <summary>
        /// How many parts were counted, and the digest over them.
        /// </summary>
        /// <remarks>
        /// Sorted ordinal by unit then socket before hashing, so the answer is a
        /// property of the set rather than of the order a group happened to
        /// enumerate in — the two machines have no reason to agree on that and
        /// never did.
        /// <para>
        /// Entries with no unit name or no socket are skipped, and skipped from
        /// the count as well as from the digest.
        /// </para>
        /// </remarks>
        public static (int Count, string Digest) Compute(IEnumerable<PartStateEntry> parts)
        {
            var kept = new List<PartStateEntry>();
            foreach (var part in parts)
            {
                if (!string.IsNullOrEmpty(part.Unit) && !string.IsNullOrEmpty(part.Socket))
                {
                    kept.Add(part);
                }
            }

            kept.Sort(Order);

            var hash = Basis;
            for (var i = 0; i < kept.Count; i++)
            {
                hash = MixText(hash, kept[i].Unit!);
                hash = MixByte(hash, Separator);
                hash = MixText(hash, kept[i].Socket!);
                hash = MixInt(hash, Quantise(kept[i].Integrity));
                hash = MixInt(hash, Quantise(kept[i].Barrier));
                hash = MixByte(hash, Separator);
            }

            return (kept.Count, hash.ToString("x16", CultureInfo.InvariantCulture));
        }

        private static int Order(PartStateEntry left, PartStateEntry right)
        {
            var byUnit = string.CompareOrdinal(left.Unit, right.Unit);
            return byUnit != 0 ? byUnit : string.CompareOrdinal(left.Socket, right.Socket);
        }

        private static ulong MixText(ulong hash, string value)
        {
            // UTF-8 explicitly, never the machine's default encoding, for the
            // same machine-independence reason as the ordinal sort.
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

        // The same shape as StateDigest's quantiser, and deliberately its own
        // copy rather than a shared helper: that one's scales define a wire
        // digest two peers compare automatically, and coupling this reading to it
        // would mean a change here could only be made by moving the protocol.
        // What must not drift is the sentinel, so it is stated rather than
        // implied — a NaN reaching a comparison has to digest, and two machines
        // in the same broken state have to agree they are.
        private static int Quantise(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return int.MinValue;
            }

            var scaled = value * Scale;
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
