using System;
using System.Collections.Generic;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class ScenarioPayloadTests
    {
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
            Assert.Empty(new ScenarioPayload("s", null).Files);
        }

        [Fact]
        public void Payload_TotalBytes_SumsEveryFile()
        {
            var payload = new ScenarioPayload("s", new[]
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
            var a = new ScenarioPayload("s", new[] { Content("one"), Metadata() });
            var b = new ScenarioPayload("s", new[] { Content("two"), Metadata() });
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_ChangesWhenAFileNameChanges()
        {
            var a = new ScenarioPayload("s", new[] { Content(), Metadata() });
            var b = new ScenarioPayload("s", new[]
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
            var a = new ScenarioPayload("s", new[] { Content(), Metadata() });
            var b = new ScenarioPayload("s", new[] { Metadata(), Content() });
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
            var a = new ScenarioPayload("s", new[] { Content(), Metadata(string.Empty) });
            var b = new ScenarioPayload("s", new[] { Content() });
            Assert.NotEqual(a.Digest, b.Digest);
        }

        [Fact]
        public void Digest_OfNullNamedFile_DoesNotThrow()
        {
            var payload = new ScenarioPayload("s", new[] { new ScenarioFile(null, new byte[] { 1 }) });
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
                new ScenarioPayload("s", new ScenarioFile[0]).Inspect());
        }

        [Fact]
        public void Inspect_RejectsTooManyFiles()
        {
            var files = new List<ScenarioFile> { Content(), Metadata() };
            while (files.Count <= ScenarioPayload.MaxFiles)
            {
                files.Add(Metadata());
            }
            Assert.Equal(ScenarioRejection.TooManyFiles, new ScenarioPayload("s", files).Inspect());
        }

        [Fact]
        public void Inspect_RejectsADisallowedName()
        {
            var payload = new ScenarioPayload("s", new[]
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
            var payload = new ScenarioPayload("s", new[] { Content(), Content(), Metadata() });
            Assert.Equal(ScenarioRejection.DuplicateName, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsADuplicateMetadataName()
        {
            // Both halves of the duplicate check matter: a repeated metadata.yaml
            // is the same "which one wins on disk" ambiguity as a repeated
            // content.zip.
            var payload = new ScenarioPayload("s", new[] { Content(), Metadata(), Metadata("ver: 2") });
            Assert.Equal(ScenarioRejection.DuplicateName, payload.Inspect());
        }

        [Fact]
        public void Inspect_RejectsAMissingRequiredFile()
        {
            Assert.Equal(
                ScenarioRejection.MissingRequiredFile,
                new ScenarioPayload("s", new[] { Content() }).Inspect());
        }

        [Fact]
        public void Inspect_RejectsAnOversizedTotal()
        {
            var payload = new ScenarioPayload("s", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[ScenarioPayload.MaxTotalBytes]),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
            });
            Assert.Equal(ScenarioRejection.TooLarge, payload.Inspect());
        }

        [Fact]
        public void Inspect_AcceptsATotalExactlyAtTheCap()
        {
            var payload = new ScenarioPayload("s", new[]
            {
                new ScenarioFile(ScenarioPayload.ContentFileName, new byte[ScenarioPayload.MaxTotalBytes - 1]),
                new ScenarioFile(ScenarioPayload.MetadataFileName, new byte[1]),
            });
            Assert.Equal(ScenarioRejection.None, payload.Inspect());
        }

        [Fact]
        public void Inspect_ChecksSizeBeforeNames_SoAFloodIsCheapToRefuse()
        {
            // Order matters for cost, not just for the message: the size check
            // must not be reachable only after per-file work.
            var payload = new ScenarioPayload("s", new[]
            {
                new ScenarioFile("../escape", new byte[ScenarioPayload.MaxTotalBytes + 1]),
            });
            Assert.Equal(ScenarioRejection.TooLarge, payload.Inspect());
        }

        // --- digest agreement ---

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
