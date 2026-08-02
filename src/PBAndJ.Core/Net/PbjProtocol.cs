namespace PBAndJ.Core.Net
{
    /// <summary>Why a host refused a connection.</summary>
    public enum RejectReason
    {
        None = 0,
        BadMagic = 1,
        VersionMismatch = 2,
        SessionFull = 3,
        DuplicateName = 4,
        InvalidName = 5,
        NotAcceptingPeers = 6,
    }

    /// <summary>
    /// Protocol identity and compatibility check.
    /// </summary>
    public static class PbjProtocol
    {
        /// <summary>"PJB1" as a little-endian int32 — sanity check that the peer speaks our protocol.</summary>
        public const int Magic = 0x504A4231;

        /// <summary>
        /// Wire format version. Bump on ANY change to message layout or
        /// <see cref="OrderPayloadCodec"/>'s field order.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// Validates a peer's handshake header. Returns null when acceptable.
        /// </summary>
        public static RejectReason? Check(int magic, int protocolVersion)
        {
            if (magic != Magic)
            {
                // Not our protocol at all — the version number is meaningless.
                return RejectReason.BadMagic;
            }
            if (protocolVersion != Version)
            {
                return RejectReason.VersionMismatch;
            }
            return null;
        }
    }
}
