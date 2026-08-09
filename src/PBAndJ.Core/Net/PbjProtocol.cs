using System;

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

        /// <summary>Peer is running a different build of this mod.</summary>
        ModVersionMismatch = 9,

        /// <summary>Peer's Phantom Brigade build differs from the host's.</summary>
        GameBuildMismatch = 10,

        /// <summary>Peer did not present the session passphrase.</summary>
        BadPassphrase = 11,
    }

    /// <summary>
    /// What a host demands of anything that connects to it.
    /// </summary>
    /// <remarks>
    /// Grouped rather than passed as three more constructor arguments, because
    /// they are one decision: "who is allowed into this session". They only
    /// started mattering when a peer stopped being another process on the same
    /// machine.
    /// </remarks>
    public sealed class SessionRequirements
    {
        /// <summary>Accepts any build and needs no passphrase — the loopback default.</summary>
        public static readonly SessionRequirements None = new SessionRequirements(null, null, null);

        public SessionRequirements(string? modVersion, string? gameBuild, string? passphrase)
        {
            ModVersion = modVersion;
            GameBuild = gameBuild;
            Passphrase = passphrase;
        }

        /// <summary>This host's mod build. A peer must match it exactly.</summary>
        public string? ModVersion { get; }

        /// <summary>
        /// This host's Phantom Brigade build, or null if it has no game.
        /// </summary>
        /// <remarks>
        /// Null on both sides is how the harness plays against itself. Null on
        /// either side skips the check rather than failing it.
        /// </remarks>
        public string? GameBuild { get; }

        /// <summary>Null or empty means the listener is open to anyone who reaches it.</summary>
        public string? Passphrase { get; }

        /// <summary>Whether a peer has to present anything to be let in.</summary>
        public bool RequiresPassphrase => !string.IsNullOrEmpty(Passphrase);
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
        /// <para>
        /// v3 (M7) added <c>GameBuild</c> and <c>Passphrase</c> to
        /// <see cref="HelloMessage"/> and <see cref="RejoinMessage"/>, for play
        /// between two machines that are not the same machine. M6's
        /// <c>Keyframes</c> did not bump it: a new message type leaves every
        /// existing layout untouched.
        /// </para>
        /// <para>
        /// M9's three scenario-transfer types did not bump it either, for the
        /// same reason. That leaves a peer built before them liable to fault on
        /// an unrecognised <c>ScenarioOffer</c> — but it cannot get that far:
        /// <see cref="ModVersion"/> moved to 0.4.0 in the same change, and the
        /// handshake refuses a peer whose mod build differs before any offer is
        /// sent. The mod version is the real compatibility gate; this constant
        /// guards <em>layout</em>, and no layout moved.
        /// </para>
        /// <para>
        /// M11a's three lobby types follow the same rule, and lean on the same
        /// gate: <see cref="ModVersion"/> moved to 0.7.0 in the very commit that
        /// added them, deliberately rather than at the next release. A host
        /// broadcasts <c>LobbyState</c> on every handshake, so a peer that got
        /// in without those types would fault on its first message — the mod
        /// version has to move with the surface, not after it.
        /// </para>
        /// </remarks>
        public const int Version = 3;

        /// <summary>
        /// This build of the mod, as peers announce it to each other.
        /// </summary>
        /// <remarks>
        /// Lives in Core because both things that report it — the mod's glue and
        /// the standalone harness — reference Core and nothing else in common.
        /// It used to be a literal in each of them, which is how a packaged
        /// harness went out announcing 0.2.0 against a 0.3.0 host and was refused
        /// on someone else's machine. Must be kept equal to <c>ver:</c> in
        /// mod/metadata.yaml; the Makefile refuses to build a distributable when
        /// they disagree, since that is the one file this constant cannot reach.
        /// </remarks>
        public const string ModVersion = "0.11.0";

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

        /// <summary>
        /// How long a connected socket may go without sending a handshake.
        /// </summary>
        /// <remarks>
        /// Shorter than <see cref="PeerTimeoutSeconds"/> on purpose: an accepted
        /// peer has proven it speaks the protocol and gets the benefit of the
        /// doubt through a hitch, while a socket that has said nothing at all has
        /// proven nothing. On a loopback-only listener this barely matters; on an
        /// internet-facing one it is the difference between a bounded connection
        /// table and an unbounded one, since anything that connects and stays
        /// mute would otherwise sit there forever.
        /// </remarks>
        public const double HandshakeTimeoutSeconds = 10.0;

        /// <summary>
        /// How long the host waits for a peer to report that it has loaded.
        /// </summary>
        /// <remarks>
        /// Far longer than any other timeout here, and deliberately generous. A
        /// campaign load is not a network round trip: it pops a controller state,
        /// waits two frames, reads a zipped save off disk, rebuilds the ECS and
        /// comes back through several more deferred frames. Nobody has measured
        /// it, so this is a bound rather than an estimate.
        /// <para>
        /// A false timeout is worse than a slow one. The failure path leaves
        /// <c>isLoadingInProgress</c> set on at least one route, so a load that
        /// fails may be terminal for that peer rather than retryable — which
        /// makes waiting too long cheap and giving up too early expensive.
        /// </para>
        /// </remarks>
        public const double LoadTimeoutSeconds = 120.0;

        /// <summary>
        /// Checks that a peer is running something we can actually play with.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Check"/>, which answers "does this speak our
        /// protocol at all". This answers "and is it the same build" — a
        /// distinction that only started mattering when peers stopped being the
        /// same machine. Without it a friend on a different game patch connects
        /// perfectly and then diverges on every single turn, which reads as a
        /// netcode bug and is miserable to diagnose remotely.
        /// <para>
        /// The passphrase is checked <em>first</em> so a peer that cannot
        /// authenticate learns only that, rather than our exact mod version and
        /// game build. It is compared in the clear over plain TCP: it keeps
        /// opportunistic connections out, and is not confidentiality against
        /// anyone on the path.
        /// </para>
        /// <para>
        /// A missing game build is "cannot say", not "does not match" — the
        /// standalone harness has no game and is a legitimate peer, which is how
        /// every in-game gate since M4 has been run.
        /// </para>
        /// </remarks>
        public static RejectReason? CheckCompatibility(
            string? hostModVersion,
            string? peerModVersion,
            string? hostGameBuild,
            string? peerGameBuild,
            string? requiredPassphrase,
            string? offeredPassphrase)
        {
            if (!string.IsNullOrEmpty(requiredPassphrase)
                && !string.Equals(requiredPassphrase, offeredPassphrase, StringComparison.Ordinal))
            {
                return RejectReason.BadPassphrase;
            }

            if (Differs(hostModVersion, peerModVersion))
            {
                return RejectReason.ModVersionMismatch;
            }

            if (Differs(hostGameBuild, peerGameBuild))
            {
                return RejectReason.GameBuildMismatch;
            }

            return null;
        }

        /// <summary>
        /// Whether two declared identities positively disagree.
        /// </summary>
        /// <remarks>
        /// An absent value on either side means "cannot say", never "does not
        /// match" — a rule that has to hold for both fields or the harness stops
        /// being able to connect to anything. It declares neither a mod version
        /// nor a game build, and it is the peer every in-game gate since M4 has
        /// been run with. A real host and a real client both declare both, so the
        /// check still bites exactly where it was added to bite.
        /// </remarks>
        private static bool Differs(string? mine, string? theirs)
        {
            return !string.IsNullOrEmpty(mine)
                && !string.IsNullOrEmpty(theirs)
                && !string.Equals(mine, theirs, StringComparison.Ordinal);
        }

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
