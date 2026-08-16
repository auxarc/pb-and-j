using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// A stable fingerprint of a set of asset pool keys, for comparing two
    /// installs against each other.
    /// </summary>
    /// <remarks>
    /// M14 plays a host's effects on a client by name, and "the names resolve on
    /// the client" currently rests entirely on the handshake refusing a
    /// mismatched game build and mod version. DLC and workshop content can
    /// diverge at identical versions, so that reasoning has a hole in it that
    /// only a comparison can close. Ten thousand keys are impractical to compare
    /// by eye; sixteen hex characters are not.
    /// <para>
    /// ⚠️ <b>A digest match does not prove the keys are playable.</b>
    /// <c>DataContainerAssetPool.OnAfterDeserialization</c> (<c>:60-66</c>) keeps
    /// its entry when <c>Resources.Load</c> fails — it warns and moves on with a
    /// null prefab — so two machines can agree on every key while one of them
    /// cannot instantiate a pool at all. Whatever reports this digest must report
    /// the null-prefab count beside it, or the answer is confidently wrong in
    /// exactly the case the comparison exists to catch.
    /// </para>
    /// </remarks>
    public static class AssetPoolDigest
    {
        // FNV-1a 64. Chosen over string.GetHashCode for the reason M14 already
        // pays for elsewhere: GetHashCode is not stable across processes, which
        // is why assetKeyHash is computed client-side rather than sent. A number
        // whose entire purpose is comparison between two machines has to be
        // stable by construction rather than by observation.
        private const ulong Basis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        // Hashed after every key. Without it the keys form one flat byte stream
        // and any set that merely moves a boundary — {"ab","c"} against
        // {"a","bc"} — reads as identical.
        private const byte Separator = 0x0A;

        /// <summary>
        /// How many keys were counted, and the digest over them.
        /// </summary>
        /// <remarks>
        /// Sorted ordinal before hashing, so the answer is a property of the set
        /// rather than of the order it was enumerated in. That also neutralises
        /// the game's own table being a <c>SortedDictionary</c> under the default
        /// comparer: a culture-sensitive order would make two identical installs
        /// under different locales disagree about keys neither of them chose.
        /// <para>
        /// Null and empty keys are skipped, and skipped from the count as well as
        /// from the digest. A count that included them would let two machines
        /// disagree on the number while their digests matched, which reads as
        /// corruption rather than as the nothing it is.
        /// </para>
        /// <para>
        /// The enumerable itself is not null-guarded. The one caller reads
        /// <c>DataMultiLinker&lt;DataContainerAssetPool&gt;.data</c>, which can
        /// return null, and already guards it — a second guard here would be an
        /// arm no test could reach for a reason, rather than one no test happens
        /// to reach.
        /// </para>
        /// </remarks>
        public static (int Count, string Digest) Compute(IEnumerable<string?> keys)
        {
            var kept = new List<string>();
            foreach (var key in keys)
            {
                if (!string.IsNullOrEmpty(key))
                {
                    kept.Add(key!);
                }
            }

            kept.Sort(StringComparer.Ordinal);

            var hash = Basis;
            for (var i = 0; i < kept.Count; i++)
            {
                // UTF-8 explicitly, never the machine's default encoding, for the
                // same machine-independence reason as the ordinal sort.
                var bytes = Encoding.UTF8.GetBytes(kept[i]);
                for (var b = 0; b < bytes.Length; b++)
                {
                    unchecked
                    {
                        hash ^= bytes[b];
                        hash *= Prime;
                    }
                }

                unchecked
                {
                    hash ^= Separator;
                    hash *= Prime;
                }
            }

            return (kept.Count, hash.ToString("x16", CultureInfo.InvariantCulture));
        }
    }
}
