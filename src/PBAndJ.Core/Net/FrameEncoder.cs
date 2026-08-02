using System;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Wraps a message payload in the length-prefixed frame that
    /// <see cref="FrameDecoder"/> expects: a little-endian int32 byte count
    /// followed by the payload.
    /// </summary>
    public static class FrameEncoder
    {
        /// <summary>Bytes of length prefix ahead of every payload.</summary>
        public const int HeaderLength = 4;

        public static byte[] Encode(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (payload.Length == 0)
            {
                // Every message carries at least a type byte, so an empty
                // payload means the caller has already lost track of the frame.
                throw new PbjProtocolException("Cannot frame an empty payload.");
            }

            var frame = new byte[HeaderLength + payload.Length];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Array.Copy(payload, 0, frame, HeaderLength, payload.Length);
            return frame;
        }
    }
}
