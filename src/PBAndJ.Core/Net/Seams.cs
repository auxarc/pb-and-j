using System.Collections.Generic;

namespace PBAndJ.Core.Net
{
    /// <summary>What happened when an order was handed to the game.</summary>
    /// <remarks>
    /// Deliberately finer-grained than a bool. The game's own load path accepts
    /// almost anything — it fails only on an unresolvable owner or an unknown
    /// blueprint — so an order can be "applied" and then silently disposed at
    /// sim start. The bridge pre-validates and reports the real outcome.
    /// </remarks>
    public enum OrderApplyResult
    {
        Applied = 0,

        /// <summary>Owner name resolves to no unit in this combat.</summary>
        UnknownUnit = 1,

        /// <summary>Blueprint is not in the game's action catalogue.</summary>
        UnknownBlueprint = 2,

        /// <summary>DataHelperAction.IsValid refused it — wrecked unit, ejected pilot, ...</summary>
        Invalid = 3,

        /// <summary>Start time falls outside the current turn's placement window.</summary>
        OutOfWindow = 4,

        /// <summary>
        /// The submitting peer does not own that unit.
        /// </summary>
        /// <remarks>
        /// No bridge ever returns this — the host session produces it, before
        /// the order is ever handed to the game. It lives in this enum so that
        /// one reason set covers every way an order can fail, on the wire and
        /// in the logs alike.
        /// </remarks>
        NotOwned = 5,
    }

    /// <summary>
    /// Sends bytes and closes connections. The only part of the stack that owns
    /// a socket.
    /// </summary>
    /// <remarks>
    /// Implementations live in PBAndJ.Mod and tools/pbj-peer, never here: this
    /// interface is the line the coverage gate stops at. A Steam implementation
    /// slots in behind it with no protocol change.
    /// </remarks>
    public interface IPbjTransport
    {
        /// <summary>Sends one already-framed payload to one peer.</summary>
        void Send(int peerId, byte[] frame);

        /// <summary>Closes one peer's connection.</summary>
        void Disconnect(int peerId, string? reason);

        /// <summary>Tears down the listener and every connection.</summary>
        void Stop();
    }

    /// <summary>Where log lines go. Debug.Log in the game, stdout in the harness.</summary>
    public interface IPbjLog
    {
        void Log(string line);
    }

    /// <summary>
    /// The half of the protocol this process is playing. Lets
    /// <see cref="PbjRuntime"/> drive a host and a client identically, so the
    /// harness self-test exercises the same effect runner the game does.
    /// </summary>
    public interface IPbjSession
    {
        IReadOnlyList<PbjEffect> Handle(PbjInboundEvent evt);

        /// <summary>
        /// Handles a decoded message. <paramref name="peerId"/> is the sender on
        /// the host, and the host connection on a client.
        /// </summary>
        IReadOnlyList<PbjEffect> HandleMessage(int peerId, PbjMessage message);

        /// <summary>Who a <see cref="BroadcastEffect"/> reaches.</summary>
        IReadOnlyList<int> ConnectedPeerIds { get; }
    }

    /// <summary>
    /// The entire game-facing surface: everything Core needs from the ECS,
    /// expressed without a single game type.
    /// </summary>
    /// <remarks>
    /// Implemented by <c>CombatGameBridge</c> in PBAndJ.Mod against Entitas, and
    /// by <c>ScriptedGameBridge</c> in the harness against plain lists. Because
    /// both satisfy the same interface, the harness self-test exercises the very
    /// same Core code path the game does.
    /// </remarks>
    public interface IPbjGameBridge
    {
        /// <summary>Current combat turn, or -1 when not in combat.</summary>
        int CurrentTurn { get; }

        bool InCombat { get; }

        /// <summary>
        /// Units eligible for assignment: player-controllable AND friendly.
        /// Friendly alone is wrong — scenario-scripted allies are friendly but
        /// AI-driven.
        /// </summary>
        IReadOnlyList<string> AssignableUnitNames { get; }

        /// <summary>The local player's planned orders for this turn.</summary>
        IReadOnlyList<OrderPayload> CaptureLocalOrders();

        /// <summary>
        /// Applies one remote order to the live ECS. There is deliberately no
        /// "clear orders" counterpart: disposing an order cascades to the
        /// owner's later primary-track orders on the next systems tick, which
        /// would eat orders applied in the same pump.
        /// </summary>
        OrderApplyResult ApplyOrder(OrderPayload order);

        /// <summary>
        /// Calls the game's execution commit and reports whether the turn
        /// actually advanced. Returns false when the game silently refused —
        /// already simulating, scenario step prohibits execution, and so on.
        /// </summary>
        bool CommitTurn();

        /// <summary>Locks or unlocks the local execute button.</summary>
        void SetExecutionLocked(bool locked);

        /// <summary>Order-independent fingerprint of post-turn unit state.</summary>
        string ComputeStateDigest();

        /// <summary>
        /// Every unit's authoritative state after execution. Host only.
        /// </summary>
        /// <remarks>
        /// Must cover exactly the units <see cref="ComputeStateDigest"/> covers —
        /// all combat units with a resolvable name, hostile ones included, not
        /// just <see cref="AssignableUnitNames"/>. Narrowing it would both drop
        /// enemies out of correction and silently change what the digest means.
        /// </remarks>
        IReadOnlyList<UnitSnapshot> CaptureSnapshot();

        /// <summary>
        /// Hard-sets local units to the host's state. Client only.
        /// </summary>
        /// <remarks>
        /// Safe precisely because a client never sets <c>combat.Simulating</c>,
        /// so no playback system is driving transforms to overwrite the write on
        /// the next tick. The same call on a simulating host would be a losing
        /// battle, which is why snapshot correction is viable as the client-side
        /// floor and useless as a host-side one.
        /// </remarks>
        void ApplySnapshot(IReadOnlyList<UnitSnapshot> units);

        /// <summary>Disposes the local player's planned orders. Client only.</summary>
        void ClearLocalOrders();
    }
}
