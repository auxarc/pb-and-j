using System;
using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>Discriminator for <see cref="PbjInboundEvent"/>.</summary>
    /// <remarks>
    /// 1-99 are transport and local-player events. 100+ are the events added
    /// after M4 — see docs/design/networking.md.
    /// </remarks>
    public enum PbjInboundEventKind : byte
    {
        PeerConnected = 1,
        PeerBytes = 2,
        PeerDisconnected = 3,
        TransportFailed = 4,
        TransportLog = 5,
        LocalReady = 6,
        LocalTurnComplete = 7,
        CommitOutcome = 8,
        OrderApplied = 9,
        SnapshotApplied = 10,
        LocalUnready = 100,
        CombatEntered = 101,
        CombatExited = 102,
        Tick = 103,
    }

    /// <summary>
    /// Something that happened outside Core and needs handling on the main
    /// thread: bytes off a socket, a peer coming or going, or a local player
    /// action. Posted to a <see cref="PbjMailbox"/>, drained by the pump.
    /// </summary>
    /// <remarks>
    /// Constructor is <c>protected</c> and <see cref="Kind"/> is
    /// <c>abstract</c> so tests can reach the <c>default:</c> arm of every
    /// switch over this hierarchy. Do not seal either away.
    /// </remarks>
    public abstract class PbjInboundEvent
    {
        protected PbjInboundEvent()
        {
        }

        public abstract PbjInboundEventKind Kind { get; }
    }

    /// <summary>A transport accepted or established a connection.</summary>
    public sealed class PeerConnectedEvent : PbjInboundEvent
    {
        public PeerConnectedEvent(int peerId, string? remote)
        {
            PeerId = peerId;
            Remote = remote;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.PeerConnected;

        public int PeerId { get; }
        public string? Remote { get; }
    }

    /// <summary>
    /// Raw bytes received from a peer. Decoding deliberately happens on the main
    /// thread, so every protocol decision stays inside the coverage gate.
    /// </summary>
    public sealed class PeerBytesEvent : PbjInboundEvent
    {
        public PeerBytesEvent(int peerId, byte[] bytes)
        {
            PeerId = peerId;
            Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.PeerBytes;

        public int PeerId { get; }

        /// <summary>
        /// The transport MUST hand over a copy, never its reused read buffer.
        /// </summary>
        public byte[] Bytes { get; }
    }

    /// <summary>A peer's connection closed, gracefully or otherwise.</summary>
    public sealed class PeerDisconnectedEvent : PbjInboundEvent
    {
        public PeerDisconnectedEvent(int peerId, string? reason)
        {
            PeerId = peerId;
            Reason = reason;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.PeerDisconnected;

        public int PeerId { get; }
        public string? Reason { get; }
    }

    /// <summary>The transport itself failed; the session cannot continue.</summary>
    public sealed class TransportFailedEvent : PbjInboundEvent
    {
        public TransportFailedEvent(string? reason)
        {
            Reason = reason;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.TransportFailed;

        public string? Reason { get; }
    }

    /// <summary>
    /// A log line from the transport thread. Routed through the mailbox rather
    /// than logged directly so background threads never touch the log sink and
    /// ordering stays deterministic.
    /// </summary>
    public sealed class TransportLogEvent : PbjInboundEvent
    {
        public TransportLogEvent(string? line)
        {
            Line = line;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.TransportLog;

        public string? Line { get; }
    }

    /// <summary>The local player pressed Execute.</summary>
    public sealed class LocalReadyEvent : PbjInboundEvent
    {
        public override PbjInboundEventKind Kind => PbjInboundEventKind.LocalReady;
    }

    /// <summary>
    /// Result of a <see cref="CommitTurnEffect"/>, fed straight back to the
    /// session by the runtime within the same pump.
    /// </summary>
    /// <remarks>
    /// This exists because <c>CombatUtilities.ConfirmExecution</c> is void and
    /// silently refuses in four normal situations. Broadcasting TurnCommit
    /// before knowing the commit landed would leave every peer locked and
    /// waiting while the host sat in planning.
    /// </remarks>
    public sealed class CommitOutcomeEvent : PbjInboundEvent
    {
        public CommitOutcomeEvent(int turn, bool committed)
        {
            Turn = turn;
            Committed = committed;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.CommitOutcome;

        public int Turn { get; }
        public bool Committed { get; }
    }

    /// <summary>
    /// Result of one <see cref="ApplyOrderEffect"/>, fed straight back to the
    /// session by the runtime within the same pump.
    /// </summary>
    /// <remarks>
    /// This is what lets the host report a per-order outcome without inventing
    /// an order id: the batch index travels out on the effect and back on this
    /// event. Because the runtime calls the session synchronously while running
    /// effects, every outcome has folded in before the commit effect runs.
    /// </remarks>
    public sealed class OrderAppliedEvent : PbjInboundEvent
    {
        public OrderAppliedEvent(int peerId, int batchIndex, OrderApplyResult result)
        {
            PeerId = peerId;
            BatchIndex = batchIndex;
            Result = result;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.OrderApplied;

        public int PeerId { get; }

        /// <summary>Index into the Ready batch that carried this order.</summary>
        public int BatchIndex { get; }

        public OrderApplyResult Result { get; }
    }

    /// <summary>
    /// A snapshot has been hard-set locally; this is what the local digest became.
    /// </summary>
    /// <remarks>
    /// The point of the whole snapshot path: the client checks its own
    /// correction, in pure Core, instead of assuming it worked. Comparing here
    /// rather than in the glue is what keeps that check inside the coverage gate.
    /// </remarks>
    public sealed class SnapshotAppliedEvent : PbjInboundEvent
    {
        public SnapshotAppliedEvent(int turn, int unitCount, string? expectedDigest, string? actualDigest)
        {
            Turn = turn;
            UnitCount = unitCount;
            ExpectedDigest = expectedDigest;
            ActualDigest = actualDigest;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.SnapshotApplied;

        public int Turn { get; }
        public int UnitCount { get; }
        public string? ExpectedDigest { get; }
        public string? ActualDigest { get; }
    }

    /// <summary>The local player withdrew their submitted orders.</summary>
    public sealed class LocalUnreadyEvent : PbjInboundEvent
    {
        public override PbjInboundEventKind Kind => PbjInboundEventKind.LocalUnready;
    }

    /// <summary>
    /// The local game entered combat, observed as an edge on
    /// <see cref="IPbjGameBridge.InCombat"/> during the pump.
    /// </summary>
    /// <remarks>
    /// Only the host acts on this. A client's local combat state is not
    /// authoritative — it learns combat state from the host's messages.
    /// </remarks>
    public sealed class CombatEnteredEvent : PbjInboundEvent
    {
        public override PbjInboundEventKind Kind => PbjInboundEventKind.CombatEntered;
    }

    /// <summary>The local game left combat.</summary>
    public sealed class CombatExitedEvent : PbjInboundEvent
    {
        public override PbjInboundEventKind Kind => PbjInboundEventKind.CombatExited;
    }

    /// <summary>
    /// Time passing. The only way a clock reaches a session.
    /// </summary>
    /// <remarks>
    /// An event rather than an <c>IPbjSession.Tick(double)</c> method on purpose.
    /// Both sessions are one switch over this hierarchy, and the whole
    /// <c>default:</c>-arm coverage discipline rests on there being exactly one
    /// switch per hierarchy; a separate method would sit outside it, carry its
    /// own coverage burden, and need a second rule about whether it runs before
    /// or after the mailbox drain.
    /// <para>
    /// Synthesized by <see cref="PbjRuntime"/> from the <c>nowSeconds</c> handed
    /// to <c>Pump</c>, throttled to <see cref="PbjProtocol.TickIntervalSeconds"/>.
    /// Sessions never read a clock, which is what keeps timeout logic a
    /// deterministic unit test.
    /// </para>
    /// </remarks>
    public sealed class TickEvent : PbjInboundEvent
    {
        public TickEvent(double nowSeconds)
        {
            NowSeconds = nowSeconds;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.Tick;

        public double NowSeconds { get; }
    }

    /// <summary>
    /// The local simulation window ended. Carries the digest of the resulting
    /// state; the executed turn number comes from the session, which captured it
    /// at commit time.
    /// </summary>
    public sealed class LocalTurnCompleteEvent : PbjInboundEvent
    {
        private static readonly UnitSnapshot[] NoUnits = new UnitSnapshot[0];

        /// <remarks>
        /// One constructor, not an overload with and without units: a second
        /// constructor is an untested branch waiting to happen, and the coverage
        /// gate would notice.
        /// </remarks>
        public LocalTurnCompleteEvent(string? digest, IReadOnlyList<UnitSnapshot>? units)
        {
            Digest = digest;
            Units = units ?? NoUnits;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.LocalTurnComplete;

        public string? Digest { get; }

        /// <summary>The state to broadcast, captured in the same read as the digest.</summary>
        public IReadOnlyList<UnitSnapshot> Units { get; }
    }
}
