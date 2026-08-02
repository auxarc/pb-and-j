using System;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class PbjPrimitiveCodecTests
    {
        private static PbjReader ReaderOver(Action<PbjWriter> write)
        {
            var writer = new PbjWriter();
            write(writer);
            return new PbjReader(writer.ToArray());
        }

        // --- writer: exact wire bytes ---

        [Fact]
        public void WriteByte_ThenReadByte_RoundTrips()
        {
            var reader = ReaderOver(w => w.WriteByte(0xA7));
            Assert.Equal(0xA7, reader.ReadByte());
        }

        [Fact]
        public void WriteBool_True_WritesOne()
        {
            var writer = new PbjWriter();
            writer.WriteBool(true);
            Assert.Equal(new byte[] { 1 }, writer.ToArray());
        }

        [Fact]
        public void WriteBool_False_WritesZero()
        {
            var writer = new PbjWriter();
            writer.WriteBool(false);
            Assert.Equal(new byte[] { 0 }, writer.ToArray());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WriteBool_ThenReadBool_RoundTrips(bool value)
        {
            var reader = ReaderOver(w => w.WriteBool(value));
            Assert.Equal(value, reader.ReadBool());
        }

        [Fact]
        public void WriteInt32_WritesLittleEndianBytes()
        {
            var writer = new PbjWriter();
            writer.WriteInt32(0x01020304);
            Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, writer.ToArray());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void WriteInt32_RoundTrips(int value)
        {
            var reader = ReaderOver(w => w.WriteInt32(value));
            Assert.Equal(value, reader.ReadInt32());
        }

        [Fact]
        public void WriteSingle_WritesIeee754LittleEndian()
        {
            // 1.0f is 0x3F800000; little-endian on the wire.
            var writer = new PbjWriter();
            writer.WriteSingle(1.0f);
            Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, writer.ToArray());
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(1.5f)]
        [InlineData(-2.25f)]
        [InlineData(float.Epsilon)]
        [InlineData(float.MaxValue)]
        [InlineData(float.MinValue)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void WriteSingle_RoundTripsSpecialValues(float value)
        {
            var reader = ReaderOver(w => w.WriteSingle(value));
            Assert.Equal(value, reader.ReadSingle());
        }

        [Fact]
        public void WriteSingle_RoundTripsNaN()
        {
            var reader = ReaderOver(w => w.WriteSingle(float.NaN));
            Assert.True(float.IsNaN(reader.ReadSingle()));
        }

        [Fact]
        public void WriteSingle_RoundTripsNegativeZeroBitPattern()
        {
            var reader = ReaderOver(w => w.WriteSingle(-0f));
            var result = reader.ReadSingle();
            Assert.True(float.IsNegative(result));
            Assert.Equal(0f, result);
        }

        // --- strings ---

        [Fact]
        public void WriteString_Ascii_RoundTrips()
        {
            var reader = ReaderOver(w => w.WriteString("unit_a"));
            Assert.Equal("unit_a", reader.ReadString());
        }

        [Fact]
        public void WriteString_NonAscii_RoundTripsAsUtf8()
        {
            var reader = ReaderOver(w => w.WriteString("ünït_â"));
            Assert.Equal("ünït_â", reader.ReadString());
        }

        [Fact]
        public void WriteString_Null_RoundTripsAsNull()
        {
            var reader = ReaderOver(w => w.WriteString(null));
            Assert.Null(reader.ReadString());
        }

        [Fact]
        public void WriteString_Null_WritesNegativeOneLength()
        {
            var writer = new PbjWriter();
            writer.WriteString(null);
            Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, writer.ToArray());
        }

        [Fact]
        public void WriteString_Empty_RoundTripsAsEmpty()
        {
            var reader = ReaderOver(w => w.WriteString(string.Empty));
            Assert.Equal(string.Empty, reader.ReadString());
        }

        [Fact]
        public void WriteString_ExceedingMaxLength_Throws()
        {
            var writer = new PbjWriter();
            var tooLong = new string('x', PbjWriter.MaxStringLength + 1);
            Assert.Throws<PbjProtocolException>(() => writer.WriteString(tooLong));
        }

        [Fact]
        public void WriteString_AtMaxLength_IsAccepted()
        {
            var writer = new PbjWriter();
            var atLimit = new string('x', PbjWriter.MaxStringLength);
            writer.WriteString(atLimit);
            Assert.Equal(atLimit, new PbjReader(writer.ToArray()).ReadString());
        }

        // --- writer buffer ---

        [Fact]
        public void ToArray_ReturnsAllWrittenBytes()
        {
            var writer = new PbjWriter();
            writer.WriteByte(1);
            writer.WriteInt32(2);
            writer.WriteBool(true);
            Assert.Equal(6, writer.ToArray().Length);
        }

        [Fact]
        public void ToArray_OnEmptyWriter_ReturnsEmptyArray()
        {
            Assert.Empty(new PbjWriter().ToArray());
        }

        // --- reader guards ---

        [Fact]
        public void Constructor_WithNullBuffer_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => new PbjReader(null!));
            Assert.Equal("buffer", ex.ParamName);
        }

        [Fact]
        public void ReadByte_PastEnd_Throws()
        {
            var reader = new PbjReader(new byte[0]);
            Assert.Throws<PbjProtocolException>(() => reader.ReadByte());
        }

        [Fact]
        public void ReadInt32_Truncated_Throws()
        {
            var reader = new PbjReader(new byte[] { 1, 2, 3 });
            Assert.Throws<PbjProtocolException>(() => reader.ReadInt32());
        }

        [Fact]
        public void ReadSingle_Truncated_Throws()
        {
            var reader = new PbjReader(new byte[] { 1, 2 });
            Assert.Throws<PbjProtocolException>(() => reader.ReadSingle());
        }

        [Fact]
        public void ReadString_TruncatedLength_Throws()
        {
            var reader = new PbjReader(new byte[] { 5, 0 });
            Assert.Throws<PbjProtocolException>(() => reader.ReadString());
        }

        [Fact]
        public void ReadString_TruncatedPayload_Throws()
        {
            // declares 8 bytes of text, supplies 2
            var reader = new PbjReader(new byte[] { 0x08, 0x00, 0x00, 0x00, 0x61, 0x62 });
            Assert.Throws<PbjProtocolException>(() => reader.ReadString());
        }

        [Fact]
        public void ReadString_WithNegativeLengthOtherThanNull_Throws()
        {
            // -2 is neither a valid length nor the null sentinel
            var reader = new PbjReader(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF });
            Assert.Throws<PbjProtocolException>(() => reader.ReadString());
        }

        [Fact]
        public void ReadString_WithLengthExceedingMaximum_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteInt32(PbjWriter.MaxStringLength + 1);
            Assert.Throws<PbjProtocolException>(() => new PbjReader(writer.ToArray()).ReadString());
        }

        // --- EnsureConsumed ---

        [Fact]
        public void EnsureConsumed_WhenFullyRead_DoesNotThrow()
        {
            var reader = ReaderOver(w => w.WriteInt32(7));
            reader.ReadInt32();
            reader.EnsureConsumed();
        }

        [Fact]
        public void EnsureConsumed_WithTrailingBytes_Throws()
        {
            var reader = new PbjReader(new byte[] { 1, 2, 3, 4, 5 });
            reader.ReadInt32();
            Assert.Throws<PbjProtocolException>(() => reader.EnsureConsumed());
        }

        // --- culture independence ---

        [Fact]
        public void Codec_IsCultureIndependent()
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var reader = ReaderOver(w =>
                {
                    w.WriteSingle(1.5f);
                    w.WriteString("1.5");
                });
                Assert.Equal(1.5f, reader.ReadSingle());
                Assert.Equal("1.5", reader.ReadString());
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = prev;
            }
        }
    }
}
