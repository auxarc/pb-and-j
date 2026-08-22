# The 1.0 rig runbook — R0 and R1, command by command

Written 2026-08-21 by the rig-instrumentation lane, against `main` = `1708a0b` plus this lane's
probes. It is the executable half of §5 of `docs/design/road-to-1-0.md`: that section says which
readings the comprehensive run takes, this one says which keys to press, what the reply should look
like, and — for every single reading — **what a zero would mean and how to tell the cases apart.**

Results are appended to §9 the same day they are taken, numbers inline. Never to a scratchpad: three
plans have already been lost that way.

---

## 0. How to read this file, and the one rule it is built on

Every reading below carries a **`ZERO:`** line. It exists because this project has now recorded
twenty-four occasions on which an instrument could not have detected the thing it was watching for,
and the distilled law is short:

> **The vacuity guard goes on the INSTRUMENT, never on the input.** A probe whose zero and whose
> "I was pointed at the wrong thing" print the same characters is not a probe.

So: **do not record a number from this file without reading its `ZERO:` line first.** A `0` that
turns out to mean "the command never ran" costs a whole two-instance booking.

Two habits that go with it, both paid for here already:

- **Read the exit code AND the tail.** `make dist` has been banked as a pass by grepping its output
  for success words while the run exited 2.
- **A grep returning zero is a claim about your pattern.** Before believing that a log line is
  absent, make the same grep bite on a line you know is there.

---

## 1. Instrument inventory

### 1.1 Shipping today (this lane, `main`, or earlier)

| command | reads | what a ZERO means |
|---|---|---|
| `pbj.debrief-probe` | debriefing state + three fingerprints (screen salvage groups, scenario `savedOutput`, inventory) | `present=False` = the overworld scene never loaded — nothing else on the line was read. `present=True entered=False stage=Summary` = the debriefing was never opened. `present=True entered=False stage=Salvage` = **the commit already happened** and the view has let go; read the inventory fingerprint, not the screen. `screenGroups=0` with `entered=True` = the only real zero. |
| `pbj.combat-edge` | the combat edge `PbjRuntime` actually observed | it prints its own verdict. `frames=0` = never armed or the `Heartbeat.Update` postfix never applied; `runtime=null` = no session; `stopped=True` = `Pump` returns on line one; `tickMoves=0` = the pump did not run across the window, so the edge **cannot** have fired and its zero is not evidence; `tickMoves>0` with `lastInCombat` unchanged = the real negative answer. |
| `pbj.combat-edge-watch <s>` | arms the above | it **refuses to arm** and names any of the five private members it cannot resolve, rather than arming and printing zeros. |
| `pbj.ow-probe` | overworld snapshot incl. the nightfall chain | prints `<absent>` / `<null>` per field rather than a bare number; `UtilityCameraLinker.ins: <null>` is stated as "nothing can be toggling shadows". |
| `pbj.ow-sample <s>` | the overworld clock, 1 Hz, unscaled | refuses without a coroutine host and says so (`Heartbeat.Start postfix never ran?`). |
| `pbj.ow-watch` | toggles the `EnterCombat` / movement / time-scale watches | **must be toggled ON before the thing you want to watch happens.** Silence afterwards is only evidence if the toggle line was seen. |
| `pbj.mg-probe` | debriefing raw fields + a per-item salvage dump | see the sweep note in §7.1 — one of its messages is ambiguous. |
| `pbj.mg-serials` | part/subsystem serial fingerprints (count/min/max/sum) | prints `none` rather than zeros when a list is empty. |
| `pbj.vfx-probe` | replay asset volume, incl. `presimulated` | refuses outside combat outright (`not in combat`), and `presimulated` is printed beside `particleBlocks`, which is its denominator. `presimulated=0 particleBlocks=0` = nothing was scanned; `presimulated=0 particleBlocks=N` = a real zero. |
| `pbj.destruct-probe` | wreck content, frame integrity, pose freeze | every column is stated to be content-or-defect in its own doc block. |
| `pbj.drive-state` | game state, patch count, session summary | `session=` carries role and `pbj.net-status`'s string. |
| `pbj.net-status` | role, session state, **turn**, participants, ready | `no session` is a distinct string, not an empty one. |

### 1.2 Arriving with another lane — do not wait for them to write this file

| command | lane | note |
|---|---|---|
| `pbj.checkpoint-stat` | **L1**, inside `CheckpointGlue.cs` | attempts / refusals-by-reason / writes / last-ms / last-turn. This runbook references it; it does **not** ship it. |
| `pbj checkpoint: turn=N ms=X writes=K` log line | **L1** | the per-turn stall line. |
| client applied-wrecked / tracker counters | **L3** | reading 6's numbers. |

---

## 2. 🔴 Two blockers R0 depends on that nobody owns yet

Found while writing this file, by reading the glue R0's reading 2 names. **Both belonged in L1's
PR-C** and neither was in its checklist.

✅ **BOTH CLOSED 2026-08-21 — absorbed into PR #51 before it merged.** `pbj.checkpoint-load` exists
(`CheckpointGlue.CheckpointLoad`, registered in `NetGlue.Commands.cs`), and `CheckpointGlue.Write`
now arms the restore diff through `SaveLoadGlue.ArmRestoreDiff`. **R0 reading 2 is takeable.** The
two blockers are kept below as written, because the shape is what recurs: both instruments were
built, reviewed and shipped green, and neither could have produced the reading it existed for. A
gate that does not cover `src/PBAndJ.Mod` cannot see this class of defect.

⚠️ **One correction to B-2's prescription, made during the fix:** the capture is keyed **by slot**,
not written into the single shared `beforeSave` field. One field with two writers would have made
`pbj.combat-save` → commit a turn → `pbj.combat-load` diff the *scenario* slot's restored actions
against the *checkpoint's* capture, printing a confident `DIFF` that means nothing — breaking M3a's
probe while adding M12c's. Each load is now diffed against its own capture, or against nothing.

**B-1 — `pbj.combat-load` cannot be pointed anywhere.** `SaveLoadGlue.CombatLoad()` takes no
argument and loads the constant `SaveName`, which is `LobbySaveNames.ScenarioSlot`
(`src/PBAndJ.Mod/SaveLoadGlue.cs:20-24, :44-48`) — **not** the checkpoint slot. The roadmap's
"via `pbj.combat-load`'s member, pointed at the slot" describes work, not a command.
⇒ *PR-C needs either a slot argument on `pbj.combat-load` or its own
`pbj.checkpoint-load`.* Cheapest is the latter, inside `CheckpointGlue`, next to the writer.

**B-2 — the action diff is armed only by `pbj.combat-save`.** `SaveLoadGlue.beforeSave` is set by
`CombatSave()` and by nothing else; `OnCombatRestored` prints
`(no pre-save capture this session — diff skipped)` when it is null (`:50-62`). A checkpoint written
by `CheckpointGlue.DoSave` will not have armed it, so **the reading "the action diff reports the
planned set intact" produces no diff at all.**
⇒ *PR-C's checkpoint write must also capture `ActionDumpGlue.BuildSnapshots()` into that field,* or
R0 reading 2 has to be re-specified as "save with `pbj.combat-save`, load the same slot" — which
measures the M3a round trip again and says nothing about the checkpoint.

⚠️ Note the shape of B-2: the roadmap's zero-meaning for that reading says *"the glue prints both
counts; read them, not the diff's silence"*. It does print both counts — `before N | after M |
MATCH` (`src/PBAndJ.Core/SnapshotDiff.cs:45-47`) — **when it prints at all.** The unexamined case was
the diff not running.

---

## 3. R0 — the mini reading

**Precondition:** PR #51 deployed (B-1 and B-2 ship inside it). Mostly one instance.
**Do not start it without `pbj.checkpoint-stat` existing** — reading 1's zero is unreadable without
its refusals-by-reason.

### R0 setup

```
make deploy                       # exit 0, with BOTH instances closed
tools/game-instance.sh 2
tools/drive.sh 2 "pbj.net-status" # TIMEOUT here = unreachable instance, not a failed reading
```
Start a campaign, host a session (`pbj.host`), enter a fight, reach planning phase.

### R0·1 — the checkpoint stall (design q6; decides N)

```
tools/drive.sh 2 "pbj.execute"        # repeat for several turns
grep 'pbj checkpoint: turn=' ~/…/Player.log
tools/drive.sh 2 "pbj.checkpoint-stat"
ls -1 <save folder> | wc -l           # the census, recorded in the SAME breath as the ms
```
**Record:** `turn`, `ms`, `writes`, and the save count. The cost scales with lifetime save count —
`RefreshSaveHeaders` re-parses every save's metadata — so **milliseconds without the census is not a
measurement**, it is a number from an unknown machine.

`ZERO:` `ms=0` with `writes>0` = the stopwatch wrapped nothing, which is suspicious because `DoSave`
zips. `writes=0` = the path never ran: read `pbj.checkpoint-stat`'s refusals-by-reason, and if
`attempts=0` the effect never reached the bridge at all — check for the NetLog `OrdersApplied` line
on the same turn before concluding anything about saving.

### R0·2+3 — checkpoint→Execute and the re-entry edge, **which are one load, not two**

The roadmap lists these as readings 2 and 3. They are two readings of **the same reload** and taking
the load twice would answer 2 against a different session state than 3. Do it once:

```
tools/drive.sh 2 "pbj.combat-edge-watch 180"   # ARM FIRST. Prints 'armed ... counters cleared'.
tools/drive.sh 2 "pbj.checkpoint-load"          # real as of PR #51; the load blocks the main thread
tools/drive.sh 2 "pbj.combat-edge"
tools/drive.sh 2 "pbj.net-status"               # is Execute pressable, at what turn
grep 'save/load diff' ~/…/Player.log            # per B-2
```

**Reading 2 — did the plan survive?** `before N | after M | MATCH`.
`ZERO:` an empty diff means either no actions survived **or** the diff never ran. Those print
differently: no-diff-at-all says `(no pre-save capture this session — diff skipped)`. If you see
that line, B-2 is unfixed and reading 2 was not taken.

**Reading 3 — did the edge fire?** `pbj.combat-edge` prints its own `VERDICT:`.
`ZERO:` there are five ways to get `enters=0 exits=0` and only the last is the answer — never armed
(`frames=0`, and the watch line was never seen), postfix not applied (`frames=0` while armed), no
session (`runtime=null`), pump dead (`tickMoves=0`, check `stopped=`), or the real one: `tickMoves>0`
and `lastInCombat` held true the whole time. `liveInCombat` beside it catches the sixth case, where
the bridge has already changed and the runtime has not pumped since.

⚠️ **This reading is not passive and the run sheet does not say so.** On a host the exit edge runs
`HostSession.HandleCombatExited` — state to Lobby, assignments dropped, barrier to −1,
`CombatEndMessage` broadcast, lobby advanced — and the enter edge runs `HandleCombatEntered`, which
raises `ShipCombatEffect` and writes a fresh scenario save
(`src/PBAndJ.Core/Net/HostSession.CombatEntry.cs:38-54, :214-246`). **Taking reading 3 ends and
restarts the session's idea of the fight.** On R0's single instance that costs nothing. On R1 it is
why it goes last.

---

## 4. R1 — the comprehensive run

**Precondition:** L1 complete, L3's build merged, this lane merged, R0 read.

### 4.1 Pre-flight, in order

- [ ] `make dist` → **exit 0**, and read the tail.
- [ ] `make deploy` → exit 0, **with both instances closed** (deploy `rm -rf`s a mod folder whose
      DLL a running instance holds open).
- [ ] Steam client running; the game **not** launched from Steam — a Steam-launched instance cannot
      be driven, and two script instances is the hard ceiling the script enforces.
- [ ] `tools/game-instance.sh 2` (host), then `tools/game-instance.sh 3` (client).
- [ ] `tools/window-arrange.sh` — without it both windows land on top of each other; that cost a
      playtest once.
- [ ] `tools/drive.sh 2 "pbj.net-status"` and the same on 3. **A TIMEOUT is an unreachable instance,
      not a failed reading** — fix it before continuing.
- [ ] Host at a clean overworld with **no debrief view pending**. PB has no view stack: a pending
      screen re-drops onto the next battle.
- [ ] Session up; both machines in one fight via the shipped M12b·2 path.
- [ ] Both HUDs woken with `pbj.select-unit <index>` on each — otherwise `pbj.execute` refuses in a
      way indistinguishable from a stalled barrier.
- [ ] Combat exit is planned as **host victory via `cm.kill-enemy`**. ⚠️ If the M17 stage 2
      `EndCombatWithOutcome` prefix has shipped, the client's console escape hatch is dead and host
      victory is the *only* exit.
      ⚠️ **CORRECTED 2026-08-21:** this step named `cm.end-combat-*`, **which does not exist**
      (pattern proven non-vacuous against 40+ real commands in `ConsoleCommandsCombat.cs`). The real
      commands are **`cm.force-victory` / `cm.force-defeat`** (`ConsoleCommandsCombat.cs:71`, `:80`),
      and `m17-stage2-plan.md` §4.2 replaces them with `pbj.force-end <victory|defeat>` carrying a
      `bypassOnce` flag once the prefix ships. An operator following the old text at 1am would have
      typed a command that does not exist and read the silence as a failed hatch.

### 4.2 Phase 1 — overworld, before the fight

**R1·1 — overworld divergence** *(closes recon measurement 2)*
```
tools/drive.sh 2 "pbj.ow-probe" ; tools/drive.sh 3 "pbj.ow-probe"
tools/drive.sh 2 "pbj.ow-sample 30" ; tools/drive.sh 3 "pbj.ow-sample 30"
```
Both idle, then again while the host drives. Compare the printed fields.
`ZERO:` identical fingerprints = PASS. An **empty sample row** is not agreement — it is the wrong
game state, and the probe prints the state field. Read the state, not the emptiness.

**R1·3 — arm the entry watch NOW** *(closes recon measurement 6; it is listed third but it is a
phase-1 action)*
```
tools/drive.sh 2 "pbj.ow-watch" ; tools/drive.sh 3 "pbj.ow-watch"   # expect 'control watch ON' on both
```
🔴 **Arming after combat entry measures nothing.** The two "ON" replies are the receipt that the
reading is live; without them, silence later is silence about the toggle.
`ZERO:` no watch line on the client = the client never took the entry path, which is the expected
result — **the host's line is the positive control**. If BOTH are silent the patch never applied and
neither result means anything.

**R1·2 — loadout-change writes** *(closes recon measurement 5)*
```
tools/drive.sh 2 "pbj.mg-serials"      # before
… drive the loadout change (pbj.mg-select / pbj.mg-select-unit / pbj.mg-advance) …
tools/drive.sh 2 "pbj.mg-serials"      # after
```
`ZERO:` no delta = either the change wrote nothing **or** the change never applied. The drive
command's own return value says whether the click landed — read it, not the serial delta alone.

### 4.3 Phase 2 — during the fight

**R1·5 — turn digests** *(continuous canary, running from here to the end)*
On the client, each turn logs `turn N digest X OK`; a mismatch logs
`turn N DIVERGED | host X | local Y` (`src/PBAndJ.Core/Net/NetLog.TurnCycle.cs:138-149`).
`ZERO:` **an absent digest line is not agreement.** Count `grep -c 'digest .* OK'` against the turn
count and make the grep bite on a line you can see before believing a low number.

**R1·4 — checkpoint cadence and stall under two-machine load** *(confirms R0·1)*
```
tools/drive.sh 2 "pbj.checkpoint-stat"   # after several executed turns
```
`ZERO:` `attempts=0` = the effect is not reaching the bridge (see R0·1). `refusals>0` names the
refusal — record which one.

**R1·6 — wrecked units on the client** *(M17 stage 2 acceptance)*
```
tools/drive.sh 2 "pbj.destruct-probe"    # host wrecks
tools/drive.sh 3 "pbj.destruct-probe"    # client applied + tracker counts (L3's counters)
tools/drive.sh 3 "pbj.drive-state"
```
`ZERO:` `applied=0` with host `wrecked>0` = the apply path is dead. The two counts print side by
side; a client's own ECS reads zero for `wrecked` by design, so read L3's *applied* counter, not the
ECS column.

**R1·7 — 🧑 client corpse stays collapsed through planning**
**Human reading, agent cannot take it.** *Exactly what to do:* on instance 3, after the host's kill
lands, watch the wrecked unit through the whole of the next planning phase and the execution after
it. *Expected:* it collapses and stays collapsed. *The ambiguity to watch for:* stage 1's defect was
that the wreck **stood back up** a moment later, so a glance at the instant of death is not the
reading — the reading is the seconds after it. Record "stayed down" / "stood up at approximately
T+Ns", with the counters from R1·6 quoted beside it.

**R1·8 — `presimulated`** *(advanced-particle-blocks decision — see §5, this does NOT close in one run)*
```
tools/drive.sh 2 "pbj.vfx-probe"     # HOST, immediately after executing a turn
```
`ZERO:` on a **client** every column reads zero because recording is gated on `recordingAllowed` —
that zero is about the machine, not the content. On the host, `presimulated=0` means something only
when `particleBlocks` beside it is non-zero; `0` with `particleBlocks=0` is "nothing was scanned".
Outside combat the command refuses outright, so that case cannot be mistaken for a zero.

### 4.4 Phase 3 — victory and the debriefing

Kill the last enemy on the host (`cm.kill-enemy`). The host takes victory and the debriefing opens.

**R1·9 — debriefing fingerprints** *(design q7, host half only)*
```
tools/drive.sh 2 "pbj.debrief-probe"     # BEFORE the commit, while the salvage screen is up
tools/drive.sh 2 "pbj.mg-probe"          # the per-item detail, if the fingerprints later disagree
tools/drive.sh 2 "pbj.mg-confirm yes"    # or press Finish by hand — ⚠️ this COMMITS
tools/drive.sh 2 "pbj.debrief-probe"     # AFTER
tools/drive.sh 3 "pbj.debrief-probe"     # the client, for the record: expect entered=False
```
Record the four numbers from each headline: `screenGroups/items/fp`, `rewardGroups/withSavedOutput/
savedFp`, `invParts/invSubsystems/invFp`.

`ZERO:` first check `fpCanary=32D6068F/811C9DC5 (expect 32D6068F/811C9DC5)`. If those disagree, the
fingerprint function itself has changed and **every hash on the line is meaningless** — stop.
`present=False` = the overworld scene never loaded. `entered=False` **before** the commit = wrong
moment. `entered=False` **after** the commit is correct and expected — `SalvageFinish` calls
`TryExit` as its second act, so the post-commit reading is the **inventory** fingerprint, not the
screen. `screenGroups=0` with `entered=True` is the one real zero. A fingerprint is never printed for
an empty collection: you get `none (0 items)`, because `811C9DC5` is what hashing nothing looks like
and two machines that both collected nothing would otherwise "agree".

### 4.5 Phase 4 — resume, last, because it destroys the fight

The debriefing must be finished (committed or skipped) before this: PB has no view stack and a
pending screen re-drops onto the next battle.

**R1·10 — checkpoint resume, two machines** *(M12c stage D acceptance)*
```
tools/drive.sh 2 "pbj.combat-edge-watch 300"    # ARM FIRST
tools/drive.sh 2 "pbj.checkpoint-load"          # real as of PR #51
tools/drive.sh 2 "pbj.combat-edge"
tools/drive.sh 2 "pbj.net-status" ; tools/drive.sh 3 "pbj.net-status"
grep -E 'shipping|offered|combat started' ~/…/Player.log
```
Both `pbj.net-status` lines carry role, state and **turn** — that is the "same turn" half of the
acceptance. ⚠️ The roadmap names `pbj.status` here; **no such command exists.** It is
`pbj.net-status` (`src/PBAndJ.Mod/Net/NetGlue.Session.cs:22-34`).

`ZERO:` the client not being re-offered means the edge never fired, and `pbj.combat-edge` separates
the causes: pump-dead (`tickMoves=0`) from level-stuck (`tickMoves>0`, `lastInCombat` unchanged) from
no-session (`runtime=null`) from never-armed (`frames=0`).

**R1·10b — 🔴 THE q9 READING: can a client open a debriefing at all?** *(decides M12d stage D0;
user's call, 2026-08-21: measure rather than choose)*

⚠️ **THIS READING MUST BE TAKEN BEFORE M17 STAGE 2 MERGES (window W2).** The stage 2 prefix on
`ScenarioUtility.EndCombatWithOutcome` closes the only route this reading uses, and the
`pbj.force-end … bypassOnce` hatch that would re-open it **ships with that same PR**. Take it now,
on today's `main`, or it costs a bypass that does not exist yet.

⭐ **No new code is needed — the sketch in the decision was wrong about that.** `cm.force-victory`
calls `ScenarioUtility.EndCombatWithOutcome(CombatOutcome.Victory, early: true)` **directly**
(`decompiled/PhantomBrigade.DebugConsole/ConsoleCommandsCombat.cs:71-79`), so it does **not** pass
through the `if (flag3)` bit-4 gate at `CombatScenarioStateSystem.cs:229` that makes a client unable
to resolve its own combat on the ordinary path. Its only guard is
`CombatStateCheck()` → `IDUtility.IsGameState("combat")` (`:31-39`), which a client in a shipped
fight satisfies. Verified in the decompile 2026-08-21.

⚠️ **Destructive on the client** — it ends that machine's fight. Take it LAST, after R1·10, and
expect to restart the session afterwards.

```
tools/drive.sh 3 "pbj.debrief-probe"            # BEFORE: baseline, expect present=False
tools/drive.sh 3 "cm.force-victory"             # on the CLIENT, not the host
tools/drive.sh 3 "pbj.debrief-probe"            # AFTER: the whole reading
tools/drive.sh 3 "pbj.net-status"
```

**What the answer means for M12d:**

- `present=True entered=True` — the debriefing opened over the client's combat scene. **Shape A is
  live**: drive the client into its own vanilla debriefing off a relayed outcome. M12d keeps the
  game's salvage UX.
- `present=True entered=False` — the view exists but will not take the client. **Shape B**: the host
  becomes sole committer and the salvage UX is rebuilt mod-side.
- `present=False` — the overworld scene never loaded at all. **Shape B**, decided harder.

`ZERO:` a `present=False` on the AFTER line is a real answer, **not** a failed probe — but only if
the BEFORE line was also taken, which is why it is in the block. If `cm.force-victory` prints
`"Command only available from combat"` the client was not in the fight and **nothing was measured**;
that is the one outcome that must not be read as Shape B.

**R1·11 — 🧑 the M10 Leave-button swap**
**Human reading.** *Exactly what to do:* with the session still up and **not in a fight**, click
Multiplayer on the main menu and look at the buttons. *Expected:* Host/Join have been replaced by
Leave. *The ambiguity to watch for:* the game never closes or restores a mod screen — every route
out of one is its own patch — so if the screen opens and will not close, that is a known property of
this UI and not a new defect; note it and quit the instance. ⚠️ **Do not take this reading while a
fight is live.** It is a non-gating ride-along and is not worth risking the rest of the run.

---

## 5. What one R1 closes, and what it cannot

**Closes (agent-readable): 7 of the 11.** R1·1, ·2, ·3, ·4, ·5, ·6, ·10 — every one has a command,
an expected shape, and a stated zero.

**Closes with a human at the keyboard: 2 more.** R1·7 and R1·11 are eyes-and-clicks. They are
specified above to the point where the human's job is to read one sentence and answer it.

**Does NOT close, and the reasons are structural, not effort:**

| # | reading | why not |
|---|---|---|
| 8 | `presimulated` | the criterion is "**0 across ≥3 varied fights**". One R1 has one fight, because each victory opens a debriefing that has to be committed before the next fight (no view stack) and each fight overwrites the checkpoint slot. R1 contributes **fight 1 of ≥3**; §9 carries the tally and the decision waits for it. Ratcheting three fights into this run would push readings 9 and 10 past the point where anyone is still reading carefully. |
| 9 | `FinishDebriefing` determinism | the **two-machine** half needs M12d machinery that does not exist. R1 fingerprints the host's commit and nothing else, which the roadmap already says. ⚠️ And the method it is named after **does not exist** — see §6.4. |

**Explicitly not in this run, and not owed one:** the nightfall chain (measured 2026-08-15; it owed
a transcription, which §7.2 of this file discharges, not a re-run); cross-session pose-digest
comparison (invalid by construction — the sim is not deterministic across sessions).

---

## 6. Refutations of §5 as written

Recorded here rather than fixed in place, because `docs/design/road-to-1-0.md` is a shared file and
this lane does not own it.

**6.1 R0's readings 2 and 3 are one load, not two.** Reading 2 loads the checkpoint; reading 3 asks
what the edge did "across the reload". Taking the load twice answers reading 2 against a session
state that reading 3 has already changed. §3 above merges them.

**6.2 Reading 3 is destructive, and R1·10 inherits that.** Observing the edge on a host runs
`HandleCombatExited` (session → Lobby, assignments dropped, barrier → −1, `CombatEndMessage`
broadcast) and then `HandleCombatEntered` (`ShipCombatEffect`, a fresh scenario save). The run sheet
describes it as a reading. It is a state change that also produces a reading, which is why it is last
in both R0 and R1.

**6.3 Reading 9's zero-meaning is right before the commit and wrong after it.** "entered=False =
wrong moment" holds for the pre-commit reading. `SalvageFinish` calls `TryExit` as its second act, so
`entered` is **false by the time the commit returns** — the post-commit reading has to come from the
inventory, and `entered=False` there is correct. `pbj.debrief-probe` prints the stage on the same
line so the two are never one reading.

**6.4 `FinishDebriefing` does not exist.** Design question 7 and §L2's B5 both name it.
`grep -rn FinishDebriefing decompiled/` returns **zero** across the whole decompile — and the pattern
is fine, since `grep -rn SalvageFinish decompiled/` finds the real thing. The commit is the private
`SalvageFinish()`, driven from outside through the public static `OnStageNextExternal()`.
`ManagementProbeGlue.cs:49-51` already said so; the roadmap inherited the wrong name anyway.

**6.5 D1's "per-group savedOutput fingerprint" describes a list that does not exist.** Two different
things are called groups: the screen's `salvageGroups` (one per unit, keyed by `unitPersistentID`,
**carrying no savedOutput at all**) and the scenario's `rewardGroupsCollapsed` (keyed by reward key,
where `savedOutput` actually lives). `pbj.debrief-probe` prints both, separately labelled. A reading
that quoted one count as the other would compare two machines on the wrong axis and could agree by
accident.

**6.6 `pbj.status` (reading 10) does not exist.** The command is `pbj.net-status`.

**6.7 R1·3's instrument has to be armed in phase 1, not read in phase 3.** `pbj.ow-watch` is a
toggle; the roadmap lists it third among readings taken in order, which reads as "do it third".
Arming it after combat entry measures nothing at all.

**6.8 A probe that would have been vacuous, and was not built.** The first design for the combat-edge
counter re-evaluated `IsGameState("combat") && combat.hasCurrentTurn` in the sampler. That is a
*copy* of `CombatGameBridge.InCombat`'s rule, not the rule — it agrees until somebody edits the
bridge, after which the probe reports confidently on a predicate the session no longer uses, and
nothing can tell that from a working probe. The shipped version reads `PbjRuntime.lastInCombat`,
which is the value the edge is computed from, and prints the live bridge value beside it.

**6.9 The pump-liveness signal is not a second copy either.** "Did the pump run across the load" is
answered from `PbjRuntime.lastTickSeconds`, which `ObserveTick` advances up to four times a second
from inside `Pump`. A frame counter would have answered "did Unity run", which is a different
question and is always yes.

---

## 7. Sweep conditions and doc debt

### 7.1 The zero-prints-its-hypothesis sweep (roadmap D3) — read, not edited

The existing run-inventory probes were swept by reading them. `ManagementProbeGlue.cs` and
`VfxProbeGlue.cs` are outside this lane's file ownership and are both on the modularization queue, so
the findings are recorded here with their fix rather than applied:

- ✅ **`pbj.vfx-probe` passes.** It refuses outside combat, and `presimulated` sits beside
  `particleBlocks`, which is its scanned total (`VfxProbeGlue.cs:345-350, :428-478`). The roadmap's
  "confirm a total-scanned figure sits beside it" is confirmed. The residual case it does not name:
  on a **client** every column is zero because recording is gated on `recordingAllowed` — recorded in
  §4.3 R1·8 instead.
- ✅ **`pbj.ow-probe`'s nightfall section passes.** Every link prints `<absent>` / `<null>` with a
  sentence, including `UtilityCameraLinker.ins: <null> — nothing can be toggling shadows`.
- 🔧 **`pbj.mg-probe` has one ambiguous message.** `ProbeSalvageEntries` prints
  `no salvageGroups on the view — open a debriefing first` for **two different causes**: the
  singleton being null, and the field lookup failing. It cannot fire for "the screen is empty",
  because `salvageGroups` is initialised at its declaration (`CIViewOverworldDebriefing.cs:427`) and
  is never null once the view exists. *Fix, one line:* split the branch — `ins is NULL` (the
  overworld scene never loaded) from `<no such field>` (this build's view is not the one the probe
  was written against). **Unclaimed; not this lane's file.**

### 7.2 The nightfall transcription — **discharged**

`docs/notes/overworld-recon.md` owed a transcription of the nightfall chain with its numbers, as
half of `OverworldProbeGlue`'s own exit condition. It is now written there, in a section titled
*"The nightfall chain, transcribed"*, from the code and the decompile rather than from the old prose.

⚠️ **State the TEST, not the observation.** The previous exit condition was cited as *"`grep -i
'nightfall|shadow'` over this file returns nothing"* — which came true by being written down, and now
returns five hits, every one of them the paragraph saying it returns none. The condition is therefore
restated as a question about content: *are the chain and its numbers transcribed?* That question has
a stable answer whatever any grep does.

### 7.3 Sweep conditions for the two probes this lane adds

Written as tests, and dated, per the rot recorded in `ModEntry.cs`'s older comments — a condition
phrased as "delete this when X" becomes an instruction to delete the moment X is true and nobody
re-reads it.

- **`pbj.debrief-probe`** — sweep when §9 of this file records R1·9's before-and-after fingerprints
  **and** M12d's two-machine determinism comparison has been taken. *Not* when the M12d design doc
  mentions the debriefing.
- **`pbj.combat-edge` / `pbj.combat-edge-watch`** — sweep when §9 records R0·3's verdict **and** M12c
  stage D has shipped whichever shape that verdict selected. Until then it is the only instrument
  that can tell a dead pump from a stuck level.
- Neither is swept on a grep. Both are swept on a number being in §9.

---

## 8. The gate this lane ran

`make dist` at `1708a0b` + this lane: **exit 0**. 2076 tests (2067 + 9 new), 100% line/branch/method
on `PBAndJ.Core`. `wire surface OK (unchanged since 0.22.0)` — this lane is wire-neutral and that the
hash did not move is the proof, not the intent. `split grouping OK (15 families, 145 parts, 1541
members)`, unchanged: the new files join no split family.

One advisory, stated rather than hidden: `tools/size-report.py` flags
`DebriefProbeGlue.cs` as a **new file over 500 lines (550)**. It gates nothing, and it sits with its
four siblings — `ManagementProbeGlue` 827, `DestructProbeGlue` 619, `OverworldProbeGlue` 572,
`VfxProbeGlue` 514 — all of which are probe glue whose bulk is the reasoning. Trading that for fifty
lines is the wrong trade in this codebase, and the split queue is explicitly not on the path to 1.0.

The new tests were **seen to fail** before being believed. `PbjRuntime.lastInCombat` was renamed in
place, the mutation was confirmed present in the file (4 occurrences) rather than inferred from `sed`
exiting 0, and exactly the two cases that should fail did — 2 failed, 2074 passed. Reverted and
verified byte-identical.

---

## 9. Results

*(empty — R0 and R1 have not been run. Numbers go here the day they are taken, inline, with the
`ZERO:` case that was actually observed named beside each one.)*

### 9.1 R0

| reading | date | number | which zero-case, if zero |
|---|---|---|---|
| R0·1 checkpoint stall + save census | | | |
| R0·2 action diff across the checkpoint load | | | |
| R0·3 re-entry edge verdict | | | |

### 9.2 R1

| reading | date | number | which zero-case, if zero |
|---|---|---|---|
| R1·1 overworld divergence | | | |
| R1·2 loadout-change writes | | | |
| R1·3 concurrent edits + entry watch | | | |
| R1·4 checkpoint cadence under load | | | |
| R1·5 digest lines vs turn count | | | |
| R1·6 client applied-wrecked / tracker | | | |
| R1·7 🧑 corpse stays collapsed | | | |
| R1·8 `presimulated` (fight 1 of ≥3) | | | |
| R1·9 debriefing fingerprints, before/after | | | |
| R1·10 checkpoint resume, two machines | | | |
| R1·11 🧑 M10 Leave-button swap | | | |
