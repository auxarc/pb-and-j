using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjProtocolTests
    {
        [Fact]
        public void Version_IsOne()
        {
            // Pinned deliberately: bumping the wire format must be an explicit
            // edit here and in Write_MinimalOrder_ProducesExactBytes.
            Assert.Equal(1, PbjProtocol.Version);
        }

        [Fact]
        public void Magic_SpellsPbj1()
        {
            Assert.Equal(0x504A4231, PbjProtocol.Magic);
        }

        [Fact]
        public void Check_WithCorrectMagicAndVersion_ReturnsNull()
        {
            Assert.Null(PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version));
        }

        [Fact]
        public void Check_WithWrongMagic_ReturnsBadMagic()
        {
            Assert.Equal(RejectReason.BadMagic, PbjProtocol.Check(0xDEAD, PbjProtocol.Version));
        }

        [Fact]
        public void Check_WithOlderVersion_ReturnsVersionMismatch()
        {
            Assert.Equal(RejectReason.VersionMismatch,
                PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version - 1));
        }

        [Fact]
        public void Check_WithNewerVersion_ReturnsVersionMismatch()
        {
            Assert.Equal(RejectReason.VersionMismatch,
                PbjProtocol.Check(PbjProtocol.Magic, PbjProtocol.Version + 1));
        }

        [Fact]
        public void Check_ChecksMagicBeforeVersion()
        {
            // Wrong magic means it is not our protocol at all, so the version
            // number is meaningless and must not drive the reported reason.
            Assert.Equal(RejectReason.BadMagic, PbjProtocol.Check(0, PbjProtocol.Version + 99));
        }
    }
}
