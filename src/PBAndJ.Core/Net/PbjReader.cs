using System;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Reads protocol primitives from a byte buffer, in the layout
    /// <see cref="PbjWriter"/> produces. Every read is bounds-checked and
    /// raises <see cref="PbjProtocolException"/> rather than trusting the peer.
    /// </summary>
    public sealed class PbjReader
    {
        private const int NullStringLength = -1;

        private readonly byte[] buffer;
        private int position;

        public PbjReader(byte[] buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            this.buffer = buffer;
        }

        public byte ReadByte()
        {
            Require(1);
            return buffer[position++];
        }

        public bool ReadBool()
        {
            // Any non-zero byte reads as true: tolerating that costs no branch,
            // whereas rejecting it would add one with no protocol benefit.
            return ReadByte() != 0;
        }

        public int ReadInt32()
        {
            Require(4);
            var value = buffer[position]
                | (buffer[position + 1] << 8)
                | (buffer[position + 2] << 16)
                | (buffer[position + 3] << 24);
            position += 4;
            return value;
        }

        public float ReadSingle()
        {
            var bits = default(FloatBits);
            bits.Int32 = ReadInt32();
            return bits.Single;
        }

        public string? ReadString()
        {
            var length = ReadInt32();
            if (length == NullStringLength)
            {
                return null;
            }
            if (length < 0)
            {
                throw new PbjProtocolException("String length " + length + " is negative.");
            }
            if (length > PbjWriter.MaxStringLength)
            {
                throw new PbjProtocolException(
                    "String length " + length + " exceeds the maximum of " + PbjWriter.MaxStringLength + ".");
            }

            Require(length);
            var value = Encoding.UTF8.GetString(buffer, position, length);
            position += length;
            return value;
        }

        /// <summary>
        /// Asserts the whole buffer was consumed. Trailing bytes mean the peer
        /// and we disagree about the message layout, which is a protocol error.
        /// </summary>
        public void EnsureConsumed()
        {
            if (position != buffer.Length)
            {
                throw new PbjProtocolException(
                    "Message has " + (buffer.Length - position) + " trailing byte(s).");
            }
        }

        private void Require(int count)
        {
            if (position + count > buffer.Length)
            {
                throw new PbjProtocolException(
                    "Truncated message: need " + count + " byte(s) at offset " + position
                    + " but only " + (buffer.Length - position) + " remain.");
            }
        }
    }
}
