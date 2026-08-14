using System;
using System.Collections.Generic;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ScenarioPayloadTests
    {
        /// <summary>
        /// A destination that passes validation, for tests aimed at something else.
        /// Was a bare "s" until M11e made the destination authoritative rather than
        /// informational — a name that is not a real save key now fails first and
        /// would mask whatever the test was actually about.
        /// </summary>
        private const string Stand = "pbj_x";

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private static ScenarioFile Content(string text = "zipped-combat")
            => new ScenarioFile(ScenarioPayload.ContentFileName, Bytes(text));

        private static ScenarioFile Metadata(string text = "ver: 1")
            => new ScenarioFile(ScenarioPayload.MetadataFileName, Bytes(text));

        private static ScenarioPayload Valid(string save = "pbj_combat_test")
            => new ScenarioPayload(save, new[] { Content(), Metadata() });

        // --- ScenarioFile ---

        [Fact]
        public void File_KeepsNameAndContent()
        {
            var file = new ScenarioFile("content.zip", new byte[] { 1, 2, 3 });
            Assert.Equal("content.zip", file.Name);
            Assert.Equal(new byte[] { 1, 2, 3 }, file.Content);
        }

        [Fact]
        public void File_NullContent_BecomesEmpty()
        {
            // A null blob off the wire must not become a null-reference at the
            // point of writing to disk, three layers away.
            Assert.Empty(new ScenarioFile("content.zip", null).Content);
        }

        [Fact]
        public void File_NullName_IsKept_AndRejectedLater()
        {
            Assert.Null(new ScenarioFile(null, new byte[0]).Name);
        }

        // --- payload basics ---

        [Fact]
        public void Payload_KeepsSaveNameAndFiles()
        {
            var payload = Valid();
            Assert.Equal("pbj_combat_test", payload.SaveName);
            Assert.Equal(2, payload.Files.Count);
        }

        [Fact]
        public void Payload_NullFiles_BecomesEmpty()
        {
            Assert.Empty(new ScenarioPayload(Stand, null).Files);
        }

        [Fact]
        public void Payload_TotalBytes_SumsEveryFile()
        {
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[10]),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[7]),
            });
            Assert.Equal(17L, payload.TotalBytes);
        }

        [Fact]
        public void None_IsEmptyAndRejectedAsHavingNoFiles()
        {
            Assert.Empty(ScenarioPayload.None.Files);
            Assert.Equal(0L, ScenarioPayload.None.TotalBytes);
            Assert.Equal(ScenarioRejection.NoFiles, ScenarioPayload.None.Inspect());
        }

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

        // --- structural name safety ---

        [Theory]
        [InlineData("content.zip")]
        [InlineData("metadata.yaml")]
        [InlineData("save_01.dat")]
        [InlineData("A-B_c.9")]
        public void IsSafeName_AcceptsPlainFileNames(string name)
        {
            Assert.True(ScenarioPayload.IsSafeName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("../content.zip")]
        [InlineData("..\\content.zip")]
        [InlineData("sub/content.zip")]
        [InlineData("sub\\content.zip")]
        [InlineData("/etc/passwd")]
        [InlineData("C:\\windows\\system32")]
        [InlineData("c:content.zip")]
        [InlineData(".hidden")]
        [InlineData("trailing.")]
        [InlineData(" leading.zip")]
        [InlineData("trailing.zip ")]
        [InlineData("content.zip\0evil")]
        [InlineData("naïve.zip")]
        [InlineData("bell\a.zip")]
        public void IsSafeName_RejectsAnythingThatCouldEscapeTheSaveFolder(string? name)
        {
            Assert.False(ScenarioPayload.IsSafeName(name));
        }

        [Fact]
        public void IsSafeName_RejectsOverLongNames()
        {
            Assert.False(ScenarioPayload.IsSafeName(new string('x', ScenarioPayload.MaxNameLength + 1)));
        }

        [Fact]
        public void IsSafeName_AcceptsNameAtTheLengthLimit()
        {
            Assert.True(ScenarioPayload.IsSafeName(new string('x', ScenarioPayload.MaxNameLength)));
        }

        // --- allowlist ---

        [Theory]
        [InlineData("content.zip")]
        [InlineData("metadata.yaml")]
        public void IsAllowedName_AcceptsExactlyTheTwoFilesASaveHas(string name)
        {
            Assert.True(ScenarioPayload.IsAllowedName(name));
        }

        [Theory]
        [InlineData("Content.Zip")]
        [InlineData("content.zip.exe")]
        [InlineData("preview.png")]
        [InlineData("../content.zip")]
        [InlineData(null)]
        public void IsAllowedName_RejectsEverythingElse(string? name)
        {
            Assert.False(ScenarioPayload.IsAllowedName(name));
        }

        // --- inspection ---

        [Fact]
        public void Inspect_AcceptsAWellFormedSave()
        {
            Assert.Equal(ScenarioRejection.None, Valid().Inspect());
        }

        [Fact]
        public void Inspect_RejectsNoFiles()
        {
            Assert.Equal(
                ScenarioRejection.NoFiles,
                new ScenarioPayload(Stand, new ScenarioFile[0]).Inspect());
        }

        [Fact]
        public void Inspect_RejectsTooManyFiles()
        {
            var files = new List<ScenarioFile> { Content(), Metadata() };
            while (files.Count <= ScenarioPayload.MaxFiles)
            {
                files.Add(Metadata());
            }
            Assert.Equal(ScenarioRejection.TooManyFiles, new ScenarioPayload(Stand, files).Inspect());
        }

        [Fact]
        public void Inspect_RejectsADisallowedName()
        {
            var payload = new ScenarioPayload(Stand, new[]
            {
                Content(),
                Metadata(),
                new ScenarioFile("../../.bashrc", Bytes("rm -rf")),
            });
            Assert.Equal(ScenarioRejection.DisallowedName, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsADuplicateName()
        {
            var payload = new ScenarioPayload(Stand, new[] { Content(), Content(), Metadata() });
            Assert.Equal(ScenarioRejection.DuplicateName, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsADuplicateMetadataName()
        {
            // Both halves of the duplicate check matter: a repeated metadata.yaml
            // is the same "which one wins on disk" ambiguity as a repeated
            // content.zip.
            var payload = new ScenarioPayload(Stand, new[] { Content(), Metadata(), Metadata("ver: 2") });
            Assert.Equal(ScenarioRejection.DuplicateName, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsAMissingRequiredFile()
        {
            Assert.Equal(
                ScenarioRejection.MissingRequiredFile,
                new ScenarioPayload(Stand, new[] { Content() }).Inspect());
        }

        [Fact]
        public void Inspect_RejectsAnOversizedTotal()
        {
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[ScenarioPayload.MaxTotalBytes]),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
            });
            Assert.Equal(ScenarioRejection.TooLarge, payload.Inspect());
        }

        [Fact]
        public void IsAllowedDestination_AcceptsADotInsideTheName()
        {
            // pbj_.hidden looks like a hidden file and is not one: the prefix means
            // the directory name starts with 'p'. The leading-dot rule is about what
            // the name actually begins with, so this is legitimate and the guard
            // must not over-reach and refuse a save someone could really own.
            Assert.True(ScenarioPayload.IsAllowedDestination("pbj_.hidden"));
        }

        [Fact]
        public void Inspect_AcceptsATotalExactlyAtTheCap()
        {
            // A payload at the cap no longer fits one file: MaxTotalBytes is three
            // times MaxPartBytes precisely so that reaching it means splitting. That
            // is the point of the parts, not an accident of the numbers.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("content.zip.0", new byte[ScenarioPayload.MaxPartBytes]),
                new ScenarioFile("content.zip.1", new byte[ScenarioPayload.MaxPartBytes]),
                new ScenarioFile("content.zip.2", new byte[ScenarioPayload.MaxPartBytes - 1]),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
            });
            Assert.Equal(ScenarioRejection.None, payload.Inspect());
        }

        [Fact]
        public void Inspect_ChecksSizeBeforeNames_SoAFloodIsCheapToRefuse()
        {
            // Order matters for cost, not just for the message: the size check
            // must not be reachable only after per-file work.
            var payload = new ScenarioPayload(Stand, new[]
            {
                new ScenarioFile("../escape", new byte[ScenarioPayload.MaxTotalBytes + 1]),
            });
            Assert.Equal(ScenarioRejection.TooLarge, payload.Inspect());
        }

        // --- digest agreement ---

        // --- destination (M11e) ---

        [Theory]
        [InlineData("pbj_combat_test")]
        [InlineData("pbj_firstrun")]
        [InlineData("pbj_x")]
        public void IsAllowedDestination_AcceptsSavesInsideTheNamespace(string key)
        {
            // M9's slot and a campaign key are both legitimate destinations: one
            // mechanism carries both, so the rule is "inside the namespace and
            // structurally safe" rather than a list of special cases.
            Assert.True(ScenarioPayload.IsAllowedDestination(key));
        }

        [Theory]
        [InlineData("pbj_../../.bashrc")]
        [InlineData("pbj_a/b")]
        [InlineData("pbj_a\\b")]
        [InlineData("pbj_C:")]
        [InlineData("pbj_trailing.")]
        public void IsAllowedDestination_RejectsAnythingThatCouldEscapeTheSaveFolder(string key)
        {
            // The reason structural safety is an explicit conjunct rather than
            // borrowed from LobbyCatalogue.IsOffered, which checks only the prefix
            // and not-the-scenario-slot — every one of these passes that.
            Assert.False(ScenarioPayload.IsAllowedDestination(key));
        }

        [Theory]
        [InlineData("firstrun")]
        [InlineData("autosave_timed_0")]
        [InlineData(null)]
        [InlineData("")]
        public void IsAllowedDestination_RejectsAnythingOutsideTheNamespace(string? key)
        {
            // A transfer may only ever land inside pbj_. Writing outside it would
            // let a peer past the passphrase overwrite a singleplayer campaign.
            Assert.False(ScenarioPayload.IsAllowedDestination(key));
        }

        [Fact]
        public void IsAllowedDestination_AcceptsAKeyAtTheLengthLimit()
        {
            // A legal display name is 64 characters and the key carries the prefix
            // on top, so the key bound is 68 and not 64. Reusing the file-name
            // bound here would make legal saves untransferable.
            Assert.True(ScenarioPayload.IsAllowedDestination(
                LobbySaveNames.Prefix + new string('a', LobbySaveNames.MaxNameLength)));
            Assert.False(ScenarioPayload.IsAllowedDestination(
                LobbySaveNames.Prefix + new string('a', LobbySaveNames.MaxNameLength + 1)));
        }

        [Fact]
        public void Inspect_RejectsADisallowedDestination()
        {
            var payload = new ScenarioPayload("../../elsewhere", new[] { Content(), Metadata() });
            Assert.Equal(ScenarioRejection.DisallowedDestination, payload.Inspect());
        }

        [Fact]
        public void Inspect_ChecksTheDestinationBeforeNames_SoAFloodIsStillCheapToRefuse()
        {
            // Same doctrine as the size check above: refuse on the one cheap string
            // before doing per-file work.
            var payload = new ScenarioPayload("nope", new[]
            {
                new ScenarioFile("../../.bashrc", Bytes("rm -rf")),
                Metadata(),
            });
            Assert.Equal(ScenarioRejection.DisallowedDestination, payload.Inspect());
        }

        [Fact]
        public void None_StillReportsNoFiles_NotABadDestination()
        {
            // Load-bearing ordering. HostSession.OfferScenario treats NoFiles as the
            // benign "nothing to offer" case and logs nothing; every other rejection
            // is reported as a fault. A host that has never taken a combat save must
            // not start warning about its destination.
            Assert.Equal(ScenarioRejection.NoFiles, ScenarioPayload.None.Inspect());
        }

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
