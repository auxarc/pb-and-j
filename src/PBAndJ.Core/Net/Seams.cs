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

        /// <summary>
        /// How every unit moved during the turn that just executed. Host only.
        /// </summary>
        /// <remarks>
        /// Returns <see cref="KeyframeCapture.None"/> rather than throwing when
        /// there is nothing recorded — a client never captures, and a host whose
        /// scenario disables prediction never gets a recorder started. Callers
        /// treat an empty capture as "send no keyframes this turn", not as a
        /// failure: snapshot correction remains the floor regardless.
        /// <para>
        /// Must be read in the same call as <see cref="CaptureSnapshot"/>, so the
        /// last key of every track and the snapshot describe one instant.
        /// </para>
        /// </remarks>
        KeyframeCapture CaptureKeyframes();

        /// <summary>
        /// Starts presenting a received turn's motion. Client only.
        /// </summary>
        /// <remarks>
        /// Presentation, not state: an implementation must not write anything the
        /// digest is computed over, or a client would verify its correction
        /// against a half-played animation. Replaces any run already in progress.
        /// </remarks>
        void PlayKeyframes(int turn, KeyframeCapture capture);

        /// <summary>Abandons any playback in progress. Client only.</summary>
        void StopKeyframes();

        /// <summary>
        /// Puts the mobile base at the host's position. Client only.
        /// </summary>
        /// <param name="x">Host's base X.</param>
        /// <param name="z">Host's base Z.</param>
        /// <remarks>
        /// Two coordinates and no height: the implementation finds its own Y by
        /// asking the game to validate the position against its own ground.
        /// <para>
        /// Like <see cref="PlayKeyframes"/> this is presentation, and like it,
        /// the implementation may write ECS components — the overworld renderer
        /// reads <c>PositionDetectedLast</c> and nothing short of a real write
        /// reaches it. What it must not do is advance the simulation clock: a
        /// same-value replace to wake the reactive collectors is the whole
        /// permitted interaction with time, because the client is not simulating
        /// and roughly twenty systems collect on that component.
        /// </para>
        /// <para>
        /// A no-op is a legitimate implementation. The harness has no overworld,
        /// and a client sitting in a management screen cannot render the change
        /// even when it lands — the recon measured that as correct rather than
        /// broken, since the position is right again on returning to the map.
        /// </para>
        /// </remarks>
        void MirrorBase(float x, float z);

        /// <summary>
        /// Loads the fight the host shipped. Clients only. M12b.
        /// </summary>
        /// <returns>
        /// Null if the load began, or the outcome if it could not start.
        /// </returns>
        /// <remarks>
        /// Distinct from <see cref="BeginLoad"/> because the implementation must
        /// skip the lobby catalogue -- which excludes the scenario slot on
        /// purpose -- and must not mark the campaign as entered. See
        /// <see cref="BeginCombatLoadEffect"/> for why both matter.
        /// <para>
        /// The digest is checked before loading rather than after: the slot is
        /// rewritten every mission, so holding the wrong fight under the right
        /// name is the expected failure, not an exotic one.
        /// </para>
        /// </remarks>
        LoadOutcome? BeginCombatLoad(string? saveName, string? digest);

        /// <summary>
        /// Write the fight now loading to the scenario slot. Hosts only. M12b.
        /// </summary>
        /// <remarks>
        /// Returns nothing on purpose: the answer is frames away. The game
        /// refuses to save while the scenario intro is running, and that flag is
        /// raised in the same tick that makes <see cref="InCombat"/> true, so an
        /// implementation must poll for its own moment. It reports by posting a
        /// <see cref="LocalCombatReadyEvent"/> — with the slot name and digest
        /// when the write lands, and with <c>null</c> for both when it never
        /// will, because a host that cannot share the fight must still be allowed
        /// to fight it.
        /// <para>
        /// A no-op is a legitimate implementation for anything with no game
        /// behind it, provided something else posts the event; the harness stands
        /// in for the write exactly that way.
        /// </para>
        /// </remarks>
        void ShipCombat();

        /// <summary>
        /// The save this machine holds under <paramref name="saveKey"/>, or
        /// <see cref="ScenarioPayload.None"/> if there is none.
        /// </summary>
        /// <param name="saveKey">
        /// Which save to read. M9 asks for <see cref="LobbySaveNames.ScenarioSlot"/>;
        /// M11e asks for whatever the lobby selected.
        /// </param>
        /// <remarks>
        /// Read by <em>both</em> sides, which is what makes the offer cheap: a
        /// host reads it to know what it can offer, and a client reads its own to
        /// know whether the offer is worth accepting. A peer rejoining a session
        /// it already transferred from therefore costs nothing.
        /// <para>
        /// Never throws for the ordinary "no save yet" case — a machine that does
        /// not hold this save is not in error, it simply has nothing to offer and
        /// everything to receive.
        /// </para>
        /// <para>
        /// <b>The key is not trusted here.</b> It reaches a path only after
        /// <see cref="ScenarioPayload.IsAllowedDestination"/>, because a request
        /// naming a save is the one place a peer's word decides what the
        /// <em>host</em> reads off its own disk.
        /// </para>
        /// </remarks>
        ScenarioPayload ReadScenario(string? saveKey);

        /// <summary>
        /// Writes a received save to disk, replacing any of the same name.
        /// False if it could not be written.
        /// </summary>
        /// <remarks>
        /// The destination is <see cref="ScenarioPayload.SaveName"/>, and M11e is
        /// what changed that: M9 used its own constant and treated the wire's name
        /// as informational, but the synchronised load makes every peer load the
        /// lobby's key, so the name has to travel. It is therefore
        /// <b>validated rather than trusted</b> — the implementation must refuse
        /// anything <see cref="ScenarioPayload.IsAllowedDestination"/> rejects, on
        /// its own terms and not merely because a session checked first.
        /// <para>
        /// Content may arrive split into numbered parts and must be reassembled
        /// with <see cref="ScenarioPayload.JoinContent"/> — the parts are a wire
        /// concern and the game must find the ordinary <c>content.zip</c> it wrote.
        /// It must also stage and move rather than write in place, so an
        /// interrupted transfer cannot leave a half-written save behind.
        /// </para>
        /// </remarks>
        bool WriteScenario(ScenarioPayload payload);

        /// <summary>
        /// Starts loading a save. Null if it began; an outcome if it could not.
        /// </summary>
        /// <remarks>
        /// Not whether it finished — a campaign load takes seconds and a scene
        /// teardown, so the finish comes back separately as a
        /// <see cref="LoadFinishedEvent"/>. A non-null outcome means the
        /// implementation could already tell the load would not happen: the save
        /// is not there or is not the one the host means
        /// (<see cref="LoadOutcome.Unavailable"/>), or the game would refuse to
        /// start it (<see cref="LoadOutcome.Refused"/>). The session reports that
        /// immediately rather than waiting out a two-minute timeout for silence.
        /// <para>
        /// An outcome rather than a bool, because those two answers are not the
        /// same question: "I do not have that save" is what M11e will act on. The
        /// distinction matters at all because the game's own completion callback
        /// fires <b>only</b> on success — everything answered here is something
        /// the caller worked out for itself.
        /// </para>
        /// </remarks>
        LoadOutcome? BeginLoad(string? saveKey, int selectionVersion, string? saveDigest);

        /// <summary>
        /// Write the combat checkpoint for <paramref name="turn"/>. Hosts only.
        /// M12c.
        /// </summary>
        /// <remarks>
        /// Returns nothing, and unlike <see cref="ShipCombat"/> it reports nothing
        /// later either. The session asks at a moment where every
        /// <c>CanSave(false)</c> refusal is already known to be false, so a refusal
        /// here is an anomaly to be logged once rather than a state to be polled
        /// out of — and nothing in the protocol waits on the write, so there is no
        /// session state for an answer to advance.
        /// <para>
        /// <b>Synchronous, deliberately.</b> An implementation must not defer the
        /// write: the instant is what gives the save its whole value, and a
        /// checkpoint taken a few frames later holds a turn that has already begun
        /// simulating.
        /// </para>
        /// <para>
        /// A no-op is a legitimate implementation for anything with no game behind
        /// it; the harness stands in for the write exactly that way.
        /// </para>
        /// </remarks>
        void WriteCheckpoint(int turn);
    }
}
