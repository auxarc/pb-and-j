using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Two separate questions about a file name: whether it is structurally safe to
    // write inside a save folder, and whether it is a name a save is allowed to
    // contain at all.
    public partial class ScenarioPayloadTests
    {
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
    }
}
