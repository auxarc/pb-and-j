using System;
using System.Text;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Where to start reading. The fixture the parts share -- a destination that
    // passes validation and the helpers a well-formed save is built from -- and what
    // the two types themselves hold once built.
    //
    // ScenarioPayloadTests is one class split across this file and its siblings; this
    // one holds what they share.
    public partial class ScenarioPayloadTests
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
    }
}
