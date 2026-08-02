using System;

namespace PBAndJ.Core.Net
{
    /// <summary>Discriminator for <see cref="PbjInboundEvent"/>.</summary>
    /// <remarks>
    /// Values 100+ are reserved for the deferred set (LocalUnready, CombatEntered,
    /// CombatExited) — see docs/design/networking.md.
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
    /// The local simulation window ended. Carries the digest of the resulting
    /// state; the executed turn number comes from the session, which captured it
    /// at commit time.
    /// </summary>
    public sealed class LocalTurnCompleteEvent : PbjInboundEvent
    {
        public LocalTurnCompleteEvent(string? digest)
        {
            Digest = digest;
        }

        public override PbjInboundEventKind Kind => PbjInboundEventKind.LocalTurnComplete;

        public string? Digest { get; }
    }
}
