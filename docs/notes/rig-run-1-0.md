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

### 1.2 ~~Arriving with another lane — do not wait for them to write this file~~ ▸ ✅ **ALL SHIPPED**

🔴 **CORRECTED 2026-08-22.** This table's heading and its whole premise are dead: **every command
in it now ships on `main`.** L1 landed as **PR #51** (mod 0.23.0) and L3 — M17 stage 2 — landed as
**PR #60** (`main` = `d3a3b3b`, mod **0.24.0**, wire **v10**). Nothing below is future work; the
"arriving" column is kept only so a reader who remembers waiting for these can see they arrived.
**Deploy 0.24.0 and they are all present.**

| command | lane | status + note |
|---|---|---|
| `pbj.checkpoint-stat` | **L1** | ✅ **SHIPPED** (`src/PBAndJ.Mod/Net/CheckpointGlue.cs`, member `CheckpointStat`). attempts / refusals-by-reason / writes / last-ms / last-turn. **Driven for real in R0·1** — see §9.1. |
| the per-turn stall log line | **L1** | ✅ **SHIPPED** — 🔴 **and its literal here was WRONG. Sighting 27.** This row used to name it ~~`pbj checkpoint: turn=N ms=X writes=K`~~. The code emits **`[pb-and-j] checkpoint: turn=… ms=… writes=… diff-armed=…`** — `src/PBAndJ.Mod/Net/CheckpointGlue.cs`, member `Write`, `:197`. `pbj checkpoint` is **not a substring** of that, so the old pattern matches nothing, ever. **Grep `'checkpoint: turn='`.** |
| `pbj.wreck-patches` | **L3** | ✅ **SHIPPED with #60** (`src/PBAndJ.Mod/Net/DestructProbeGlue.cs`). Reading 6a. Whether the three stage-2 Harmony patches resolved **and applied** — the only instrument for a failure mode `make dist` cannot see, because `src/PBAndJ.Mod` is in `UNCOVERED_PROJECTS`. |
| `pbj.destruct-probe` `cascade:` / `wreckFlags:` columns | **L3** | ✅ **SHIPPED with #60.** Readings 6b and 6c. |
| `pbj.force-end <victory\|defeat>` | **L3** | ✅ **SHIPPED with #60** (`DestructProbeGlue.cs`, member `ForceEnd`, registered as `Add(nameof(ForceEnd), "pbj.force-end", typeof(string))`). Reading 6f — **and now also the route for R1·10b**, see §4.5. Prints which branch it took. ⚠️ It has never run in a game; the risk is named in §4.5, and its failure is loud by construction. |

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

🆕 **Four things that step actually does, learned by doing it 2026-08-22 (R0's run).** The line
above is one sentence; it was the most expensive sentence in this file.

1. **A synchronised single-party load settles in `basecrawler`, not `overworld`** — and it reads
   `overworld` *transiently* on the way. Poll until it stops moving; a state read taken too early
   is a different answer, not a wrong one.
2. **`ow.load-scenario` from the wrong state returns an EMPTY reply with `rc=0`.** ⚠️ This is a
   drive-channel **vacuity shape**, and it is the one that will fool you: an empty reply is *not*
   the same as a bad command key, and `rc=0` is not "it worked". Read the state first; treat an
   empty reply as "the command was refused silently" and never as a null result.
3. **Leaving the base raises a disengage dialog**, which blocks everything behind it until it is
   answered — drive `pbj.dialog-confirm`. A script that does not expect it hangs at a step that
   looks like a network stall.
4. **Budget `make deploy` at >10 minutes wall here** (pb-dev container first entry + the full
   coverage run). A first attempt on 2026-08-22 died at a 10-minute timeout mid-`make test`. Any
   timeout an executor sets must clear that, or the tooling reports a failure the build never had.

### R0·1 — the checkpoint stall (design q6; decides N)

```
tools/drive.sh 2 "pbj.execute"        # repeat for several turns
grep 'checkpoint: turn=' ~/…/Player.log
tools/drive.sh 2 "pbj.checkpoint-stat"
ls -1 <save folder> | wc -l           # the census, recorded in the SAME breath as the ms
```

🔴 **CORRECTED 2026-08-22 — SIGHTING 27, and it was found by running this block.** The grep above
used to read:

> `grep 'pbj checkpoint: turn=' ~/…/Player.log`

**That pattern returns 0 against a log holding five of the line.** The line the code emits is
`[pb-and-j] checkpoint: turn=0 ms=435.6241 writes=1 diff-armed=yes` —
`src/PBAndJ.Mod/Net/CheckpointGlue.cs`, **member `Write`**, `:197`, which concatenates
`"[pb-and-j] checkpoint: turn="`. The tag is `[pb-and-j]`; **`pbj checkpoint` is not a substring
of it.** An operator following this block verbatim would have read a *working* checkpoint as "the
path never ran", gone to `pbj.checkpoint-stat` for the explanation, and found `refusals 0` — which
explains nothing, because there was nothing to explain. **The zero was an artefact of the
instruction, not a property of the system.** Two sections above, §0 warns about exactly this.

⭐ **The same day's counter-example, and it is the fix's whole point.** `make split-selftest` names
its vacuity cases *in its own output*: `tools/split/selftest.py`, in `main()`, checks
`"REFUSES a tree with no families at all rather than recording an empty ledger"` (`:1093`) and
`"the family rule proves itself on a canary before any recording"` (`:1106`, asserting
`grouping.prove_discovery() is None`). **Those PASSes mean something because the failure modes are
CASES, not comments** — a harness that would have caught its own blindness. This runbook's R0·1
grep was the same project failing the same test, in the file that states the rule.
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
✅ **ALL FOUR MET as of 2026-08-22** — L1 = PR #51, this lane = PR #52, **L3's build = PR #60**,
R0 readings 1–3 taken (§9.1). **R1 is one sitting, taken AFTER W2**, per the user's ruling; the
"R1 must precede W2" text that used to sit in §4.5 is refuted there, with the derivation.
⚠️ **The GPU ladder does not gate this.** It gates *headless* two-instance work only. Two
**desktop** instances are proven for the entire life of the rig (M12–M17 verifications;
`gpu-wedge-forensics.md` §6, first paragraph), and R1 contains 🧑 readings (6d, 7, 11) anyway —
so **attended desktop R1 can be booked today**, ladder or no ladder.

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
- [ ] Combat exit is planned as **host victory via `cm.kill-enemy`**. ⚠️ ~~If~~ **CORRECTED
      2026-08-22 — the conditional resolved: the M17 stage 2 `EndCombatWithOutcome` prefix HAS
      shipped (PR #60, `main` = `d3a3b3b`, mod 0.24.0, wire v10).** So, in the present tense: the
      client's *vanilla* console escape hatch (`cm.force-victory` / `cm.force-defeat`) **is dead**,
      and the exits are host victory **or** `pbj.force-end <victory|defeat>`, which ships with the
      prefix and bypasses it deliberately (`src/PBAndJ.Mod/Net/DestructProbeGlue.cs`, member
      `ForceEnd`). Host victory is no longer the *only* exit — but `pbj.force-end` is destructive on
      the client, so it stays in phase 4 (§4.5).
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

⚠️ **CORRECTED 2026-08-21 (M17 stage 2 build).** This block used to be one reading and it carried
two claims stage 2 makes false. It said *"a client's own ECS reads zero for `wrecked` by design"* —
**stage 2's whole point is that it no longer does**, so the ECS column is now the reading rather
than a known-zero to look past. And the roadmap's row named "corpse not orderable/targetable" as an
acceptance property: **a client's corpse stays clickable**, because selection is a physics raycast
filtered by `InputCombatUnitSelectionUtility.IsSelectable`, which consults neither `IsUnitActive`
nor `isWrecked`. Do not record that as a defect.

**R1·6a — did the patches even apply?** *(take this FIRST; no fight needed)*
```
tools/drive.sh 3 "pbj.wreck-patches"
```
`ZERO:` `resolved=False` = the string-name attribute is wrong or the game moved the member.
`resolved=True owners=0` = the target is fine and **our patch class never applied**. 🔴 `PBAndJ.Mod`
is outside the 100% gate, so a dead Harmony patch builds green, deploys green and simply never
fires — this command is the only thing in the project that can tell you.

**R1·6b/6c — the cascade and the flag** *(after the host's kill lands)*
```
tools/drive.sh 2 "pbj.destruct-probe"    # host: wrecked=N
tools/drive.sh 3 "pbj.destruct-probe"    # client: cascade=..., wreckFlags=..., wrecked=N
```
`ZERO (6b):` `cascade: filtered=0 passed=0` = the `Filter` was never called at all — the patch is
dead, or nothing was wrecked. `filtered=0 passed>0` = the patch applied and the predicate was
false; read `suppressing=` on the same line before concluding anything.
`ZERO (6c):` `wreckFlags: set=0` with host `wrecked>0` = the apply path is dead. `refused>0` names
an exception in the log — quote it.
⚠️ `wrecksPlayed`, `pose: frozen` and `wreckFlags: set` are **three different questions**. A
wreck-visual counter moving is not evidence stage 2 ran; stage 1 already paid for that confusion.
⚠️ Do **not** take 6b or 6c during a `pbj.fx-hold` — the hold clamps the cursor so the window never
finishes, and these counters will legitimately stand still.

**R1·6d — 🧑 the enemy tracker corrects**
**Human reading, agent cannot take it.** *Exactly what to do:* on instance 3, after the host's kill
lands, look at the unit-tab row. *Expected:* the wrecked enemies are gone from it, and the count
matches the host's. *The ambiguity to watch for:* `set=N` says the ECS moved, not that the UI
redrew — this is the reading that tests the explicit `RedrawUnitTabs`, and it is the exact artefact
M16 photographed (host at VICTORY while the client still counted six live enemies).

**R1·6e — no modal dialog, no frozen debris**
Read the client `Player.log` for exceptions during combat, and look at a corpse's core for stuck
fragments. `ZERO:` a clean log is **not** evidence the cascade was suppressed — only that it did
not throw. 6b is that evidence. Read them together.

**R1·6f — the escape hatch**
```
tools/drive.sh 3 "pbj.force-end victory"
```
`ZERO:` there is none — the command names its branch: `BYPASSED`, `prefix was NOT ARMED (no live
client session)`, `NOT IN COMBAT`, `BAD ARGUMENT` or `THREW`. A silent return is impossible by
construction, which is the point. ⚠️ ~~Take R1·10b **before** this PR merges; the prefix closes the
route that reading uses.~~ 🔴 **CORRECTED 2026-08-22: that PR (#60) has merged, and the ordering it
asked for was never real.** R1·10b now uses **this same command** — see §4.5's R1·10b block.
🔴 **AND A REAL ORDERING HAZARD THIS CREATES — new 2026-08-22, flagged rather than smoothed over.**
6f as written sits in **phase 2 (during the fight)**, but `pbj.force-end victory` **ends the
client's combat**. Taken here it destroys the preconditions of every later client reading — 6d, 7,
9 and 10 included — and 10b in particular, which is *the same command on the same machine*.
⇒ **6f and R1·10b are one act, not two.** Take them together, in phase 4, last: probe → `force-end`
→ probe. 6f is the command's own return string; 10b is the `pbj.debrief-probe` pair around it. One
`force-end` invocation yields both readings. **Do not drive `pbj.force-end` in phase 2.**

**R1·7 — 🧑 client corpse stays collapsed through planning**
**Human reading, agent cannot take it.** *Exactly what to do:* on instance 3, after the host's kill
lands, watch the wrecked unit through the whole of the next planning phase and the execution after
it. *Expected:* it collapses and stays collapsed. *The ambiguity to watch for:* stage 1's defect was
that the wreck **stood back up** a moment later, so a glance at the instant of death is not the
reading — the reading is the seconds after it. Record "stayed down" / "stood up at approximately
T+Ns", with the counters from R1·6c quoted beside it.

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

🔴 **THE ORDERING WARNING THAT STOOD HERE IS REFUTED. Corrected 2026-08-22.** It read:

> *"⚠️ **THIS READING MUST BE TAKEN BEFORE M17 STAGE 2 MERGES (window W2).** The stage 2 prefix on
> `ScenarioUtility.EndCombatWithOutcome` closes the only route this reading uses, and the
> `pbj.force-end … bypassOnce` hatch that would re-open it **ships with that same PR**. Take it
> now, on today's `main`, or it costs a bypass that does not exist yet."*

**Read literally it refutes itself — closed and re-opened in one commit is not closed.** The two
routes were derived side by side and are **call-for-call identical for this reading**:

| | pre-W2 | post-W2 (today) |
|---|---|---|
| command | `cm.force-victory` | `pbj.force-end victory` |
| guard | `CombatStateCheck()` → `IDUtility.IsGameState("combat")` | the same `IsGameState("combat")` test |
| call | `ScenarioUtility.EndCombatWithOutcome(CombatOutcome.Victory, early: true)` | **the identical** `EndCombatWithOutcome(resolved, early: true)`, inside a `BypassCombatEndOnce` window cleared in a `finally` |
| where | `decompiled/PhantomBrigade.DebugConsole/ConsoleCommandsCombat.cs`, members `ForceVictory` + `CombatStateCheck` | `src/PBAndJ.Mod/Net/DestructProbeGlue.cs`, member `ForceEnd`; prefix predicate `src/PBAndJ.Mod/Net/WreckingPatches.cs`, member `SuppressCombatEnd` |

Same static method, same arguments, same precondition, synchronous — so the whole vanilla body runs
inside the bypass window. ⇒ **the user ruled W2 FIRST (2026-08-22); PR #60 is merged** (`main` =
`d3a3b3b`, mod 0.24.0, wire v10) **and R1·10b is taken through `pbj.force-end`, in this sitting,
alongside R1·6a–6f — which need stage 2 deployed anyway.** Derivation:
`docs/design/backlog-2026-08-22.md` §D.

⭐ **Still true, and still the point:** `cm.force-victory` never passed through the `if (flag3)`
bit-4 gate (`CombatScenarioStateSystem.cs:229`) that blocks a client from resolving its own combat
on the ordinary path — and neither does `pbj.force-end`, for the same reason. **No new code was
needed then and none is needed now.**

⚠️ **The residual risk, named rather than hidden:** `pbj.force-end` has **never run in a game**, and
it lives in `src/PBAndJ.Mod`, which is in `UNCOVERED_PROJECTS` — `make dist` cannot see it. If it
fails at the rig, this reading is lost for the sitting. **The failure is loud, not silent:** the
command returns one of `BYPASSED` / `prefix was NOT ARMED` / `NOT IN COMBAT` / `BAD ARGUMENT` /
`THREW`, and a silent return is impossible by construction. Read the return string before the probe.

⚠️ **Destructive on the client** — it ends that machine's fight. Take it LAST, after R1·10, and
expect to restart the session afterwards. **This one invocation is also R1·6f** (see phase 2's 6f
note): the return string is 6f's reading, the probe pair is 10b's. Do not spend a second one.

```
tools/drive.sh 3 "pbj.debrief-probe"            # BEFORE: baseline, expect present=False
tools/drive.sh 3 "pbj.force-end victory"        # on the CLIENT, not the host — READ THE RETURN
                                                # STRING: it names its branch, and that is R1·6f.
                                                # (was `cm.force-victory`; dead on a client since #60)
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
the BEFORE line was also taken, which is why it is in the block. ~~If `cm.force-victory` prints
`"Command only available from combat"`~~ **CORRECTED 2026-08-22 for the `pbj.force-end` route:** if
the command returns **`NOT IN COMBAT`** the client was not in the fight and **nothing was
measured** — and likewise for **`BAD ARGUMENT`** and **`THREW`**. Those three are the outcomes that
must not be read as Shape B. **`prefix was NOT ARMED` is not one of them:** it means the call went
straight through and the reading is valid (it only says no live client session had armed the
prefix — read `pbj.net-status` beside it, because on a *client* that string is itself suspicious).

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

⚠️ **Do not carry that 1541 forward — dated 2026-08-22.** It was correct *at `1708a0b`* and is a
historical record, not a baseline. **`split-grouping.lock:11` is the census, and the prose is not:**
1541 → **1556** at #51 → **1577** at #60 (`main` = `d3a3b3b`). ⭐ Every baseline in this file is a
reading from a run on a named commit; **recount from your own `make dist`, never inherit.**

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

*(R1 has not been run. Numbers go here the day they are taken, inline, with the `ZERO:` case that was
actually observed named beside each one.)*

### 9.1 R0 — **TAKEN 2026-08-22**, headless, one instance

**Conditions.** `main` = `093bfeb`, mod 0.23.0. `make deploy` **exit 0** (2102/2102 tests, `wire
surface OK (unchanged since 0.23.0)`, `split grouping OK (15 families, 145 parts, 1556 members)`,
peer selftest `ALL PASS (11 scenarios)`); tail read, not grepped for success words. One instance
only, launched as `gamescope -W 1280 -H 720 --backend headless -- tools/game-instance.sh 2`.
Campaign `pbj_fromsp` (the save with a deployable squad), scenario `generic_elimination`, host
session, 1 participant. GPU canary pair taken **before and after**: `GPU Recovery Action : None`,
`vulkaninfo` creates a device on the RTX 4070 Ti (driver 610.57.04). No `NVRM ... Xid` line in
`journalctl -k -b` at any point — pattern proven non-vacuous by the same grep matching the one NVRM
line that IS present (the module-load line), and by *not* matching the r8169 `XID 609` NIC line.

| reading | date | number | which zero-case, if zero |
|---|---|---|---|
| R0·1 checkpoint stall + save census | 2026-08-22 | **mean 433.2703 ms, worst 454.6161 ms over 5 writes**; per turn 435.6241 / 431.8885 / 452.7222 / 391.5005 / 454.6161; `attempts 5, writes 5, refusals 0, faults 0`; **census 21 saves** (20 before the first checkpoint write created `pbj_combat_turn`; 23 directory entries, of which 21 carry `metadata.yaml`) | not zero. `writes=5` excludes "the path never ran"; `attempts=5` excludes "the effect never reached the bridge"; every reading is ~4×10⁶ × the printed stopwatch resolution (0.0001 ms/tick), so it is not a clock that did not move |
| R0·2 action diff across the checkpoint load | 2026-08-22 | **`save/load diff \| before 31 \| after 31 \| MATCH`** — the plan survived the reload intact | not zero, and the diff **ran**: `diff skipped` appears **0** times in the log, and the arming line is present verbatim — `restore diff ARMED by the automatic checkpoint write at turn 4 \| slot 'pbj_combat_turn' \| 31 action(s) captured before the save`. **B-2's fix is live on a real game.** |
| R0·3 re-entry edge verdict | 2026-08-22 | **`exits=1 enters=1 trail=X@680,E@764`** — VERDICT: *the edge fired both ways*, exit **before** enter, which is the order M12c stage D wants | not zero, and all six ways to get a false zero are excluded on the same line: `armed=True` with the arm receipt seen (never-armed), `frames=13766` (postfix not applied), `runtime=present hasSession=True` (no session), `tickMoves=888 stopped=False` (dead pump), `enters/exits` non-zero (level-stuck), and `lastInCombat=True liveInCombat=True` agree (stale bridge) |

**R0·2+3 were taken as ONE load**, per §6.1, and last, per §6.2. Session before the load: `HOST |
Planning | turn 5`. After: `HOST | Planning | turn 4` — Execute pressable, one turn back, which is
the checkpoint the write at turn 4 laid down. The destructive edge behaved exactly as §3 predicts;
the log carries the whole sequence in order: `left the multiplayer campaign` → `combat ended —
unlocking 0 peers` → `action dump | turn 4 | 31 actions` → `restore diff ARMED …` → `save/load diff
… MATCH` → `in combat on turn 4 — writing the fight for 0 peers` → `fight written to
'pbj_combat_test' after 3.4s` → `combat started on turn 4`.

**What R0·1 says about N (design q6).** ~0.43 s of main-thread stall per Execute, on a machine with
**21** saves. That is the cost the cadence decision is about, and it is not small: it is roughly the
whole budget of a turn boundary, paid every turn, and it **scales with the player's lifetime save
count** rather than with this save — `RefreshSaveHeaders` YAML-parses every `metadata.yaml` in the
Normal folder. 21 is a *developer's* folder; a campaign player accumulates autosaves without
bound, so this number is a floor, not a typical case. Measuring the slope (the same reading against
a deliberately inflated save folder) is the cheapest way to turn this into a cadence, and it needs
no second instance — see NEW WORK below.

**Rendering was real.** A `gamescopectl` screenshot taken after the reload is the live combat scene
(1280×720, **93,917 distinct colours**, mission panel, unit paths, a burning wreck, turn counter at
4) — so the headless verdict from 2026-08-22 holds for a *combat* scene and not only the main menu.

⚠️ **One defect in this runbook, found by running it.** §3's R0·1 block says
`grep 'pbj checkpoint: turn=' ~/…/Player.log`. **That pattern returns 0 against a log that contains
five of the line.** The real line is `[pb-and-j] checkpoint: turn=0 ms=435.6241 writes=1
diff-armed=yes` — the tag is `[pb-and-j]`, and `pbj checkpoint` is not a substring of it. An
operator following §3 verbatim would have read a working checkpoint as "the path never ran", gone to
the refusals-by-reason for an explanation of a zero that was an artefact of their own pattern, and
found `refusals 0` — which explains nothing, because there was nothing to explain. This is the
twenty-seventh sighting of the house defect, and it was in the file whose §0 warns about it. The
grep that works is `grep 'checkpoint: turn=' …`.

### 9.2 R1

| reading | date | number | which zero-case, if zero |
|---|---|---|---|
| R1·1 overworld divergence | | | |
| R1·2 loadout-change writes | | | |
| R1·3 concurrent edits + entry watch | | | |
| R1·4 checkpoint cadence under load | | | |
| R1·5 digest lines vs turn count | | | |
| R1·6a `pbj.wreck-patches` resolved/owners | | | |
| R1·6b cascade filtered/passed | | | |
| R1·6c wreckFlags set/cleared/refused | | | |
| R1·6d 🧑 enemy tracker corrects | | | |
| R1·6e clean log, no frozen debris | | | |
| R1·6f `pbj.force-end` branch taken | | | |
| R1·7 🧑 corpse stays collapsed | | | |
| R1·8 `presimulated` (fight 1 of ≥3) | | | |
| R1·9 debriefing fingerprints, before/after | | | |
| R1·10 checkpoint resume, two machines | | | |
| R1·11 🧑 M10 Leave-button swap | | | |

### 9.3 B5b — the checkpoint-cadence **slope**, **TAKEN 2026-08-22**, headless, one instance

**What this closes.** §9.1 measured one point — 433.27 ms mean at a census of 21 — and 21 is a
developer's folder. This is the curve through it, and it decides **q6**, the cadence `N`.

**Conditions.** Working area was the **`553540-pbj2`** prefix throughout; the real campaign prefix
(`553540`, no suffix) was never read, written or deleted from. Deployed mod **0.23.0** — the same
binary §9.1 measured with, deliberately, so the census-21 point is a *control on the whole chain*
rather than a fresh unknown. `main` is now 0.24.0; the instrument is unchanged between them
(`git diff 093bfeb..2e3a059 -- src/PBAndJ.Mod/Net/CheckpointGlue.cs` is empty), so nothing was
rebuilt. One instance, `gamescope -W 1280 -H 720 --backend headless -- tools/game-instance.sh 2`.
Campaign `pbj_fromsp`, scenario `generic_elimination`, host session, 1 participant, 5 Execute
presses per point. GPU canary pair **before and after**: `GPU Recovery Action : None`, `vulkaninfo`
creates a device on the RTX 4070 Ti (610.57.04). `journalctl -k -b | grep -c 'NVRM.*Xid'` = **0**
at both ends — pattern proven non-vacuous by the same grep matching the one `NVRM` line that IS
present (module load), and by *not* matching the r8169 `XID 609` NIC decoy. **`dmesg` was not used.**

#### 9.3.1 The checkpoint reading, three censuses — and why it is the WRONG instrument for a slope

| census | mean ms | worst ms | which zero-case was ruled out, and how |
|---|---|---|---|
| **21** | **440.61** | 449.27 | not zero. `attempts 5, writes 5, refusals 0, faults 0` — `writes=5` excludes "the path never ran", `attempts=5` excludes "the effect never reached the bridge". Every reading is ~4.4×10⁶ × the printed stopwatch resolution (0.0001 ms/tick), so it is not a clock that did not move. Log control: 186 `[pb-and-j]` lines, so the `checkpoint: turn=` grep's hits are not an artefact of an empty log |
| **50** | **420.65** | 439.21 | as above; `5/5/0/0`, control 184 lines |
| **150** | **462.16** | 478.61 | as above; `5/5/0/0`, control 179 lines |

Census was recorded **in the same breath as the milliseconds**, by the same script, and re-counted
after the 5 turns to prove it had not moved under the reading (`CENSUS_BEFORE == CENSUS_AFTER` at
all three points).

🔴 **The 50-point is BELOW the 21-point.** More than doubling the census made the save *faster*.
That is not noise to be averaged away — it is the instrument telling you it cannot see what you are
asking it. A least-squares line through these three gives 231 µs/dir with residuals of ±15 ms on a
total signal of 21 ms, and its "super-linear" verdict is produced entirely by the negative first
half. **A slope fitted inside its own noise is not a measurement.**

⭐ **The confound, and `du -sb` is what exposed it.** The checkpoint stopwatch wraps *all* of
`DataManagerSave.DoSave` — serialise, YAML write, `NewFormatSave` (zip), *then*
`RefreshSaveHeaders`. The zip term scales with the **payload**, and the payload was not held
constant across the three runs:

| point | census | `du -sb pbj_combat_turn` |
|---|---|---|
| A | 21 | 116 978 |
| B | 50 | 102 419 |
| C | 150 | 96 461 |

The save shrank ~18% from A to C (different fight, different surviving units). A falling zip cost
and a rising header cost **partly cancelled**, which is exactly why the total barely moved. Reading
the flat total as "the census does not matter" would have been the wrong conclusion drawn from a
real number.

#### 9.3.2 The header probe — the same walk, isolated, with a control

`pbj.saves` is `LobbyCatalogue.Multiplayer(SaveCatalogueGlue.List())`; `List()` calls
`DataManagerSave.GetSaveHeaders(true)` → **`RefreshSaveHeaders()`** — the same full walk of the same
folder — and only filters to the `pbj_` namespace *afterwards* (`SaveCatalogueGlue.cs`, member
`List`, `:49`). So its latency carries the walk and no zip, and it needs only the main menu:
~2 minutes per census instead of ~12.

**The control is what makes it a measurement.** Every sample times **both** `pbj.drive-state`
(trivial, reads live ECS fields, walks no directories) and `pbj.saves` (same round trip **plus** the
walk). The difference is the walk; `drive-state` absorbs the TCP hop and the wait for the game's
next frame. Medians of 8 samples, first of each discarded (JIT + cold cache).

| census | control ms | walk ms | **difference = header refresh** | µs per directory |
|---|---|---|---|---|
| **21** | 19 | 51 | **32** | 1333 |
| **150** | 18 | 127 | **109** | 712 |
| **300** | 9 | 238 | **229** | 756 |
| **600** | 15 | 517 | **502** | 833 |
| **1000** | 12 | 823 | **811** | 809 |

`ZERO:` none of these is zero, and the control is the reason that means something — **the control
did not grow with the census** (19 → 18 → 9 → 15 → 12 while the walk went 51 → 823). Had it tracked
the census, the instrument would have been measuring something other than the walk and the run
would have to be thrown away. The census-21 row is the least precise: 32 ms is close to the frame
quantum, and its control sample carried a 304 ms outlier.

**Fit over all five:** `header ms = −0.63 + 0.8142 × census`, residuals within ±16 ms.

> **≈ 814 µs of main-thread stall per save directory, and the intercept is zero** — a purely
> proportional cost, as the code shape predicts.

**It is LINEAR**, and checked in the way averaging cannot hide: 21→300 gives 706 µs/dir, 300→1000
gives 831 µs/dir. No blow-up at the top; the loop is `Directory.GetDirectories` plus one small YAML
parse each (`UtilitiesYAML.cs:533`, `DataManagerSave.cs:621`), and it behaves like it.

#### 9.3.3 The honest ceiling — **derived, not assumed**

The caveat this reading was asked to answer, not assume: is a 150-save census realistic? **No, and
the bound is structural rather than a guess.**

* **Vanilla autosaves are capped at 9 directories, for ever.** `AutosaveFilenames` declares seven
  fixed literal names (`quicksave`, `before_combat`, `after_combat`, `before_travel`, `after_stop`,
  `campaign_end`, `game_exit`) — each `DoSave`d under that exact string, so each **overwrites**.
  Checked one by one, including the two that are not in the obvious place:
  `DataManagerSave.cs:3508` (quicksave) and `SettingUtility.cs:131` (campaign end).
  The only numbered family is the timed ring, `"autosave_timed_" + lastUsedSlot`, and it wraps at
  `DataShortcuts.overworld.autoSaveSlotCount`
  (`OverworldTimedAutosaveSystem.cs:46-51`). The shipped value is
  **`autoSaveSlotCount: 2`** (`Configs/Data/Settings/overworld.yaml:235`). 7 + 2 = **9**.
* **The mod adds a bounded set too:** `pbj_combat_test` and `pbj_combat_turn`
  (`LobbySaves.cs:54,75`), plus the `pbj_`-namespace mirrors of those same autosaves — at most 11.
* **So ~20 directories are fixed-name and self-limiting. Only MANUAL saves grow**, and each one
  costs a deliberate menu visit and a typed name.
* **Observed:** all three real folders on this machine sit at 21–27 entries.

**My read on where a real player lands: 20–40 directories typically; 100 is a heavy hoarder who
never deletes; 150 is an outlier; 300+ is not a person.** At those censuses the header walk costs
**16–33 ms** typically and **~81 ms** at the hoarder end — against a checkpoint whose *other* ~420 ms
is payload, not census.

#### 9.3.4 **Verdict on q6: N = 1 STANDS, and q6 closes**

The decision rule was: `N>1` gets priced only if the per-turn write at a realistic lifetime census
costs a visible hitch of order 1 s. It does not.

| census | header term | per-turn checkpoint |
|---|---|---|
| 21 (measured) | 17 ms | **~440 ms** |
| 30 (typical) | 24 ms | ~444 ms |
| 100 (heavy) | 81 ms | ~501 ms |
| 150 (outlier) | 122 ms | ~541 ms |
| ~700 | ~570 ms | ~1 s ← the census that would trip the rule |

**Reaching the 1 s bar needs ~700 save directories — about 35× the entire self-limiting autosave
set, and reachable only by hand, one deliberate save at a time.** The lifetime-save-count scaling
that `HostSession`'s remark flags as the thing "nobody has ever measured" is real, is linear, and
is **not** the term that decides the cadence.

⚠️ **What this does NOT say.** The ~420 ms constant is real, census-independent, paid every Execute,
and it is *most* of the cost. Raising `N` would not make any single checkpoint cheaper — only rarer,
at the price of the per-turn checkpoint the milestone is specified in terms of. If ~0.4 s of
main-thread stall per turn is later judged too much to pay, **the lever is an async or
smaller-payload save shape, not the cadence** — and that is a different question from q6, which
asked about the census and is now answered. The cadence itself remains a one-line change:
`checkpointEveryNTurns` has a single production construction site
(`src/PBAndJ.Mod/Net/NetGlue.Connect.cs:86`).

#### 9.3.5 Save-folder discipline, stated for the record

Created **979** directories (`zzcensus_0001`–`zzcensus_0979`, each a `cp -a` of the real save
`pbj_drift1` so its `metadata.yaml` parses like a real save). Deleted **979**. Final census **21
directories / 23 entries** — byte-identical listing to the baseline recorded before any change *and*
to the user's own pre-taken `baseline-pbj2.txt`. Both non-save files (`pbj_combat_test.zip`,
`steam_autocloud.vdf`) present. **0 survivors carrying the marker prefix.** Every name was appended
to a manifest **before** it was created, so a crash could orphan a manifest line but never an
undeclared directory; every deletion named one exact path from that manifest and passed four checks
(marker prefix, no separator/traversal/glob character, resolved parent is exactly the working
folder, is a real directory) immediately before removal. **No wildcard, glob, or `find -delete` was
used for any deletion.** The census was only ever changed with the game closed.

**H-6:** `du -sb pbj_combat_turn` = **96 461 bytes** (2 files). Note it is payload-dependent, not
fixed: the same slot read 116 978 / 102 419 / 96 461 across the three runs above.

#### 9.3.6 Two traps this run paid for

1. ⚠️ **`pbj.saves` is the WRONG instrument for "does the game see my extra saves?"** It reports
   only the `pbj_`-prefixed catalogue, so at a census of 150 it answered `7 multiplayer save(s)` and
   `grep -c zzcensus_` on its reply returned **0**. That zero is a claim about the *filter*, not
   about the walk — the walk underneath it had just enumerated all 150. Read as written it would
   have "proved" the inflation was invisible and condemned the whole reading as vacuous. The
   command is still the right instrument for the *latency* of the walk; it is simply not a listing
   of it.
2. ⚠️ **`grep -c` exits 1 when the count is zero**, so under `set -e` the guard
   `RUNNING="$(ps -eo comm= | grep -c '^PhantomBrigade')"` aborts the script **precisely when the
   machine is clean** — the only state in which it is allowed to proceed. It failed closed here
   (nothing was created; the census was still 21), but a guard that cannot run in the safe case is
   not a guard. Fix is `|| true` on the substitution, and the same shape sits in
   `tools/headless-experiment.sh` — harmless there only because that script does not set `-e`.
