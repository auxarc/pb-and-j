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

        /// <summary>
        /// Longest opaque byte blob accepted on the wire, in bytes.
        /// </summary>
        /// <remarks>
        /// Four orders of magnitude above <see cref="MaxStringLength"/> because
        /// blobs carry files rather than identifiers — M9's scenario transfer is
        /// the only caller. Deliberately half of
        /// <see cref="PbjRuntime.MaxFrameLength"/>, so a single blob can never on
        /// its own fill a frame and the message layer still has room for the
        /// envelope around it. The message layer applies its own, tighter cap on
        /// the *total* of a multi-blob message; this is the floor under both.
        /// </remarks>
        public const int MaxBytesLength = 1 << 19;

        private const int NullLength = -1;

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
                WriteInt32(NullLength);
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

        /// <summary>
        /// Writes an opaque blob: a length prefix then the bytes verbatim, with
        /// the same <c>-1</c> null sentinel <see cref="WriteString"/> uses.
        /// </summary>
        /// <remarks>
        /// No encoding, no compression, no interpretation. The one caller
        /// carries save-file contents, which are already a zip.
        /// </remarks>
        public void WriteBytes(byte[]? value)
        {
            if (value == null)
            {
                WriteInt32(NullLength);
                return;
            }

            if (value.Length > MaxBytesLength)
            {
                throw new PbjProtocolException(
                    "Blob of " + value.Length + " bytes exceeds the maximum of " + MaxBytesLength + ".");
            }

            WriteInt32(value.Length);
            buffer.AddRange(value);
        }

        public byte[] ToArray()
        {
            return buffer.ToArray();
        }
    }
}
