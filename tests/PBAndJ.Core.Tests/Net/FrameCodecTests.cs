using System;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    public class FrameCodecTests
    {
        private const int Max = 1024;

        private static FrameDecoder Decoder(int maxFrameLength = Max) => new FrameDecoder(maxFrameLength);

        private static byte[] Frame(params byte[] payload) => FrameEncoder.Encode(payload);

        // --- encoder ---

        [Fact]
        public void Encode_PrependsLittleEndianLength()
        {
            Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC }, Frame(0xAA, 0xBB, 0xCC));
        }

        [Fact]
        public void Encode_WithNullPayload_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => FrameEncoder.Encode(null!));
            Assert.Equal("payload", ex.ParamName);
        }

        [Fact]
        public void Encode_WithEmptyPayload_Throws()
        {
            // Every message carries at least a type byte, so a zero-length
            // frame is always a protocol violation rather than a no-op.
            Assert.Throws<PbjProtocolException>(() => FrameEncoder.Encode(new byte[0]));
        }

        // --- decoder: happy paths ---

        [Fact]
        public void Feed_WithZeroCount_ReturnsNoFrames()
        {
            Assert.Empty(Decoder().Feed(new byte[] { 1, 2, 3 }, 0, 0));
        }

        [Fact]
        public void Feed_WithCompleteFrame_ReturnsOneFrame()
        {
            var frames = Decoder().Feed(Frame(0x10, 0x20), 0, 6);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x10, 0x20 }, frames[0]);
        }

        [Fact]
        public void Feed_WithTwoFramesInOneBuffer_ReturnsBothInOrder()
        {
            var buffer = Frame(0x01).Concat(Frame(0x02, 0x03)).ToArray();
            var frames = Decoder().Feed(buffer, 0, buffer.Length);
            Assert.Equal(2, frames.Count);
            Assert.Equal(new byte[] { 0x01 }, frames[0]);
            Assert.Equal(new byte[] { 0x02, 0x03 }, frames[1]);
        }

        [Fact]
        public void Feed_WithHeaderSplitAcrossCalls_ReturnsFrameWhenComplete()
        {
            var decoder = Decoder();
            var buffer = Frame(0x77);
            Assert.Empty(decoder.Feed(buffer, 0, 2));
            var frames = decoder.Feed(buffer, 2, buffer.Length - 2);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x77 }, frames[0]);
        }

        [Fact]
        public void Feed_WithBodySplitAcrossCalls_ReturnsFrameWhenComplete()
        {
            var decoder = Decoder();
            var buffer = Frame(0xA1, 0xA2, 0xA3, 0xA4);
            Assert.Empty(decoder.Feed(buffer, 0, 6));
            var frames = decoder.Feed(buffer, 6, buffer.Length - 6);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0xA1, 0xA2, 0xA3, 0xA4 }, frames[0]);
        }

        [Fact]
        public void Feed_OneByteAtATime_ReturnsFrameOnFinalByte()
        {
            var decoder = Decoder();
            var buffer = Frame(0x5A, 0x5B);
            for (var i = 0; i < buffer.Length - 1; i++)
            {
                Assert.Empty(decoder.Feed(buffer, i, 1));
            }
            var frames = decoder.Feed(buffer, buffer.Length - 1, 1);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x5A, 0x5B }, frames[0]);
        }

        [Fact]
        public void Feed_WithFrameAndPartialNext_RetainsRemainder()
        {
            var decoder = Decoder();
            var buffer = Frame(0x01).Concat(Frame(0x02, 0x03)).ToArray();
            // everything except the final payload byte
            var frames = decoder.Feed(buffer, 0, buffer.Length - 1);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x01 }, frames[0]);

            var rest = decoder.Feed(buffer, buffer.Length - 1, 1);
            Assert.Single(rest);
            Assert.Equal(new byte[] { 0x02, 0x03 }, rest[0]);
        }

        [Fact]
        public void Feed_RespectsOffsetAndCount()
        {
            var buffer = new byte[] { 0xEE, 0xEE }.Concat(Frame(0x42)).Concat(new byte[] { 0xEE }).ToArray();
            var frames = Decoder().Feed(buffer, 2, 5);
            Assert.Single(frames);
            Assert.Equal(new byte[] { 0x42 }, frames[0]);
        }

        [Fact]
        public void Feed_WholeFrameInOneCall_IsAlreadyCompleteForSteamStyleTransports()
        {
            // Steam messages arrive pre-framed; the decoder handles that as the
            // degenerate one-frame-per-Feed case with no conditional code.
            var frames = Decoder().Feed(Frame(0x09), 0, 5);
            Assert.Single(frames);
        }

        // --- decoder: faults ---

        [Fact]
        public void Feed_WithZeroLengthFrame_ThrowsAndFaults()
        {
            var decoder = Decoder();
            Assert.Throws<PbjProtocolException>(() => decoder.Feed(new byte[] { 0, 0, 0, 0 }, 0, 4));
            Assert.True(decoder.IsFaulted);
        }

        [Fact]
        public void Feed_WithNegativeLengthFrame_ThrowsAndFaults()
        {
            var decoder = Decoder();
            Assert.Throws<PbjProtocolException>(() => decoder.Feed(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4));
            Assert.True(decoder.IsFaulted);
        }

        [Fact]
        public void Feed_WithOversizeLength_ThrowsAndFaults()
        {
            var decoder = Decoder(maxFrameLength: 8);
            var header = new byte[] { 0x09, 0x00, 0x00, 0x00 };
            Assert.Throws<PbjProtocolException>(() => decoder.Feed(header, 0, 4));
            Assert.True(decoder.IsFaulted);
        }

        [Fact]
        public void Feed_AtExactlyMaxFrameLength_IsAccepted()
        {
            var decoder = Decoder(maxFrameLength: 2);
            var frames = decoder.Feed(Frame(0x01, 0x02), 0, 6);
            Assert.Single(frames);
            Assert.False(decoder.IsFaulted);
        }

        [Fact]
        public void Feed_AfterFault_ThrowsInvalidOperation()
        {
            var decoder = Decoder();
            Assert.Throws<PbjProtocolException>(() => decoder.Feed(new byte[] { 0, 0, 0, 0 }, 0, 4));
            Assert.Throws<InvalidOperationException>(() => decoder.Feed(Frame(0x01), 0, 5));
        }

        // --- decoder: argument guards ---

        [Fact]
        public void Feed_WithNullBuffer_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => Decoder().Feed(null!, 0, 0));
            Assert.Equal("buffer", ex.ParamName);
        }

        [Fact]
        public void Feed_WithNegativeOffset_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Decoder().Feed(new byte[4], -1, 1));
            Assert.Equal("offset", ex.ParamName);
        }

        [Fact]
        public void Feed_WithNegativeCount_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Decoder().Feed(new byte[4], 0, -1));
            Assert.Equal("count", ex.ParamName);
        }

        [Fact]
        public void Feed_WithCountBeyondBuffer_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Decoder().Feed(new byte[4], 2, 3));
            Assert.Equal("count", ex.ParamName);
        }

        [Fact]
        public void Constructor_WithNonPositiveMaxFrameLength_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDecoder(0));
            Assert.Equal("maxFrameLength", ex.ParamName);
        }

        [Fact]
        public void IsFaulted_InitiallyFalse()
        {
            Assert.False(Decoder().IsFaulted);
        }
    }
}
