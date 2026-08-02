using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Reassembles length-prefixed frames from an arbitrarily chunked byte
    /// stream. One instance per peer.
    /// </summary>
    /// <remarks>
    /// Deliberately a pure object fed <c>byte[]</c> rather than something that
    /// owns a stream: every partial-delivery case a socket can produce is
    /// reproduced in a unit test by choosing how the test slices the array, so
    /// the whole reassembly path is covered without a single socket.
    /// <para>
    /// It also lives on the main thread, not the receive thread — see
    /// docs/design/networking.md. The receive thread's only job is to post
    /// bytes; every protocol decision, including "this peer sent garbage",
    /// happens here, inside the coverage gate.
    /// </para>
    /// </remarks>
    public sealed class FrameDecoder
    {
        private static readonly IReadOnlyList<byte[]> NoFrames = new byte[0][];

        private readonly List<byte> accumulator = new List<byte>();
        private readonly int maxFrameLength;

        public FrameDecoder(int maxFrameLength)
        {
            if (maxFrameLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxFrameLength));
            }
            this.maxFrameLength = maxFrameLength;
        }

        /// <summary>
        /// True once an unrecoverable framing error has been seen. The stream
        /// cannot be resynchronised, so the peer must be disconnected.
        /// </summary>
        public bool IsFaulted { get; private set; }

        /// <summary>
        /// Feeds freshly received bytes and returns whatever complete frames
        /// that completes — possibly none, possibly several.
        /// </summary>
        public IReadOnlyList<byte[]> Feed(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (IsFaulted)
            {
                throw new InvalidOperationException("Decoder has faulted; the peer must be disconnected.");
            }

            for (var i = 0; i < count; i++)
            {
                accumulator.Add(buffer[offset + i]);
            }

            List<byte[]>? frames = null;
            while (accumulator.Count >= FrameEncoder.HeaderLength)
            {
                var length = accumulator[0]
                    | (accumulator[1] << 8)
                    | (accumulator[2] << 16)
                    | (accumulator[3] << 24);

                if (length <= 0)
                {
                    IsFaulted = true;
                    throw new PbjProtocolException("Frame length " + length + " is not positive.");
                }
                if (length > maxFrameLength)
                {
                    IsFaulted = true;
                    throw new PbjProtocolException(
                        "Frame length " + length + " exceeds the maximum of " + maxFrameLength + ".");
                }
                if (accumulator.Count < FrameEncoder.HeaderLength + length)
                {
                    break;
                }

                var frame = new byte[length];
                accumulator.CopyTo(FrameEncoder.HeaderLength, frame, 0, length);
                accumulator.RemoveRange(0, FrameEncoder.HeaderLength + length);

                frames ??= new List<byte[]>();
                frames.Add(frame);
            }

            return frames ?? NoFrames;
        }
    }
}
