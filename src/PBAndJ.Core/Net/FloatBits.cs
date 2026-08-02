using System.Runtime.InteropServices;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Reinterprets a float as its IEEE-754 bit pattern and back.
    /// </summary>
    /// <remarks>
    /// netstandard2.0 has no <c>BitConverter.SingleToInt32Bits</c>, and
    /// <c>BitConverter.GetBytes</c> would force an <c>IsLittleEndian</c> branch
    /// that is unreachable on every platform we run on — and therefore
    /// uncoverable. An explicit-layout union plus explicit shifts in
    /// <see cref="PbjWriter"/> makes the wire little-endian by construction,
    /// with no branches at all.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)]
        public float Single;

        [FieldOffset(0)]
        public int Int32;
    }
}
