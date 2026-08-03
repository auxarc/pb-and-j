# Networking architecture (M4+)

Our design, not reverse-engineering — `docs/notes/` holds the game research this builds on.
Game facts cited here are verified against decompiled `Assembly-CSharp.dll` for 2.2.2-b8339.

Status: current as of M5e. Sections marked **(M6+)** describe the intended path, not what ships now.

## Topology

Host-authoritative star. One player's game process is the host and the single source of truth;
every other player connects directly to it. 2–4 players.

- No dedicated server — the game has no headless build.
- No peer-to-peer mesh — a mesh has no arbiter for a non-deterministic sim.
- No lockstep — the combat sim is non-deterministic (established in M0–M3), which rules it out.
- **No host migration, ever.** If the host leaves, the session ends; each client keeps its local
  combat state and continues single-player.

`MaxPeers` is a Core constant (3 clients + host by default), configurable per session.

## Host authority

The host's ECS *is* the game state. Clients hold a presentation copy and never make an
authoritative decision.

- Everything a client sends is a **request**: "I would like these orders for turn N", "I am ready".
- The host validates every request against server-side truth and may reject it. **A client is
  never trusted about which units it owns.**
- The host is the only process that calls `CombatUtilities.ConfirmExecution`.
- The host is the only process that runs the sim. Clients never flip `combat.Simulating`.

## Session lifecycle

Host: `Idle → Listening → Lobby → InCombat(Planning ⇄ Committing ⇄ Executing) → Closing → Closed`
Client: `Idle → Connecting → Handshaking → Lobby → InCombat(Planning → Submitted → Watching) → Disconnected | Faulted`

Handshake:

1. Client → `Hello { magic, protocolVersion, modVersion, playerName }`
2. Host → `Welcome { protocolVersion, sessionId, assignedPeerId, hostName, peers[], currentTurn }`
   or `Reject { reason, detail }` followed by an immediate disconnect.
3. Host → broadcast `PeerJoined { peerId, name }` to everyone else.

`Welcome` carries `currentTurn` because without it a joining peer cannot construct a matching
`Ready { turn }` at all.

Peer ids: host is always `0`; clients get monotonically increasing ids from `1`, **never reused
within a session**. That invariant still holds after M5e — see [Reconnect](#reconnect-m5e), which
works around it rather than amending it.

Mid-combat join is not supported in M4–M9. Late join would ride the combat-save transfer path
(`docs/notes/save-and-replay.md` establishes that a planning-phase save is near-lossless) — which
M9 has now built as [Scenario transfer](#scenario-transfer-m9), so what remains is the join, not the
transport.

## Combat lifecycle

The host detects combat entry/exit as an edge on `IPbjGameBridge.InCombat`, observed in `Pump`,
and broadcasts `CombatStart { turn }` followed immediately by `Assignments`, then `CombatEnd` on
exit. Assignment travels in exactly one message type so there is one way to express it — the same
`Assignments` message is re-broadcast if the roster changes mid-combat.

- `TurnBarrier` resets on `CombatStart`.
- `CombatEnd` unlocks every peer — including any sitting Ready when the host's combat resolves.

**The edge is observed *after* the mailbox drain, not before** (M5a). When the last turn's
execution is what ended the combat, the queued `LocalTurnComplete` and the already-cleared
`InCombat` flag arrive in the same pump. Observing the edge first moves the host to `Lobby`, and
the drained `LocalTurnComplete` is then swallowed by its "only while executing" guard — taking the
final turn's `TurnComplete`, and from M5d its snapshot, with it. Draining first delivers the
results and takes the exit edge on the next pump. `PbjRuntime` seeds `lastInCombat` from the bridge
in its constructor, so a session started mid-combat reports no spurious entry.

A client acts on `CombatStart`/`CombatEnd` from the host, never on its own edge — its local combat
state carries no authority. It still has arms for its own edges so the event cannot throw.

Remaining gap: a client joining a host that is **already** in combat still derives Planning/Lobby
from its own `bridge.InCombat` in `HandleWelcome`. `CombatStart` fixes the edge case, not the
join-time case.

`Unready` withdraws a submitted batch so a peer can re-plan. It is idempotent, is ignored while the
host is executing or when it names a turn other than the current one, and has no UI hook — the game
has no un-ready button to intercept, because single-player has nothing to wait for. The console
command `pbj.unready` and the harness's `unready` are the PoC affordances.

## The turn barrier

**Batch-at-ready, not incremental streaming.**

1. Every peer plans locally in its own UI. Local orders are real `ActionEntity`s in that peer's own
   ECS, so all local prediction and preview work unchanged.
2. When a peer presses Execute, `CIViewCombatExecution.CheckAndAttemptExecution` is intercepted.
   Instead of executing, the peer sends `Ready { turn, orders[] }` — its **complete** order set for
   that turn — and enters a locked "submitted" state.
3. The host records that set, replacing any previous set from that peer for that turn. `Ready` is
   therefore idempotent, and a peer may un-ready and re-ready.
4. The host presses Execute → the host marks itself ready.
5. When every connected peer including the host is ready, the host runs the commit sequence below.

### Why batch, not incremental

`DataContainerSavedAction` has no stable action ID — its dictionary key is `blueprint_runtimeID`,
and the runtime id is not stable across a reload. The planning UI also creates, destroys and
re-drags orders constantly. Incremental sync would require inventing a mod-owned order identity
plus create/update/delete/reorder messages and conflict resolution — a large amount of protocol for
watching an ally plan in real time, which is not needed to play.

Batch-at-ready is one message type, idempotent, and stateless per turn.

If live plan-sharing is ever wanted **(M5+)**, add a mod-owned `PbjOrderId` component in a postfix
on `ActionCreationSystem.Execute` — the universal capture point for every newly-owned action.

### Commit sequence: apply → commit → verify → broadcast

**This ordering is load-bearing.** `CombatUtilities.ConfirmExecution` is `void` and has four silent
early-return exits (`CombatUtilities.cs:50–74`), each logging only a warning:

- not in game state `"combat"`
- `combat.Simulating` already true
- no current scenario or current step
- `stepCurrent.core.executionAllowed == false` — a **normal gameplay condition** on scripted steps

So `IPbjGameBridge.CommitTurn()` returns `bool`, verified by observing that `currentTurn` actually
advanced. `TurnCommit` is broadcast **only after** that verification succeeds.

**Verified in-game (M4 Step 0b, 2026-08-02):** calling `ConfirmExecution(1)` during planning gave
`turn 0 -> 1 | COMMITTED`; calling it while already simulating gave `turn 1 -> 1 | REFUSED`, with
nothing but a `Debug.LogWarning` to signal it.

Broadcasting before committing would leave every peer locked and waiting while the host silently
sits in planning, with nothing detecting it. On commit failure: unlock all peers, log, stay in
planning.

### Turn advance is the authoritative signal, not the button

`ConfirmExecution` does **not** check `combat.isScenarioAllowingExecution` — only
`CheckAndAttemptExecution` does (`CIViewCombatExecution.cs:202`). The execution lock is therefore
**UI only**.

Worse, the commit point is reachable without any button: `PhantomBrigade.Functions.CombatForceExecution`
is a `[Serializable] ICombatFunction` whose `Run()` calls `ConfirmExecution(turnsAdvanced, timeScaleForced)`
directly from scenario YAML, with `turnsAdvanced` clamped 1–50. The debug console can call it too.

Therefore the host detects execution start by postfixing `ConfirmExecution` / watching `CurrentTurn`,
and treats a forced advance as an authoritative turn change to broadcast. Button interception is UX.

### Turn numbering

`ConfirmExecution` advances `currentTurn` *before* simulation begins. By the time `Simulating` is
removed, the ECS already shows the next planning turn. The executed-turn number is captured in
`HostSession` at commit time and carried through `TurnComplete` — **never** read from the bridge at
the execution-end hook, which would report executed-turn + 1.

### Barrier edge cases

| Case | Behaviour |
|---|---|
| `Ready` for a past turn | ignore (late duplicate after the host already committed) |
| `Ready` for a **future** turn | **resync the peer to the host's turn** — do not disconnect |
| `Ready` while executing | ignore with a warning log |
| Peer disconnects | **recompute** the barrier; commit immediately if the rest are ready |

Future-turn is a resync rather than a protocol violation because a scenario force-execute makes it
reachable through no fault of the client — a race between a client's `Ready` and a forced advance
would otherwise kick an innocent peer. A dead peer must never wedge the session.

## Message flows

A client never sends positions, damage, hit results, or turn advances.

The message set is phased, so M4's handshake milestone carries the smallest set that can prove
itself. Type bytes are assigned once and never reused.

**Shipping in M4 (9 types, `PbjMessageType` 1–9):**

| # | Message | Direction | Payload |
|---|---|---|---|
| 1 | `Hello` | up | magic, protocolVersion, modVersion, playerName |
| 2 | `Welcome` | down | protocolVersion, sessionId, assignedPeerId, hostName, peers[], currentTurn |
| 3 | `Reject` | down | reason, detail |
| 4 | `PeerJoined` | down | peerId, name |
| 5 | `PeerLeft` | down | peerId, name |
| 6 | `Ready` | up | turn, orders[] |
| 7 | `TurnCommit` | down | turn |
| 8 | `TurnComplete` | down | turn, digest |
| 9 | `Bye` | both | reason |
| 10 | `Assignments` | down | per-peer unit lists |

**Added in M5a (types 11–14):**

| # | Message | Direction | Payload |
|---|---|---|---|
| 11 | `Unready` | up | turn |
| 12 | `OrderResult` | down | turn, accepted, rejected[] |
| 13 | `CombatStart` | down | turn |
| 14 | `CombatEnd` | down | *(none — the type byte is the whole message)* |

**Added in M5c (types 15–16):** `Ping` (down, nonce) and `Pong` (up, nonce). A nonce rather than a
timestamp, because Core never reads a clock and putting one process's time on the wire would imply
a clock synchronisation the protocol does not have; it costs nothing now and makes a
round-trip-time measurement possible later with no wire change.

**Added in M5d (type 17):** `Snapshot` (down: turn, digest, units[]).

**Added in M5e (type 18):** `Rejoin` (up: magic, protocolVersion, modVersion, playerName,
sessionId, claimedPeerId, resumeToken). `Welcome` also gained `ResumeToken` — a layout change, and
the reason `PbjProtocol.Version` is now **2**.

**Unallocated:** type bytes 19+.

Adding message types does not bump `PbjProtocol.Version`, which pins *layout*. It is not a
compatibility guarantee either way: a v1 host that receives a `Pong` hits `HandleMessage`'s
`default:` arm and disconnects the peer. Both binaries are built from one tree by `make deploy`
and gated by `peer-selftest`, so mixed versions do not arise; if the mod and the harness are ever
distributed separately, bump on every wire change instead.

`Assignments` was originally deferred and then pulled forward during 4e: without it a client is
never told which units it may plan, which makes it unusable as anything but a harness driven from
the host's log. It is advisory — the host re-checks every inbound order against its own copy, so a
client that ignores or forges it simply gets its orders rejected.

`Ready` carries its order list from the start, even though M4's client is a harness — the payload
codec already exists, so there is no saving in leaving it out and no wire change later.

### `OrderResult` identifies orders by batch index

Orders have no stable ID — that is the whole reason for batch-at-ready. `OrderResult` therefore
replies to one specific `Ready` with an accepted count plus `rejected: [(index, reason)]`, indexing
into the submitted batch. Echoing `(ownerName, blueprint)` instead would be self-describing in logs
but ambiguous the moment a unit has two orders of the same type in one turn.

The index makes the round trip on the existing effect/event pair rather than in session state:
`ApplyOrderEffect` carries a `BatchIndex` out, and `OrderAppliedEvent` carries the verdict back.
**The ordering guarantee is not the effect queue's FIFO order — it is that `PbjRuntime.Execute`
calls `session.Handle` synchronously before returning.** Every order's fate has therefore folded
into the accumulator before the `CommitTurnEffect` queued behind it is dequeued. A refactor that
posted `OrderAppliedEvent` to the mailbox instead would break this silently, so a test pins it.

Ownership rejection reuses the same reason enum as the game's, as `OrderApplyResult.NotOwned`. No
bridge ever returns it — the host produces it before the order is handed to the game — but keeping
one reason set means one wire encoding and one `default:`-arm risk.

**`OrderResult` is sent only after a commit succeeds.** A refused commit re-opens planning, so
those orders have no outcome yet; reporting an accepted count for a turn that never ran would be a
lie. The accumulator is discarded on a refused commit and when a peer leaves, so a stale rejection
can never attach itself to the next commit or to a departed peer.

### Messages do not validate; sessions do

Message constructors carry no blank-string or range guards. A handshake message arrives from an
*unauthenticated stranger*, so a guard there would turn "peer sent an empty name" into a decode
exception and a disconnect, when the correct behaviour is a clean `Reject { InvalidName }` from
`PbjPeerRegistry`. Validation is the session state machine's job.

The two exceptions, both about not trusting a peer with our memory, live in the codec: null lists
normalise to empty, and every count is bounds-checked on decode (orders per `Ready`, peers per
`Welcome`, points per order). `OrderPayload` keeps its own constructor guards because it is applied
to the live game; `OrderPayloadCodec` wraps those into `PbjProtocolException` so they surface as a
protocol fault rather than a caller bug.

## Presenting execution on the client

The sim is non-deterministic, so **the client must not simulate.** Any client-side preview
execution diverges visibly within a single turn.

| Milestone | Client sees |
|---|---|
| M4 | Nothing — no state stream. `TurnCommit`/`TurnComplete` only; the client is a harness. |
| M5 | **Snapshot correction** (the guaranteed floor): compact authoritative state hard-set on arrival — shipped in M5d, see below. |
| M6 | **Keyframe streaming** (the target): the host serializes `CombatReplayHelper`'s per-unit transform track for the executed turn and the client animates its view along it — shipped in M6, see below. Presentation only; the snapshot remains the correction. |

### Snapshot correction (M5d)

`SnapshotMessage` carries the executed turn, the host's digest, and a `UnitSnapshot` per unit:
name, position, rotation, facing, integrity, dead flag, death time. Broadcast immediately *after*
`TurnComplete`, never before — see that message's remarks.

**Why hard-setting works on a client and would not on a host.** A client never sets
`combat.Simulating`, so `ActionPlaybackSystem` is not driving transforms and a direct component
write is not overwritten on the next tick. The same call on a simulating host would be a losing
battle. That asymmetry is the entire reason snapshot correction is viable as the client-side floor.

**The client verifies its own correction, in pure Core.** `ApplySnapshotEffect` hard-sets, the
runtime recomputes the local digest, and a `SnapshotAppliedEvent` carries expected-vs-actual back
into the session — the same effect/outcome shape `CommitTurnEffect` already uses. The comparison
therefore lives inside the coverage gate rather than in the glue.

**Capture and digest are one walk, not two.** `ComputeStateDigest` is a projection of
`CaptureSnapshot` via `UnitSnapshot.ToUnitState`. If they were independent walks they could
disagree about which units exist, and a client would fail its post-correction check for reasons
having nothing to do with correction. Capture covers every unit with a resolvable name — hostiles
included, not just `AssignableUnitNames`.

**Wire floats are raw, not quantised.** Quantisation is a *digest* rule: the digest compares values
across two runtimes and so must never touch float formatting. There is no formatting on the wire —
`PbjWriter.WriteSingle` reinterprets the bits and emits them with explicit little-endian shifts, so
the bytes are identical under Mono-on-Wine and .NET, NaN payloads included.

**Size.** ~85 bytes a unit; the 128-unit cap lands near 11 KB, about 1% of `MaxFrameLength`. Over
the cap, capture clamps and logs loudly rather than letting the encoder build a frame the far side
would reject — a silently truncated snapshot reads as a correct one.

**Stale client orders are cleared first.** A client plans real `ActionEntity`s that never execute,
so left alone they accumulate and `CaptureLocalOrders` starts re-submitting orders the host already
ran. `ClearLocalOrdersEffect` runs immediately before the hard-set. The disposal cascade does not
bite, because applying a snapshot writes components, not actions.

**The "resolving…" overlay was cut.** It would have been invisible: the raise and the lower both
happen inside one `PbjRuntime.Run` loop — one pump, one frame, nothing rendered between — and the
window it was meant to cover is already covered by the execution lock set at `TurnCommit`. A
visible overlay is a Mod-side presentation change, not a Core effect.

**Writing components is enough to render it.** `PositionLinkSystem` and `RotationLinkSystem` are
reactive on `CombatMatcher.Position` / `.Rotation` and call `CombatView.OnPosition`/`OnRotation`,
which set the view transform; neither is gated on the simulation running, so a correction arriving
between turns is visible immediately. M6 briefly assumed otherwise — see finding 4 under "Keyframe
streaming (M6)" for why `TransformLinkSystem` is a red herring for units.

**Two things a snapshot cannot fix**, both worth knowing before reading a `STILL DIVERGED` line as
a bug:

- Entities are never created from a snapshot. If the two sides disagree about which units exist,
  no amount of position-setting helps; the mismatch is reported and skipped.
- A client's own `combat.currentTurn` never advances, because it never commits. Harmless for a
  harness client; it is one of the reasons a second real game instance is hard.

### Divergence detection (ships in M4)

`TurnComplete` carries a `digest` — a Core-computed, order-independent hash over
`(unitName, position, integrity)` for all units. The client compares against its own and logs
`DIVERGED` loudly. This gives a diagnostic before any state sync exists.

**The digest must be computed over integer quantizations** (e.g. `(int)Math.Round(x * 10)` combined
via explicit shifts), never over formatted strings. The host is Mono/net472 under Wine; the harness
is .NET 9 on Linux. Float-to-string formatting differs between those runtimes, so a
string-formatting-shaped digest — which this codebase's "one invariant-culture test per formatter"
convention naturally invites — would report permanent false divergence.

### Keyframe streaming (M6)

The host records everything the replay UI scrubs through, in `CombatReplayHelper.units` — a
`static Dictionary<int, ReplayUnit>` of parallel keyframe lists. M6 serialises the transform track
so a client watches the turn rather than being teleported to its outcome.

**Transforms only.** `ReplayKeyframeUnitState` (heat plus six part integrities) and
`ReplayKeyframeUnitPose` (two sync bools and a `ReplayKeyframeUnitJoint[]`) are both deliberately
out. Poses are orders of magnitude heavier and need the puppet machinery the replay UI turns on;
state keys would double the wire to drive damage *decals* when frame integrity already travels in
the snapshot. `TransformKey` maps onto the `Vec3`/`Vec4` M5d added, with no new primitives.

#### The four things the game does that capture has to work around

Each of these was verified against decompiled 2.2.2-b8339, and each one reverses a decision that
looked obvious beforehand.

**1. Tracks accumulate for the whole combat, not per turn.** `experimentalMode` is `true`
(`CombatReplayHelper.cs:26`) and `OnExecutionStart` only clears `units` when it is *false* (line
279). Capture therefore slices to the current turn — **by list index**, walking back from the end
to the key `OnExecutionStart` wrote, never by comparing times against `turnStartTime`. That value is
`Mathf.RoundToInt(GetSimulationTime())` (line 240) while `OnExecutionEnd` stamps its key with the
unrounded time (line 346), so a time comparison can drag the previous turn's final key into this
turn's window and produce an out-of-order track.

**2. The recorder is already switched off when we run, and its last key is the wrong position.**
`OnExecutionEnd` clears `recordingAllowed` (line 416), and it runs *earlier in the same frame* than
our capture hook: `CombatUILinkSimulationEnd` sits in `CombatUISystems` (slot 72 of
`CombatSystems.cs`), ahead of `CombatExecutionEndLateSystem` (slot 93), both reacting to the same
`Simulating.Removed()` collector. Two consequences:

- Capture must **not** gate on `CombatReplayHelper.IsRecordingAllowed()`. It is false every turn by
  the time we look, and gating on it would ship the feature inert while every test still passed.
- `CombatExecutionEndLateSystem`'s own `OnUnitSnapshot(entity, timeChecked: false)` call
  (`CombatExecutionEndLateSystem.cs:65`), which happens after `ForceUnitPosition` snaps the unit to
  its projected path, is a **no-op** for the same reason. So the recorder's final key predates the
  force-set and does not match what `CaptureSnapshot` reads.

Capture therefore **appends its own final key** at `windowEnd`, read from the same
`unit.position`/`unit.rotation` the snapshot reads, in the same call. That is what makes *the last
key of every track equals the snapshot for that unit* true by construction rather than by hope — and
that equality is the entire reason playback and correction cannot fight each other.

**3. Recording is conditional on prediction.** `OnExecutionStart` is only called when
`ScenarioUtility.predictionEnabled` (`CombatUILinkSimulationStart.cs:64`). With prediction off there
are no tracks at all; capture logs `no keyframes recorded this turn` once and the host broadcasts
nothing. The turn still completes and snapshot correction still lands, so the degraded case is
exactly M5 behaviour.

**4. Units are driven by `CombatView`, not `TransformLink` — and ECS writes do render.**
`TransformLinkSystem` looks like the ECS→view path and is gated on `CombatMatcher.SimulationTime`
(`TransformLinkSystem.cs:29`), which nothing replaces outside the simulating branch of
`SimulationTimeSystem.cs:112`. That reading led to a wrong prediction — that corrections were
invisible between turns — and to a `hasTransformLink` filter that silently matched **zero units**,
which is how it was caught: `pbj.replay-last` reported "no recorded unit is present in this combat"
despite a perfectly good capture.

`CombatEntity.ReplaceTransformLink` is never called anywhere in the game. No combat unit has that
component, so `TransformLinkSystem` never sees one. Units are driven by **`PositionLinkSystem` /
`RotationLinkSystem`** (registered via `CombatViewLinkSystems`, `CombatSystems.cs:63`), which are
reactive on `CombatMatcher.Position` / `.Rotation` and call `CombatView.OnPosition` /
`OnRotation` — a plain `transform.position = v`. **Nothing gates them on the simulation running**, so
`ApplySnapshot`'s component writes have always rendered. M5d was never broken.

The correct handle for a unit's rendered transform is `unit.combatView.view.transform`, which is
exactly what the game's own `ApplyTimeToUnit` writes (`CombatReplayHelper.cs:1090-1091`).

#### Playback writes the view transform, never the ECS

We reject `CombatReplayHelper`'s *activation machinery* — `SetReplayActive` sleeps puppets and
disables ragdoll physics, and is gated behind `IsReplayAllowed()`: scenario `replayUsed`, an
unlocked `feature_combat_replay`, and `Unit_Selection` UI mode (lines 193-203), none of which a
client can assume. But we adopt its *write target*: `ApplyTimeToUnit` writes
`view.transform.position/rotation` (lines 1109-1110), and so does `KeyframePlayer`.

Writing the view rather than the ECS is a choice, not a necessity — finding 4 established that ECS
writes render too. It is the right choice for three reasons:

- It is genuinely presentation. ECS position feeds order authoring, scenario state volumes and the
  state digest, so animating it sixty times a second would let a player author orders from
  historical positions and would put a half-played animation into the correction check.
- It self-heals. The next `ReplacePosition` on a unit — from execution, or from the next snapshot
  correction — fires `PositionLinkSystem` and snaps its view straight back to ECS truth, so an
  abandoned playback cannot leave anything permanently displaced.
- It makes `pbj.replay-last` safe on a host, which is what gives M6 an in-game gate with only one
  game instance: authoritative state is never touched.

#### Ordering: no deferral machinery

`TurnComplete` → `Snapshot` → `Keyframes`. The snapshot hard-sets and verifies its digest exactly as
in M5d — that path is untouched — and playback then animates the view from the turn's start towards
the same final state. Because the last key equals the snapshot, the two agree at the end. Nothing in
`PbjRuntime` became asynchronous, `ClientSession` gained no new state, and a playback interrupted
mid-flight is corrected by the next turn's snapshot anyway.

`PlayKeyframesEffect` reports nothing back, unlike `ApplySnapshotEffect`: there is no correctness
claim to verify. `StopKeyframesEffect` fires on `CombatEnd`, on `Bye` and on `Fault` — the last of
these matters most, because a faulted session handles nothing further, so a playback left running
there would never be stopped by anything else.

#### Size

`unitSamplingInterval` is `0.1f`, so a 5-second turn is ~53 keys per unit. At 32 bytes a key
(time plus `Vec3` plus `Vec4`, raw floats) that is ~1.7 KB per unit, i.e. ~51 KB for a 30-unit
combat — two orders of magnitude above a snapshot, which is where 5b's outbound queue starts
genuinely earning its place. `MaxTracksPerKeyframes` is 128 (mirroring `MaxUnitsPerSnapshot`) and
`MaxKeysPerTrack` is 192, bounding a message near 786 KB, under `MaxFrameLength`; a test pins the
arithmetic rather than trusting it. Note that a worst-case frame crosses the 256 KiB slow-link
warning threshold, so that warning can fire on a perfectly healthy link during a large turn.

Over the per-track cap, capture **decimates rather than truncates** — it keeps the first and last
key and thins between them, so a long turn loses temporal resolution instead of its ending. A track
truncated at the tail would end playback short of the state everyone was just corrected to.

#### Verification, and what it does not cover

Two gates, neither sufficient alone:

- `make peer-selftest` scenario 5 drives **synthetic** tracks through the real codec and asserts the
  wire fidelity, that sampling at `windowEnd` reproduces the snapshot exactly, that sampling at
  `windowStart` does not (so the test cannot pass on a constant track), and that `CombatEnd` stops
  playback. It pins the protocol and the sampler; it cannot prove capture is right.
- `pbj.replay-last` in the running game is the real-data half. It round-trips a genuine capture
  through `PbjMessageCodec.Encode`/`Decode` before playing it, so one command exercises capture,
  re-key, slicing, codec, sampler and render together. Expect units to slide rather than walk —
  poses are out of scope, and sliding is exactly what a client sees today.

#### Why playback slides instead of walking, and what it would take

Confirmed by eye on the host via `pbj.replay-last`: units translate along their real paths in an idle
pose. Expected — M6 moves the root transform and nothing drives the animator. Two routes out, with
the costs measured rather than guessed:

**Streaming poses does not fit.** `ReplayKeyframeUnitJoint` is a `Vector3` plus a `Quaternion`, 28
bytes, and `recordedBones` is built from every equipped part's `jointsLookup`
(`UnitVisualManagerSimple.RefreshRecordedBones`) — dozens per mech. At ~30 bones × 28 B × ~53 keys
that is ~44 KB *per unit per turn*, so a 30-unit combat lands near 1.3 MB: **over
`PbjRuntime.MaxFrameLength`**. It would also need the bones not to be fought by the animator, which
is what `SetReplayActive`'s puppet-sleeping does — the machinery M6 deliberately avoided because it
is gated behind `IsReplayAllowed()`.

**Writing `velocity` does not work, and this section used to say it did.** The claim was that
`MechAnimationSystem`'s walk blend — `currentMovementSpeed`, `currentMovementSpeedFlattened`,
`speedNormalized`, `isMoving` — reads `actor.velocity.v`, so a client could derive velocity from the
transform track it already receives, write that one ECS component, and get a walk for zero extra
bytes. An adversarial review, re-verified by hand against decompiled 2.2.2-b8339, refuted it on two
counts:

- **`isMoving` is not derived from velocity.** It is
  `actor.hasCurrentMovementAction && !actor.hasCurrentMeleeAction` (`MechAnimationSystem.cs:1278`),
  OR-ed with melee-follow logic, and written unconditionally at `:1319`. `CurrentMovementAction` is
  written *only* by `ActionPlaybackSystem.cs:2015`, during simulation — which a client never runs.
  Velocity feeds the *speed floats* alone (`:1322-1334`), so writing it leaves the mech in idle with
  a non-zero speed parameter: the slide we already have. The movement VFX confirm the gating — they
  fire on the `isMoving` transition (`:2130-2141`), not on speed.
- **The system does not run when we would need it to.** Its non-reactive path is gated on
  `Time.timeScale > 0f` (`MechAnimationSystem.cs:130-133`), and `SimulationTimeSystem.cs:132` sets
  `Time.timeScale = predictionEnabled ? 0f : timeScaleMain` on *every* Simulating→false transition.
  With prediction on — the normal case — the host in post-turn planning sits at `timeScale == 0`, so
  `UpdateAnimationsForAll` never executes and `Time.deltaTime` is zero regardless. **`pbj.replay-last`
  on the host, the only single-instance gate M6 has, cannot display animation at all.**

What *did* hold, and is worth keeping: `Velocity`/`VelocityDirection` are never a reactive-collector
trigger — only group filters, and `CombatCrashingSystem` (`:63`) additionally needs the `Crashing`
flag and triggers on `SimulationTime` (`:76-79`) — so writing them could not have woken the sim. And
`mechCollector`'s membership requirements (`:57`) are satisfied on a client, so `UpdateUnit`'s
unguarded reads would not have thrown. The idea was safe. It simply would not have worked.

**The rejected middle option, recorded so it is not re-derived a fourth time.** An animator-only
variant does work: skip the ECS entirely and write `isMoving`, `currentMovementSpeed*`,
`speedNormalized`, `runningParallel = local.z × |v|` and `runningLateral = −local.x × |v|`
(`:2107-2108`, `:2165-2166`) straight onto `view.animator`, with `view.pauseUpdates = true` so
`UpdateUnit` does not fight it and an explicit `view.animator.Update(dt)` so `timeScale == 0` stops
mattering. It is host-verifiable today and it preserves M6's view-only invariant. It was declined
because M8 deletes all of it — `PrepareUnitForReplay` disables the animator outright
(`CombatReplayHelper.cs:839`) — and because a third code-complete-but-unverified milestone is not
what this project needs. **Sliding is the accepted degraded state until M8.**

### Replay handoff (M8) — the intended answer to "why does it slide"

**Do not stream poses. Hand the client the host's replay and let the game play it.**

The game already owns a system that turns recorded combat into a visually complete playback — poses,
particles, beams, projectiles, terrain destruction, audio, the lot. Re-implementing any part of that
is the wrong trade. M8's shape is: reconstruct the host's `CombatReplayHelper` state on the client,
call `SetReplayActive(true)`, and let `CombatReplayHelper.Update` drive it.

This also fits what a client already is. It is inherently one turn behind — it never simulates, and
it is corrected at end of turn — and the host sits in planning waiting for it to ready. There is a
natural window in which the client can watch the turn it just missed, as if the combat were playing
out for the first time.

#### The activation gate is satisfiable, which M6 got wrong

M6 rejected the scrubber partly because it is "gated behind `IsReplayAllowed()`". That is true —
`SetReplayActive` self-gates (`CombatReplayHelper.cs:578`) — but the gate was never examined:

| Condition | On a client, post-turn |
|---|---|
| `activationAllowed` | True — set at `OnExecutionEnd`, which is exactly when playback would start |
| UI mode is `Unit_Selection` | True — where a client sits after a turn |
| scenario `coreProc.replayUsed` | Same scenario as the host, when both loaded the same save |
| `feature_combat_replay` unlocked | Same campaign as the host, when both loaded the same save |

The last two hold **because of** the save-file transfer that stage 2 already requires. And all four
are reachable from a Harmony patch if one ever does not. The gate is not the obstacle; it was
assumed to be one without being read.

#### Joint identity must travel, because bone order is positional and derived

This is the part most likely to fail silently and horribly. `ApplyTimeToUnit` writes
`recordedBones[l].localPosition = joints[l].position` — a **positional** correspondence between the
pose array and a list rebuilt per-unit from its equipment. `RefreshRecordedBones`
(`UnitVisualManagerSimple.cs:2062`) composes that list from, in order:

1. every `visualsWithJoints[].jointsLookup` entry — a `Dictionary<string, Transform>`, so these
   have **real string keys**;
2. socket-mapped skinned-mesh bones — which contribute nothing: the guard is
   `skinnedMeshRenderer.bones.Length == 0` and the loop then runs to `bones.Length`, so the body is
   unreachable. Appears to be a bug in the game; harmless, but do not rely on it staying that way;
3. `visualLegGroupComposite.legs[]`, or failing that every `visualLegGroups[].legs[]`, each
   contributing `jointYawRoot`, `jointPitchRoot`, `jointPitchMid`, and `jointPitchLow` when
   `tripleMode` — **unnamed, purely positional**.

With identical saves the two sides have identical equipment, and the orders would almost certainly
agree. "Almost certainly" is doing far too much work: group 1 depends on `Dictionary` enumeration
order, which is an implementation detail rather than a guarantee, and groups 3 and 4 depend on leg
enumeration and on `tripleMode` matching per leg. A mismatch does not throw — it writes the elbow's
transform onto the knee, for every frame of playback.

**So capture a stable identity per bone alongside the pose, and remap on arrival.** The identity is
available or derivable for every contributing group:

- group 1 → the `jointsLookup` key, prefixed by the owning visual's index to keep it unique across
  parts, e.g. `v2:joint_shoulder_l`;
- group 3/4 → a composed structural key, e.g. `leg3:pitchMid`, from the leg's index and its role.

The wire then carries `(key, position, rotation)` per joint. The client builds the same key list
from its own `recordedBones`, resolves an index map once per unit per turn, and applies by name. A
key present on one side and not the other is reported and skipped — the same "structural mismatch is
reported, never papered over" rule `ApplySnapshot` already follows. That turns the worst failure
mode of this design from silent visual nonsense into a log line.

#### Volume, and why it is the least interesting problem

Poses dominate: ~28 bytes a joint, dozens of joints per mech, ~53 samples a turn — call it 44 KB per
unit per turn, ~1.5 MB for a 30-unit combat, before the level, projectile and beam tracks. That is
over `MaxFrameLength`, but that constant is ours and the payload chunks naturally along turn and
unit boundaries. It arrives during the host's planning phase, which is dead time on the wire, and
5b's outbound queue and writer thread exist precisely so a payload this size cannot stall a frame.

#### Making it look like execution, not like a replay

The obvious way to do this is the trap. **Do not advance `combat.simulationTime` on a client.**

It looks like the right lever — it is what drives the execution HUD, the timeline, the shader
globals and the animation clock — but `CombatMatcher.SimulationTime` is the trigger for **~38
reactive systems**, and only a minority check whether they should be simulating:

| Self-gates on `combat.Simulating` | Does **not** |
|---|---|
| `ActionPlaybackSystem`, `ScheduledAttackSystem` | `CombatDamageSystem`, `ProjectileCollisionSystem`, `PhysicsSystem`, `HeatDissipationSystem`, `BarrierRegenerationSystem`, `OverheatingSystem`, `UnitStatusBuildupSystem`, `CombatCrashingSystem`, … |

So replacing `simulationTime` runs most of the combat simulation, `Simulating` flag or not — on a
client, against a non-deterministic sim, which is the exact divergence the whole architecture exists
to prevent. The M5d note that "a client never sets `combat.Simulating`" is necessary but it is not
sufficient: the clock is the real switch, and it is wired to far more than presentation.

**Drive the presentation hooks directly instead.** Every element of the execution look is reachable
without the clock:

| Element | How |
|---|---|
| Execution HUD state | `input.ReplaceCombatUIMode(CombatUIModes.Simulating)` — a component write |
| Unit motion | already shipped: M6 keyframes onto `combatView.view.transform` |
| Unit animation | The recorded bone poses, via `ApplyTime`. **Not the animator** — `PrepareUnitForReplay` → `SleepPuppet` sets `view.animator.enabled = false` (`CombatReplayHelper.cs:839`), so animator parameters are dead here. An earlier version of this row claimed `MechAnimationSystem` runs during planning and "only needs `velocity` written"; both halves are wrong — see the section above |
| Timeline scrubbing | `CIViewCombatTimeline.ins.OnTimeChange(t)` — imperative, callable with the playback cursor |
| Simulation-time shader globals | `Shader.SetGlobalFloat`/`SetGlobalVector` directly |
| Projectiles, beams, VFX | the replay tracks — nothing else can supply these without the sim |

#### Call `SetReplayActive`'s pieces, not `SetReplayActive`

This is what makes "looks like the battle, not like a replay" achievable. `SetReplayActive` is a
sequence of separable statements, and only two of them are the drawing:

- `PrepareUnitForReplay(entity)` per unit — sleeps the puppet and disables ragdoll so bone writes
  are not fought. **Required.**
- `ins.ApplyTime(t, timeCheck, applyShaderProperties)` each frame — the actual playback.

Everything else is replay-mode dressing we simply do not perform: `ReplaceCombatUIMode(Replay)`, the
`ui_combat_replay_start` audio sting, `CIViewCombatTimeControl.OnReplayActive`,
`CombatSceneHelper.OnReplayActive`, `CIHelperOverlays.OnReplayEnabled`. Skipping those and setting
`CombatUIModes.Simulating` ourselves gives the visuals of the host's turn inside the execution HUD.

Both entry points are private (`CombatReplayHelper.cs:748` and `:955`), so this needs
`AccessTools`/reflection rather than a direct call — ordinary practice for a Harmony mod, and
cheaper than the alternatives by a wide margin. It also sidesteps the `IsReplayAllowed()` gate
entirely, since that is checked in `SetReplayActive` and nowhere further down.

The cost of going around the front door is that we own the invariants `SetReplayActive` maintains:
puppets must be woken again afterwards, `activeLast` stays false so the game does not believe it is
in replay mode, and `CombatReplayHelper.Update` will not be driving `previewTime` for us — our own
playback cursor does. Those are worth listing in the implementation, not discovering.

**And a fourth, which vanilla never needs: set `view.pauseUpdates = true` per tracked unit.**
`SleepPuppet` disables `view.animator` and deactivates the *FBBIK* GameObject
(`CombatReplayHelper.cs:839-840`), but the animator's own GameObject stays active — and
`LateUpdateUnit` is gated only on `view.pauseUpdates` and `animator.gameObject.activeInHierarchy`
(`MechAnimationSystem.cs:164`), then runs **manual FinalIK solves**: `view.ikAimTorso.solver.Update()`
(`:591`) and `view.ikFullBodyIK.solver.Update()` (`:653`, `:994`). Those write bones regardless of
`animator.enabled`.

Vanilla replay never hits this because it always runs at `Time.timeScale == 0`, where the calling
path is gated off entirely (`:130-133`). **A client's `Time.timeScale` during playback is unknown** —
it never passes through the Simulating→false branch that sets it (`SimulationTimeSystem.cs:132`), so
it keeps whatever combat start or the save restore left. If that is non-zero, `LateUpdateUnit` fights
`ApplyTime`'s bone writes every frame, and the symptom would be twitching limbs that look like a
netcode bug. `pauseUpdates` is checked at `:164` and `:1254` and shuts both paths off.

#### What a client session should feel like

Two coherent answers, and they should be chosen rather than inherited:

- **"It is a replay"** — call `SetReplayActive(true)` and accept its UI: replay mode, scrub bar,
  audio stings, input suspended. Honest about what is happening, cheapest to build, and the client
  is unmistakably a spectator for those five seconds.
- **"It is the battle"** — drive `PrepareUnitForReplay` + `ApplyTime` under
  `CombatUIModes.Simulating`, as above. More work, more invariants owned by us, and the client
  experiences the turn the way the host does.

The second is the point of the exercise, but the first is a legitimate stepping stone and shares
almost all its machinery — the difference is which statements around `ApplyTime` get made.

#### Dependency

None of this is worth building before stage 2. The gate analysis, the equipment match that makes
joint remapping tractable, and the shared scenario all rest on both machines having loaded the same
save. Prove stage 2 with M6's sliding playback first; it is the precondition, not a detour.

M9 removes the manual step that made stage 2 awkward to arrange — see
[Scenario transfer (M9)](#scenario-transfer-m9) — but it does not remove the dependency. Stage 2 is
two people running two games; that still has to actually happen.

#### Still open

- A client whose entity set differs from the host's is the same structural mismatch that limits
  snapshot correction, and keyframes cannot fix it either.
- A client's `combat.currentTurn` still never advances, because it never commits. Playback is driven
  by the window on the wire rather than by local turn state, so this does not bite yet.
- Units spawned mid-turn are never in `CombatReplayHelper.units`, which is seeded from the
  `OnExecutionStart` roster, so they have no track and simply appear at their corrected position.
- `keyframeReveal`/`keyframeHidden` are not carried, so a unit revealed mid-turn is visible for the
  whole playback.
- **A client's `Time.timeScale` during playback has never been observed.** It gates whether
  `MechAnimationSystem` runs at all (`:130-133`) and therefore whether the `pauseUpdates` hazard
  above is live; a client never executes the branch that sets it. Worth logging on the first M8 run
  rather than reasoning about further.

Allocated by M6: `PbjMessageType.Keyframes = 19`, `PbjEffectKind.PlayKeyframes = 10` and
`StopKeyframes = 11`. By M9: `PbjMessageType.ScenarioOffer = 20`, `ScenarioRequest = 21`,
`Scenario = 22`; `PbjEffectKind.WriteScenario = 12`; `PbjInboundEventKind.LocalScenarioPull = 104`.
`PbjMessageType` 23+ and `PbjEffectKind` 13+ remain unallocated.

## Unit ownership and assignment

Assignment is per-session, decided by the host, sent as `Assignments` at combat start.

**The candidate set is `isPlayerControllable && CombatUIUtility.IsUnitFriendly(unit)` — not all
friendlies.** Friendly does not imply player-controllable: `ScenarioUtility.cs:2612–2631` sets
`isPlayerControllable` from scenario flags, so escort/convoy/scripted allies exist, and saves carry
independent `playerControllable`/`aiControllable` per unit. Dealing AI-driven units to peers would
put their orders in a fight with the AI planning systems.

**Default policy** (pure Core, `UnitAssignmentPlanner`): candidate `nameInternal` strings sorted
ordinal for stability, dealt round-robin across peers ordered by peer id, host first; remainder to
the host. Deterministic, testable, and re-derivable if a peer drops.

**Client-side enforcement is UX.** For units not assigned to it, the client sets
`isPlayerControllable = false` + `CIHelperWorldMarkers.OnUnitControlChanged(id)`. The unit stays
visible and friendly, but `InputCombatUnitSelectionUtility.AttemptUnitSelectionAtCursor` refuses to
select it, so the player physically cannot plan it.

**Host-side enforcement is the security boundary.** Every order in a `Ready` message is checked
against `UnitAssignments.IsOwnedBy(peerId, ownerName)`. Rejected orders are dropped and reported in
`OrderResult`; the rest of the batch still applies. A peer that repeatedly sends unowned orders is
logged but not disconnected — it could be a race after a reassignment.

> **Never move a check from the host to the client.** Client-side enforcement is a convenience for
> the honest player; host-side enforcement is what makes the rule true.

**Known gaps, not solved in M4:** nothing stops the *host player* from editing or deleting a peer's
applied orders during host planning — they are ordinary `ActionEntity`s on player-controllable
units. Assignments are also not pruned when a unit dies.

## Order validation

`ApplyOrder` returning success means far less than it sounds. The `LoadToECSCombat` path
(`DataManagerSave.cs:3254–3385`) fails only on a missing or unresolvable owner name, or a blueprint
absent from `DataMultiLinker<DataContainerAction>`. There is no range, energy, equipment, path, or
time-window validation on that path.

- An order with a `startTime` outside the current turn window applies verbatim — the game's own
  `CombatUtilities.ClampTimeInCurrentTurn` is never invoked here.
- An order for a wrecked or pilot-ejected unit "applies", then `ActionPlaybackSystem.CleanActionsList`
  disposes it at sim start via `DataHelperAction.IsValid`.

So the bridge **pre-validates** with `IsValid` plus a turn-window check and reports those as
rejections. Otherwise `OrderResult` would report success for orders that never execute.

### Movement geometry and duration are host-authoritative

Two fields of a movement order are **never trusted**, because the game's load path applies them
verbatim while `ActionUtility.CreatePathAction` — the path a local order actually goes through —
derives both:

- **Duration is recomputed** from `pathLength / (movementSpeedCurrent * movementSpeedScalar)`, then
  floored at 0.25s. Trusting the wire value lets any peer slide a unit any distance in any time.
- **The path is re-anchored** so its first point is the unit's pathing origin *on the host*, with
  the whole path translated by the same delta. Otherwise a unit teleports to wherever the path
  happens to start.

Both are no-ops for an honest client: it computed the same duration from the same unit at the same
speed over the same path, and its path already started at that unit's pathing origin. They only bite
a peer that is lying or desynced.

Found the hard way in 4e — a synthetic harness order with a hardcoded origin and a 2s duration made
a mech slide across the entire map instead of walking. With both fixes the same order walks
normally, with animation (verified in-game 2026-08-02).

### The disposal cascade

`ActionDisposalSystem` is reactive on `ActionMatcher.Disposed.Added()`, so it runs on the *next*
systems tick — after our effects have executed. For a disposed primary-track action whose end time
is not before turn start (true of every planning-phase action) it disposes **all later non-locked
primary-track actions of the same owner**.

`LoadToECSCombat` never exercises this, because it runs against a freshly rebuilt context with zero
pre-existing actions. Clear-then-apply within a single pump would therefore silently delete the
orders just applied, one frame after logging success.

**Verified in-game (M4 Step 0b, 2026-08-02).** Two `move_run` orders on one unit, `@0.00s +0.36s`
and `@0.36s +0.71s`:

- Disposing the earlier one removed **both** — the unit ended with zero actions.
- Repeating with `isLocked = true` on the later order removed only the earlier one; **the locked
  order survived**.

Consequences:

- **There is no `ClearOrdersFor` in the commit sequence.** Under batch-at-ready, orders are applied
  exactly once per turn, so nothing needs clearing. If replacement is ever genuinely needed, it must
  span two pumps (dispose, let `ActionDisposalSystem` run, then apply).
- `isLocked` is a confirmed escape hatch, but it also short-circuits `DataHelperAction.IsValid`, so
  a locked order on a dead unit would still execute. Documented option, not a default.

(Incidental: that loop has a `return` where it means `continue` when the owner lookup fails, so one
dead unit in a disposal batch aborts processing of the rest. Not ours to fix, but worth knowing.)

## Reconnect (M5e)

**The player is continuous. The peer id is not.** The barrier, assignments, submissions and
keepalive clocks are all keyed on peer id, so rebinding one entry is far cheaper than making the id
space reusable — and it keeps the invariant that a peer id always addresses exactly one socket.

### Grace-deferred reassignment

Through M5d, `HandleDisconnect` called `Reassign` unconditionally, which re-plans from scratch over
the remaining peers. That is right for a genuine departure and fatal for a reconnect: by the time
the player returns, the units it held have already been dealt to someone else and there is nothing
left to rebind. So:

- **On disconnect inside the grace window, the host does not reassign.** It records a departure and
  leaves `assignments` alone. Those units stay bound to a peer id no live connection holds, so
  `IsOwnedBy` refuses everyone — reserved, visible, uncommandable.
- Registry, barrier and submissions are still cleared immediately. Holding units must never mean
  holding the turn; a dead peer cannot wedge the barrier.
- **On rejoin, `UnitAssignments.WithPeerRebound` moves that one peer's share to the new id** and
  leaves everybody else exactly as they were. `UnitAssignmentPlanner.Plan` remains the path for
  genuine joins and leaves.
- **On grace expiry, the hold is dropped *and* a reassignment runs.** This is not bookkeeping — it
  is the only path that puts a permanently-gone player's units back into play.

A hold is only taken when the session has ticked at least once (there is otherwise no clock to
expire it with) and the peer actually held units.

### The resume token

`Welcome` carries a `ResumeToken`; `Rejoin` presents it along with the session id and the peer id
it claims. Both must match, so possessing a token is not on its own enough to claim a departure.

**The token is derived from a per-session secret that the host mints and never sends.** The obvious
cheaper scheme — hash the session id, peer id and player name — is worthless, because every one of
those reaches every client in `Welcome`, the roster and `PeerJoined`. Any peer could compute a
departed player's token and steal its units, which is exactly the attack that rules out plain
name-based reconnect. Deriving from a secret keeps `HostSession` a deterministic pure machine
(tests pass a fixed secret) without adding a randomness seam to `Seams.cs`.

This is a PoC-grade credential, not a cryptographic one: FNV-1a, 32 bits, on a listener that binds
`127.0.0.1` by default. It binds a returning player to its own units; it is not a defence against
someone who can already reach the socket and is willing to brute-force.

**A held player's name is reserved — against `Hello`, not against `Rejoin`.** Otherwise a stranger
takes the name during the grace window and the real owner's rejoin is refused as a duplicate
through no fault of its own. The check lives in `HandleHello` alone; by the time a rejoin is
handled the departed peer has already left the registry, so the name is free at that level and the
token is what authorises the claim. Both halves are verified: the harness self-test pins the
impostor refusal, and the in-game run below pins the legitimate return.

A returning peer that arrives mid-execution is sent the current `TurnCommit`, so it goes to
`Watching` rather than letting its player plan a turn already running. Its submitted orders are
never restored — they either committed already or belong to a turn that must be re-planned.

Client side: a disconnect faults a `ClientSession` terminally, so returning means a fresh transport
and a fresh session carrying the old one's token. The glue therefore keeps the token, session id
and peer id *outside* the session, surviving `Shutdown` — a credential that dies with the session
that issued it is no use. `pbj.rejoin` in the game, `--token/--session/--peer-id` on the harness.

## Failure and disconnect

| Event | Host | Client |
|---|---|---|
| Client TCP close / RST | drop peer, broadcast `PeerLeft`, recompute barrier, possibly commit | n/a |
| Malformed frame / undecodable message | disconnect that peer; session continues | n/a |
| Message illegal for current state | disconnect that peer | n/a |
| Host closes or crashes | broadcast `Bye` if graceful | log connection lost, tear down, **unlock local execute**, continue single-player |
| No traffic from a peer for `PeerTimeoutSeconds` | drop peer as above | treat as host loss |
| Remote order rejected by the game | log, omit from turn, report in `OrderResult` | log |
| Exception anywhere in the pump | log once, hard-stop the session, never touch the socket again | same |

A lost host must never leave the local execute button permanently disabled.

### Keepalive (M5c)

The host pings each quiet peer; each side tracks last-inbound-traffic time. **Time is passed into
Core as an explicit `double nowSeconds` argument on every `Pump` call — Core never reads a clock.**
The Mod passes `Time.realtimeSinceStartupAsDouble`; the harness passes a `Stopwatch` plus a skew it
can advance at will, which is what lets it exercise a 20-second timeout in milliseconds.

Time reaches a session as a synthesized `TickEvent`, not as an `IPbjSession.Tick(double)` method —
see that event's remarks for why. `PbjRuntime` throttles it to `TickIntervalSeconds` so the timeout
machinery does not allocate an effect list 60 times a second to learn that nothing has expired.

Because a tick is the only place a clock enters a session, `HandleMessage` has no `now` of its own:
it stamps with the time carried by the last tick, at most a quarter second stale against a
twenty-second timeout.

**The timeouts are deliberately asymmetric — peer 20s, host 30s.** The host is the side that
hitches (scenario loads, shader compilation under Proton), and a client `Fault` is terminal with no
automatic recovery, so symmetric timeouts would let one long host hitch permanently kill every
client. Only the host pings; the client's `Pong` keeps the host's timer alive and the host's `Ping`
keeps the client's.

**Both sides seed on their first tick rather than judging.** In-game the clock is process uptime,
so it can be in the thousands at session start; anything defaulting to zero would read as "silent
since time zero" and reap every peer on the very first tick.

**Keepalive is also the fix for the missing `ReceiveTimeout`.** Neither transport sets one, and it
would be wrong to — a blocking `Read` with a timeout throws on a healthy idle connection. A
cable-pull is now caught by the Core timeout, which issues a `DisconnectEffect`, which closes the
socket, which makes the blocked `Read` throw.

Known gap: only *registered* peers are timed out. A socket that connects and never sends `Hello` is
never reaped. It holds an accept slot but not a peer slot, and the listener binds `127.0.0.1` by
default, so this is noted rather than solved.

## Threading

**Core is a pure `(state, event) → (state, effect[])` machine plus a pure byte codec. Mod and
harness are a socket, a thread, and a switch over effect types.**

```
 background thread            main thread (Heartbeat.Update postfix)
 ─────────────────            ─────────────────────────────────────
 TcpClient.Read(byte[])
   → IPbjInbox.Post(bytes) ──►  PbjMailbox.DrainAll()        [Core]
                                  → FrameDecoder.Feed()      [Core]
                                  → PbjMessageCodec.Decode() [Core]
                                  → HostSession.Handle(evt)  [Core] → PbjEffect[]
                                  → PbjRuntime runs effects  [Core]
                                       ├─ IPbjTransport.Send()      [Mod: socket]
                                       ├─ IPbjGameBridge.ApplyOrder [Mod: ECS]
                                       └─ IPbjLog.Log()             [Mod: Debug.Log]
```

**Entitas has no locking anywhere** — contexts, groups and collectors are entirely unsynchronised.
A component write from a background thread can corrupt group membership and crash inside an
unrelated system on a later frame, with a stack trace pointing nowhere near the bug.

This is prevented **structurally, not defensively**: the background thread's entire reachable object
graph is `Socket` + `byte[]` + `PbjMailbox`. It never touches `Contexts.sharedInstance`, never owns
a `FrameDecoder`, and never calls `Debug.Log` — transport log lines are posted as events and emitted
on the main thread, which also makes log ordering deterministic. Decoding was deliberately moved to
the main thread for this reason.

Backstop: the Mod captures the main-thread `ManagedThreadId` on the first pump and throws if `Pump`
is ever entered from another thread.

The receive thread **must copy the read buffer before `Post`** — posting the reused buffer would
corrupt queued events.

**Pump site:** a Harmony postfix on `PhantomBrigade.Heartbeat.Update` (private instance method, runs
`SteamHelper.RunCallbacks(); _gameController.OnUpdate();` every frame on the main thread). Chosen
because it is the main thread unconditionally in every game state including the main menu — the
lobby must work outside combat, where no Entitas combat system exists — and because it requires no
injection into `CombatSystems`'s ordered `Feature` list, which is fragile across game patches. It is
also where `SteamHelper.RunCallbacks()` already lives, so the Steam transport will pump from the
identical place with no change.

The postfix body is wrapped in try/catch: on the first exception it logs once, disposes the session,
and sets a permanent kill flag, so a networking bug can never spam per-frame or brick the game loop.
With no session, the cost is one null check per frame.

**Threads:** host = 1 accept thread + 1 receive thread per connection; client = 1 receive thread.
All `IsBackground = true`. All started only on an explicit `pbj.host`/`pbj.join` command — never at
load, since `ModLink.OnLoadEnd()` runs inside `Heartbeat.Awake` → `ModManager.LoadEverything`,
before `GameController` exists. `Stop()` is also called from `Heartbeat.OnDestroy` /
`OnApplicationQuit` postfixes.

## Wire format

A Core-owned POCO (`OrderPayload`) with a hand-rolled binary codec, mirroring
`DataContainerSavedAction` field-for-field, with Core-owned `Vec3`/`PathLink` replacing
`UnityEngine.Vector3`/`AreaNavLink`.

**`dashBulldoze` is included.** It is not a recomputed prediction cache: its only writers are
`InputCombatDashUtility.cs:93` (planning-time painting) and `DataManagerSave.cs:3378` (save
restore). It is *consumed* during execution — `ActionPlaybackSystem.cs:1180` destroys terrain at
recorded times, `:1640` gates bulldoze behaviour, and `ActionUtility.cs:202` feeds `GetUnitStability`,
which awards a different stability bonus for bulldoze vs standard dash. A remote dash order without
it executes with wrong semantics.

### Why not the game's YAML

1. **Core cannot reference it.** `DataContainerSavedAction` lives in `Assembly-CSharp.dll` and drags
   in `UnityEngine.Vector3`. Referencing either breaks netstandard2.0 and the zero-dependency rule,
   and makes Core untestable outside Unity — which collapses the 100% coverage gate. This alone
   decides it.
2. **The harness must not need the game.** `pbj-peer` speaks the protocol using only `PBAndJ.Core`.
   YAML would drag in `Assembly-CSharp.dll` plus YamlDotNet with the game's exact tag-mapping state.
3. **YAML output is environment-dependent.** The game sets `CultureInfo.DefaultThreadCurrentCulture`
   to invariant in `Heartbeat.Awake`; a fresh harness process does not. Binary has no culture.
4. **It is not versioned by us.** A game patch can add, rename or retype a field silently, with no
   protocol version bump and no compile error.
5. **YAML is not self-delimiting.** We would still need framing, so we would hand-roll half the codec
   anyway — and pay YAML's size and parse cost on top.
6. **Binary is byte-exactly testable.** `Assert.Equal(new byte[] { … }, encoded)` is a real
   wire-compatibility regression test. You cannot write that against a serializer you do not control.

The bridge back to the game's format is `OrderMapper` in Mod: the write direction transcribes
`DataHelperSaveSerialization`, the read direction transcribes `DataManagerSave.LoadToECSCombat`.
Pure field copying — legitimate glue.

### Codec

Little-endian, no alignment, no compression. Length-prefixed frames.

- `string` = int32 length + UTF-8 bytes; `-1` means null
- `T?` = one presence byte + value
- arrays = int32 count + elements
- guards, each a covered branch: path points > 512, string length > 4096, negative counts other
  than `-1` → `PbjProtocolException`

## Transport abstraction and the Steam path

`IPbjTransport` is the only thing that changes between TCP and Steam.

`com.rlabrecque.steamworks.net.dll` ships with the game and exposes `SteamNetworkingSockets`,
`SteamNetworkingMessages` and `SteamMatchmaking` — no new native dependencies. The Steam
implementation **(M5+)** uses a `SteamMatchmaking` lobby for discovery,
`CreateListenSocketP2P`/`ConnectP2P` for the connection, and `SendMessageToConnection` /
`ReceiveMessagesOnConnection` for bytes, pumped from the same `Heartbeat.Update` postfix.

**No protocol change is needed.** Steam messages are already framed, so the Steam transport posts
each received message as one already-complete frame, which `FrameDecoder` handles as a degenerate
case — zero conditional code.

## Opt-in and privacy

No listener, no thread, and no socket exists unless the user explicitly starts a session with
`pbj.host` or `pbj.join`. With no session, the mod behaves byte-identically to M3.

Binds `127.0.0.1` by default. Reaching the network requires the three-argument form,
`pbj.host <bind> <port> <passphrase>`, which **refuses to start without a passphrase** and logs a
warning naming the exposure. No telemetry, no third-party service.

This section exists because the README commits to it under Brace Yourself Games' mod policy.

## Remote play (M7)

Everything before M7 assumed the other peer was another process on the same machine. Playing with
someone who is not raises three problems that had nothing to do with the protocol working.

### Builds must be checked, or a mismatch reads as a netcode bug

Through M6 the handshake validated the magic number and the wire version. `modVersion` travelled in
`Hello` and was *logged and ignored*, and nothing described the game build at all. Two peers on
different builds therefore connected perfectly and then reported `DIVERGED` every turn — the single
most misleading failure this system can produce, and the worst one to diagnose with someone waiting
at the other end of a phone call.

Wire v3 carries the game build and a session passphrase in `Hello` and `Rejoin`, and
`PbjProtocol.CheckCompatibility` refuses mismatches with a reason that names both sides. The game
build comes from `BuildInfoHelper.GetBuildInfo()` — the whole string, unparsed, because two peers
only need to agree and the raw value separates builds that share a version number.

**An absent value is "cannot say", never "does not match"**, for both the mod version and the game
build. That rule is load-bearing rather than lenient: the standalone harness declares neither and is
the peer every in-game gate since M4 has been run with. A real host and a real client both declare
both, so the check still bites exactly where it was added to bite.

The passphrase is checked **first**, so a caller that cannot authenticate learns nothing about our
mod version or game build.

### An exposed listener needs a door, and the door is not a safe

The passphrase is compared in the clear over plain TCP. It stops anything that finds the port from
joining — this protocol is public and an accepted peer can submit orders for the units it is dealt —
and it is **not** confidentiality against anyone on the network path. Documented that way rather
than dressed up: the honest mitigation for the path is an overlay VPN or a tunnel, and the honest
description of this is a door lock.

Requiring it for any non-loopback bind is deliberate. It removes the state where someone opens a
port "just to try it" and leaves it open.

### A socket that says nothing must be reaped

`HandshakeTimeoutSeconds` (10s) drops connections that arrive and never send `Hello`. This was a
listed limitation and an acceptable one while the listener was loopback-only; it stops being
acceptable the moment the port is reachable, because such a socket costs a connection slot for free
and nothing else was tracking it — an accepted socket is not a peer and never entered the registry.
Deliberately shorter than `PeerTimeoutSeconds`: an established peer has proven it speaks the
protocol and gets the benefit of the doubt through a hitch; a mute stranger has proven nothing.

### Scenario transfer, and the correction to the 5f record

M5 recorded 5f — two real game instances — as blocked because "there is no scenario transfer, so
both processes would have to independently enter an identical combat". **That was wrong, and the
mechanism had already been built in M3a.** `pbj.combat-save` writes `SavedGames/pbj_combat_test/`, a
plain directory beside `Mods/`. Copy it to the other machine's `SavedGames/`, run `pbj.combat-load`,
and both processes hold the same combat with the same `persistent.nameInternal` values — which is
exactly the join key snapshot correction and keyframe playback are built on. M3a verified the
round-trip at 37/37 planned actions restored, diff MATCH.

It transfers a whole campaign save, so both players are playing the host's campaign. For a test
session that is fine. It is not mid-combat join, which remains unsupported. It **was** manual —
M9 moved it onto the wire.

## Scenario transfer (M9)

The save no longer travels by hand. A host that has run `pbj.combat-save` offers it to every peer
that handshakes; a peer that wants it asks; the bytes cross. Both machines end up holding a
byte-identical save, which is what makes their `persistent.nameInternal` values agree — the join key
everything else is built on.

This happens in `Lobby`, which both state machines already had and neither used for anything. Two
games sitting at the main menu can `pbj.host` / `pbj.join`, transfer the scenario, and both
`pbj.combat-load` into the same combat. That is stage 2 with the USB stick removed.

### Offer, request, send — not a push

```
Host   → ScenarioOffer  { saveName, totalBytes, digest }   on handshake, if it has a save
Client → ScenarioRequest{ digest }                          only if it wants it
Host   → Scenario       { saveName, digest, files[] }       the bytes
```

Three types rather than one, for three separate reasons. At ~124 KB a save is fifty times a
snapshot, and a peer that already holds it should pay nothing — which **every rejoining peer** does,
since it transferred on its first connection. A peer mid-combat should not have a save dropped on it
unasked. And accept/decline is the shape a real lobby needs anyway: M9 is the first piece of one,
not a one-off file copy.

The client decides by reading **its own** save through the same `IPbjGameBridge.ReadScenario` the
host reads its own with, and comparing digests locally. No bytes move to find out.

Its two conditions are deliberately conservative — lobby only, and only if it does not already hold
the save — with `pbj.scenario-pull` as the override for everything they exclude: a save deleted
since, a host that re-saved mid-session, or simply wanting it now. A manual pull asks with a null
digest, meaning "whatever you have".

A host serving a request always sends what it holds **now**, even if the request names an older
digest. The receiver validates against the digest on the `Scenario` message itself, so answering
with the current save is both simpler and always makes progress.

### Writing wire bytes to disk is the risky part

This is the only place in the mod where bytes off a socket become files, and the passphrase is a
door lock rather than an envelope (see [Opt-in and privacy](#opt-in-and-privacy)). So the guards are
three deep, each sufficient on its own today:

1. **The save directory name is ours, never the wire's.** `ScenarioPayload.SaveName` is logged and
   compared; it is not a path component. The receiver writes to `SaveLoadGlue.SaveName`.
2. **`IsSafeName` rejects anything structurally capable of escaping a directory** — an allowlist of
   permitted characters, not a denylist of forbidden ones, so a separator this code has never heard
   of is refused by default. That one rule subsumes the cases worth naming: `..` cannot escape
   without a separator and `C:` cannot anchor without a colon.
3. **`IsAllowedName` then narrows to `content.zip` and `metadata.yaml`**, the two files
   `DataManagerSave.DoSave` writes.

They are all there because the allowlist is the one most likely to be widened later, and widening it
must not silently re-open traversal. On top of them the digest is **recomputed from the bytes that
arrived** and compared with the sender's claim before anything is written, so a truncated or
substituted transfer is refused rather than written and then loaded. The write itself stages into a
sibling directory and moves into place, so an interrupted transfer cannot leave half a save for
`pbj.combat-load` to find.

Refusing is not a fault. A peer that sends a bad scenario has not broken the session, and dropping
the connection over it would turn a recoverable annoyance into a lost game.

### It writes; it does not load

The client logs `scenario written — run pbj.combat-load to enter it` and stops there. Loading a save
yanks the player out of whatever they are doing, and doing that on an inbound network message is a
surprise. It is also exactly the decision a real lobby's "ready to load" step should own, so it is
left explicit until there is one.

### Size

The real save is 124 KB — `content.zip` at 118 KB plus a 652-byte `metadata.yaml` — against
`PbjRuntime.MaxFrameLength` of 1 MiB, so one frame is right today. `ScenarioPayload.MaxTotalBytes`
caps a transfer at 512 KB and `MaxFiles` at 4, with a test pinning the encoded size against the
frame limit rather than trusting the arithmetic. Over the cap the transfer is **refused, not
truncated**: half a save is worse than none, because `pbj.combat-load` would try to enter it.

Chunking is the named extension point if a real campaign save ever exceeds the cap — a `Scenario`
carrying an index, reassembled client-side. The payload already divides along file boundaries, and
5b's outbound queue and writer thread exist precisely so a payload this size cannot stall a frame.

`PbjProtocol.Version` did **not** move. Three new message types leave every existing layout
untouched, which is the same rule M6's `Keyframes` followed; `ModVersion` went to 0.4.0 in the same
change, and the handshake refuses a peer whose mod build differs long before an offer is sent. The
mod version is the compatibility gate; the wire version guards layout, and no layout moved.

### Staging

The standalone `pbj-peer` publishes as a self-contained single-file `win-x64` executable, so the
network path between two machines can be proven with no game, no save transfer and no mod install on
the far side. Doing that first separates "our routers can talk" from "our games agree", which are
otherwise diagnosed together and badly.

## Known limitations

| Limitation | Milestone to fix |
|---|---|
| ~~Sends are synchronous from the main thread~~ | **fixed in M5b** — see [The outbound queue](#the-outbound-queue) |
| ~~No client state stream~~ | **snapshot correction shipped in M5d**, ~~keyframe streaming in M6~~ |
| Keyframes carry no poses, so playback slides rather than walks | M8 — [replay handoff](#replay-handoff-m8--the-intended-answer-to-why-does-it-slide), gated on stage 2 |
| Units spawned mid-turn have no track and simply appear | unscheduled |
| A client's own `combat.currentTurn` never advances | unscheduled |
| ~~No reconnect-after-drop~~ | **fixed in M5e** — see [Reconnect](#reconnect-m5e) |
| Resume tokens are FNV-1a/32-bit, not a cryptographic credential | unscheduled |
| The session passphrase travels in the clear over plain TCP | unscheduled — an overlay VPN or tunnel is the real answer |
| No mid-combat join | unscheduled — M9 built the transport it needs; the join itself is untouched |
| ~~Scenario transfer is a manual folder copy~~ | **fixed in M9** — see [Scenario transfer](#scenario-transfer-m9) |
| A transferred scenario is written but never loaded; `pbj.combat-load` is still by hand | deliberate — see M9 |
| A client joining a host already in combat derives its own combat state | unscheduled |
| Host player can edit peers' applied orders during host planning | unscheduled |
| Assignments not pruned when a unit dies | unscheduled |
| ~~No keepalive; relies on TCP FIN/RST~~ | **fixed in M5c** |
| ~~A socket that connects and never sends `Hello` is never timed out~~ | **fixed in M7** — see [Remote play](#remote-play-m7) |

## The outbound queue

Each connection owns a `PeerWriter`: a bounded queue plus one thread that drains it into the
socket. `IPbjTransport.Send` enqueues and returns; it never writes.

**The reason is not snapshot size.** M4 recorded the writer thread as a hard prerequisite for
snapshots on the assumption they would be megabytes. They are not — a `UnitSnapshot` is ~85 bytes,
so a 128-unit cap is ~11 KB and a realistic combat is ~2.6 KB, well under `MaxFrameLength`'s 1 MiB.
The real problem was always the M4 path itself: `Send` wrote synchronously from the pump with a 1s
`SendTimeout`, and `BroadcastEffect` loops peers, so three unresponsive peers meant a three-second
frame. It also made keepalive self-defeating — the main thread would sit blocked writing to the
very peer the timeout was trying to reap.

**Per-peer, never shared.** A shared queue reintroduces head-of-line blocking across peers, the
exact failure being removed. The host already runs one receive thread per connection.

**Backpressure is: disconnect. Never drop, never block.** Dropping a frame is not available — the
protocol is a stateful stream with no resend and no sequence numbers, so a silently dropped
`TurnCommit` strands that client forever with nothing on either side able to notice. Blocking the
enqueue puts the main-thread stall straight back. So a peer that exceeds 4 MiB or 1024 queued
frames is dropped; one that cannot absorb 4 MiB of a 3 KB/turn protocol is gone regardless.
Crossing 256 KiB posts a single latched warning so a slow link is visible before it is fatal.

**Closes go through the same queue as frames.** `HostSession.Reject` emits the `Reject` and then a
disconnect; closing the socket out of band would turn every rejection into a bare RST, and a peer
sending a bad protocol version would never learn why. A null frame is the close sentinel, so Core's
effect ordering is preserved with no change to Core.

Writer threads report through the existing `TransportLogEvent` channel — added in M4 and, until
now, never posted by anything. A background thread must not touch the log sink, but composing a
`NetLog` string touches nothing but the string.

Consequence worth knowing: a peer can now produce **two** `PeerDisconnectedEvent`s, one from the
writer and one from the receive loop. Both sessions already tolerate it — `HandleDisconnect`
early-returns for a peer no longer registered, and the client's is terminal-state guarded — and a
test pins that, because it is exactly the invariant a later edit would break silently.

## Verified environment facts

- Unity Mono's `System.Net.Sockets` works under Proton/Wine, **including the native-Linux →
  Wine boundary in both directions** (M4 Step 0a, 2026-08-02: in-process loopback echo 16/16 MATCH;
  external peer from a host python3 process greeted 13, read 16, EXTERNAL OK).
- The `pb-dev` distrobox runs with `NetworkMode: host`, so `127.0.0.1` inside the container is the
  same loopback the Proton-hosted game binds.
- `ActionDisposalSystem`'s cascade is real, and `isLocked` defeats it (M4 Step 0b — see
  [The disposal cascade](#the-disposal-cascade)).
- `CombatUtilities.ConfirmExecution` silently refuses while simulating (M4 Step 0b — see
  [Commit sequence](#commit-sequence-apply--commit--verify--broadcast)).
- The full handshake works between a native-Linux process and the Wine-hosted game (M4 4d,
  2026-08-02): `peer connected: #1 from 127.0.0.1:36968`, `handshake ok: #1 'ally'`,
  `assignment: #0 <- pb_mech_01, workshop_utl_unit_frame | #1 <- pb_mech_02, workshop_utl_unit_frame_2`.
  Assignment used real ECS units, so the `isPlayerControllable && IsUnitFriendly` filter is correct
  in-game. `pbj.net-stop` released the port cleanly — re-hosting on 27600 immediately afterwards
  succeeded. A peer claiming protocol v999 got `VersionMismatch (peer v999, host v1)`.
- With no session started, a whole launch produces zero networking lines and M3 behaviour is
  unchanged, so the opt-in guarantee holds in practice.
- **Snapshot correction works against the real game (M5d, 2026-08-02).** An 18-unit `move_run`
  for `pb_mech_02` was authored in `pbj-peer`, accepted by the host, executed, and the resulting
  state broadcast — after which the harness's *independently recomputed* digest matched the
  host's. That single comparison is what proves the whole chain: ECS capture, the binary codec,
  the socket, the hard-set, and — because the digest is built from integer quantisations on both
  sides while the wire carries raw IEEE-754 bits — that neither the Mono-under-Wine host nor the
  .NET harness disagrees about a float.
- **Reconnect works against the real game (M5e, 2026-08-02).** A dropped peer returned inside the
  grace window under the same player name, was issued a fresh peer id (`rejoined as #4 (was #3)`),
  and `pb_mech_02` came back with it. Confirms both that the id space stays append-only while the
  *player* is continuous, and that the name reservation refuses an impostor's `Hello` without
  standing in the way of the owner's `Rejoin`.
- A plain C# `lock` block does **not** strand the 100% branch gate. It lowers to
  `Monitor.Enter(o, ref lockTaken)` plus `if (lockTaken)` in the finally, but coverlet 6.0.2
  accounts for that generated branch — measured 2026-08-02 on `PbjMailbox.Post`: 4 branches, 0
  uncovered. No hand-rolled `Monitor.Enter`/`try`/`finally` is needed anywhere in Core.
