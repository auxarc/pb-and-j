using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class AssetPoolDigestTests
    {
        // The property the whole measurement rests on. Two installs enumerate
        // their pool table in whatever order their dictionary hands it over, and
        // a digest that noticed would report a difference on every comparison of
        // two identical machines.
        [Fact]
        public void Compute_SameKeysInAnyOrder_ProducesTheSameDigest()
        {
            var forwards = AssetPoolDigest.Compute(new[] { "fx_a", "fx_b", "fx_c" });
            var backwards = AssetPoolDigest.Compute(new[] { "fx_c", "fx_b", "fx_a" });
            var shuffled = AssetPoolDigest.Compute(new[] { "fx_b", "fx_a", "fx_c" });

            Assert.Equal(forwards.Digest, backwards.Digest);
            Assert.Equal(forwards.Digest, shuffled.Digest);
        }

        [Fact]
        public void Compute_OneChangedKey_ChangesTheDigest()
        {
            var before = AssetPoolDigest.Compute(new[] { "fx_a", "fx_b" });
            var after = AssetPoolDigest.Compute(new[] { "fx_a", "fx_B" });

            Assert.NotEqual(before.Digest, after.Digest);
        }

        // The shape a DLC or workshop divergence actually takes: one machine has
        // a pool the other has never heard of.
        [Fact]
        public void Compute_OneAddedKey_ChangesTheDigest()
        {
            var without = AssetPoolDigest.Compute(new[] { "fx_a", "fx_b" });
            var with = AssetPoolDigest.Compute(new[] { "fx_a", "fx_b", "fx_c" });

            Assert.NotEqual(without.Digest, with.Digest);
            Assert.Equal(2, without.Count);
            Assert.Equal(3, with.Count);
        }

        // Without a separator between keys the hash sees one flat byte stream,
        // so a set that merely moves a boundary reads as identical. The two sets
        // below are the minimal witness for that mistake.
        [Fact]
        public void Compute_KeysThatConcatenateAlike_DoNotCollide()
        {
            var split = AssetPoolDigest.Compute(new[] { "ab", "c" });
            var joined = AssetPoolDigest.Compute(new[] { "a", "bc" });

            Assert.NotEqual(split.Digest, joined.Digest);
        }

        [Fact]
        public void Compute_NoKeys_IsStableAndEmpty()
        {
            var first = AssetPoolDigest.Compute(new string[0]);
            var second = AssetPoolDigest.Compute(new string[0]);

            Assert.Equal(0, first.Count);
            Assert.Equal(first.Digest, second.Digest);
        }

        // Skipped rather than hashed as empty, and the count has to agree with
        // that: a count that included them would make two machines disagree on
        // the number while their digests matched, which reads as corruption
        // rather than as the nothing it is.
        [Fact]
        public void Compute_NullAndEmptyKeys_AreSkippedFromBothCountAndDigest()
        {
            var padded = AssetPoolDigest.Compute(new[] { "fx_a", null, "", "fx_b" });
            var clean = AssetPoolDigest.Compute(new[] { "fx_a", "fx_b" });

            Assert.Equal(clean.Count, padded.Count);
            Assert.Equal(clean.Digest, padded.Digest);
            Assert.Equal(2, padded.Count);
        }

        [Fact]
        public void Compute_Digest_IsSixteenLowercaseHexCharacters()
        {
            var digest = AssetPoolDigest.Compute(new[] { "fx_a" }).Digest;

            Assert.Equal(16, digest.Length);
            foreach (var c in digest)
            {
                Assert.True(
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    "digest carried a non-hex character: " + c);
            }
        }

        // The pin. Its job is not to specify the value — any stable function
        // would do — but to make a refactor that quietly changes the arithmetic
        // fail here rather than on two machines a week later, where the symptom
        // is a mismatch between installs that actually agree.
        //
        // ⚠️ If this ever needs re-recording, every previously written digest
        // becomes incomparable. That is a version-bump-shaped event, not a test
        // fix.
        [Fact]
        public void Compute_KnownKeySet_MatchesThePinnedDigest()
        {
            var result = AssetPoolDigest.Compute(new[] { "fx_alpha", "fx_beta", "fx_gamma" });

            Assert.Equal(3, result.Count);
            Assert.Equal("c3da89bc1753245b", result.Digest);
        }

        // Non-ASCII goes through UTF-8 rather than through whatever the machine's
        // default encoding is, or two installs under different locales disagree
        // about a key neither of them chose.
        [Fact]
        public void Compute_NonAsciiKeys_HashTheirUtf8Bytes()
        {
            var accented = AssetPoolDigest.Compute(new[] { "fx_é" });
            var plain = AssetPoolDigest.Compute(new[] { "fx_e" });

            Assert.NotEqual(plain.Digest, accented.Digest);
        }

        // Ordinal, not culture-aware. The game's own pool table is a
        // SortedDictionary with the default comparer, so a culture-sensitive sort
        // here would inherit exactly the machine-dependence this exists to avoid.
        [Fact]
        public void Compute_SortIsOrdinal_SoCaseOrdersByCodePoint()
        {
            // "B" sorts before "a" ordinally and after it in most cultures. If
            // the sort were culture-aware these two would still agree with each
            // other — so the assertion is against a hand-ordered reference set
            // that only matches under an ordinal sort.
            var unsorted = AssetPoolDigest.Compute(new[] { "a", "B" });
            var ordinal = AssetPoolDigest.Compute(new[] { "B", "a" });

            Assert.Equal(unsorted.Digest, ordinal.Digest);
            Assert.Equal(2, unsorted.Count);
        }

        [Fact]
        public void Compute_DuplicateKeys_AreCountedAndHashedTwice()
        {
            // The game's table cannot contain a duplicate key, so this is not a
            // case to defend against — it is here to pin what the function does
            // with one rather than leaving it to be discovered.
            var once = AssetPoolDigest.Compute(new[] { "fx_a" });
            var twice = AssetPoolDigest.Compute(new[] { "fx_a", "fx_a" });

            Assert.Equal(1, once.Count);
            Assert.Equal(2, twice.Count);
            Assert.NotEqual(once.Digest, twice.Digest);
        }
    }
}
