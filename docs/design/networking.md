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

Mid-combat join is not supported in M4–M6. Late join would ride the combat-save transfer path
(`docs/notes/save-and-replay.md` establishes that a planning-phase save is near-lossless).

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
| M6 | **Keyframe streaming** (the target): the host serializes the pure-data subset of `CombatReplayHelper`'s per-unit transform/state/pose keyframes and the client applies them with the same scrubber the replay UI uses. |

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

Binds `127.0.0.1` by default; `0.0.0.0` requires an explicit argument. No telemetry, no third-party
service.

This section exists because the README commits to it under Brace Yourself Games' mod policy.

## Known limitations

| Limitation | Milestone to fix |
|---|---|
| ~~Sends are synchronous from the main thread~~ | **fixed in M5b** — see [The outbound queue](#the-outbound-queue) |
| ~~No client state stream~~ | **snapshot correction shipped in M5d**; keyframes still M6 |
| A client's own `combat.currentTurn` never advances | unscheduled |
| ~~No reconnect-after-drop~~ | **fixed in M5e** — see [Reconnect](#reconnect-m5e) |
| Resume tokens are FNV-1a/32-bit, not a cryptographic credential | unscheduled |
| No mid-combat join | M6+ |
| A client joining a host already in combat derives its own combat state | unscheduled |
| Host player can edit peers' applied orders during host planning | unscheduled |
| Assignments not pruned when a unit dies | unscheduled |
| ~~No keepalive; relies on TCP FIN/RST~~ | **fixed in M5c** |
| A socket that connects and never sends `Hello` is never timed out | unscheduled |

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
