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

        /// <summary>A rejoin named a session this host is not running.</summary>
        UnknownSession = 7,

        /// <summary>A rejoin's token did not match the departure it claimed.</summary>
        BadResumeToken = 8,
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
        /// <remarks>
        /// v2 (M5e) added <c>ResumeToken</c> to <see cref="WelcomeMessage"/>.
        /// The M5 message types added before that — 11 through 17 — left every
        /// existing layout untouched and so kept v1, matching how
        /// <c>Assignments</c> was pulled forward during M4.
        /// </remarks>
        public const int Version = 2;

        /// <summary>
        /// How long a departed peer's units stay reserved for its return.
        /// </summary>
        /// <remarks>
        /// While the reservation stands the host does <em>not</em> re-plan
        /// assignments, so those units sit bound to a peer id no live connection
        /// holds — visible, uncommandable, and waiting. Reassignment happens when
        /// this expires.
        /// </remarks>
        public const double ReconnectGraceSeconds = 120.0;

        /// <summary>
        /// Validates a peer's handshake header. Returns null when acceptable.
        /// </summary>
        /// <summary>
        /// Shortest gap between synthesized ticks. Throttles the timeout machinery
        /// so it does not allocate an effect list every frame.
        /// </summary>
        public const double TickIntervalSeconds = 0.25;

        /// <summary>How often the host pings a quiet peer.</summary>
        public const double PingIntervalSeconds = 5.0;

        /// <summary>Silence after which the host drops a peer — four missed pings.</summary>
        public const double PeerTimeoutSeconds = 20.0;

        /// <summary>
        /// Silence after which a client gives up on the host.
        /// </summary>
        /// <remarks>
        /// Deliberately longer than <see cref="PeerTimeoutSeconds"/>. The host is
        /// the side that hitches — scenario loads and shader compilation under
        /// Proton routinely stall for seconds — and a client fault is terminal,
        /// with no automatic recovery. Symmetric timeouts would let one long host
        /// hitch permanently kill every client.
        /// </remarks>
        public const double HostTimeoutSeconds = 30.0;

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
