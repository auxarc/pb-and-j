using System;
using System.Collections.Generic;
using System.Linq;
using PBAndJ.Core.Net;
using Xunit;

namespace PBAndJ.Core.Tests.Net
{
    // What the codec refuses: a null message on encode; a null or empty buffer,
    // an unknown type byte, trailing bytes, a negative collection count and a
    // truncated message on decode; and the two collection caps -- Ready's orders
    // and Welcome's peers -- that decode enforces rather than encode.
    // UnsupportedMessage lives here rather than in the shared fixture because the
    // one test that instantiates it is in this part.
    //
    // One part of PbjMessageCodecTests, a single class split across 10 files.
    // Helpers used by more than one part live in PbjMessageCodecTests.cs; a helper lives
    // here only because this part is effectively its sole user.
    public partial class PbjMessageCodecTests
    {
        // Exists purely to reach PbjMessageCodec.Encode's default: arm. This is
        // why PbjMessage's ctor is protected and Type is abstract — do not
        // "tidy" either away.
        private sealed class UnsupportedMessage : PbjMessage
        {
            public override PbjMessageType Type => (PbjMessageType)200;
        }

        // --- guards ---

        [Fact]
        public void Encode_WithNullMessage_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => PbjMessageCodec.Encode(null!));
            Assert.Equal("message", ex.ParamName);
        }

        [Fact]
        public void Encode_WithUnsupportedMessageSubclass_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Encode(new UnsupportedMessage()));
        }

        [Fact]
        public void Decode_WithNullBuffer_Throws()
        {
            var ex = Assert.Throws<ArgumentNullException>(() => PbjMessageCodec.Decode(null!));
            Assert.Equal("payload", ex.ParamName);
        }

        [Fact]
        public void Decode_WithEmptyBuffer_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(new byte[0]));
        }

        [Fact]
        public void Decode_WithUnknownTypeByte_Throws()
        {
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(new byte[] { 200 }));
        }

        [Fact]
        public void Decode_WithTrailingBytes_Throws()
        {
            var bytes = PbjMessageCodec.Encode(new TurnCommitMessage(1)).Concat(new byte[] { 0xFF }).ToArray();
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(bytes));
        }

        [Fact]
        public void Decode_ReadyWithTooManyOrders_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Ready);
            writer.WriteInt32(1);
            writer.WriteInt32(PbjMessageCodec.MaxOrdersPerReady + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_WelcomeWithTooManyPeers_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Welcome);
            writer.WriteInt32(1);
            writer.WriteString("s");
            writer.WriteInt32(1);
            writer.WriteString("host");
            writer.WriteInt32(PbjMessageCodec.MaxPeersPerWelcome + 1);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_WithNegativeCollectionCount_Throws()
        {
            var writer = new PbjWriter();
            writer.WriteByte((byte)PbjMessageType.Ready);
            writer.WriteInt32(1);
            writer.WriteInt32(-3);
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(writer.ToArray()));
        }

        [Fact]
        public void Decode_TruncatedMessage_Throws()
        {
            var full = PbjMessageCodec.Encode(new TurnCompleteMessage(7, "digest"));
            var truncated = full.Take(full.Length - 3).ToArray();
            Assert.Throws<PbjProtocolException>(() => PbjMessageCodec.Decode(truncated));
        }
    }
}
