# Networking architecture (M4+)

Our design, not reverse-engineering — `docs/notes/` holds the game research this builds on.
Game facts cited here are verified against decompiled `Assembly-CSharp.dll` for 2.2.2-b8339.

Status: written for M4. Sections marked **(M5+)** describe the intended path, not what ships now.

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
within a session**. Consequence: reconnect-after-drop is not supported in M4 — a rejoining player
is a new peer with no units until assignment re-binding exists **(M5+)**.

Mid-combat join is not supported in M4–M6. Late join would ride the combat-save transfer path
(`docs/notes/save-and-replay.md` establishes that a planning-phase save is near-lossless).

## Combat lifecycle

The host detects combat entry/exit as an edge on `IPbjGameBridge.InCombat`, observed in `Pump`,
and broadcasts `CombatStart { turn }` followed immediately by `Assignments`, then `CombatEnd` on
exit. Assignment travels in exactly one message type so there is one way to express it — the same
`Assignments` message is re-broadcast if the roster changes mid-combat.

- `TurnBarrier` resets on `CombatStart`.
- `CombatEnd` unlocks every peer — including any sitting Ready when the host's combat resolves.

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

**Deferred to M5 (type bytes 11+ reserved):** `Unready`, `OrderResult`, `CombatStart`,
`CombatEnd`, `Ping`, `Pong`.

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
| M5 | **Snapshot correction** (the guaranteed floor): a "resolving…" overlay during the execution window, then a compact authoritative state (positions, rotations, integrity, statuses, dead units) hard-set on arrival. |
| M6 | **Keyframe streaming** (the target): the host serializes the pure-data subset of `CombatReplayHelper`'s per-unit transform/state/pose keyframes and the client applies them with the same scrubber the replay UI uses. |

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
a mech slide across the entire map instead of walking.

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

Keepalive: the host pings each peer periodically; each side tracks last-inbound-traffic time.
**Time is passed into Core as an explicit `double nowSeconds` argument on every `Pump` call — Core
never reads a clock.** The Mod passes `Time.realtimeSinceStartupAsDouble`; the harness passes a
`Stopwatch`. This is what keeps timeout logic a pure, non-flaky unit test.

Keepalive is deferred if M4 runs long: TCP FIN/RST already covers graceful close and process kill,
which is everything the smoke checklist exercises. It only matters for cable-pull and NAT idle.

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
| Sends are synchronous from the main thread. Accepted for M4 (every message < 4 KB, bounded by `SendTimeout`). **An outbound queue + writer thread is a hard prerequisite before state snapshots — potentially megabytes — go over the wire.** | M5, blocking |
| No client state stream; the client cannot watch execution | M5 (snapshot), M6 (keyframes) |
| No reconnect-after-drop; peer ids are never reused | M5 |
| No mid-combat join | M6+ |
| Host player can edit peers' applied orders during host planning | unscheduled |
| Assignments not pruned when a unit dies | unscheduled |
| No keepalive; relies on TCP FIN/RST | M5 |

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
- A plain C# `lock` block does **not** strand the 100% branch gate. It lowers to
  `Monitor.Enter(o, ref lockTaken)` plus `if (lockTaken)` in the finally, but coverlet 6.0.2
  accounts for that generated branch — measured 2026-08-02 on `PbjMailbox.Post`: 4 branches, 0
  uncovered. No hand-rolled `Monitor.Enter`/`try`/`finally` is needed anywhere in Core.
