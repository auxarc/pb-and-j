using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The payload digest -- the short answer to "is this the save I already have"
    // and "did it arrive intact", and what it is deliberately blind to -- together
    // with the Matches comparison that lets a transfer be skipped.
    //
    // The Matches tests were written under a "digest agreement" banner and cut off
    // from it when M11e inserted the destination and split-content sections in
    // between. The orphaned banner is gone and its tests are back beside the digest.
    public partial class ScenarioPayloadTests
    {
        // --- digest ---

        [Fact]
        public void Digest_IsEightHexDigits()
        {
            var digest = Valid().Digest;
            Assert.Equal(8, digest.Length);
            Assert.All(digest, c => Assert.True("0123456789abcdef".IndexOf(c) >= 0, digest));
        }

        [Fact]
        public void Digest_IsStableForIdenticalPayloads()
        {
            Assert.Equal(Valid().Digest, Valid().Digest);
        }

        [Fact]
        public void Digest_ChangesWhenContentChanges()
        {
            var a = new ScenarioPayload(Stand, new[] { Content("one"), Metadata() });
            var b = new ScenarioPayload(Stand, new[] { Content("two"), Metadata() });
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_ChangesWhenAFileNameChanges()
        {
            var a = new ScenarioPayload(Stand, new[] { Content(), Metadata() });
            var b = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("other.zip", Bytes("zipped-combat")),
                Metadata(),
            });
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_IgnoresFileOrder()
        {
            // Directory enumeration order is not a protocol guarantee, so two
            // hosts holding the same save must agree on its identity regardless.
            var a = new ScenarioPayload(Stand, new[] { Content(), Metadata() });
            var b = new ScenarioPayload(Stand, new[] { Metadata(), Content() });
            Assert.Equal(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_IgnoresSaveName()
        {
            // The save name is local naming, not scenario identity: the client
            // always writes to its own SaveName regardless of what the host calls it.
            var a = new ScenarioPayload("host_name", new[] { Content(), Metadata() });
            var b = new ScenarioPayload("client_name", new[] { Content(), Metadata() });
            Assert.Equal(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_DistinguishesEmptyFileFromAbsentFile()
        {
            var a = new ScenarioPayload(Stand, new[] { Content(), Metadata(string.Empty) });
            var b = new ScenarioPayload(Stand, new[] { Content() });
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_OfNullNamedFile_DoesNotThrow()
        {
            var payload = new ScenarioPayload(Stand, new[] { new ScenarioFile(null, new byte[] { 1 }) });
            Assert.Equal(8, payload.Digest.Length);
        }

        [Fact]
        public void Matches_IsTrueForTheSameDigest()
        {
            Assert.True(Valid().Matches(Valid().Digest));
        }

        [Fact]
        public void Matches_IsCaseInsensitive()
        {
            Assert.True(Valid().Matches(Valid().Digest.ToUpperInvariant()));
        }

        [Fact]
        public void Matches_IsFalseForADifferentDigest()
        {
            Assert.False(Valid().Matches("deadbeef"));
        }

        [Fact]
        public void Matches_IsFalseForNull()
        {
            Assert.False(Valid().Matches(null));
        }
    }
}
