# Enemy AI previews on a client — recon, and the measurement that settled it

> ## ⚡ MEASURED ON TWO REAL GAMES, 2026-08-14. Read this box first.
>
> The first revision of this file was a decompile read proposing **five compounding mechanisms**.
> `pbj.action-probe`, run on both machines across three turns, **refuted three of them** and replaced
> a complicated story with a much starker one:
>
> **A client has no enemy plan at all. Not stale, not divergent — absent, from the first frame,
> permanently.**
>
> The refuted parts are kept below, struck through with what was actually seen, because the wrong
> reading is the useful part of the record. This is the sixth time a careful decompile read has been
> overturned by one probe.

Found by the user during the M8 playtest and recorded only in session memory until now.

## The gap, in one line

The host plans the enemy's turn and shows it; a client shows nothing. In Phantom Brigade the enemy's
telegraphed plan is the thing you plan *against*, so this is a gameplay defect rather than a cosmetic
one — a client is planning blind while believing it is not.

## ✅ What was measured

`generic_elimination`, two instances, `pbj_fromsp`, three turns, `pbj.action-probe` on both machines
after each:

| turn | host actions | host `aiTagged` | host `plan=` | client actions | client `plan=` | client `combat.currentTurn` |
|---|---|---|---|---|---|---|
| 0 | 10 | 10 | `Finished` | **0** | **`NONE`** | 0 |
| 1 | 14 | 13 | `Finished` | **0** | **`NONE`** | 0 |
| 2 | 11 | 11 | `Finished` | **0** | **`NONE`** | 0 |

Every host action was an enemy's (`friendly=False` throughout — the player gave no orders). Nothing
was ever disposed on either machine (`disposed=0`).

### 1. ✅ CONFIRMED — nothing carries enemy actions over the wire

`OrderPayload` travels client → host only. The host's `CaptureLocalOrders`
(`CombatGameBridge.cs:74`) skips `action.AIAction` outright, and there is no message type for a plan
the client does not own. This was always certain — it is our own code — and the client's flat zero is
what it looks like from the other end.

### 2. ✅ CONFIRMED, AND STRONGER THAN EXPECTED — a client's AI planner never runs at all

`plan=NONE` on every sample. That is not "the planner ran once and stopped"; it means the
`AIPlanningRequest` component **was never created**, so `CombatAIBehaviorInvokeSystem.Execute`
(`:1546`) returned at its first line every frame of the fight.

The per-turn trigger is `CombatScenarioConditionsSystem` (`:26`), a reactive system collecting
`CombatMatcher.Simulating.Removed()`. A client never gains `Simulating` — established in M8, and
visible again here: the client reads `simTime=0.00 predTime=0.00` while the host runs 0 → 5 → 10.

The only other trigger reachable on a client is `DataManagerSave.cs:3150`, inside a
`Co.DelayFrames(3)` on load and only for units whose saved `aiDestination` is non-zero. **It did not
fire on this run.**

### 3. ❌ REFUTED — ~~the client's own AI plans a divergent enemy turn~~

~~`CombatAIBehaviorInvokeSystem.Execute` is gated on `!Simulating`, which is *open* on a client, so
its behaviour trees really do run against its own navigation state and tag their output `AIAction`.~~

The gate is indeed open, and that is exactly why the reading was seductive. But the system also
requires `hasAIPlanningRequest`, and nothing on a client ever creates one. **An open gate on a road
nobody drives down.** There is no divergent client plan because there is no client plan.

This mattered: it is the difference between "the client shows the wrong enemy moves" and "the client
shows no enemy moves", which look identical in a log and completely different on screen. That
distinction is the entire reason the probe was written rather than the recon being trusted.

### 4. ❌ REFUTED AS THE MECHANISM — ~~the save carries the host's plan untagged~~

~~`DataManagerSave` restores in-flight actions (`:3287` onward) and never sets `AIAction` — the
string does not appear anywhere in that file — so the host's real plan reaches the client through the
save and is indistinguishable from the player's own orders.~~

The restore path really does omit the tag (that half of the read holds — grep it and see). But the
client had **zero** actions immediately after following the host into the fight, so on this run the
plan did not arrive by save either.

**Left open, deliberately, because the probe cannot separate them:** whether the host's save simply
did not contain the enemy plan yet, or contained it and the restore dropped it. Timing makes the
first plausible — `pbj.combat-ship` wrote the fight 25.3 s after combat entry, and the host's AI
plans on the `Simulating.Removed()` edge, so the write may well have preceded the plan. Deciding this
needs a probe on the save's own action list, not on the ECS.

### 5. ❌ REFUTED AS THE MECHANISM — ~~`ClearLocalOrders` disposes the host's plan~~

~~`CombatGameBridge.ClearLocalOrders` (`:879`) disposes every action except `AIAction`-tagged ones,
so the first correction throws away the untagged enemy plan the save delivered.~~

`disposed=0` on the client, every turn. It cannot dispose actions that were never there. The rule
itself is unchanged and still correct for what it was written for; it is simply not what is causing
this.

## 🐛 Two further findings the probe turned up, neither of them what it was looking for

### The `AIAction` tag is NOT universal — one enemy action arrived untagged on the host

Turn 1, host: `cm_step_…_vhc_mech_w3_lr_marksman dash ai=False friendly=False t=8.33 d=1.00 path=10`.
Thirteen of fourteen actions were tagged; the `dash` was not.

`CombatAIBehaviorInvokeSystem` tags at `:1135`, `:1159` and `:1393`, so most AI output carries it —
but evidently not every creation path does. **This matters because two of our own rules key on that
tag**: `CaptureLocalOrders` excludes it and `ClearLocalOrders` spares it. On a host neither is
harmful. On a client that *did* hold enemy actions, an untagged one would be submitted to the host as
the player's own order — **which is exactly the shape of stage 2's 13 rejected orders**
(`stage2-run-2026-08-02.md`). So that observation is still unexplained by anything measured here, and
this is the most likely explanation of it.

Worth deciding regardless of what happens to previews: **`ClearLocalOrders` and `CaptureLocalOrders`
should probably filter by ownership rather than by the `AIAction` tag.** Ownership is a fact about
the session; the tag is a fact about which game code path happened to create the entity, and it is
demonstrably not applied everywhere.

### A client's `combat.currentTurn` never advances

`turn=0` on the client at every sample while the host went 0 → 1 → 2, even though the *session's*
turn tracked correctly (`ClientSession` reported turn 2). So the ECS combat turn counter and the
session turn counter disagree on a client for the whole fight.

Nothing observed depends on it yet, and M8's pose playback is keyed on message labels rather than on
`combat.currentTurn` precisely because message labels were known to be the trustworthy reference.
But it is a divergence nobody had recorded, and anything that later reads `combat.currentTurn` on a
client will be wrong.

## What a fix costs, now that the shape is known

The absence answer is *easier* to fix than the divergence answer would have been — there is nothing
to reconcile, only something to send.

- Enemy actions become a wire type broadcast with the turn. They are plain data: blueprint key, start
  time, duration, target, movement path. The path is the heavy field; the same slicing and capping
  arguments as M8's pose tracks apply, and the measured volume here is small (10–14 actions a turn,
  most with no path at all).
- The client applies them tagged `AIAction`. **`OrderMapper` already knows how to rebuild an action
  from a payload** (`:241` writes movement paths), and it is a deliberate transcription of the game's
  own load path, so the apply half may be largely built already.
- **No need to suppress the client's own planner** — mechanism 3's refutation removes that whole
  requirement. It never runs.
- The client's frozen `combat.currentTurn` may matter here, since action start times are absolute
  simulation times and the client's clock is stopped at 0. Decide it explicitly rather than
  discovering it.

## Related

- `docs/notes/replay-handoff-recon.md` — the same `Simulating` flag, the same shape of client-only
  divergence.
- `docs/notes/stage2-run-2026-08-02.md` — the 13 rejected orders, still not explained by anything
  measured here.
