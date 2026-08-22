# M18 — host migration: promote the sole client (N=2)

Stage **H0** of M18: the design, and its self-refutation. **No production code is written by this
stage.** H1 (the checkpoint mirror), H2 (the promotion itself) and H3 (the acceptance sitting) are
later stages with their own owners; the pricing is `backlog-2026-08-22.md` §M18.

Written 2026-08-22 in worktree `pbj-lane-h0`, at `main` = `a40a2ef`, mod **0.24.0**, wire **v10**.
Gate re-run from this worktree before filing: `make dist` exit **0**, **2119** tests at **100%**
line/branch/method, `mod version OK (0.24.0)`, `wire surface OK (unchanged since 0.24.0)`,
`split grouping OK (15 families, 145 parts, 1577 members)`.

**Every `file:line` below was opened in this session and each also names the MEMBER**, because line
numbers rot and two of the citations handed to this stage had already rotted (§11).

**How that is checked, and where the check lives.** `tools/cite-check/cite-check.sh` resolves a
manifest of `path | line | expected substring` against the tree;
`tools/cite-check/m18-host-migration.cites` is this document's manifest. Current run: **177
verified, 0 failed, 1 control**. ⭐ **The control is the point and it is on the INSTRUMENT, not the
input** — the manifest carries a citation that is deliberately absent, and the script exits **2**
(distinct from a citation failure's 1) if that control ever *matches*, or if a manifest contains no
control at all. A harness that cannot fail reports "all citations verified" for a tree it never
opened, which reads identically to no harness. The first run of this manifest found **11 of my own
line numbers wrong**; they are fixed above.

⚠️ **An earlier version of this stage ran the same comparison and never wrote it down.** That is the
defect this file now closes: **an unpersisted instrument is a claim, not a check** — indistinguishable
from a negative test that was never performed. Persisted here so the next stage can re-run it rather
than re-trust it.

**Scope ruling (user, 2026-08-22):** host-drop resume is in 1.0. Design point **N=2** — on host
drop, promote the one client. No selection policy, no stability metric, no election ships in 1.0.
The selection problem is therefore gone. **The authority handover is the substance, and it is
untouched by that simplification.**

---

## 0. The control this milestone must beat, and the pitch it is allowed to make

**Today, without M18, a host fault is already survivable for the human.** `ClientSession`, member
`Fault` (`src/PBAndJ.Core/Net/ClientSession.cs:306-321`), stops playback, forgets the replay
buffers, and emits `SetExecutionLockEffect(false)` at `:320` with the comment that states the
intent: *"A lost host must never leave the local execute button disabled — the player continues
single-player from here."* That is reached from the keepalive (`ClientSession.Link.cs`, member
`HandleTick`, `:86-92`, on `PbjProtocol.HostTimeoutSeconds = 30.0`, `PbjProtocol.cs:295`) and from
the refusal and protocol-violation arms in `ClientSession.Dispatch.cs` and `ClientSession.Link.cs:48`.

So the honest pitch is narrow:

> **M18 buys "the session survives". It does not buy "the player survives" — that already ships and
> already works.**

Two obligations fall straight out of keeping the control, and neither was written down before:

- **M17 stage 2 depends on `Faulted` staying excluded.** `ClientSession.ClientOwnsCombatOutcome`
  (`ClientSession.cs:156-160`) excludes `Closed` and `Faulted` *as a correctness requirement* — its
  own remark says an armed prefix on a faulted client makes the fight "unwinnable and unlosable for
  ever". A promotion path must therefore never park the survivor in `Faulted` with the combat
  prefix still armed; it tears the client session down entirely and builds a host.
- 🔴 **`pbj.promote` runs AFTER `Fault`, so the player may already have executed locally.** The
  unlock at `:320` is immediate. Between the fault and the promotion the human can plan and execute
  a single-player turn on the replayed fight — and the mirror load **discards it**. `pbj.promote`
  must say so before it loads, not after. Nobody has written this down; it is a direct consequence
  of the control being good.

---

## 1. What promotion actually is — the authority handover, derived

The pricing's §M18.1 inventory holds. Restated as the one sentence that matters:

> **Everything a promoted host needs is either already replicated or exists only as
> `pbj_combat_turn` on the dead host's disk.**

`[DERIVED]`, and here is the derivation for the load-bearing half. `WriteCheckpointEffect` is
emitted at exactly one place — `HostSession.Turn.cs`, member `TryCommit`, `:194-197` — and carried
out by `CheckpointGlue.Write` (`src/PBAndJ.Mod/Net/CheckpointGlue.cs`, member `Write`, `:100-...`),
which calls `DataManagerSave` against `LobbySaveNames.CheckpointSlot` (`:164`, `:191`). Nothing
puts it on the wire:

```
grep -n "heckpoint" src/PBAndJ.Core/Net/PbjMessage.cs src/PBAndJ.Core/Net/PbjMessageCodec.cs
  → 0 hits
grep -n "heckpoint" src/PBAndJ.Core/Net/HostSession.Turn.cs src/PBAndJ.Core/Net/PbjEffect.cs
  → 12 hits            (the same pattern, on files known to contain it)
```

The second line is the point: **a grep returning zero is a claim about the pattern**, so the pattern
was made to bite on a known-present case first. It does. The zero is real. (The same control was run
for `promot|hostmigrat|migrate` across `src/`: **0 hits**, against **15** for `reassign` — there is
no promotion code anywhere in the tree today.)

**The transport precedent is already in the tree.** `ScenarioMessage` (`PbjMessage.cs:778-801`)
carries a full save's files with a sender digest; `ScenarioPayload` (`ScenarioPayload.cs:85`) does
the structural checking, the part splitting (`MaxPartBytes = 1 << 18`, `:138`) and the size bound
(`MaxTotalBytes = 3 << 18` = 768 KiB, `:119`); `ClientSession.Scenario.cs`, member `HandleScenario`
(`:98-128`), inspects (`:102-107`), re-derives and compares the digest (`:109-113`) and only then
emits `WriteScenarioEffect` (`:117`); and `CombatGameBridge.Scenario.cs`, member `WriteScenario`
(`:78`), stages into `folder + ".pbj-incoming"` (`:94`) and moves into place, so an interrupted
write cannot leave a half-save behind. **The mirror is that machinery pointed at a second slot.**

---

## 2. The phase list, DERIVED from the session state machines

§M18.3's matrix was already caught asserting its partition instead of deriving it. So: the partition
below is derived from the **complete set of writers to `HostSession.State`**, plus the two
sub-barriers that are in flight *inside* a state, plus the sub-instants of one effects batch that a
host death can bisect, plus — added after review — **the Mod-side static that holds a window Core
cannot see**. Nothing here is taken from the previous table.

⚠️ **Read the fourth source as a scar, not as thoroughness.** The first version of this section used
only the first three and claimed totality; review found a missing window on the strength of the
fourth. **The partition below is the best available, and it is no longer offered as provably total** —
§2's replacement rule says what a re-derivation must sweep.

**Every writer to `HostSession.State` — six sites, and 🔴 the pattern that finds all six is
`grep -rn "State = " src/PBAndJ.Core/Net/HostSession*.cs`, NOT `"State = HostSessionState"`:**

| site | member | to |
|---|---|---|
| `HostSession.cs:221` | ctor | `bridge.InCombat ? Planning : Lobby` |
| `HostSession.CombatEntry.cs:40` | `HandleCombatEntered` | `Planning` |
| `HostSession.Turn.cs:222` | `HandleCommitOutcome` (committed arm only) | `Executing` |
| `HostSession.TurnComplete.cs:113` | **`HandleLocalTurnComplete`** (`:17` — the file's only member) | `Planning` |
| `HostSession.CombatEntry.cs:216` | `HandleCombatExited` | `Lobby` |
| `HostSession.Dispatch.cs:51` | **`case TransportFailedEvent`** (`:48`) | `Closed` |

🔴 **Two corrections and a self-inflicted lesson, all found by review.**

- **`HandleTurnComplete` is a `ClientSession` member; the host's is `HandleLocalTurnComplete`.** In a
  document whose stated defence against line rot is *"name the member"*, naming the wrong member is
  the whole defence failing.
- **`Dispatch.cs:51` is the transport-failure arm, not stop/bye.** A `ByeMessage` routes to
  `HandleDisconnect` (`HostSession.Dispatch.cs:189-190`) and **never writes `State`**, and
  `pbj.net-stop` drops the session object with `State` untouched. ⇒ **A host death essentially never
  produces `Closed`** — the enum value exists for a transport fault on *this* machine, which is not
  the case M18 is about.
- 🔴 **The proof grep I cited returns FIVE, not six** — `"State = HostSessionState"` misses
  `HostSession.cs:221`, where the enum name is on the far side of a ternary. My *count* was right and
  my *cited proof* was a pattern-miss: **a grep returning n is a claim about the pattern**, and I
  published one that under-reported the very table it was offered as evidence for.

**Sub-barriers in flight inside a state** (`HostSession.cs:81`, `:92` — two `LoadBarrier`s, kept
separate on purpose):

- `load.InFlight` — the M11d synchronized campaign load (`HostSession.Load.cs:41`, `:50`).
- `combatEntry.InFlight` — everyone loading into the fight (`HostSession.CombatEntry.cs:106`;
  the abandonment guard at `:226`).

🔴 **Sub-states held nowhere in Core at all — and this is where the derivation FAILED.** Between
`HandleCombatEntered` (`HostSession.CombatEntry.cs:38-54`), which emits `ShipCombatEffect` at `:53`,
and `combatEntry.Start` at `:106`, the session sits in `Planning` with `combatEntry.InFlight` **false**
and no barrier running. The only thing that knows this window exists is
**`CombatShipGlue.armed` — a `private static bool` (`src/PBAndJ.Mod/Net/CombatShipGlue.cs:61`) in an
`[ExcludeFromCodeCoverage]` class in the uncovered Mod assembly.** It is invisible to the state enum,
to the barriers, and to the effects batch — all three of my derivation sources.

**Sub-instants inside one effects batch.** `TryCommit` queues `WriteCheckpointEffect` at
`HostSession.Turn.cs:196` and `CommitTurnEffect` at `:200` into the *same* list, and the comment at
`:180-184` states the ordering is by construction because `PbjRuntime.Run` is a queue. The commit
*broadcast* is later still and conditional: `BroadcastEffect(new TurnCommitMessage(...))` at `:237`,
inside `HandleCommitOutcome`, which runs only when the game answers (`PbjRuntime.cs:255`).

### The derived windows — TEN, and three of them the pricing's matrix does not have

| # | window | how it is derived | in §M18.3's matrix? |
|---|---|---|---|
| W-a | `Lobby`, nothing in flight | ctor / `HandleCombatExited` | yes |
| **W-b** | **`Lobby`, `load.InFlight` — the M11d synchronized load** | `HostSession.Load.cs:50` | 🔴 **NO — missing** |
| W-c | `Planning`, `combatEntry.InFlight` — first entry of a fight | `HostSession.CombatEntry.cs:106` | yes |
| W-c′ | `Planning`, `combatEntry.InFlight` — re-entry during a stage-D resume | same site, reached by the combat edge | yes (added by the planner) |
| W-d | `Planning`, barrier filling, no checkpoint yet for this turn | between `HandleTurnComplete:113` and `TryCommit:196` | yes |
| W-d′ | `Planning`, inside `TryCommit`, between `:196` and `:200` | one effects batch, bisectable | yes |
| **W-e** | **`Planning`, commit REFUSED — turn does not advance, checkpoint already written** | `HandleCommitOutcome` refusal arm, `:205-220` | 🔴 **NO — missing** |
| W-f | `Executing`, simulating, keyframes not yet broadcast | `:222` → `TurnComplete.cs:113` | yes |
| W-g | `Executing` → the fight RESOLVES rather than completing a turn | `ObserveCombatEdge` false, `PbjRuntime.cs:117-128`, run after the drain (`:82`, and the remark at `:110-116` explains the ordering) | yes (added by the planner) |
| **W-s** | 🔴 **`Planning`, ARMED but not yet shipped — the ship wait** | `CombatShipGlue.armed` (`:61`); poll at `:115`, timeout `ShipTimeoutSeconds = 90f` at `:57` | 🔴 **NO — missing, and it is the LONGEST window in a fight** |
| W-h | `Lobby`, post-fight debriefing (M12d, unbuilt) | after `HandleCombatExited` | yes |

**W-b — the M11d load barrier — is the more serious of the two omissions, and it is the one window
where the control M18 must beat is itself broken.** `campaign-coop.md`'s M11d probe records that
`DataHelperLoading.LoadingStart` returns without clearing what `TryLoading` set on the failure path,
so *"M11d should treat a failed load as possibly-terminal for that peer rather than retryable"*. A
host that dies while a client is inside `TryLoading` leaves that client mid-teardown of its own
campaign — the "continue single-player" fallback is not obviously available, because there is no
combat to continue. **Promotion does not help here either** (no fight, no mirror), so the row's
answer is the same "NO" the overworld row gets — but the *reason* it is a NO is worse than the
overworld row's, and the plan should not present a lobby drop as uniformly benign.

**W-e — the refused commit — is the window that breaks the obvious versioning scheme.** It gets its
own section (§5).

### 🔴 W-s, and the refutation of this document's own safeguard

**The ship wait is measured at 26.7 s, 26.9 s and 27.4 s** (`CombatShipGlue.cs:47`, the remark that
raised `ShipTimeoutSeconds` from 30 s to 90 s), and B5 measured **33.2 s**. It is longer than any
other window in a fight, and I omitted it — while quoting its own duration elsewhere in this
document.

⇒ 🔴 **The safeguard this document and §M18.3 both leaned on is refuted.** "A missing window is a
missing PHASE, checkable against `HostSession`'s state machine" is **false**: a window can be held
entirely in Mod-side glue that the state machine cannot see, and this one is. **Replacement rule,
and it is the transferable part:**

> **Every static mutable field in the Mod glue is a potential session window. Sweep them when
> re-deriving a phase list.** (`grep -rn "private static \|internal static " src/PBAndJ.Mod/Net/ |
> grep -vE "readonly|const|\(|=>"` returns **251** candidates today — the sweep is not free, which
> is precisely why it has to be named rather than assumed.)

**W-s also has a failure path the matrix must own**, and it is a session loss with a *live* host:
when the ship gives up, `HandleLocalCombatReady`'s empty-name arm (`HostSession.CombatEntry.cs:72-88`)
logs `CombatShipFailed` (`:78`), calls `DropEveryoneFromTheFight` (`:85`) and then
`StartCombatForEveryone` (`:86`) — *"the host gives up, drops the peer with 'the fight could not be
shared', and fights alone"* (`CombatShipGlue.cs:48-56`). **M18 cannot help with it at all**, because
the host is alive; the mitigation already shipped, and it was raising the timeout to 90 s. It belongs
in the matrix as an explicitly out-of-scope row rather than as an omission.

### The matrix, re-derived

| host drops during | what the sole client holds | recoverable by promotion? | the claim being bought |
|---|---|---|---|
| W-a `Lobby`, no fight | its copy of the shared campaign as of the last synchronized load, stale by every host-only map action since (`campaign-coop.md`, "Authority split": world movement, mission selection and the campaign clock are HOST-ONLY; nothing mirrors overworld bytes) | **NO — not in 1.0** | promotion is a COMBAT-resume feature; the overworld half of "the session survives" is explicitly not bought |
| **W-b `Lobby`, load in flight** | possibly nothing: it is mid-`TryLoading`, having torn its own campaign down | **NO, and worse than W-a** — the single-player fallback may not exist either | 🔴 stated so it is not discovered; the fix is M11d's failure path, not M18's |
| **W-s armed, not yet shipped (26.7–33.2 s measured)** | for a FIRST entry: **nothing for this fight** — it has not been told a fight started (`HandleCombatEntered`'s remark: the announcement deliberately waits) | **NO**, like W-a. ⚠️ On a stage-D RE-entry the same window recurs and a mirror *does* exist, so the answer there is **YES** — it splits exactly as W-c/W-c′ do | 🔴 the longest window in a fight, and M18 buys nothing in its first-entry form |
| **W-s-fail: the ship gives up (>90 s of machine-paced refusal)** | a disconnect → `Fault` → single-player continuation | **NO — and M18 is not the fix.** The host is alive; the fix already shipped as `ShipTimeoutSeconds` 30 s → 90 s | owned here as explicitly out of scope, not omitted |
| W-c combat entry, FIRST entry | the fight's bytes **only once the offer has landed**. ⚠️ In the offer-in-flight sub-window the slot may still hold the **previous** fight under exactly this name — `ClientSession.CombatEntry.cs:28-30`: *"the scenario slot is rewritten at the start of every mission … only the digest tells them apart"* | fight restarts from its beginning | acceptable, and said out loud — but **name-matching is not enough**, which §12 R2 turns into a rule for H1-lite |
| W-c′ combat entry during a stage-D resume | a mirror EXISTS and turns existed — W-c's "zero turns" is false here | resume from the mirror once promoted | same recovery arm as W-d; the re-entry window is not a special case |
| W-d planning, checkpoint N mirrored | mirror of turn N + a live pose replay | **YES — the headline case** | full resume at turn N. **Planned-but-uncommitted orders die with the barrier** — they are in no checkpoint, the same loss R0·2's diff measures for the host's own reload |
| W-d′ inside `TryCommit`, mirror shipped, commit not broadcast | a VALID mirror of a turn nobody saw committed | **YES**, and this is designed rather than tolerated — see §5 | no extra loss; the checkpoint holds the complete not-yet-executed plan |
| **W-e commit REFUSED** | a mirror of turn N whose plan was **replanned** and re-checkpointed at the same turn number | **YES**, provided the version is a sequence and not the turn — see §5 | 🔴 the window that forces `MirrorSequence` |
| W-f mid-execution | mirror of turn N; keyframes stop mid-replay | resume at turn N — the in-flight turn's simulation died with the host | the in-flight turn is refought; **at most one turn only under §6's assumptions** |
| W-g the fight resolves, outcome processing in flight | at most a `CombatEndMessage`; no outcome is mirrored | rollback: resume from the last checkpoint and re-finish | the ending is re-fought |
| W-h mid-debriefing (post-M12d) | manifest + selections + budget (M12d's own replication) | rollback per `m12d-plan.md` §2.7; migration adds only *who hosts the re-fight* | ending re-fought, loot selections not preserved — already M12d's story |
| **the control (today, no M18)** | `Fault` → `SetExecutionLockEffect(false)` → single-player continuation of the replayed fight (`ClientSession.cs`, member `Fault`, `:306-321`) | n/a | **the baseline M18 must beat, and it already works** |

**The player-facing pause is in seconds, not frames.** B5's run (2026-08-22) measured a first fight
entry blocking **33.2 s** on the ship and a re-entry after a checkpoint load at **3.4 s**. A
promotion resume pays a full campaign load of the mirror (`PbjProtocol.LoadTimeoutSeconds = 120.0`,
`PbjProtocol.cs:327`) plus `CombatShipGlue`'s write plus the old host's fetch plus *its* load. Price
the UX at **tens of seconds, twice**.

---

## 3. Q1 — the promotion trigger: EXPLICIT `pbj.promote`

**Settled: explicit.** Not on the "policies rot" argument alone — that argument is true but soft.
Three pieces of evidence, all in the tree or already measured:

1. 🔴 **A client really can fault against a live host — but by a RACE, not by the ship duration.**
   ⚠️ **An earlier draft of this section argued from B5's 33.2 s ship against
   `HostTimeoutSeconds = 30.0` (`PbjProtocol.cs:295`). That argument is REFUTED and is struck**, and
   the correction is the transferable part: **33.2 s is a retry-loop WALL DURATION, not 33.2 s of
   silence.** `CombatShipGlue` is a per-frame poll — *"Called every frame, immediately after
   `NetGlue.Pump`"* (`CombatShipGlue.cs:115`) — and `WaitAndSayWhy` (`:172`) prints every second
   (`SayWhyEverySeconds = 1f`, `:59`; the guard at `:186`), **so the reading is itself proof the pump
   ran throughout.** `HostSession.HandleTick` (`HostSession.Tick.cs:60`) pings any peer quiet for
   ≥5 s, and any inbound stamps the client alive (`ClientSession.Dispatch.cs:151-154`,
   *"Any traffic proves the host is alive"*). `ShipTimeoutSeconds`'s own remark records real entries
   at 26.7 / 26.9 / 27.4 s (`CombatShipGlue.cs:47`) **with no client host-fault**. I compared a wall
   duration against a silence threshold — **different units**.

   ⭐ **The unit that actually governs these timeouts is the longest single FRAME GAP, not any
   operation's wall time.** It has been measured for exactly one thing — the checkpoint write, at
   **433 ms mean / 455 ms worst** — and for nothing else.

   **The real race is on this side of the wire.** `ClientSession.HandleMessage` stamps
   `lastInboundSeconds = nowSeconds` (`ClientSession.Dispatch.cs:154`), and `nowSeconds` is the time
   carried by the **last `TickEvent`** (`ClientSession.Link.cs:72`), not the current time.
   `PbjRuntime.Pump` drains the mailbox *before* `ObserveTick` (`PbjRuntime.cs:64` then `:83`). ⇒ if
   the **client's own** process hitches — a load, a shader compile — the resumed pump stamps the
   arriving traffic with the **pre-hitch clock**, and the tick immediately after computes
   `silent = nowSeconds - lastInboundSeconds` (`ClientSession.Link.cs:85`) across the whole hitch and
   **faults against a host whose messages it just processed in the same pump.** That is a genuine
   auto-promote-against-a-live-host path, and unlike the struck argument it is a race rather than a
   unit error.
2. **`Fault` is reached from more than a dead host.** Its call sites are `HandleTick`'s timeout
   (`ClientSession.Link.cs:91`), `HandleWelcome`'s second-Welcome violation (`:48`), and the refusal
   and decode arms in `ClientSession.Dispatch.cs` (`:185` sets `Faulted` directly). Auto-promotion on
   `Fault` would promote on *"the host refused my passphrase"*.
3. **The cost of waiting for a human is bounded and already paid.** `Fault` unlocks execution
   immediately (`:320`), so the player is not frozen while deciding, and the fallback during the
   decision window is precisely the shipping control.

### 🔴 The limitation the explicit trigger does NOT remove, and which I originally failed to state

**`Faulted` cannot distinguish a dead host from a partitioned one.** `Fault` is reached from silence
(`ClientSession.Link.cs:91`), and silence is what a network partition looks like. ⇒ **under a ≥30 s
partition the EXPLICIT path also yields two live hosts.** The sentence I used against auto-promotion
— *two hosts, two continuations of one fight, and no way for either to know* — **applies to my own
chosen design**, and the only thing mitigating it is **out-of-band human knowledge** (a voice call:
*"did you crash?"*). That is a real mitigation and it is why explicit still wins; it is not a
guarantee, and pretending otherwise would be the milestone overselling itself again.

🔴 **And the return path in §9 assumes the old host's PROCESS DIED. Under a partition it did not** —
and a partitioned-but-alive old host **cannot even `pbj.join` the survivor**, because `Connect`
refuses while `runtime != null` (`NetGlue.Connect.cs:161`, the same guard `Host` carries at `:49`).
⇒ **the split-brain choreography is an H2 obligation:**

- the old host must `pbj.net-stop` **first**, abandoning its own fork of the fight, before it can
  join the survivor;
- **nothing tells it to.** It has no idea a promotion happened — it saw a peer drop, nothing more;
- ⇒ H2 owes a `pbj.net-status` line that says *"you are hosting; if the other player promoted, run
  `pbj.net-stop` then `pbj.join <their address>` — your copy of this fight will be discarded"*, and
  the human has to be the one who decides which fork survives. **There is no protocol answer to this
  at N=2**, because the two halves cannot talk.

**The honest counter to explicitness, and its answer.** An explicit trigger loses the session for
anyone who does not know the command. The answer is **not** an automatic policy; it is a **prompt**:
when a client faults *and* holds a mirror for the fight it is in, the fault line names `pbj.promote`
and the turn it would resume from. That is a log/UI change in the mod, costs no protocol, and belongs
to H2.

**`pbj.promote` must REFUSE, loudly and by name, in five cases** — each derived:

| refusal | why | evidence |
|---|---|---|
| the session is not faulted | promoting away from a live host forks the fight | `ClientSession.State` — refuse unless `Faulted` (or `Closed` after a `Bye`) |
| no mirror is held | there is nothing to resume from; say what the alternatives are | client-local |
| the mirror's `SessionId` is not this session's | a mirror from an earlier session would resume the wrong fight | §4 rule 1 |
| the roster held more than one other peer | H-8's default, §8 | `ClientSession.LobbyRoster` (`ClientSession.cs:219`), count at `:244` |
| the roster is EMPTY (never received a `LobbyState`) | "I do not know" must not be read as "there were two of us" | same — `LobbyRoster` defaults to `NoLobbyPeers` (`:53`, `:219`) |

That last row is the vacuity guard, and it is on the **instrument** (the roster the client actually
holds), not on the input.

---

## 4. Q3 — mirror versioning and integrity

### The message

`CheckpointMirrorMessage`, host → client, new `PbjMessageType` taken at H1's build time (the enum
ends `ReplayAssets = 32`, `PbjMessage.cs:56`; M12d's plan proposes 33–38, so H1 recounts after W3):

| field | why |
|---|---|
| `SaveName` | compared, logged, **never used as a path** — the rule `ScenarioPayload`'s class remark states (`ScenarioPayload.cs:65-84`). Must equal `LobbySaveNames.CheckpointSlot` (`LobbySaves.cs:75`) |
| `SessionId` | so a mirror cannot outlive the session that produced it |
| `Turn` | the turn whose **not-yet-executed plan** the checkpoint holds — `WriteCheckpointEffect.Turn`'s own doc, `PbjEffect.cs:70-74` |
| **`MirrorSequence`** | 🔴 the ordering key. Monotonically increasing per host session, never reset, **independent of `Turn`** — see §5 |
| `PeerCount` (byte) | eligible-peer count as the host sees it, so the N>2 refusal is a fact the client is *told* — §8 |
| `MirrorFlags` (byte) | reserved; sent 0, required 0 on receipt — §8 |
| `Digest` | over the payload files, **recomputed by the receiver** from the bytes that arrived |
| `Files` | `IReadOnlyList<ScenarioFile>`, identical to `ScenarioMessage.Files` (`PbjMessage.cs:800`) — same splitting, same allowlist |

### The acceptance rule, in order, each with its own named refusal

1. `SessionId` ≠ this session → **`WrongSession`**
2. `SaveName` ≠ `CheckpointSlot` → **`DisallowedDestination`** (the existing
   `ScenarioRejection.DisallowedDestination`, `ScenarioPayload.cs:19`)
3. `MirrorFlags` ≠ 0 → **`UnknownMirrorFlags`** (a forward-compat refusal, loud)
4. `MirrorSequence` **not strictly greater** than the held one → **`StaleMirror`** (held starts at −1)
5. `payload.Inspect() != None` → the existing structural refusals, by name: `TooLarge`,
   `PartsNotContiguous` (`:31`), `MixedContentForm` (`:37`), `DisallowedName`, …
6. `!payload.Matches(Digest)` → **`DigestMismatch`**
7. only then `WriteCheckpointMirrorEffect(payload)`

### 🔴 A partial mirror fails LOUDLY, and it fails at the CAUSE

Two independent properties, and both are needed:

- **Nothing is written until the whole payload verifies.** Steps 5 and 6 run *before* any effect is
  emitted — exactly the discipline `HandleScenario` already uses (`ClientSession.Scenario.cs:102-117`).
- **A refused mirror leaves the held one untouched and does NOT advance the held sequence.** So a
  torn transfer degrades the ceiling by one mirror, never to nothing. The write itself inherits
  `WriteScenario`'s stage-and-move (`CombatGameBridge.Scenario.cs`, member `WriteScenario`,
  staging at `:94`), so even a crash mid-write cannot leave a half-directory for a promotion to find.

**Failing at the cause, not the symptom**, means the refusal names *which* of the seven rules bit and
prints the two numbers that disambiguate it (held sequence vs offered sequence; claimed digest vs
recomputed digest). The symptom — "the resume loaded a broken save" — must be unreachable, and the
sequence-preservation above is what makes it unreachable rather than merely unlikely.

### The mutations that prove it — because a comment asserting an invariant is not a check on it

All five are Core tests under the 100% gate. No game, no rig.

| # | mutation | expected RED before the guard | expected GREEN after |
|---|---|---|---|
| **M1** | drop the last byte of one `ScenarioFile.Content` | written and loadable | `DigestMismatch`; held mirror and held sequence unchanged |
| **M2** | send seq 5, then seq 4 with different bytes | the older one replaces the newer | `StaleMirror`; held stays 5 |
| **M3** | send (turn 7, seq 5), then **(turn 7, seq 6)** — the W-e case | a naive `turn > heldTurn` rule **refuses the good mirror** | **accepted** |
| **M4** | send seq 5, then seq 9 (mirrors lost while the client was loading) | — | **accepted**: a mirror is a snapshot, not a log; refusing gaps would make one dropped mirror terminal |
| **M5** | same bytes, different `SessionId` | resumed the wrong fight | `WrongSession` |

**M3 is the one that earns its place.** It is the mutation a reader would fail to write, because the
rule it kills — "version by turn number" — is the obvious one and is what the pricing proposed.

### One host-side obligation that falls out of an open hole

H-6 (the mirror's byte size) is unmeasured; H0 proceeds on "the same order as the scenario ship"
(a combat scenario measured **119 KB**, `ScenarioPayload.cs:110-113`, against `MaxTotalBytes` =
768 KiB at `:119`). **If it is ever over that bound, `Inspect()` returns `TooLarge` and the mirror is
refused every single turn — and from the player's seat that is silent.** So H1 must log a `TooLarge`
mirror on the **host** side before sending, not only on the client side after receiving. That is a
design instruction produced by an open hole rather than blocked by it.

---

## 5. Q5 — the mirror-ahead-of-commit window, and the one nobody priced

### The benign half, stated rather than discovered

> **A mirror is valid for a turn the client never saw committed. That is the designed behaviour.**

`WriteCheckpointEffect(barrier.Turn)` is queued at `HostSession.Turn.cs:196`; `CommitTurnEffect` at
`:200`; the commit *broadcast* only at `:237` after the game answers (`PbjRuntime.cs:255`). A host
can die between any two of those three instants. What the mirror holds is settled by `TryCommit`'s
own comment at `:176-178` — *"the only instant at which a save holds a complete, not-yet-executed
plan"* — and by `WriteCheckpointEffect.Turn`'s doc (`PbjEffect.cs:70-74`). **So a mirror of turn N
is exactly the input to turn N's execution, committed or not.** Resuming from it re-runs turn N with
everybody's orders intact. Nothing is lost that was not already lost by the barrier.

### 🔴 The half that nothing priced: the commit can be REFUSED, at the same turn

`bridge.CommitTurn()` returns a bool, and false is a *normal* answer:
`CombatGameBridge.Turn.cs`, member `CommitTurn` (`:73-96`) — *"ConfirmExecution is void and refuses
silently in four normal situations, so the only honest test is whether the turn moved"* — returning
`after != before`. `PbjRuntime.cs:255` feeds that into `CommitOutcomeEvent`.

`HandleCommitOutcome`'s refusal arm (`HostSession.Turn.cs:205-220`) then:

- logs `CommitRefused` (`:207`),
- **unreadies the host and every peer** (`:208-212`),
- clears `submitted` (`:213`) and the pending results (`:217`),
- re-opens the UI with `SetExecutionLockEffect(false)` (`:218`),
- and **leaves `State` at `Planning` and `barrier.Turn` unchanged.**

⇒ The barrier can fill again **at the same turn**, and the next `TryCommit` emits a second
`WriteCheckpointEffect(barrier.Turn)` — **same turn number, different bytes.**

**Therefore turn number is not a version.** It is not unique and it is not monotonic across mirrors.
Any rule of the form *"refuse unless the turn is greater than the one held"* refuses the legitimate
second mirror of turn N — the one holding the plan that actually ran. Any rule of the form *"refuse
unless the turn is at least the one held"* accepts it but cannot order two same-turn mirrors at all.
Hence `MirrorSequence`, and hence mutation **M3**.

**A second-order note, recorded so H1 does not build on it:** `committedTurn = barrier.Turn` is
assigned at `:199`, *before* the outcome is known, so after a refusal `committedTurn` names a turn
that was not committed. It has no wire consequence today — it is sent from **two** sites, `HandleHello`'s
`Executing` arm (`HostSession.Handshake.cs:100`) **and `HandleRejoin`'s** (`:231-233`), and *both*
are gated on `State == Executing`, which only a **successful** commit reaches (`:222`). (An earlier
draft said "only from `HandleHello`" — the conclusion stands, the word "only" was wrong.) **H1 must version the mirror on `MirrorSequence`, never on
`committedTurn`.**

---

## 6. The recovery ceiling, with its assumptions where the ceiling is

> **At most one turn is lost — under two conditions, both of which are today's defaults, and either
> of which the plan is licensed to move.**

1. **`checkpointEveryNTurns == 1`.** The constructor default (`HostSession.cs:185`), refused below 1
   at `:199-208`, exposed as `CheckpointEveryNTurns` at `:228`, applied as
   `barrier.Turn % checkpointEveryNTurns == 0` at `HostSession.Turn.cs:194`. Its own doc
   (`HostSession.cs:168-176`) names the number that would move it: `DataManagerSave.SaveData`'s
   unconditional `RefreshSaveHeaders`, *"a cost that scales with the player's lifetime save count …
   which nobody has ever measured"*. **B5b is licensed to raise it.**
2. **One mirror shipped per checkpoint.** H-6 may force an N-checkpoint mirror cadence if the payload
   is large.

⚠️ **The pricing says "if either moves, the ceiling is N turns". That is wrong: the two knobs are
independent and they MULTIPLY.** With cadence N and mirror-every-M-checkpoints, the ceiling is
**N × M turns**, not N. Stated here so the ceiling is never quoted without its assumptions.

---

## 7. Q4 — 🔴 the reachability reversal

**The assumption, in one sentence, as required:**

> **M18 assumes the surviving client's machine can accept an INBOUND TCP connection from the old
> host's machine on the promoted port — a property the original topology never required of it, which
> nothing in the mod can establish, discover or work around.**

### What it means for a real two-machine session

- **The mod's own policy makes it explicit rather than silent.** `NetGlue.Connect.cs`, member
  `Host(string, int, string?)` (`:47-107`), calls `ConnectRules.CheckHostBind` (`:57`) →
  `ConnectForm.cs`, member `CheckHostBind` (`:124-132`) → `CheckPassphraseForBind` (`:141-149`),
  which returns `OpenBindNeedsPassphrase` for any **non-loopback** bind with no passphrase, surfaced
  as a refusal at `NetGlue.Connect.cs:62-66`. ⇒ **`pbj.promote` must take a bind address and a
  passphrase; it cannot default them** for any session that is not on one machine.
- **Whatever made the old host reachable, the survivor has not done.** A forwarded port, an open
  firewall rule, a known LAN address — all of them belonged to the machine that just died. The mod
  must not pretend otherwise: the promoted host **listens and says so**
  (`NetLog.HostListening` at `NetGlue.Connect.cs:92`, and the deliberately loud
  `NetLog.HostListeningOpenly` for a non-loopback bind at `:98`). If the old host's `pbj.join` never
  connects, that is a network fact outside this milestone.
- 🔴 **The direction that worked before is exactly the direction that stops working.** The old host
  was the reachable end *by construction* — it was the one being connected to. Across the internet
  with the survivor behind NAT, promotion produces a host nobody can reach.

### ⚠️ H3 is structurally BLIND to this, and must say so in its results

H3's acceptance sitting is a local pair: both ends are one machine, so the bind is loopback and the
one property that fails on a real pair is the one never exercised. **H3 accepts everything about
promotion except this**, and its report must say that in those words rather than reporting
"promotion verified".

### The scope reading, and the sentence 1.0 owes

`road-to-1-0.md:640` keeps *"a release before 1.0 / remote-friend compatibility"* off the path:
*"settled by the release policy; local two-instance playtesting is the verification route."* So the
reversal is **moot for verification and not moot for the claim.** If 1.0 ships saying "the session
survives a host drop", it says that to people playing across the internet. The mitigation is not
code; it is one release-note sentence, and H0 owes it:

> *Host migration resumes the fight on the surviving player's machine. That machine must be
> reachable from the other player — the same reachability the original host needed. On one machine
> or a LAN this is automatic; across the internet it may require the surviving player to forward a
> port, which the original host may already have done and the survivor has not.*

**And the fix is not in M18.** It is either a relay/rendezvous or a "the old host re-hosts and the
survivor mirrors back" inversion. Both are new milestones; **neither needs room reserved in H1's
wire**, because both change the *transport*, not the message set.

---

## 8. Q6 — the two holes for the user

### H-7 · Campaign ownership after promotion

**The framing, derived.** The fight the survivor resumes was built from the **old host's** campaign
bytes: `campaign-coop.md` step 1 and the M11d synchronized load mean one save, chosen by the host,
loaded by everyone under the same `pbj_` key. But the overworld is host-only
(`campaign-coop.md`, "Authority split") and **nothing mirrors overworld bytes**, so at the moment of
promotion the survivor's copy of the shared campaign is stale by every host-only map action since
the load — and `campaign-coop.md`'s third correction records that generated contracts are rolled per
machine and diverge across **50–87%** of each file.

| option | what happens | cost | verdict |
|---|---|---|---|
| **H-7·A — the fight ends the session** | the promoted host finishes the fight; the outcome and salvage land in **nobody's** campaign | 🔴 **NOT zero new code — see below** | ⭐ still recommended for 1.0, with its exit path designed |
| **H-7·B — the survivor's campaign consumes it** | the promoted host applies the outcome to its own copy and becomes the campaign's owner | 🔴 silently **rewinds the overworld** to the last synchronized load — base position, clock, and a wholly different contract set. Needs an overworld mirror nobody has priced | post-1.0, and the more dangerous of the two |
| **H-7·C — the old host's campaign consumes it on its return** | the campaign never changes hands; only the simulation did. The outcome transfers back | needs an outcome/manifest crossing the wire — **M12d-shaped**, so it collides with M12d's surface, not M18's | post-1.0, cleaner than B |

🔴 **H-7·A's "zero new code" was wrong, and the reason matters.** `HandleCombatExited`
(`HostSession.CombatEntry.cs:214-246`) ends the *session's* combat state. It reloads nobody's
campaign — **"both machines return to their own overworlds" is not something any cited code does.**
The mirror is a full `DoSave` of the old host's campaign-in-combat, so after the fight the survivor's
*game* is sitting **inside a fork of the old host's campaign, loaded from `pbj_combat_turn`** — a
directory the mod rewrites at every turn boundary. Worse, `SaveNamespacePatches.CampaignBitFromLoad`
fires on the game's own `LoadingEnd2` and calls `MultiplayerCampaign.Enter("pbj_combat_turn")`
(`m12c-stage-d-fork.md` §6), so the survivor is marked as being *in* a multiplayer campaign named
after a scratch slot.

⇒ **H-7·A needs an exit path, and it is H2's:**

- **A1 — the survivor loads its own last campaign save** once the fight resolves, explicitly and with
  a prompt. Costs one console/UI path; leaves nothing in a scratch slot.
- **A2 — the survivor is TOLD it is in a scratch campaign** and must `pbj.save-as` to keep anything.
  Cheaper, and honest, but one Execute later the slot is overwritten — so the window to act is a
  single turn. **A1 is the safer default; A2 is the one-line fallback.**

**Recommendation to the user: H-7·A with exit path A1.** It is still the only option that does not
silently rewind a campaign, and §M18.3's own "the overworld half of 'the session survives' is
explicitly NOT bought" still stands. **Nothing in H1 or H2's promotion mechanics changes under any of
the three** — only the post-fight arm forks, as the pricing predicted.

### H-8 · The N>2 seam

**Is the N=2 mechanism arbitrary, refusing, unoptimised, or WRONG at N>2? Honest answer:
under-specified, not incorrect.** Promotion itself — mirror, load, re-ship, re-offer — works
identically with three peers, because `HandleHello` and `Reassign` (`HostSession.Roster.cs:131-140`)
deal whatever roster shows up. What is missing at N>2 is only *which* client promotes. So it is
**unoptimised-and-racy**, and refusing is a policy choice rather than a structural necessity.

| option | consequence | verdict |
|---|---|---|
| **REFUSE at N>2, with a named error** | N>2 sessions keep today's fault behaviour — the control, which works. Wrong-but-loud | ⭐ **recommended** (and the backlog's own default) |
| **First-come** | both clients promote, each builds a listener, and you get **two divergent continuations of one fight — silently**. That is the exact failure the milestone exists to prevent | reject |
| **Elect** | out of scope by the user's ruling | — |

**Does the N=2 design foreclose a later policy? No for the mirror side; YES-at-a-cost for the roster
side — and that distinction has to be decided now, because wire windows are scarce.**

- A later selection policy needs a **stability metric** known to every candidate. Today nothing
  carries one: `LobbyStateMessage`'s roster entries are `LobbyPeerState(peerId, name, ready)`
  (`PbjMessage.cs:810-817`) and nothing else, and the mirror travels host→client only.
- **The cheap insurance, decided now** (§4's message): a **`MirrorFlags` reserved byte** (sent 0,
  required 0) and a **`PeerCount` byte**. Two bytes on a message already carrying tens of kilobytes.
  `PeerCount` makes the N>2 refusal a fact the client is *told* rather than one it infers from a
  roster it may not have; `MirrorFlags` is where a later metric's presence bit goes without a new
  message type.
- ⚠️ **Be honest about what that insurance does NOT buy.** A per-peer stability metric wants a
  per-peer field in `LobbyStateMessage` — an existing layout, so **still a protocol bump and still a
  wire window.** ⇒ **The seam is open on the mirror side and closed on the roster side: a later
  election will cost one wire window.** Said now, because W4 is the last window currently scheduled
  and someone will otherwise assume the reserved byte bought more than it did.

---

## 9. Q2 — the rejoin flow for the old host, and the ordering trap inside it

### 🔴 The old host CANNOT use `pbj.rejoin`. It comes back through `pbj.join`, as a newcomer.

`[DERIVED — this corrects an assumption in the pricing's H0 row.]` Two independent reasons, both
forced by code:

1. **It has no token to present.** `NetGlue.Connect.cs`, member `Rejoin` (`:150-157`), needs
   `resumeToken` and `lastAddress`; `resumeToken` is captured in `NetGlue.cs`, member `Shutdown`
   (`:157-162`), **only from a departing `ClientSession`**. The old host was never a client, so
   `Rejoin` returns *"nothing to rejoin"*.
2. **Even with one, it would be refused twice over.** `HostSession.Handshake.cs`, member
   `HandleRejoin` (`:159`), refuses at `:185-189` with `RejectReason.UnknownSession` unless
   `rejoin.SessionId == SessionId` — and the promoted host's `SessionId` is a fresh
   `NewSessionId()` (`NetGlue.Connect.cs:234-237`, a new GUID). It would then refuse at `:193-198`
   with `BadResumeToken`, because `departed` is empty on a session that has just been constructed.

**This is not a workaround; it is the correct reading of what promotion is.** The session identity
changed.

⚠️ **And there is a precondition on all of it that §3 now makes load-bearing: `HandleHello` cannot run
at all while the old host's own session survives.** The old host must `pbj.net-stop` before it can
`pbj.join` (`NetGlue.Connect.cs:161`). Under a crash that is automatic; under a partition it is a
human decision about which fork to discard. See §3.

The newcomer path then does everything needed, in the right order —
`HostSession.Handshake.cs`, member `HandleHello`:

| step | site | what the old host gets |
|---|---|---|
| 1 | `:81-83` | `WelcomeMessage` with the **new** `SessionId` and the survivor's `barrier.Turn` |
| 2 | `:91` | `AnnounceLobby` |
| 3 | `:92` | `OfferScenario` → `OfferSave(peerId, ScenarioSlot)` (`HostSession.Scenario.cs:33`) — **the fight, as the promoted host re-shipped it** |
| 4 | `:93` | `TellNewcomerAboutCombat` (`:309-316`) → `CombatStartMessage(barrier.Turn)` while Planning/Executing |
| 5 | `:95-101` | `TurnCommitMessage(committedTurn)` if Executing |
| 6 | `:103` | `Reassign` (`HostSession.Roster.cs:131-140`) → units re-dealt |

The old host then takes the M12b path it has been on the other side of all along:
`ClientSession.CombatEntry.cs`, member `HandleCombatOffer` (`:32-58`) — hold it already and load, or
request by **digest** (`:57`) and load when the bytes land (`ClientSession.Scenario.cs:123-127`).
**The precedent the pricing named is right for the fight; it is wrong for the connection.**

Two consequences worth stating rather than discovering:

- **Unit ownership is re-dealt on the old host's return**, at `HandleHello:103`. That is the same
  consequence stage-D branch A carries (`m12c-stage-d-fork.md` §3.3) and it is the *same*
  `Reassign`. ⇒ `[DERIVED]` **if H-3's reading forces stage D branch B (preserve assignments across a
  rollback), H2 must reuse branch B's suppression here rather than build a second one** —
  `HandleHello:103` and `StartCombatForEveryone:211` call the same method.
- **The seal is not a problem.** `admitted` (`HostSession.cs:78-79`) is empty and `lobbySealed`
  (`:59`) is false on a fresh session, so nothing locks the old host out. The flip side: for that
  window the promoted host is as open as the original was — no worse, and the passphrase is the same
  door lock it always was.

### 🔴 The ordering trap H2 must not fall into

Both the runtime and the session **seed themselves from `bridge.InCombat` at construction**:

- `PbjRuntime.cs:47` (ctor) — `lastInCombat = bridge.InCombat`, with the comment *"a session started
  mid-combat must not report entering one on its first pump"*;
- `HostSession.cs:221` (ctor) — `State = bridge.InCombat ? Planning : Lobby`.

The survivor **is in combat** at the moment of promotion — it was replaying the host's fight.
Therefore:

> **If `pbj.promote` loads the mirror FIRST and constructs the `HostSession`/`PbjRuntime`
> afterwards, `lastInCombat` is seeded true, `ObserveCombatEdge` (`PbjRuntime.cs:117-128`) never
> sees a false→true edge, `HandleCombatEntered` never runs, `ShipCombatEffect`
> (`HostSession.CombatEntry.cs:53`) is never emitted — and `pbj_combat_test` on the survivor's disk
> is still the START-OF-FIGHT bytes. The old host would then join, be offered those, load them, and
> the two machines would sit several turns apart. Silently: every digest matches, because the client
> really did receive exactly the bytes it was offered.**

⭐ **This is not hypothetical — the identical failure is reachable in the shipping mod today.**
`pbj.host` typed from *inside* a fight builds the bridge (`NetGlue.Connect.cs:80`), the session
(`:86`) and the runtime (`:89`) with `InCombat` already true, pre-seeds the edge, **never emits
`ShipCombatEffect`**, and then offers newcomers whatever stale bytes are in the scenario slot via
`OfferScenario` (`HostSession.Handshake.cs:92`) — **with every digest matching.** Cite it in H2's
review as live corroboration rather than as a thought experiment.

⇒ **The order is forced.** Construct the promoted `HostSession` and `PbjRuntime` **while still in the
replayed combat**, then begin the mirror load. The load pops the controller state to `mainmenu`
(`DataHelperLoading.TryLoading`, traced in `m12c-stage-d-fork.md` §2 step 4), so:

1. `InCombat` → false → `CombatExitedEvent` → `HandleCombatExited`
   (`HostSession.CombatEntry.cs:214-246`). Harmless here and only here: there are **no peers yet**,
   so the `CombatEndMessage` broadcast at `:240` reaches nobody and the
   `assignments = UnitAssignments.Empty` at `:217` discards a mapping about to be re-dealt anyway.
2. the load completes → `InCombat` → true → `CombatEnteredEvent` → `HandleCombatEntered` (`:38-54`)
   → `ShipCombatEffect` (`:53`) → `CombatShipGlue.Write` writes `pbj_combat_test` **from the resumed
   state** → `HandleLocalCombatReady` (`:59-112`) offers it.

**That is stage D branch A's machinery, running on a second machine** — which is precisely why H2
depends on M12c stage D, and the dependency is `[DERIVED]`, not scheduling.

### ⭐ `Shutdown()` + `Host()`, executed INSIDE the fight, is the simple path — and the two
### constraints compose in its favour

⚠️ **An earlier draft of this section said H2 "cannot" be composed from `Shutdown()` + `Host()`. That
was judgment wearing a `[DERIVED]` tag and it is withdrawn.** The mechanics it cited are all real:
`Host` refuses while `runtime != null` (`NetGlue.Connect.cs:49`), so promotion must tear down first;
`Shutdown` (`NetGlue.cs:142-169`) nulls `bridge` (`:164`) and calls `CombatGameBridge.ResetLock()`
(`:167`); `Host` then builds a fresh `CombatGameBridge` (`:80`). **But "a fresh bridge mid-combat is
unsafe" is a claim I offered no failing mutation for, and the code argues the other way:**
`ResetLock` clears **exactly one static bool** — `ExecutionLocked` (`CombatGameBridge.Turn.cs:35-37`)
— leaving `CommitInProgress` (`:33`) and the rest of the bridge's state, most of which is static and
survives reconstruction anyway.

⭐ **And here is the composition neither section originally ran.** `Shutdown()` + `Host(bind, port,
passphrase)` executed **while still in the replayed fight** reads `bridge.InCombat == true` at
construction — which **automatically satisfies the construct-before-load ordering above**, because
the seeds at `PbjRuntime.cs:47` and `HostSession.cs:221` are exactly what the ordering rule is about.
**The trap and the simple path are the same fact seen from two sides, and they agree.**

⇒ **H2's plan:** try `Shutdown()` + `Host()`-inside-the-fight **first**, and build a bespoke
role-change path only if a *failing mutation* shows the fresh bridge is unsafe. The honest statement
of the risk is §M18.5's, unchanged: the session-role change is the first this codebase will ever
perform and is where the refutation pass must aim — but the cheap composition is now the default
hypothesis rather than a rejected one.

---

## 10. What M12c plan KILL 2 reopens — precisely, and what it costs

KILL 2 killed *"M12c is blocked on M17 because `isWrecked` lands in a client's autosaves"* by noting
that a client takes no checkpoint, and flagged: *"if a future revision reinstates client-side
checkpoints, KILL 2 reverses and the dependency comes straight back."* (Quoted verbatim from
`backlog-2026-08-22.md` §M18.2, which is the citable in-repo source; the original plan lives in a
user-level memory file.) **The mirror is that revision.** Three parts:

### 10.1 What stays closed

The client **stores the host's bytes verbatim and never runs its own save serializer for the slot** —
the mirror write is `WriteScenario`-shaped (`CombatGameBridge.Scenario.cs:78`), a directory of files
off the wire, not a `DataManagerSave.SaveData` call. So the original worry — a client's own ECS, with
M17 stage 2's pilot bits, M16's part integrity and M15's wreck state, being serialized into a
checkpoint — **never happens.** The client-side-ECS-contamination half of KILL 2 stays closed.

⚠️ **But one shipped doc line becomes WRONG the day H1 lands, and it must be amended in the same
PR.** `LobbySaveNames.CheckpointSlot`'s remark (`LobbySaves.cs:68-73`) says *"**Only the host writes
one.** A client's ECS never receives its peers' orders … so a client's own checkpoint would reload
into a half-planned turn."* After H1 a client holds a checkpoint directory. The corrected wording:
**only the host PRODUCES one; a client may HOLD A MIRROR of one, and never writes its own.** Filed
here so it is not the project's next stale-prose incident — prose I write is the least-checked thing
in the commit.

### 10.2 What comes back: the slot discipline, on a second machine

All seven `IsNonCampaignSlot` sites now guard a directory that exists on the client too. Enumerated
with their members (`grep -rn "IsNonCampaignSlot" src/`, production sites only):

| # | site | member | machine-agnostic? |
|---|---|---|---|
| 1 | `HostSession.Scenario.cs:41` | `OfferScenario` | yes — pure predicate over a key |
| 2 | `HostSession.Scenario.cs:144` | `ResolveRequested` | yes |
| 3 | `HostSession.Lobby.cs:88` | `OfferSelectedSave` | yes |
| 4 | `LobbySaveWrites.cs:121` | `IsProtectedFromOverwrite` | yes |
| 5 | `LobbySaves.cs:286` | `LobbySaveRules.IsReserved` | yes |
| 6 | `LobbySaves.cs:403` | `LobbyCatalogue.IsOffered` | yes |
| 7 | `SaveVisibilityPatches.cs:164` | the `GetSaveHeaders` postfix | 🔴 **mod-side Harmony patch** |

🔴 **"Six of seven are machine-agnostic, therefore H1 needs no new guard sites" was an unsound
inference, and the unsoundness is instructive.** Sites 1, 2 and 3 are **`HostSession` paths that never
execute on an un-promoted client at all** — they are not *fine*, they are **vacuously** fine, which is
the same shape as a control case structurally unable to reach the bug it guards. (They stop being
vacuous the instant the client promotes: the promoted host runs `OfferScenario` at
`HostSession.Handshake.cs:92` for the rejoining old host, and sites 1–2 then bite for real. So they
are *deferred*, not dead — but they buy nothing during the window the mirror is actually sitting on
the client's disk.)

**The claim that actually needs proving is a totality claim over CLIENT surfaces**, and this document
never enumerated them. Enumerated now — every route by which `pbj_combat_turn` can be seen, written,
overwritten, deleted or loaded **on a client machine**:

| # | client surface | mechanism | guard | verdict |
|---|---|---|---|---|
| C1 | singleplayer **load** grid — browse and select | `GetSaveHeaders` postfix | site 7 (`SaveVisibilityPatches.cs:164`) | ✅ guarded, 🔴 **no oracle** |
| C2 | singleplayer **save** grid — the slot appearing as an overwrite target | same `GetSaveHeaders` filter | site 7 | ✅ same patch, same no-oracle |
| C3 | overwrite by **typing the name** | `DataPathHelper.IsReservedFilename` postfix (`SaveVisibilityPatches.cs:259`, `:264`) → `IsProtectedFromOverwrite` | site 4 (`LobbySaveWrites.cs:121`) | ✅ pure Core predicate |
| C4 | **delete** from the save screen | `CIViewPauseSave.OnDeleteButton` prefix (`SaveVisibilityPatches.cs:281`) | refuses **any** `IsMultiplayerKey` — broader than the slot rule, so the mirror is covered | ✅ (mod-side, no oracle) |
| C5 | creating or converting a campaign under that name | `pbj.save-as` / `pbj.save-convert` | site 5 (`LobbySaves.cs:286`) | ✅ |
| C6 | the **lobby picker**, which a client renders read-only | `LobbyCatalogue.IsOffered` | site 6 (`LobbySaves.cs:400-403`) | ✅ |
| **C7** | 🔴 **`pbj.checkpoint-load` typed on a CLIENT** | `CheckpointGlue.CheckpointLoad` (`CheckpointGlue.cs:229`) — **not role-gated in any way** | **NONE** | 🔴 **a hole H1 must close** |

**C7 is new and it is the useful one.** Today `pbj.checkpoint-load` is harmless on a client because
the slot is empty there. After H1 it is not: a human typing it **loads the host's mirror into their
own game without promoting** — no role change, no re-ship, no `pbj.promote` refusals — while the
session is still live. The result is a solo copy of the host's fight on a machine that is still a
client. ⇒ **H1 or H2 must gate `pbj.checkpoint-load` on the session role**, or route it through
`pbj.promote`. It is one `if`, and nothing would have found it without enumerating the surfaces.

⇒ **The corrected accounting: H1 needs no new guard sites for C1–C6 (four of them genuinely
machine-agnostic, two mod-side and already broad enough), and ONE new gate for C7.**

🔴 **The seventh is the obligation.** `src/PBAndJ.Mod` sits in `UNCOVERED_PROJECTS` (the gate's own
`coverage scope OK (1 measured, 2 declared not)` line), so if that postfix were ever dropped or its
`[HarmonyPatch]` attribute lost, **nothing would fail the build** and `pbj_combat_turn` would appear
in the client's singleplayer save grid and picker. **No oracle in this repo can see it.** ⇒ H1's
acceptance must include *eyeballing the save grid on the client machine*, and H3's sitting is where
that happens.

### 10.3 What comes back: staleness

A mirror is always ≤ the host's latest checkpoint. Bounded by §4's versioning and §6's ceiling, and
by nothing else. Stated rather than left implicit.

---

## 11. Dependency rows, tagged — and the two the pricing had rotted

Before filing any dependency as intent: *what would have to be true in the code for this to hold?*
If that has an answer it is **DERIVABLE**.

| row | tag | evidence, by member |
|---|---|---|
| H1 needs a wire window of its own | `[DERIVED]` | one `wire-surface.lock`; this worktree's gate prints `wire surface OK (unchanged since 0.24.0)` — a bump re-records it, so two bumps cannot share a window |
| H1 after W3 | `[INTENT]` | release-definition ordering only; §M18.5 already retagged this and it stays retagged. H1 could take v11 at zero code cost |
| H2 depends on M12c stage D | `[DERIVED]` | the promoted host's resume **is** the combat-edge round trip: `PbjRuntime.ObserveCombatEdge` (`:117-128`) → `HandleCombatEntered` (`HostSession.CombatEntry.cs:38`) → `ShipCombatEffect` (`:53`). §9 |
| **H2's ownership handling depends on stage D's BRANCH, not just on stage D** | `[DERIVED — new]` | `HandleHello:103` and `StartCombatForEveryone:211` call the same `Reassign` (`HostSession.Roster.cs:131`) |
| **H2 must construct the session BEFORE loading the mirror** | `[DERIVED — new]` | `PbjRuntime.cs:47` and `HostSession.cs:221` both seed from `bridge.InCombat`. §9 |
| **The old host rejoins via `pbj.join`, never `pbj.rejoin`** | `[DERIVED — new]` | `HandleRejoin:185-189` + a fresh `NewSessionId()` (`NetGlue.Connect.cs:234`) |
| ~~H2 cannot be `Shutdown()` + `Host()`~~ **WITHDRAWN** — try it first, inside the fight | `[INTENT]`, was mis-tagged `[DERIVED]` | mechanics real (`NetGlue.Connect.cs:49`, `NetGlue.cs:164,167`, `:80`) but "unsafe" had no failing mutation; `ResetLock` clears one bool (`CombatGameBridge.Turn.cs:35-37`). §9 |
| **The split-brain choreography (old host must `pbj.net-stop` first)** | `[DERIVED — new]` | `NetGlue.Connect.cs:161` refuses `Connect` while `runtime != null`. §3 |
| **`pbj.checkpoint-load` needs a role gate after H1** | `[DERIVED — new]` | `CheckpointGlue.CheckpointLoad` (`:229`) consults no session role. §10.2 C7 |
| H3 is blind to the reachability reversal | `[DERIVED]` | one machine ⇒ loopback ⇒ the bind that fails on a real pair is never exercised. §7 |
| H0 assumes the mirror is scenario-sized | `[INTENT — H-6 open]` | `du -sb` on the slot not yet taken; rides B5b. §4's last paragraph makes the assumption non-silent |

### Citations handed to this stage that had already rotted

- 🔴 **`ClientSession.cs`, member `Fault`, is at `:306-321`, not `:278-292`.** M17 stage 2 (`0.24.0`)
  moved it. The member name was correct and is why the citation was recoverable.
- 🔴 **`road-to-1-0.md:640`'s neighbourhood cites `HostSession.Turn.cs:176` as `CommitTurnEffect`'s
  sole emitter.** It is **`:200` today**, member `TryCommit`. (`:176` now lands in the M12c comment
  block — which reads plausibly, which is what makes it dangerous.)
- Minor, not this file's to fix: `PbjMessage.cs:9` says *"31+ are unallocated"* while `Poses = 31`
  (`:51`) and `ReplayAssets = 32` (`:56`) are both allocated. One-line stale-prose fix for whoever
  next opens that file.

---

## 12. Self-refutation pass

Seven attacks on this document. Two land.

**R1 — "The mirror is unnecessary; the survivor resumes from its own replayed ECS."** ❌ **Refuted.**
A client's ECS never receives peers' orders — `ApplyOrderEffect` is emitted only by
`HostSession.TryCommit` (`HostSession.Turn.cs:169`), and `CheckpointSlot`'s own doc
(`LobbySaves.cs:68-73`) states the consequence. It is a pose replay, not a simulation. Resuming from
it resumes a fight in which half the plan never existed.

**R2 — "Skip the mirror: promote from the scenario slot the client ALREADY holds."** ⭐ **LANDS, and
it is the most useful thing in this document — but BOTH of the "strictly" claims I attached to it are
false, and the review was right to kill them.** The client holds `pbj_combat_test` by construction —
it loaded it at combat entry (`ClientSession.CombatEntry.cs:32-58`) or at the last re-ship. Promoting
from *those* bytes resumes **the fight from its start**, needing **no new message type, no protocol
bump, and no wire window at all.** Call it **H1-lite**:

| | control (today) | **H1-lite** | H1 (the mirror) |
|---|---|---|---|
| the session | **lost** | **survives** | **survives** |
| this fight's progress | **fully preserved** — solo continuation at turn N | **discarded** — refought from turn 0 | ≤ 1 turn lost (§6) |
| works with no mirror (W-c, W-s) | yes | **yes** | 🔴 **no — refused by §3's rule 2** |
| wire cost | — | **none** | a window (W4) |
| new Core code | — | small (promotion + a freshness rule) | mirror message + codec + effect + tests |

🔴 **(a) "Strictly better than the control" is `[INTENT]`, not derivation.** The control preserves
**all** of this fight's progress; H1-lite throws it away for a from-scratch refight. **For a player
deep in a winnable fight the control plausibly wins.** The two are not ordered — they trade the
*session* against the *fight*, and only the player knows which they wanted.

🔴 **(b) "Strictly worse than H1" fails wherever no mirror exists** — W-c and W-s — because there
H1's `pbj.promote` **refuses by my own §3 rule 2** while H1-lite can still act. ⇒ **the design fix:
H1 must SUBSUME H1-lite as its no-mirror fallback**, offering the from-the-start resume by name when
no valid mirror is held, rather than refusing outright. That is one extra arm in `pbj.promote` and it
removes the whole "which variant did we ship" question.

🔴 **(c) H1-lite has no analogue of §4's freshness rules, and without one it resumes the WRONG
FIGHT.** The slot is *"rewritten at the start of every mission … only the digest tells them apart"*
(`ClientSession.CombatEntry.cs:28-30`), and `pendingCombatSave/Digest/Turn` (`ClientSession.cs:81-83`)
are written once in `HandleCombatOffer` (`:34-36`) and **never cleared by anything** — so after a
`CombatEnd` they still name the previous fight. **The rule H1-lite needs:**

> `pbj.promote --from-slot` recomputes the slot's digest through `bridge.ReadScenario` and refuses
> unless it equals `pendingCombatDigest` **and** `HostIsFighting` was true at the fault. A null
> digest is a refusal, not a pass.

**And it needs one Core fix to be sound: clear `pendingCombatSave/Digest/Turn` on `CombatEnd`.** That
is a one-line change that also repairs the W-c matrix cell, and it belongs to H1 regardless of which
variant ships.

**So the honest verdict: H1-lite is a real, wire-free option that trades differently from the control
rather than dominating it, and H1 should absorb it rather than compete with it.** It goes to the user
with H-7 and H-8 — before H1 is built, not after W4 is missed. The pricing does not contain it.

**R3 — "The refused-commit double-checkpoint can't happen; the barrier only fills once."**
❌ **Refuted.** The refusal arm explicitly unreadies everyone (`:208-212`), clears `submitted`
(`:213`) and re-opens the UI (`:218`) so the barrier *can* fill again at the same turn. The path is
deliberate, not defensive.

**R4 — "The reachability reversal is moot; 1.0 is verified on a local pair."** ❌ **Refuted, and this
is the trap.** Moot for *verification*, not for the *claim*. §7's release-note sentence is the
mitigation, and it is prose, not code — which is exactly the category this project has been burned by
before, so it is written out verbatim there rather than described.

**R5 — "H2 can just `Shutdown()` then `Host()`."** ❌ **Refuted.** §9: a fresh `CombatGameBridge`
mid-combat, plus an ordering that forbids doing it after the load anyway.

**R6 — "The N>2 refusal can be added later, so nothing is needed in H1."** ⚠️ **Half-refuted.** The
refusal *logic* is client-local and can move any time — upheld. The **`PeerCount` byte it should key
off cannot**: adding it later is a layout change on an existing message, i.e. a window. Decide the
bytes now, the policy whenever.

**R8 — "Explicit promotion prevents split-brain."** ⭐ **LANDS against my own §3** — see the block
added there. A partition is indistinguishable from a death at the `Faulted` boundary, so the explicit
path can also produce two live hosts; it is mitigated by human out-of-band knowledge, not by design,
and the old host cannot even rejoin without first abandoning its own fork. **Explicit still wins, for
the reasons in §3 — but not for this reason, and the document now says so.**

**R7 — "`MirrorSequence` is redundant; TCP is ordered."** ⚠️ **Lands, then fails.** TCP ordering does
hold on one connection. But **the mirror outlives the connection**: a client can reconnect with a
resume token (`ClientSession.Link.cs:35-41`) and start receiving mirrors on a new connection — and
the host's turn can legitimately go *backwards* across a stage-D rollback
(`barrier.AdvanceTo(-1)` at `HostSession.CombatEntry.cs:218`, then `AdvanceTo(bridge.CurrentTurn)` at
`:41`). A monotone sequence survives both; a turn number survives neither. **Upheld.**

---

## 13. The fit verdict — restated after designing it

**M18 still fits inside 1.0 as the last milestone, and designing it did not change H1/H2/H3's shape.
It did change two things:**

1. **H2's shape is now known, and it is CHEAPER than the first draft claimed** (§9, revised).
   `Shutdown()` + `Host()` executed inside the replayed fight satisfies the construct-before-load
   ordering by construction, so the default hypothesis is the simple composition, with a bespoke
   role-change only if a failing mutation forces it. The risk §M18.5 predicted is still real and
   still lives in `src/PBAndJ.Mod`, **outside the coverage gate** — its correctness rests on review
   and H3's sitting, not on tests.
2. **A cheaper variant exists that the pricing does not contain** (R2 / H1-lite), though it **trades
   against the control rather than dominating it**, and H1 should absorb it as its no-mirror
   fallback. **The thing that could push M18 out of 1.0 is not M18 — it is W4.** H1 is the only stage
   needing a window; if W3 slips, W4 slips. H1-lite needs none.
3. 🔴 **M18 buys less than the phrase "the session survives" implies, and the design pass is what
   revealed it.** Three of the ten windows are outside it entirely — W-a and W-b (no fight), W-s in
   its first-entry form (26.7–33.2 s, the longest window in a fight, and nothing to resume from) —
   and W-s-fail is a session loss M18 cannot touch at all. Add the partition case (§3 R8), where the
   explicit path still yields two live hosts. **The defensible 1.0 claim is: "a fight in progress
   survives the host dropping, on a pair where the survivor is reachable."** That is worth shipping;
   it is not "the session survives", and the release note should not say so.

**So: fits, with a named fallback, a narrower claim, and one new gate (C7).** Everything else in
§M18.5 survives: nothing in M12c/M12d depends on M18, H1-after-W3 remains `[INTENT]` and re-orderable
by the user alone, and the release still cuts after H3's acceptance.

---

## 14. Open holes this design leaves, each naming who can take it

| hole | what it is | who can take it | what proceeds meanwhile |
|---|---|---|---|
| **H-6** | the mirror's byte size (`du -sb` on `pbj_combat_turn`) | rides **B5b** (rig) | H1's design assumes scenario-sized; §4's last paragraph makes an over-size mirror loud on the HOST side rather than silent |
| **H-7** | campaign ownership after promotion | **the user**, at H0 review — §8 gives three concrete options and a recommendation | H1 and H2 are identical under all three; only the post-fight arm forks |
| **H-8** | N>2 behaviour | **the user** decides the policy; §8 derives that the seam stays open on the mirror side and costs a window on the roster side | H0 writes "refuse at N>2"; the two reserved bytes go into H1's message regardless |
| **H1-lite** 🆕 | ship promotion with no wire change, resuming the fight from its start | **the user** — it is a product trade (recovery depth vs a wire window) | H0 is complete either way; the two variants share every line of H2 |
| **H-3** | stage D's branch (A or B) | **the user at the desktop**, or a rig-headless agent after B6 rung 4 | H2 inherits whichever lands; §9 derives that branch B's suppression covers H2's rejoin too |
| **H-10** 🆕 | the **frame-gap** distribution during a fight entry — the unit that actually governs every timeout here, measured for the checkpoint write alone (433 ms mean / 455 ms worst) and for nothing else | **rides B5b** (rig) | §3 no longer argues from wall durations, so nothing waits on it; it would tell us whether the stale-stamp race is reachable in practice |
| **H-11** 🆕 | **the split-brain arbitration at N=2** — under a partition both halves are live and cannot talk | **the user**: it is a product decision about what the two humans are TOLD, not a protocol one | H2 ships the `pbj.net-status` advisory in §3; there is no protocol answer at N=2 |

---

## 15. Revision log — what the adversarial review killed

Kept visible rather than silently corrected, because in this repo a claim that was believed and is now
known false is worth more than a clean document.

| # | claim in the first draft | verdict | where |
|---|---|---|---|
| 1 | *"a checker … 110 pass, control included"* — reported but **never written to disk** | 🔴 **unpersisted instrument = a claim, not a check.** Now `tools/cite-check/` | header |
| 2 | B5's 33.2 s ship exceeds `HostTimeoutSeconds`, so auto-promote fires against a live host | 🔴 **REFUTED — different units.** A per-frame poll that prints every second proves the pump ran. Replaced with the stale-stamp race | §3 |
| 3 | the nine-window partition is derived and total | 🔴 **REFUTED.** W-s (the 26.7–33.2 s ship wait) lives in a Mod-side `private static bool`. The state machine is not a sufficient oracle | §2 |
| 4 | `grep -rn "State = HostSessionState"` proves the six writers | 🔴 **returns FIVE** — a pattern-miss, in the document that preaches against them | §2 |
| 5 | `HandleTurnComplete` / stop-and-bye | 🔴 **wrong members** — `HandleLocalTurnComplete`; `case TransportFailedEvent`. Host death never yields `Closed` | §2 |
| 6 | H1-lite is *strictly* better than the control and *strictly* worse than H1 | 🔴 **both false.** It trades session against fight; and it works where H1 refuses | §12 R2 |
| 7 | H1-lite needs no freshness rule | 🔴 **would resume the previous fight.** Digest recheck specified; `pendingCombat*` never cleared | §12 R2 |
| 8 | H2 *cannot* be `Shutdown()` + `Host()` | 🔴 **judgment tagged `[DERIVED]`. Withdrawn** — and the two constraints compose in the simple path's favour | §9 |
| 9 | six of seven guard sites are machine-agnostic ⇒ no new sites needed | 🔴 **three are VACUOUSLY fine.** Client surfaces enumerated; C7 is a real new hole | §10.2 |
| 10 | H-7·A costs zero new code | 🔴 **false** — the survivor is left inside a fork of the old host's campaign, in a scratch slot | §8 |
| 11 | `committedTurn` is sent from *only* `HandleHello` | 🔴 two sites; conclusion stands, "only" struck | §5 |
| 12 | explicit promotion avoids two-live-hosts | 🔴 **it does not, under a partition.** R8 added | §3, §12 |
| — | W-e / `MirrorSequence` / mutation M3 | ✅ **survived end-to-end**, including the decompiled refusal count | §5 |
| — | the ordering trap | ✅ **survived**, and gained live corroboration (`pbj.host` mid-fight) | §9 |
| — | the rejoin correction | ✅ **survived**, all four refusal facts | §9 |
| — | the reachability reversal | ✅ **survived** | §7 |

**One review finding refined rather than accepted:** the missing window W-s was described as
answering "**NO**, like W-a". That holds for a *first* entry; on a stage-D **re-entry** the same
window recurs while a mirror exists, so the answer there is **YES**. W-s splits exactly as W-c/W-c′
do, and the matrix now says so.

---

*H0, stage 1 of 4, revised after adversarial review. H1 (mirror, wire window W4), H2 (promotion),
H3 (acceptance sitting, rig-attended) follow. Reports fold back into `backlog-2026-08-22.md` §M18 via
the planner, never into this file.*
