using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // The digest is blind to how the content is framed: a save carried as numbered
    // parts has to identify as the same save as the single file on disk. The banner
    // below says why that matters.
    //
    // Pattern, Unsplit and Split live here rather than in the shared fixture because
    // every one of their call sites is in this file.
    public partial class ScenarioPayloadTests
    {
        // --- digest identifies the save, not the framing ---
        //
        // A save over MaxPartBytes ships as content.zip.0/.1/.2 while the very
        // same save sits on disk as one content.zip. The digest is the "am I
        // already holding this?" answer, so it has to be blind to that split:
        // otherwise a large fight re-transfers on every single offer, for ever,
        // because the client's local digest can never equal the offered one.

        private static byte[] Pattern(int length, byte seed = 0)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                bytes[i] = (byte)((i * 31 + seed) & 0xFF);
            }
            return bytes;
        }

        private static ScenarioPayload Unsplit(byte[] content)
            => new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, content),
                Metadata(),
            });

        private static ScenarioPayload Split(byte[] content)
        {
            var files = new List<ScenarioFile>(ScenarioPayload.SplitContent(content)) { Metadata() };
            return new ScenarioPayload(Stand, files);
        }

        [Fact]
        public void Digest_OfSplitContent_EqualsUnsplit()
        {
            var content = Pattern(ScenarioPayload.MaxPartBytes + 1000);
            var split = Split(content);

            // Guard the premise: if this stops splitting, the test below passes
            // for the wrong reason and proves nothing.
            Assert.True(split.Files.Count > 2, "expected content to have split into parts");

            Assert.Equal(Unsplit(content).Digest, split.Digest);
        }

        [Fact]
        public void Digest_OfSplitContent_StillChangesWithContent()
        {
            // Normalising the split must not blunt the digest into a length check.
            var a = Split(Pattern(ScenarioPayload.MaxPartBytes + 1000));
            var b = Split(Pattern(ScenarioPayload.MaxPartBytes + 1000, seed: 7));
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_OfSplitContent_IgnoresPartOrder()
        {
            // Parts are reassembled by their numbered name, never by position:
            // arrival order is not a protocol guarantee any more than directory
            // enumeration order is.
            var content = Pattern(ScenarioPayload.MaxPartBytes + 1000);
            var forward = new List<ScenarioFile>(ScenarioPayload.SplitContent(content)) { Metadata() };
            var reversed = new List<ScenarioFile>(forward);
            reversed.Reverse();

            Assert.Equal(
                new ScenarioPayload(Stand, forward).Digest,
                new ScenarioPayload(Stand, reversed).Digest);
        }

        [Fact]
        public void Digest_OfTransposedParts_DiffersFromTheOriginal()
        {
            // The merge is by part INDEX, so swapping which bytes carry which
            // number is a different save and must digest differently. Without
            // this, "order independent" would have quietly meant "content
            // order-insensitive", which is a much weaker and wrong property.
            var content = Pattern(ScenarioPayload.MaxPartBytes + 1000);
            var parts = ScenarioPayload.SplitContent(content);
            var transposed = new List<ScenarioFile>
            {
                new ScenarioFile(parts[0].Name, parts[1].Content),
                new ScenarioFile(parts[1].Name, parts[0].Content),
                Metadata(),
            };

            Assert.NotEqual(Split(content).Digest, new ScenarioPayload(Stand, transposed).Digest);
        }

        [Fact]
        public void Digest_OfGappedParts_DoesNotThrow()
        {
            // The digest is computed in the constructor, ahead of Inspect, so a
            // malformed payload has to survive being identified on its way to
            // being refused. Parts .0 and .2 with .1 missing is the shape Inspect
            // rejects as PartsNotContiguous.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName + ".0", Bytes("head")),
                new ScenarioFile(ScenarioPayload.ContentFileName + ".2", Bytes("tail")),
                Metadata(),
            });

            Assert.Equal(8, payload.Digest.Length);
            Assert.Equal(ScenarioRejection.PartsNotContiguous, payload.Inspect());
        }

        [Fact]
        public void Digest_OfASinglePartNamedZero_EqualsUnsplit()
        {
            // Belt and braces on the boundary: a host that split into exactly one
            // numbered part must still agree with a disk copy that never split.
            var content = Bytes("small");
            var numbered = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName + ".0", content),
                Metadata(),
            });
            Assert.Equal(Unsplit(content).Digest, numbered.Digest);
        }
    }
}
