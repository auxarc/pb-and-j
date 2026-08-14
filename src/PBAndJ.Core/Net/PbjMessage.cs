using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Discriminator byte at the head of every encoded message.
    /// </summary>
    /// <remarks>
    /// Values are assigned once and never reused. 31+ are unallocated.
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
        ScenarioOffer = 20,
        ScenarioRequest = 21,
        Scenario = 22,
        LobbyState = 23,
        LobbyReady = 24,
        LobbyUnready = 25,
        LobbyLoad = 26,
        LobbyLoaded = 27,

        /// <summary>Where the host's mobile base is. Host to client only.</summary>
        BasePosition = 28,

        /// <summary>The host's fight is on disk and ready to be fetched.</summary>
        CombatOffer = 29,

        /// <summary>A client reporting whether it made it into the fight.</summary>
        CombatEntered = 30,

        /// <summary>One unit's skeletal animation for the turn just executed. M8.</summary>
        Poses = 31,
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

    /// <summary>
    /// One unit's skeletal animation for the turn just executed. M8.
    /// </summary>
    /// <remarks>
    /// <b>One track per message, and the messages precede
    /// <see cref="KeyframesMessage"/>, which terminates them.</b> Three
    /// decisions live in that sentence and each one replaced something worse.
    /// <para>
    /// <i>Poses before transforms.</i> The client cannot begin playback until it
    /// knows whether it has poses, because a unit slept for replay but holding
    /// no pose is a rigid statue sliding down its path — worse than M6's
    /// idle-animated slide, not equal to it. Sending poses first and letting the
    /// existing transform message end the burst makes "the terminator arrived"
    /// mean "everything arrived", since one send site and an ordered stream
    /// admit no other outcome. The alternative was a deadline, i.e. a timer that
    /// is either too short on a slow link or a visible stall on a fast one.
    /// </para>
    /// <para>
    /// <i>One track per message.</i> Packing several under a byte budget needs a
    /// rule for the track that alone exceeds it — an arm that cannot be reached
    /// once the caps below bound a single track, and an unreachable arm fails
    /// the coverage gate. One track per message makes the size bound provable
    /// from the caps rather than from a heuristic.
    /// </para>
    /// <para>
    /// <i>The part numbering is still carried</i> even though it is derivable by
    /// counting, because a client must be able to distinguish "the host sent
    /// four and I have four" from "the host sent five".
    /// </para>
    /// </remarks>
    public sealed class PosesMessage : PbjMessage
    {
        public PosesMessage(int turn, int partIndex, int partCount, UnitPoseTrack? track)
        {
            Turn = turn;
            PartIndex = partIndex;
            PartCount = partCount;
            Track = track;
        }

        public override PbjMessageType Type => PbjMessageType.Poses;

        /// <summary>
        /// The turn these poses belong to, matching
        /// <see cref="KeyframesMessage.Turn"/>.
        /// </summary>
        /// <remarks>
        /// The client compares this against the terminator's turn and against
        /// its own buffer label — <b>never</b> against its session's current
        /// turn, which by then reads one higher: <c>TurnComplete</c> travels
        /// ahead of these and advances it. Comparing against session state would
        /// discard every part on every turn and fall back to transform-only
        /// forever, silently.
        /// </remarks>
        public int Turn { get; }

        public int PartIndex { get; }

        public int PartCount { get; }

        public UnitPoseTrack? Track { get; }
    }

    /// <summary>
    /// "I have a combat save, and this is which one." Host to peer, in the
    /// lobby.
    /// </summary>
    /// <remarks>
    /// An offer rather than a push, for three reasons. A peer that already holds
    /// this save skips ~124 KB, which matters most on a rejoin, when it holds it
    /// by definition. A peer that is mid-combat should not have a save dropped on
    /// it unasked. And accept/decline is the shape a real lobby needs anyway —
    /// M9 is the first piece of one, not a one-off file copy.
    /// </remarks>
    public sealed class ScenarioOfferMessage : PbjMessage
    {
        public ScenarioOfferMessage(string? saveName, int totalBytes, string? digest)
        {
            SaveName = saveName;
            TotalBytes = totalBytes;
            Digest = digest;
        }

        public override PbjMessageType Type => PbjMessageType.ScenarioOffer;

        /// <summary>What the host calls this save. Informational — see <see cref="ScenarioPayload"/>.</summary>
        public string? SaveName { get; }

        /// <summary>Summed file size, so a peer can refuse before the bytes arrive.</summary>
        public int TotalBytes { get; }

        /// <summary>Identifies the contents, so a peer holding them can decline.</summary>
        public string? Digest { get; }
    }

    /// <summary>"Send me that scenario." Peer to host.</summary>
    /// <remarks>
    /// Carries the digest it is answering so a host that has re-saved between
    /// offer and request does not answer a stale question. A peer holding
    /// nothing sends <c>null</c> rather than a placeholder, which cannot
    /// accidentally match.
    /// </remarks>
    public sealed class ScenarioRequestMessage : PbjMessage
    {
        public ScenarioRequestMessage(string? digest)
        {
            Digest = digest;
        }

        public override PbjMessageType Type => PbjMessageType.ScenarioRequest;

        public string? Digest { get; }
    }

    /// <summary>The save itself. Host to peer, on request.</summary>
    /// <remarks>
    /// The one message whose payload becomes a file on the receiver's disk, so
    /// the receiver validates it through <see cref="ScenarioPayload"/> before
    /// writing anything. Per the standing rule, this message does not validate —
    /// sessions do.
    /// </remarks>
    public sealed class ScenarioMessage : PbjMessage
    {
        private static readonly ScenarioFile[] NoFiles = new ScenarioFile[0];

        public ScenarioMessage(string? saveName, string? digest, IReadOnlyList<ScenarioFile>? files)
        {
            SaveName = saveName;
            Digest = digest;
            Files = files ?? NoFiles;
        }

        public override PbjMessageType Type => PbjMessageType.Scenario;

        public string? SaveName { get; }

        /// <summary>
        /// The sender's digest of <see cref="Files"/>. Recomputed by the
        /// receiver and compared before a byte is written, so a truncated or
        /// substituted transfer is refused rather than loaded.
        /// </summary>
        public string? Digest { get; }

        public IReadOnlyList<ScenarioFile> Files { get; }
    }

    /// <summary>One roster entry in a <see cref="LobbyStateMessage"/>.</summary>
    /// <remarks>
    /// Carries the ready flag that <see cref="PeerInfo"/> does not. Kept
    /// separate rather than adding a field to <c>PeerInfo</c>, whose layout is
    /// pinned by byte-exact tests on <see cref="WelcomeMessage"/> — and where a
    /// lobby flag would mean nothing anyway.
    /// </remarks>
    public readonly struct LobbyPeerState
    {
        public LobbyPeerState(int peerId, string? name, bool ready)
        {
            PeerId = peerId;
            Name = name;
            Ready = ready;
        }

        public int PeerId { get; }
        public string? Name { get; }

        /// <summary>Whether this member has agreed to load the selected save.</summary>
        public bool Ready { get; }
    }

    /// <summary>
    /// The whole lobby, as the host sees it: which save is selected and who has
    /// agreed to it. Host to everyone, on every change.
    /// </summary>
    /// <remarks>
    /// Full state rather than a per-peer delta, for the same reason
    /// <see cref="AssignmentsMessage"/> is: it is idempotent, a client that
    /// misses one is corrected by the next, and there is no ordering hazard
    /// between "who joined" and "who is ready". A lobby is small — the roster
    /// caps at 16 — so the bytes are not worth a diff protocol.
    /// <para>
    /// <see cref="SelectionVersion"/> is what a <see cref="LobbyReadyMessage"/>
    /// names, so a ready for a save the host has since changed away from can be
    /// recognised and ignored rather than counted.
    /// </para>
    /// </remarks>
    public sealed class LobbyStateMessage : PbjMessage
    {
        private static readonly LobbyPeerState[] NoPeers = new LobbyPeerState[0];

        public LobbyStateMessage(
            int selectionVersion,
            string? saveKey,
            string? saveDigest,
            IReadOnlyList<LobbyPeerState>? peers)
        {
            SelectionVersion = selectionVersion;
            SaveKey = saveKey;
            SaveDigest = saveDigest;
            Peers = peers ?? NoPeers;
        }

        public override PbjMessageType Type => PbjMessageType.LobbyState;

        /// <summary>Bumped by the host every time the selected save changes.</summary>
        public int SelectionVersion { get; }

        /// <summary>The chosen save, or null when the host has not chosen one.</summary>
        public string? SaveKey { get; }

        /// <summary>
        /// Identifies the contents, so a peer can tell whether it already holds
        /// this save. Null is normal — the host may not have hashed it.
        /// </summary>
        public string? SaveDigest { get; }

        /// <summary>Everyone in the lobby, the host included, with their ready flag.</summary>
        public IReadOnlyList<LobbyPeerState> Peers { get; }
    }

    /// <summary>
    /// "I agree to load that save." Peer to host.
    /// </summary>
    /// <remarks>
    /// A separate type from <see cref="LobbyUnreadyMessage"/> rather than one
    /// message with a flag, mirroring <see cref="ReadyMessage"/> and
    /// <see cref="UnreadyMessage"/> so both barriers read the same way in the
    /// codec, the switch arms and the logs.
    /// <para>
    /// Carries the selection it is answering for the same reason a
    /// <see cref="ReadyMessage"/> carries its turn: without it, a ready sent
    /// just before the host changed the save would be counted as agreement to
    /// the new one.
    /// </para>
    /// </remarks>
    public sealed class LobbyReadyMessage : PbjMessage
    {
        public LobbyReadyMessage(int selectionVersion)
        {
            SelectionVersion = selectionVersion;
        }

        public override PbjMessageType Type => PbjMessageType.LobbyReady;

        public int SelectionVersion { get; }
    }

    /// <summary>"Actually, wait." Peer to host.</summary>
    /// <remarks>
    /// Idempotent like <see cref="UnreadyMessage"/>: withdrawing when not ready
    /// is a no-op, not an error.
    /// </remarks>
    public sealed class LobbyUnreadyMessage : PbjMessage
    {
        public LobbyUnreadyMessage(int selectionVersion)
        {
            SelectionVersion = selectionVersion;
        }

        public override PbjMessageType Type => PbjMessageType.LobbyUnready;

        public int SelectionVersion { get; }
    }

    /// <summary>How a peer's attempt to load the lobby's save turned out.</summary>
    /// <remarks>
    /// Three answers rather than a bool, because the host's response differs.
    /// <see cref="Unavailable"/> deliberately covers both "I do not have that
    /// save" and "mine does not match the digest": the host does the same thing
    /// either way — and will, in M11e, send the bytes — while a fourth arm no
    /// test could reach would break the coverage gate for a distinction nobody
    /// acts on.
    /// <para>
    /// There is no "still loading". Silence is what that looks like, and the
    /// host's timeout is what answers it.
    /// </para>
    /// </remarks>
    public enum LoadOutcome : byte
    {
        Loaded = 0,
        Refused = 1,
        Unavailable = 2,
    }

    /// <summary>"Everyone agreed — load it now." Host to everyone.</summary>
    /// <remarks>
    /// Carries the selection version so a peer can tell this instruction from
    /// one for a choice the host has since moved on from.
    /// <para>
    /// <b>The host advances the selection when it fires this</b>, so the version
    /// here is one the client has not seen yet. That is why the host emits the
    /// <see cref="LobbyStateMessage"/> carrying the new version <em>first</em>,
    /// in the same batch: the stream is ordered, so the client has already
    /// adopted the version by the time this arrives. Reverse them and every
    /// client rejects the load while the host — which never validates its own —
    /// loads alone.
    /// </para>
    /// </remarks>
    public sealed class LobbyLoadMessage : PbjMessage
    {
        public LobbyLoadMessage(int selectionVersion, string? saveKey, string? saveDigest)
        {
            SelectionVersion = selectionVersion;
            SaveKey = saveKey;
            SaveDigest = saveDigest;
        }

        public override PbjMessageType Type => PbjMessageType.LobbyLoad;

        public int SelectionVersion { get; }

        public string? SaveKey { get; }

        /// <summary>
        /// What the host's copy hashes to, so a peer can refuse a save of the
        /// same name that is not the same save.
        /// </summary>
        public string? SaveDigest { get; }
    }

    /// <summary>"I am in", or why not. Peer to host.</summary>
    public sealed class LobbyLoadedMessage : PbjMessage
    {
        public LobbyLoadedMessage(int selectionVersion, LoadOutcome outcome)
        {
            SelectionVersion = selectionVersion;
            Outcome = outcome;
        }

        public override PbjMessageType Type => PbjMessageType.LobbyLoaded;

        public int SelectionVersion { get; }

        public LoadOutcome Outcome { get; }
    }

    /// <summary>
    /// Where the host's mobile base is, so a passenger can watch it move.
    /// </summary>
    /// <remarks>
    /// <b>X and Z only — the height is deliberately not on the wire.</b> The
    /// receiving machine finds its own Y by setting <c>isPositionUnchecked</c>
    /// and letting <c>OverworldPositionValidationSystem</c> snap to its own
    /// ground; the recon watched it correct 33.3 to 13.3. Sending Y would be
    /// sending the host's idea of a surface the client renders for itself, which
    /// is how a base ends up hovering.
    /// <para>
    /// There is no simulation time on this message either, and that is the
    /// point. The mirror re-replaces the client's <em>own</em> time value to
    /// wake the reactive collectors; writing the host's value instead would run
    /// roughly twenty <c>SimulationTime</c> systems with a real delta on a
    /// machine that is not simulating — the overworld cousin of the standing
    /// rule against advancing <c>combat.simulationTime</c> on a client.
    /// </para>
    /// </remarks>
    public sealed class BasePositionMessage : PbjMessage
    {
        public BasePositionMessage(float x, float z)
        {
            X = x;
            Z = z;
        }

        public override PbjMessageType Type => PbjMessageType.BasePosition;

        public float X { get; }

        public float Z { get; }
    }

    /// <summary>
    /// The host has entered a fight, written it to the scenario slot, and is
    /// waiting for everyone to join it. M12b.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> M9's <see cref="ScenarioOfferMessage"/>, even
    /// though it says almost the same thing. A client refuses a scenario offer
    /// outright while <c>HostIsFighting</c> — a guard built on purpose, since
    /// pulling a save mid-fight is normally nonsense — and this is the one flow
    /// where the host being in a fight is precisely the reason to pull. Relaxing
    /// that guard would have re-opened it for every other case; a second message
    /// leaves it exactly as strict as it was.
    /// <para>
    /// The digest is not decoration. The slot is rewritten at the start of every
    /// mission, so a client may be holding the <em>previous</em> fight under the
    /// same name, and the name alone cannot tell the two apart.
    /// </para>
    /// </remarks>
    public sealed class CombatOfferMessage : PbjMessage
    {
        public CombatOfferMessage(string? saveName, string? digest, int turn)
        {
            SaveName = saveName;
            Digest = digest;
            Turn = turn;
        }

        public override PbjMessageType Type => PbjMessageType.CombatOffer;

        public string? SaveName { get; }

        public string? Digest { get; }

        /// <summary>The turn the fight starts on, for the barrier to quote back.</summary>
        public int Turn { get; }
    }

    /// <summary>
    /// A client saying whether it got into the host's fight. M12b.
    /// </summary>
    /// <remarks>
    /// Carries the turn it is answering about, for the same reason every other
    /// barrier report in this protocol carries its version: a report for a fight
    /// that has since been abandoned must be recognisable as stale rather than
    /// counted toward the current one.
    /// <para>
    /// <see cref="LoadOutcome.Unavailable"/> is the interesting value. The load
    /// callback is success-only, so a client that never answers is the host's
    /// timeout to notice — but a client that knows it cannot join says so
    /// immediately, which is the difference between a two-minute wait and none.
    /// </para>
    /// </remarks>
    public sealed class CombatEnteredMessage : PbjMessage
    {
        public CombatEnteredMessage(int turn, LoadOutcome outcome)
        {
            Turn = turn;
            Outcome = outcome;
        }

        public override PbjMessageType Type => PbjMessageType.CombatEntered;

        public int Turn { get; }

        public LoadOutcome Outcome { get; }
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
