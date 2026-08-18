using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PartStateEntryTests
    {
        [Fact]
        public void Constructor_RetainsEveryField()
        {
            var entry = new PartStateEntry("pb_mech_01", "core", 0.5f, 0.25f);
            Assert.Equal("pb_mech_01", entry.Unit);
            Assert.Equal("core", entry.Socket);
            Assert.Equal(0.5f, entry.Integrity);
            Assert.Equal(0.25f, entry.Barrier);
        }
    }

    public class PartStateDigestTests
    {
        private static PartStateEntry E(string? unit, string? socket, float integrity, float barrier = 1f) =>
            new PartStateEntry(unit, socket, integrity, barrier);

        private static (int Count, string Digest) Digest(params PartStateEntry[] entries) =>
            PartStateDigest.Compute(entries);

        // The whole point: two machines walk their own ECS in their own order,
        // so the answer has to be a property of the set. A host enumerating a
        // group and a client enumerating the same group cannot be assumed to
        // agree on order, and never could.
        [Fact]
        public void Compute_IsIndependentOfEnumerationOrder()
        {
            var forwards = Digest(
                E("a", "core", 1f), E("a", "secondary", 0.5f), E("b", "core", 0.25f));
            var backwards = Digest(
                E("b", "core", 0.25f), E("a", "secondary", 0.5f), E("a", "core", 1f));

            Assert.Equal(forwards.Digest, backwards.Digest);
            Assert.Equal(forwards.Count, backwards.Count);
        }

        [Fact]
        public void Compute_CountsTheEntriesItHashed()
        {
            Assert.Equal(3, Digest(E("a", "core", 1f), E("a", "secondary", 1f), E("b", "core", 1f)).Count);
        }

        [Fact]
        public void Compute_OverNothing_IsStableAndEmpty()
        {
            var empty = PartStateDigest.Compute(new PartStateEntry[0]);
            Assert.Equal(0, empty.Count);
            Assert.Equal(empty.Digest, PartStateDigest.Compute(new PartStateEntry[0]).Digest);
            Assert.NotEmpty(empty.Digest);
        }

        // Skipped from the COUNT as well as from the digest. A count that
        // included them would let two machines disagree on the number while
        // their digests matched, which reads as corruption rather than as the
        // nothing it is — the rule AssetPoolDigest already follows.
        [Fact]
        public void Compute_SkipsEntriesWithNoJoinKey()
        {
            var full = Digest(E("a", "core", 1f));
            var padded = Digest(
                E("a", "core", 1f),
                E(null, "core", 0.5f),
                E(string.Empty, "core", 0.5f),
                E("b", null, 0.5f),
                E("b", string.Empty, 0.5f));

            Assert.Equal(1, padded.Count);
            Assert.Equal(full.Digest, padded.Digest);
        }

        [Fact]
        public void Compute_NoticesADifferentIntegrity()
        {
            Assert.NotEqual(Digest(E("a", "core", 1f)).Digest, Digest(E("a", "core", 0.5f)).Digest);
        }

        // Barrier is hashed too, and separately. A digest built from integrity
        // alone would miss a whole synced field — and barrier is the half that
        // regenerates, so it is the half most likely to drift.
        [Fact]
        public void Compute_NoticesADifferentBarrier()
        {
            Assert.NotEqual(
                Digest(E("a", "core", 1f, 1f)).Digest,
                Digest(E("a", "core", 1f, 0.5f)).Digest);
        }

        [Fact]
        public void Compute_NoticesADifferentUnitOrSocket()
        {
            var baseline = Digest(E("a", "core", 1f)).Digest;
            Assert.NotEqual(baseline, Digest(E("b", "core", 1f)).Digest);
            Assert.NotEqual(baseline, Digest(E("a", "secondary", 1f)).Digest);
        }

        // Without a separator between the two strings the pair forms one flat
        // byte stream, and any set that merely moves the boundary reads as
        // identical. Both halves are game identifiers and both are attacker-free,
        // but a digest that collides on a rename is a digest that reports
        // convergence it has not checked.
        [Fact]
        public void Compute_DoesNotCollideWhenTheJoinBoundaryMoves()
        {
            Assert.NotEqual(Digest(E("ab", "c", 1f)).Digest, Digest(E("a", "bc", 1f)).Digest);
        }

        // Quantised on the game's own 0.001 threshold, the same one it uses to
        // decide a value moved at all. Two machines that agree to within a
        // quantum must not report divergence.
        [Fact]
        public void Compute_IgnoresDifferencesBelowTheQuantum()
        {
            Assert.Equal(
                Digest(E("a", "core", 0.5f)).Digest,
                Digest(E("a", "core", 0.5f + 0.0001f)).Digest);
        }

        [Fact]
        public void Compute_NoticesDifferencesAboveTheQuantum()
        {
            Assert.NotEqual(
                Digest(E("a", "core", 0.5f)).Digest,
                Digest(E("a", "core", 0.502f)).Digest);
        }

        // NaN integrity is real enough that the game guards it in its own
        // serializer, so this must digest rather than throw — and two NaNs must
        // agree, or the instrument reports divergence between two machines that
        // are in the same broken state.
        [Fact]
        public void Compute_DigestsNaNAndInfinityAsOneSentinel()
        {
            var nan = Digest(E("a", "core", float.NaN, float.NaN)).Digest;
            Assert.Equal(nan, Digest(E("a", "core", float.NaN, float.NaN)).Digest);
            Assert.Equal(nan, Digest(E("a", "core", float.PositiveInfinity, float.NegativeInfinity)).Digest);
            Assert.NotEqual(nan, Digest(E("a", "core", 0f, 0f)).Digest);
        }

        // Out-of-range values clamp rather than wrap. A value large enough to
        // overflow the quantisation would otherwise alias onto a legitimate one.
        [Fact]
        public void Compute_ClampsValuesBeyondTheQuantisableRange()
        {
            var high = Digest(E("a", "core", 1e30f, 1f)).Digest;
            Assert.Equal(high, Digest(E("a", "core", 1e31f, 1f)).Digest);

            var low = Digest(E("a", "core", -1e30f, 1f)).Digest;
            Assert.Equal(low, Digest(E("a", "core", -1e31f, 1f)).Digest);
            Assert.NotEqual(high, low);
        }

        [Fact]
        public void Compute_AcceptsAnyEnumerable()
        {
            IEnumerable<PartStateEntry> entries = new List<PartStateEntry> { E("a", "core", 1f) };
            Assert.Equal(1, PartStateDigest.Compute(entries).Count);
        }
    }
}
