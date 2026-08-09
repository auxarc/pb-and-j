using System;
using System.Collections.Generic;

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
        ApplySnapshot = 8,
        ClearLocalOrders = 9,
        PlayKeyframes = 10,
        StopKeyframes = 11,
        WriteScenario = 12,
        BeginLoad = 13,
        MirrorBase = 14,
        BeginCombatLoad = 15,

        // 16+ unallocated.
    }

    /// <summary>
    /// Load the fight the host shipped us. Clients only. M12b.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BeginLoadEffect"/> because the glue behind it
    /// must skip the lobby catalogue check — <c>LobbyCatalogue.IsOffered</c>
    /// excludes the scenario slot on purpose, so routing a fight through the
    /// campaign path returns <c>Unavailable</c> every time, silently, and reads
    /// as a save problem rather than a wiring one.
    /// <para>
    /// It also must not mark the campaign as entered: the slot is a fight, not
    /// the campaign, and claiming it would point the save-namespace redirect at
    /// a directory that is rewritten at the start of the next mission.
    /// </para>
    /// </remarks>
    public sealed class BeginCombatLoadEffect : PbjEffect
    {
        public BeginCombatLoadEffect(string? saveName, string? digest)
        {
            SaveName = saveName;
            Digest = digest;
        }

        public override PbjEffectKind Kind => PbjEffectKind.BeginCombatLoad;

        public string? SaveName { get; }

        /// <summary>
        /// What the host says the fight hashes to. Checked before loading,
        /// because the slot is rewritten every mission and this machine may be
        /// holding the previous one under the same name.
        /// </summary>
        public string? Digest { get; }
    }

    /// <summary>
    /// Put the mobile base where the host's is. Clients only.
    /// </summary>
    /// <remarks>
    /// Carries X and Z and no height, because the receiving machine snaps to its
    /// own ground — see <see cref="BasePositionMessage"/>, which explains why at
    /// length.
    /// <para>
    /// The glue that applies this must use the game's own teleport recipe rather
    /// than writing Position alone: <c>StopMovement</c> first or the client's own
    /// path fights the write, then Position <em>and</em> PositionTarget, because
    /// <c>OverworldMovementSystem</c> drags position back toward a stale target
    /// whenever the clock runs. Then <c>isPositionUnchecked</c> for the ground
    /// snap, and a same-value <c>ReplaceSimulationTime</c> to wake the reactive
    /// collectors while paused.
    /// </para>
    /// </remarks>
    public sealed class MirrorBaseEffect : PbjEffect
    {
        public MirrorBaseEffect(float x, float z)
        {
            X = x;
            Z = z;
        }

        public override PbjEffectKind Kind => PbjEffectKind.MirrorBase;

        public float X { get; }

        public float Z { get; }
    }

    /// <summary>
    /// Start loading the lobby's save. Every participant, host included.
    /// </summary>
    /// <remarks>
    /// The effect-out, event-back shape <see cref="CommitTurnEffect"/> already
    /// uses: the session never touches the game, so it asks, and the answer
    /// arrives later as a <c>LoadFinishedEvent</c>. It has to be later — a
    /// campaign load tears the game down and comes back several seconds and one
    /// scene teardown afterwards.
    /// <para>
    /// <see cref="SelectionVersion"/> rides along so the report can be matched
    /// to the load that asked for it rather than to whatever the lobby is doing
    /// by the time it lands.
    /// </para>
    /// </remarks>
    public sealed class BeginLoadEffect : PbjEffect
    {
        public BeginLoadEffect(string? saveKey, int selectionVersion, string? saveDigest)
        {
            SaveKey = saveKey;
            SelectionVersion = selectionVersion;
            SaveDigest = saveDigest;
        }

        public override PbjEffectKind Kind => PbjEffectKind.BeginLoad;

        public string? SaveKey { get; }

        public int SelectionVersion { get; }

        /// <summary>
        /// The contents the lobby agreed on, or null if it published none.
        /// </summary>
        /// <remarks>
        /// Carried so the machine about to load can check that its copy is the one
        /// everyone else is loading. Without it the check could only be "a save of
        /// that name exists", and same-name-different-contents is precisely the case
        /// that diverges silently — every peer loads its own campaign and nothing
        /// notices until the states disagree.
        /// </remarks>
        public string? SaveDigest { get; }
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
        public ApplyOrderEffect(int peerId, int batchIndex, OrderPayload order)
        {
            PeerId = peerId;
            BatchIndex = batchIndex;
            Order = order ?? throw new ArgumentNullException(nameof(order));
        }

        public override PbjEffectKind Kind => PbjEffectKind.ApplyOrder;

        /// <summary>Who submitted it — for attributing rejections in the log.</summary>
        public int PeerId { get; }

        /// <summary>
        /// Where it sat in that peer's Ready batch. Travels back on the
        /// <see cref="OrderAppliedEvent"/> so the outcome can be reported
        /// without orders needing a stable id.
        /// </summary>
        public int BatchIndex { get; }

        public OrderPayload Order { get; }
    }

    /// <summary>
    /// Hard-set local units to the host's authoritative state, then report what
    /// the local digest became.
    /// </summary>
    /// <remarks>
    /// The expected digest rides on the effect rather than living in session
    /// state, which keeps the effect self-contained and lets the runner be tested
    /// with no session at all. The runtime feeds the comparison back as a
    /// <see cref="SnapshotAppliedEvent"/> in the same pump, the same way
    /// <see cref="CommitTurnEffect"/> reports its outcome.
    /// </remarks>
    public sealed class ApplySnapshotEffect : PbjEffect
    {
        private static readonly UnitSnapshot[] NoUnits = new UnitSnapshot[0];

        public ApplySnapshotEffect(int turn, IReadOnlyList<UnitSnapshot>? units, string? expectedDigest)
        {
            Turn = turn;
            Units = units ?? NoUnits;
            ExpectedDigest = expectedDigest;
        }

        public override PbjEffectKind Kind => PbjEffectKind.ApplySnapshot;

        public int Turn { get; }
        public IReadOnlyList<UnitSnapshot> Units { get; }
        public string? ExpectedDigest { get; }
    }

    /// <summary>
    /// Dispose the local player's planned orders.
    /// </summary>
    /// <remarks>
    /// Client-side only, and not optional. A client plans real
    /// <c>ActionEntity</c>s that never execute, because it never simulates — so
    /// without this its timeline accumulates junk and <c>CaptureLocalOrders</c>
    /// starts re-submitting orders the host already ran.
    /// </remarks>
    public sealed class ClearLocalOrdersEffect : PbjEffect
    {
        public override PbjEffectKind Kind => PbjEffectKind.ClearLocalOrders;
    }

    /// <summary>
    /// Present a received turn's motion, replacing any playback already running.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ApplySnapshotEffect"/> this reports nothing back. There
    /// is nothing to verify: playback is presentation, the snapshot that arrived
    /// just before it is what the digest was checked against, and a playback that
    /// never finishes is corrected by the next turn's snapshot anyway.
    /// </remarks>
    public sealed class PlayKeyframesEffect : PbjEffect
    {
        public PlayKeyframesEffect(int turn, KeyframeCapture capture)
        {
            Turn = turn;
            Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        }

        public override PbjEffectKind Kind => PbjEffectKind.PlayKeyframes;

        public int Turn { get; }
        public KeyframeCapture Capture { get; }
    }

    /// <summary>
    /// Abandon any playback in flight.
    /// </summary>
    /// <remarks>
    /// Needed at every edge that ends the turn being presented — combat ending,
    /// the host saying goodbye, the session faulting. Without it a unit keeps
    /// sliding along last turn's path through whatever comes next, and on a
    /// faulted session nothing would ever stop it.
    /// </remarks>
    public sealed class StopKeyframesEffect : PbjEffect
    {
        public override PbjEffectKind Kind => PbjEffectKind.StopKeyframes;
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

    /// <summary>
    /// Put a received combat save on disk, so <c>pbj.combat-load</c> can find it.
    /// </summary>
    /// <remarks>
    /// The session has already checked the payload — allowlisted names, size
    /// cap, digest agreement — before emitting this, because this is the one
    /// effect that turns wire bytes into files. Deliberately does <em>not</em>
    /// load the save: that would yank the player out of whatever they are doing
    /// on a network message. The runtime logs the outcome rather than feeding it
    /// back as an event; nothing in the protocol depends on the write, so there
    /// is no state for a session to advance.
    /// </remarks>
    public sealed class WriteScenarioEffect : PbjEffect
    {
        public WriteScenarioEffect(ScenarioPayload payload)
        {
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public override PbjEffectKind Kind => PbjEffectKind.WriteScenario;

        public ScenarioPayload Payload { get; }
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
