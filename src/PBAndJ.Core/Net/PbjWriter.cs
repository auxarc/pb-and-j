using System.Collections.Generic;
using System.Text;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Appends protocol primitives to a byte buffer. Little-endian, no
    /// alignment, no compression. Pairs with <see cref="PbjReader"/>.
    /// </summary>
    /// <remarks>
    /// Backed by a <see cref="List{T}"/> rather than a MemoryStream: no
    /// IDisposable, so no try/finally noise in every call site.
    /// </remarks>
    public sealed class PbjWriter
    {
        /// <summary>Longest UTF-8 string accepted on the wire, in bytes.</summary>
        public const int MaxStringLength = 4096;

        private const int NullStringLength = -1;

        private readonly List<byte> buffer = new List<byte>();

        public void WriteByte(byte value)
        {
            buffer.Add(value);
        }

        public void WriteBool(bool value)
        {
            buffer.Add(value ? (byte)1 : (byte)0);
        }

        public void WriteInt32(int value)
        {
            buffer.Add((byte)value);
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 24));
        }

        public void WriteSingle(float value)
        {
            var bits = default(FloatBits);
            bits.Single = value;
            WriteInt32(bits.Int32);
        }

        public void WriteString(string? value)
        {
            if (value == null)
            {
                WriteInt32(NullStringLength);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > MaxStringLength)
            {
                throw new PbjProtocolException(
                    "String of " + bytes.Length + " bytes exceeds the maximum of " + MaxStringLength + ".");
            }

            WriteInt32(bytes.Length);
            buffer.AddRange(bytes);
        }

        public byte[] ToArray()
        {
            return buffer.ToArray();
        }
    }
}
