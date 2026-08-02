using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Discriminator byte at the head of every encoded message.
    /// </summary>
    /// <remarks>
    /// Values are assigned once and never reused. 19+ are unallocated.
    /// </remarks>
    public enum PbjMessageType : byte
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        PeerJoined = 4,
        PeerLeft = 5,
        Ready = 6,
        TurnCommit = 7,
        TurnComplete = 8,
        Bye = 9,
        Assignments = 10,
        Unready = 11,
        OrderResult = 12,
        CombatStart = 13,
        CombatEnd = 14,
        Ping = 15,
        Pong = 16,
        Snapshot = 17,
        Rejoin = 18,
        Keyframes = 19,
    }

    /// <summary>
    /// Base for everything that travels over the wire.
    /// </summary>
    /// <remarks>
    /// The constructor is <c>protected</c> and <see cref="Type"/> is
    /// <c>abstract</c> so the test suite can define an out-of-range subclass and
    /// reach <see cref="PbjMessageCodec"/>'s <c>default:</c> arm. Sealing either
    /// would leave that arm permanently uncovered and break the 100% gate.
    /// <para>
    /// Messages carry no validation — see docs/design/networking.md,
    /// "Messages do not validate; sessions do".
    /// </para>
    /// </remarks>
    public abstract class PbjMessage
    {
        protected PbjMessage()
        {
        }

        public abstract PbjMessageType Type { get; }
    }

    /// <summary>One entry in a session roster.</summary>
    public readonly struct PeerInfo
    {
        public PeerInfo(int peerId, string? name)
        {
            PeerId = peerId;
            Name = name;
        }

        public int PeerId { get; }
        public string? Name { get; }
    }

    /// <summary>Client's opening request. Arrives from an unauthenticated stranger.</summary>
    public sealed class HelloMessage : PbjMessage
    {
        public HelloMessage(
            int magic,
            int protocolVersion,
            string? modVersion,
            string? playerName,
            string? gameBuild,
            string? passphrase)
        {
            Magic = magic;
            ProtocolVersion = protocolVersion;
            ModVersion = modVersion;
            PlayerName = playerName;
            GameBuild = gameBuild;
            Passphrase = passphrase;
        }

        public override PbjMessageType Type => PbjMessageType.Hello;

        public int Magic { get; }
        public int ProtocolVersion { get; }
        public string? ModVersion { get; }
        public string? PlayerName { get; }

        /// <summary>
        /// The peer's Phantom Brigade build, or null if it has no game.
        /// </summary>
        /// <remarks>
        /// Null is a real and expected answer: the standalone harness is a
        /// legitimate peer with no game to report. The host therefore treats an
        /// absent build as "cannot say" rather than "does not match".
        /// </remarks>
        public string? GameBuild { get; }

        /// <summary>
        /// The session passphrase, when the host requires one.
        /// </summary>
        /// <remarks>
        /// Travels in the clear over plain TCP. It exists so an internet-facing
        /// listener is not open to anything that finds the port — this protocol
        /// is public, so without it any scanner could join and inject orders. It
        /// is not confidentiality against anyone on the network path.
        /// </remarks>
        public string? Passphrase { get; }
    }

    /// <summary>Host's acceptance, carrying the roster and the current turn.</summary>
    public sealed class WelcomeMessage : PbjMessage
    {
        private static readonly PeerInfo[] NoPeers = new PeerInfo[0];

        public WelcomeMessage(
            int protocolVersion,
            string? sessionId,
            int assignedPeerId,
            string? hostName,
            IReadOnlyList<PeerInfo>? peers,
            int currentTurn,
            string? resumeToken)
        {
            ProtocolVersion = protocolVersion;
            SessionId = sessionId;
            AssignedPeerId = assignedPeerId;
            HostName = hostName;
            Peers = peers ?? NoPeers;
            CurrentTurn = currentTurn;
            ResumeToken = resumeToken;
        }

        public override PbjMessageType Type => PbjMessageType.Welcome;

        public int ProtocolVersion { get; }
        public string? SessionId { get; }
        public int AssignedPeerId { get; }
        public string? HostName { get; }
        public IReadOnlyList<PeerInfo> Peers { get; }

        /// <summary>Without this a joining peer cannot construct a matching Ready.</summary>
        public int CurrentTurn { get; }

        /// <summary>
        /// What this peer must present to reclaim its units after a drop.
        /// </summary>
        /// <remarks>
        /// Adding this field is the reason the wire version is 2. Derived from a
        /// secret the host mints per session and never sends, so it cannot be
        /// computed from anything else on the wire — see
        /// <see cref="RejoinMessage"/>.
        /// </remarks>
        public string? ResumeToken { get; }
    }

    /// <summary>
    /// A returning player reclaiming the units its previous connection held.
    /// </summary>
    /// <remarks>
    /// A distinct type rather than an extended <see cref="HelloMessage"/>: Hello
    /// arrives from an unauthenticated stranger and its layout is pinned by
    /// byte-exact wire tests, and joining and rejoining want separate switch
    /// arms, separate reject reasons and separate tests.
    /// <para>
    /// The magic and version fields mirror Hello's so the same
    /// <see cref="PbjProtocol.Check"/> runs against both.
    /// </para>
    /// </remarks>
    public sealed class RejoinMessage : PbjMessage
    {
        public RejoinMessage(
            int magic,
            int protocolVersion,
            string? modVersion,
            string? playerName,
            string? sessionId,
            int claimedPeerId,
            string? resumeToken,
            string? gameBuild,
            string? passphrase)
        {
            Magic = magic;
            ProtocolVersion = protocolVersion;
            ModVersion = modVersion;
            PlayerName = playerName;
            SessionId = sessionId;
            ClaimedPeerId = claimedPeerId;
            ResumeToken = resumeToken;
            GameBuild = gameBuild;
            Passphrase = passphrase;
        }

        /// <inheritdoc cref="HelloMessage.GameBuild"/>
        public string? GameBuild { get; }

        /// <summary>
        /// The session passphrase, re-presented on return.
        /// </summary>
        /// <remarks>
        /// Carried again rather than being implied by the resume token: a
        /// returning peer is as unauthenticated as a new one until the host has
        /// checked something, and the token proves which departure this is, not
        /// that the sender belongs on this listener at all.
        /// </remarks>
        public string? Passphrase { get; }

        public override PbjMessageType Type => PbjMessageType.Rejoin;

        public int Magic { get; }
        public int ProtocolVersion { get; }
        public string? ModVersion { get; }
        public string? PlayerName { get; }

        /// <summary>The session this peer believes it is returning to.</summary>
        public string? SessionId { get; }

        /// <summary>The peer id its previous connection held.</summary>
        public int ClaimedPeerId { get; }

        public string? ResumeToken { get; }
    }

    /// <summary>Host's refusal, sent immediately before disconnecting.</summary>
    public sealed class RejectMessage : PbjMessage
    {
        public RejectMessage(RejectReason reason, string? detail)
        {
            Reason = reason;
            Detail = detail;
        }

        public override PbjMessageType Type => PbjMessageType.Reject;

        public RejectReason Reason { get; }
        public string? Detail { get; }
    }

    /// <summary>Roster addition, broadcast to everyone already connected.</summary>
    public sealed class PeerJoinedMessage : PbjMessage
    {
        public PeerJoinedMessage(int peerId, string? name)
        {
            PeerId = peerId;
            Name = name;
        }

        public override PbjMessageType Type => PbjMessageType.PeerJoined;

        public int PeerId { get; }
        public string? Name { get; }
    }

    /// <summary>Roster removal. Receiving it must recompute the turn barrier.</summary>
    public sealed class PeerLeftMessage : PbjMessage
    {
        public PeerLeftMessage(int peerId, string? name)
        {
            PeerId = peerId;
            Name = name;
        }

        public override PbjMessageType Type => PbjMessageType.PeerLeft;

        public int PeerId { get; }
        public string? Name { get; }
    }

    /// <summary>
    /// A peer's complete order set for one turn. Idempotent: re-sending replaces
    /// the previous batch for that turn rather than adding to it.
    /// </summary>
    public sealed class ReadyMessage : PbjMessage
    {
        private static readonly OrderPayload[] NoOrders = new OrderPayload[0];

        public ReadyMessage(int turn, IReadOnlyList<OrderPayload>? orders)
        {
            Turn = turn;
            Orders = orders ?? NoOrders;
        }

        public override PbjMessageType Type => PbjMessageType.Ready;

        public int Turn { get; }
        public IReadOnlyList<OrderPayload> Orders { get; }
    }

    /// <summary>Host has verified the commit landed; execution is starting.</summary>
    public sealed class TurnCommitMessage : PbjMessage
    {
        public TurnCommitMessage(int turn)
        {
            Turn = turn;
        }

        public override PbjMessageType Type => PbjMessageType.TurnCommit;

        public int Turn { get; }
    }

    /// <summary>
    /// Execution finished. <see cref="Turn"/> is the turn that just executed,
    /// captured at commit time — the ECS has already advanced past it.
    /// </summary>
    public sealed class TurnCompleteMessage : PbjMessage
    {
        public TurnCompleteMessage(int turn, string? digest)
        {
            Turn = turn;
            Digest = digest;
        }

        public override PbjMessageType Type => PbjMessageType.TurnComplete;

        public int Turn { get; }
        public string? Digest { get; }
    }

    /// <summary>One peer's share of the units, inside an <see cref="AssignmentsMessage"/>.</summary>
    public sealed class PeerAssignment
    {
        private static readonly string[] NoUnits = new string[0];

        public PeerAssignment(int peerId, IReadOnlyList<string>? unitNames)
        {
            PeerId = peerId;
            UnitNames = unitNames ?? NoUnits;
        }

        public int PeerId { get; }
        public IReadOnlyList<string> UnitNames { get; }
    }

    /// <summary>
    /// Who controls which units. Broadcast when combat starts and whenever the
    /// roster changes.
    /// </summary>
    /// <remarks>
    /// A client cannot function without this — it needs to know which units it
    /// may plan. It is advisory only: the host re-checks every incoming order
    /// against its own copy, so a client that ignores this message simply gets
    /// its orders rejected.
    /// </remarks>
    public sealed class AssignmentsMessage : PbjMessage
    {
        private static readonly PeerAssignment[] None = new PeerAssignment[0];

        public AssignmentsMessage(IReadOnlyList<PeerAssignment>? assignments)
        {
            Assignments = assignments ?? None;
        }

        public override PbjMessageType Type => PbjMessageType.Assignments;

        public IReadOnlyList<PeerAssignment> Assignments { get; }
    }

    /// <summary>
    /// A peer withdrawing a <see cref="ReadyMessage"/> it already sent, so it can
    /// re-plan before the barrier fills.
    /// </summary>
    /// <remarks>
    /// Idempotent, like Ready itself: un-readying when not ready is a no-op, not
    /// an error. The turn is carried so a late Unready for an already-committed
    /// turn can be recognised and ignored.
    /// </remarks>
    public sealed class UnreadyMessage : PbjMessage
    {
        public UnreadyMessage(int turn)
        {
            Turn = turn;
        }

        public override PbjMessageType Type => PbjMessageType.Unready;

        public int Turn { get; }
    }

    /// <summary>
    /// One order the host refused, identified by its position in the
    /// <see cref="ReadyMessage"/> batch that carried it.
    /// </summary>
    /// <remarks>
    /// Orders have no stable id — that is the whole reason for batch-at-ready —
    /// so the batch index is the only unambiguous handle. Echoing
    /// (ownerName, blueprint) instead would be self-describing in logs but
    /// ambiguous the moment a unit has two orders of the same type in one turn.
    /// </remarks>
    public readonly struct RejectedOrder
    {
        public RejectedOrder(int index, OrderApplyResult reason)
        {
            Index = index;
            Reason = reason;
        }

        /// <summary>Index into the submitted batch.</summary>
        public int Index { get; }

        public OrderApplyResult Reason { get; }
    }

    /// <summary>
    /// What became of one peer's submitted batch. Sent only after a successful
    /// commit — a refused commit re-opens planning, so its orders have no
    /// outcome to report yet.
    /// </summary>
    public sealed class OrderResultMessage : PbjMessage
    {
        private static readonly RejectedOrder[] NoRejections = new RejectedOrder[0];

        public OrderResultMessage(int turn, int accepted, IReadOnlyList<RejectedOrder>? rejected)
        {
            Turn = turn;
            Accepted = accepted;
            Rejected = rejected ?? NoRejections;
        }

        public override PbjMessageType Type => PbjMessageType.OrderResult;

        public int Turn { get; }
        public int Accepted { get; }
        public IReadOnlyList<RejectedOrder> Rejected { get; }
    }

    /// <summary>
    /// The host entered combat. Always followed immediately by an
    /// <see cref="AssignmentsMessage"/>.
    /// </summary>
    public sealed class CombatStartMessage : PbjMessage
    {
        public CombatStartMessage(int turn)
        {
            Turn = turn;
        }

        public override PbjMessageType Type => PbjMessageType.CombatStart;

        public int Turn { get; }
    }

    /// <summary>
    /// The host's combat resolved. Unlocks every peer, including any sitting
    /// Ready when it happened.
    /// </summary>
    /// <remarks>
    /// Carries no payload: the type byte is the entire message. Adding a field
    /// later is a wire break, which is why a test pins the encoded length.
    /// </remarks>
    public sealed class CombatEndMessage : PbjMessage
    {
        public override PbjMessageType Type => PbjMessageType.CombatEnd;
    }

    /// <summary>
    /// Host's keepalive probe. The client answers with a matching
    /// <see cref="PongMessage"/>.
    /// </summary>
    /// <remarks>
    /// Carries a nonce, not a timestamp: Core never reads a clock, and putting
    /// one process's time on the wire would imply a clock synchronisation the
    /// protocol does not have. The nonce is free now and makes a round-trip-time
    /// measurement possible later with no wire change.
    /// </remarks>
    public sealed class PingMessage : PbjMessage
    {
        public PingMessage(int nonce)
        {
            Nonce = nonce;
        }

        public override PbjMessageType Type => PbjMessageType.Ping;

        public int Nonce { get; }
    }

    /// <summary>
    /// Client's answer to a <see cref="PingMessage"/>. Its only job is to be
    /// inbound traffic, which is what keeps the host's timer for this peer alive.
    /// </summary>
    public sealed class PongMessage : PbjMessage
    {
        public PongMessage(int nonce)
        {
            Nonce = nonce;
        }

        public override PbjMessageType Type => PbjMessageType.Pong;

        public int Nonce { get; }
    }

    /// <summary>
    /// Authoritative unit state at the end of an executed turn — the correction
    /// a client hard-sets to, since it never simulates.
    /// </summary>
    /// <remarks>
    /// Broadcast immediately after <see cref="TurnCompleteMessage"/>, never
    /// before it. Sending the snapshot first would leave the client's digest
    /// already matching by the time TurnComplete was compared, silencing the
    /// divergence diagnostic permanently. The client genuinely <em>is</em>
    /// diverged at that instant, so reporting it is honest; the interesting
    /// number is the one it reports after applying this.
    /// <para>
    /// <see cref="Digest"/> is the host's own, over the same units, so the client
    /// can check its correction landed rather than merely assuming it did.
    /// </para>
    /// </remarks>
    public sealed class SnapshotMessage : PbjMessage
    {
        private static readonly UnitSnapshot[] NoUnits = new UnitSnapshot[0];

        public SnapshotMessage(int turn, string? digest, IReadOnlyList<UnitSnapshot>? units)
        {
            Turn = turn;
            Digest = digest;
            Units = units ?? NoUnits;
        }

        public override PbjMessageType Type => PbjMessageType.Snapshot;

        /// <summary>The turn that just executed, captured at commit time.</summary>
        public int Turn { get; }

        public string? Digest { get; }
        public IReadOnlyList<UnitSnapshot> Units { get; }
    }

    /// <summary>
    /// How every unit moved during an executed turn — what a client plays back
    /// so it watches the turn instead of being teleported to its outcome.
    /// </summary>
    /// <remarks>
    /// Broadcast after <see cref="SnapshotMessage"/>, not before it. The snapshot
    /// is the authoritative correction and the thing the digest is checked
    /// against; keyframes are presentation and must never be able to delay or
    /// displace it. Applying the correction first and then animating towards the
    /// same final state costs nothing, because the last key of every track is
    /// captured from the same read the snapshot is — see the capture rules in
    /// docs/design/networking.md.
    /// <para>
    /// Two orders of magnitude heavier than a snapshot (~51 KB for a 30-unit
    /// turn against ~2.6 KB), which is where the outbound queue and the writer
    /// thread start genuinely earning their place.
    /// </para>
    /// </remarks>
    public sealed class KeyframesMessage : PbjMessage
    {
        private static readonly UnitTrack[] NoTracks = new UnitTrack[0];

        public KeyframesMessage(
            int turn, float windowStart, float windowEnd, IReadOnlyList<UnitTrack>? tracks)
        {
            Turn = turn;
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            Tracks = tracks ?? NoTracks;
        }

        public override PbjMessageType Type => PbjMessageType.Keyframes;

        /// <summary>The turn that just executed, captured at commit time.</summary>
        public int Turn { get; }

        /// <summary>
        /// Simulation time at the start of the executed turn.
        /// </summary>
        /// <remarks>
        /// Travels explicitly rather than being read off the first key: a unit
        /// destroyed early has a short track, and every track has to be played
        /// against one shared time base or units drift apart on screen.
        /// </remarks>
        public float WindowStart { get; }

        /// <summary>Simulation time when execution ended.</summary>
        public float WindowEnd { get; }

        public IReadOnlyList<UnitTrack> Tracks { get; }
    }

    /// <summary>Graceful goodbye from either side.</summary>
    public sealed class ByeMessage : PbjMessage
    {
        public ByeMessage(string? reason)
        {
            Reason = reason;
        }

        public override PbjMessageType Type => PbjMessageType.Bye;

        public string? Reason { get; }
    }
}
