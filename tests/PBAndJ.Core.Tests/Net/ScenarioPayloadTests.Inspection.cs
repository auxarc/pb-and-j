using System.Collections.Generic;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Inspect: the single verdict a payload gives about itself -- the first problem
    // it finds rather than all of them -- and the order it looks in, which is cheap
    // checks first, so a peer cannot make us do per-file work before we notice the
    // whole thing is oversized.
    public partial class ScenarioPayloadTests
    {
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
    }
}
