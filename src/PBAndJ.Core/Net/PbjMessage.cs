using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>
    /// Discriminator byte at the head of every encoded message.
    /// </summary>
    /// <remarks>
    /// Values are assigned once and never reused. 11+ are reserved for the
    /// deferred set: Unready, OrderResult, CombatStart, CombatEnd, Ping, Pong
    /// (see docs/design/networking.md).
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
        public HelloMessage(int magic, int protocolVersion, string? modVersion, string? playerName)
        {
            Magic = magic;
            ProtocolVersion = protocolVersion;
            ModVersion = modVersion;
            PlayerName = playerName;
        }

        public override PbjMessageType Type => PbjMessageType.Hello;

        public int Magic { get; }
        public int ProtocolVersion { get; }
        public string? ModVersion { get; }
        public string? PlayerName { get; }
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
            int currentTurn)
        {
            ProtocolVersion = protocolVersion;
            SessionId = sessionId;
            AssignedPeerId = assignedPeerId;
            HostName = hostName;
            Peers = peers ?? NoPeers;
            CurrentTurn = currentTurn;
        }

        public override PbjMessageType Type => PbjMessageType.Welcome;

        public int ProtocolVersion { get; }
        public string? SessionId { get; }
        public int AssignedPeerId { get; }
        public string? HostName { get; }
        public IReadOnlyList<PeerInfo> Peers { get; }

        /// <summary>Without this a joining peer cannot construct a matching Ready.</summary>
        public int CurrentTurn { get; }
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
