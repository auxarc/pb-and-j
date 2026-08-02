using System;

namespace PBAndJ.Core.Net
{
    /// <summary>Discriminator for <see cref="PbjEffect"/>.</summary>
    public enum PbjEffectKind : byte
    {
        Send = 1,
        Broadcast = 2,
        Disconnect = 3,
        ApplyOrder = 4,
        CommitTurn = 5,
        SetExecutionLock = 6,
        Log = 7,
    }

    /// <summary>
    /// Something the session wants done to the outside world. Sessions never
    /// touch a socket, the ECS or the log directly — they return these, and
    /// <see cref="PbjRuntime"/> carries them out.
    /// </summary>
    /// <remarks>
    /// Constructor is <c>protected</c> and <see cref="Kind"/> is
    /// <c>abstract</c> so tests can reach the <c>default:</c> arm of the effect
    /// runner. Do not seal either away.
    /// </remarks>
    public abstract class PbjEffect
    {
        protected PbjEffect()
        {
        }

        public abstract PbjEffectKind Kind { get; }
    }

    /// <summary>Send one message to one peer.</summary>
    public sealed class SendEffect : PbjEffect
    {
        public SendEffect(int peerId, PbjMessage message)
        {
            PeerId = peerId;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public override PbjEffectKind Kind => PbjEffectKind.Send;

        public int PeerId { get; }
        public PbjMessage Message { get; }
    }

    /// <summary>Send one message to every connected peer, optionally skipping one.</summary>
    public sealed class BroadcastEffect : PbjEffect
    {
        public BroadcastEffect(PbjMessage message, int? exceptPeerId = null)
        {
            Message = message ?? throw new ArgumentNullException(nameof(message));
            ExceptPeerId = exceptPeerId;
        }

        public override PbjEffectKind Kind => PbjEffectKind.Broadcast;

        public PbjMessage Message { get; }

        /// <summary>Peer to skip — typically the one that caused the broadcast.</summary>
        public int? ExceptPeerId { get; }
    }

    /// <summary>Close a peer's connection.</summary>
    public sealed class DisconnectEffect : PbjEffect
    {
        public DisconnectEffect(int peerId, string? reason)
        {
            PeerId = peerId;
            Reason = reason;
        }

        public override PbjEffectKind Kind => PbjEffectKind.Disconnect;

        public int PeerId { get; }
        public string? Reason { get; }
    }

    /// <summary>Apply one remote order to the live game.</summary>
    public sealed class ApplyOrderEffect : PbjEffect
    {
        public ApplyOrderEffect(int peerId, OrderPayload order)
        {
            PeerId = peerId;
            Order = order ?? throw new ArgumentNullException(nameof(order));
        }

        public override PbjEffectKind Kind => PbjEffectKind.ApplyOrder;

        /// <summary>Who submitted it — for attributing rejections in the log.</summary>
        public int PeerId { get; }
        public OrderPayload Order { get; }
    }

    /// <summary>
    /// Advance the turn. The runtime reports the outcome straight back to the
    /// session as a <see cref="CommitOutcomeEvent"/>, because the game's commit
    /// can silently refuse and nothing may be broadcast until it is known to
    /// have landed.
    /// </summary>
    public sealed class CommitTurnEffect : PbjEffect
    {
        public CommitTurnEffect(int turn)
        {
            Turn = turn;
        }

        public override PbjEffectKind Kind => PbjEffectKind.CommitTurn;

        /// <summary>The turn being committed, captured before the ECS advances.</summary>
        public int Turn { get; }
    }

    /// <summary>Lock or unlock the local execute button.</summary>
    public sealed class SetExecutionLockEffect : PbjEffect
    {
        public SetExecutionLockEffect(bool locked)
        {
            Locked = locked;
        }

        public override PbjEffectKind Kind => PbjEffectKind.SetExecutionLock;

        public bool Locked { get; }
    }

    /// <summary>Emit an already-composed line. Always built by <see cref="NetLog"/>.</summary>
    public sealed class LogEffect : PbjEffect
    {
        public LogEffect(string line)
        {
            Line = line ?? throw new ArgumentNullException(nameof(line));
        }

        public override PbjEffectKind Kind => PbjEffectKind.Log;

        public string Line { get; }
    }
}
