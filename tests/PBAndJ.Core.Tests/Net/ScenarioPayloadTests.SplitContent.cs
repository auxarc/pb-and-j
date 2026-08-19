using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Content too large for one blob travels as numbered parts: splitting it,
    // rebuilding it, and what Inspect makes of a part sequence.
    public partial class ScenarioPayloadTests
    {
        // --- split content (M11e) ---

        [Fact]
        public void Inspect_AcceptsContentSplitAcrossParts()
        {
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.0", Bytes("first")),
                new ScenarioFile("content.zip.1", Bytes("second")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.None, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsPartsThatDoNotStartAtZero()
        {
            // A gap means the receiver would reassemble a truncated zip and write it
            // as if whole. Half a save is worse than none: something would then try
            // to load it.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.1", Bytes("second")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.PartsNotContiguous, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsAGapBetweenParts()
        {
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.0", Bytes("first")),
                new ScenarioFile("content.zip.2", Bytes("third")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.PartsNotContiguous, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsWholeAndSplitContentTogether()
        {
            // Ambiguous: the receiver would have to guess which one is the save.
            var payload = new ScenarioPayload(Stand, new[]
            {
                Content(),
                new ScenarioFile("content.zip.0", Bytes("first")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.MixedContentForm, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsAPartOverTheBlobCap()
        {
            // The refusal that keeps PbjWriter.WriteBytes from throwing mid-encode.
            // PbjRuntime.SendTo deliberately does not guard encoding, so an oversize
            // blob would escape the effect pump and lose every effect queued behind
            // it — a refusal here is the only thing between us and that.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.0", new byte[ScenarioPayload.MaxPartBytes + 1]),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.PartTooLarge, payload.Inspect());
        }

        [Fact]
        public void MaxPartBytes_StaysUnderTheWritersBlobCap()
        {
            // The invariant that makes splitting worth doing at all. If this ever
            // fails, a legal payload throws during encode instead of being refused.
            Assert.True(ScenarioPayload.MaxPartBytes <= PbjWriter.MaxBytesLength);
        }

        [Fact]
        public void SplitContent_LeavesSmallContentWhole()
        {
            // Every real save measured is far under one part, so the common case
            // must stay byte-identical to what M9 already sends.
            var files = ScenarioPayload.SplitContent(Bytes("small"));
            Assert.Single(files);
            Assert.Equal(ScenarioPayload.ContentFileName, files[0].Name);
        }

        [Fact]
        public void SplitContent_SplitsOversizeContentIntoNumberedParts()
        {
            var content = new byte[ScenarioPayload.MaxPartBytes + 10];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 251);
            }

            var files = ScenarioPayload.SplitContent(content);

            Assert.Equal(2, files.Count);
            Assert.Equal("content.zip.0", files[0].Name);
            Assert.Equal("content.zip.1", files[1].Name);
            Assert.Equal(content.Length, files[0].Content.Length + files[1].Content.Length);
        }

        [Fact]
        public void JoinContent_RebuildsExactlyWhatSplitProduced()
        {
            // The round trip is the whole contract: a save that does not come back
            // byte-for-byte is a corrupt campaign nobody would notice until it fails
            // to load.
            var content = new byte[(ScenarioPayload.MaxPartBytes * 2) + 7];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 253);
            }

            var payload = new ScenarioPayload(Stand, Combine(ScenarioPayload.SplitContent(content)));

            Assert.Equal(ScenarioRejection.None, payload.Inspect());
            Assert.Equal(content, ScenarioPayload.JoinContent(payload));
        }

        [Fact]
        public void JoinContent_RebuildsWholeContentUnchanged()
        {
            var payload = new ScenarioPayload(Stand, new[] { Content("zipped"), Metadata() });
            Assert.Equal(Bytes("zipped"), ScenarioPayload.JoinContent(payload));
        }

        [Fact]
        public void Inspect_RejectsTheSamePartTwice()
        {
            // Two copies of part zero and nothing else would otherwise reassemble
            // into a plausible-looking save of the wrong length.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.0", Bytes("first")),
                new ScenarioFile("content.zip.0", Bytes("again")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.DuplicateName, payload.Inspect());
        }

        [Fact]
        public void SplitContent_Null_ProducesOneEmptyFileRatherThanThrowing()
        {
            // Same contract as ScenarioFile's own null handling: a null in is an
            // empty file out, refused downstream as a missing file rather than
            // throwing inside the bridge.
            var files = ScenarioPayload.SplitContent(null);
            Assert.Single(files);
            Assert.Empty(files[0].Content);
        }

        [Fact]
        public void JoinContent_Null_IsEmptyRatherThanThrowing()
        {
            Assert.Empty(ScenarioPayload.JoinContent(null));
        }

        [Fact]
        public void JoinContent_OrdersPartsByIndexNotByArrival()
        {
            // Wire order is not guaranteed to survive anything, and the digest is
            // deliberately order-independent, so reassembly must not lean on it.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.1", Bytes("second")),
                Metadata(),
                new ScenarioFile("content.zip.0", Bytes("first")),
            });

            Assert.Equal(ScenarioRejection.None, payload.Inspect());
            Assert.Equal(Bytes("firstsecond"), ScenarioPayload.JoinContent(payload));
        }

        private static ScenarioFile[] Combine(IReadOnlyList<ScenarioFile> content)
        {
            var files = new List<ScenarioFile>(content) { Metadata() };
            return files.ToArray();
        }
    }
}
