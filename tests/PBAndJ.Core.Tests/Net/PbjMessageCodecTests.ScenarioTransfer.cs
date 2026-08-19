using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // Scenario transfer (M9): the offer, the request and the scenario itself,
    // preserved file by file and byte for byte, and its size cap.
    // The two resume-token tests at the end are here because they PREDATE this
    // banner -- M5 added them, and M9 later inserted the scenario section above
    // them -- not because the resume token rides this exchange. It does not: it
    // is issued on the Welcome and spent on the Rejoin, both part of the
    // reconnect handshake.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        // --- scenario transfer (M9) ---

        [Fact]
        public void Encode_ScenarioOffer_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new ScenarioOfferMessage("s", 2, "ab"));

            var expected = new byte[]
            {
                0x14,                               // type ScenarioOffer (20)
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveName "s"
                0x02, 0x00, 0x00, 0x00,             // totalBytes 2
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // digest "ab"
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void Encode_Scenario_ProducesExactBytes()
        {
            var bytes = PbjMessageCodec.Encode(new ScenarioMessage("s", "ab", new[]
            {
                new ScenarioFile("f", new byte[] { 0xDE, 0xAD }),
            }));

            var expected = new byte[]
            {
                0x16,                               // type Scenario (22)
                0x01, 0x00, 0x00, 0x00, 0x73,       // saveName "s"
                0x02, 0x00, 0x00, 0x00, 0x61, 0x62, // digest "ab"
                0x01, 0x00, 0x00, 0x00,             // one file
                0x01, 0x00, 0x00, 0x00, 0x66,       // name "f"
                0x02, 0x00, 0x00, 0x00, 0xDE, 0xAD, // content
            };
            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void RoundTrip_ScenarioOffer_PreservesEveryField()
        {
            var m = RoundTrip(new ScenarioOfferMessage("pbj_combat_test", 124546, "3f9c1a04"));
            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal(124546, m.TotalBytes);
            Assert.Equal("3f9c1a04", m.Digest);
        }

        [Fact]
        public void RoundTrip_ScenarioRequest_PreservesTheDigest()
        {
            Assert.Equal("3f9c1a04", RoundTrip(new ScenarioRequestMessage("3f9c1a04")).Digest);
        }

        [Fact]
        public void RoundTrip_ScenarioRequest_WithNoDigest_KeepsItNull()
        {
            // A peer that holds nothing asks with no digest at all, rather than
            // inventing one that could accidentally match.
            Assert.Null(RoundTrip(new ScenarioRequestMessage(null)).Digest);
        }

        [Fact]
        public void RoundTrip_Scenario_PreservesEveryFileByteForByte()
        {
            var m = RoundTrip(new ScenarioMessage("pbj_combat_test", "3f9c1a04", new[]
            {
                File("content.zip", 3000),
                new ScenarioFile("metadata.yaml", new byte[] { 0x00, 0xFF, 0x7F, 0x80 }),
            }));

            Assert.Equal("pbj_combat_test", m.SaveName);
            Assert.Equal("3f9c1a04", m.Digest);
            Assert.Equal(2, m.Files.Count);

            Assert.Equal("content.zip", m.Files[0].Name);
            Assert.Equal(File("content.zip", 3000).Content, m.Files[0].Content);

            Assert.Equal("metadata.yaml", m.Files[1].Name);
            Assert.Equal(new byte[] { 0x00, 0xFF, 0x7F, 0x80 }, m.Files[1].Content);
        }

        [Fact]
        public void RoundTrip_Scenario_PreservesAnEmptyFile()
        {
            // Zero-length is a real state on disk and must not decode as null,
            // or the digest the sender computed stops matching.
            var m = RoundTrip(new ScenarioMessage("s", "d", new[] { new ScenarioFile("f", new byte[0]) }));
            Assert.Empty(m.Files[0].Content);
        }

        [Fact]
        public void RoundTrip_ScenarioWithNoFiles_Survives()
        {
            Assert.Empty(RoundTrip(new ScenarioMessage("s", "d", null)).Files);
        }

        [Fact]
        public void Decode_ScenarioWithTooManyFiles_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Scenario);
            writer.WriteString("s");
            writer.WriteString("d");
            writer.WriteInt32(ScenarioPayload.MaxFiles + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_ScenarioWithANullFileBlob_YieldsEmptyContent()
        {
            // -1 is the writer's null sentinel. It is not a shape we ever send,
            // but a peer can, and it must land as empty rather than as a null
            // that surfaces three layers away at the point of writing to disk.
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Scenario);
            writer.WriteString("s");
            writer.WriteString("d");
            writer.WriteInt32(1);
            writer.WriteString("f");
            writer.WriteInt32(-1);

            var m = Assert.IsType<ScenarioMessage>(PbjMessageCodec.Decode(writer.ToArray()));
            Assert.Empty(m.Files[0].Content);
        }

        // The size claim M9 rests on: the real save is ~124 KB, the cap is 512 KB,
        // and the frame limit is 1 MiB. Pinned rather than assumed, because
        // exceeding it would fail only on a real transfer.
        [Fact]
        public void Encode_ScenarioAtTheSizeCap_StaysUnderTheFrameLimit()
        {
            var half = (int)(ScenarioPayload.MaxTotalBytes / 2);
            var bytes = PbjMessageCodec.Encode(new ScenarioMessage("pbj_combat_test", "3f9c1a04", new[]
            {
                File("content.zip", half),
                File("metadata.yaml", half),
            }));

            Assert.True(bytes.Length < PbjRuntime.MaxFrameLength,
                $"a scenario at the size cap was {bytes.Length} bytes, over the frame limit");
        }

        [Fact]
        public void RoundTrip_Welcome_PreservesTheResumeToken()
        {
            Assert.Equal("3f9c1a04",
                RoundTrip(new WelcomeMessage(2, "s", 1, "h", null, 0, "3f9c1a04")).ResumeToken);
        }

        [Fact]
        public void RoundTrip_Rejoin_PreservesEveryField()
        {
            var m = RoundTrip(new RejoinMessage(PbjProtocol.Magic, 2, "0.2.0", "ally", "7f3a91", 4, "tok", null, null));
            Assert.Equal(PbjProtocol.Magic, m.Magic);
            Assert.Equal(2, m.ProtocolVersion);
            Assert.Equal("0.2.0", m.ModVersion);
            Assert.Equal("ally", m.PlayerName);
            Assert.Equal("7f3a91", m.SessionId);
            Assert.Equal(4, m.ClaimedPeerId);
            Assert.Equal("tok", m.ResumeToken);
        }
    }
}
