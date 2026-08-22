# The road to 1.0 — every remaining blocker, laned and ordered

✅ **EXECUTION STATUS 2026-08-21 (later the same day): PR #51 and PR #52 are MERGED.** `main` =
`8f163a7`, mod **0.23.0**, wire v9 (protocol unmoved; the wire-SURFACE hash moved with `Seams.cs`),
**2102 tests at 100%**, `make dist` exit 0, `peer-selftest` ALL PASS.
**Rows 1 and 2 of the merge table below are done; the rest of this file stands as written.**
Next is **R0** — the rig, which no agent can take — and it now has every instrument it needs.
⭐ Both R0 blockers were absorbed into #51 before it merged; ⚠️ **the review's prescription for one of
them was itself defective** and the shipped fix keys captures **by slot** rather than reusing the
single `beforeSave` field — see §5 reading 2.

Status: **written 2026-08-21 at `main` = `1708a0b`, tree clean, nothing open.** Mod 0.22.0, wire v9,
2067 tests at 100% line/branch/method, split-kit selftest 90/90. This file is the execution plan; it
writes no code and every lane below is sized for an agent with no prior context. Citations were
verified against the working tree on the date above unless marked UNVERIFIED. Where an older doc and
the code disagree, the code wins and the disagreement is named.

⚠️ **Citation-decay rule, inherited from the M12c plan:** any `File.cs:NNN` here rots the moment a
split or feature touches that file. Every citation also names the member; navigate by member, re-derive
the number before quoting it onward.

🔴 **CORRECTED 2026-08-21 by the review lane** (`docs/design/road-to-1-0-review.md` carries the
evidence for every edit; nothing is merged — all four lanes' output is uncommitted). The dated
corrections below overrule the original text where they conflict: the serial spine's item 3, PR-D's
mechanism, the L2/L3/L4 checklist items marked 🔴, §5's R0 shape and R1 fixes, §6's schedule, §7's
"impossible by construction" line, and §8's answered questions. The trial integration of L1+L4
passed: `make dist` exit 0, **2102** tests, wire surface recorded at 0.23.0, `peer-selftest`
ALL PASS (11 scenarios).

---

## 1. What 1.0 means — the definition of done

**Release policy, set by the user 2026-08-16** (recorded in the project memory under "⭐ RELEASE
POLICY"): *"No further release until feature completion. The next release is 1.0, and 1.0 means M12
is done."* M12 done = **M12c (session-owned combat checkpoints) + M12d (assigned gear and the
salvage screen) — real concurrent out-of-combat management.** v0.6.0 was a proof of concept, not an
MVP; the MVP ships as 1.0.

Four blockers stand between `main` and that definition, established and verified this session:

1. **M12c — zero code.** `grep -rn 'WriteCheckpoint\|pbj_combat_turn\|CheckpointSlot' src/` returns
   nothing (verified 2026-08-21; the pattern was proven able to match by finding the same names in
   the plan text). A build-ready, self-refuted plan exists in memory
   (`m12c-plan.md` / `m12c-plan-full.md`).
2. **M17 stage 2 — the wrecked-unit cluster.** Not built; planned and refuted twice; the rewritten
   plan text lived in a session scratchpad and must be presumed lost. Zero `"Filter"` Harmony
   patches in `src/` (verified). The wire already carries `IsWrecked`
   (`src/PBAndJ.Core/Net/UnitSnapshot.cs:335`, verified) but nothing applies the ECS flag.
   One wire v10 break.
3. **M12d — zero code, largest, least prepared.** Design at
   `docs/design/m12-concurrent-management.md:416` (§M12d). The `DataBlockSavedSubsystem` has no
   `customTags` field (measured twice: 606 subsystem tags in, 0 out), mitigated by the
   riders/standalone analysis in that section.
4. **The unsettled M12b dependency.** `m12-concurrent-management.md:654` says "**M12b gates M12d**:
   the salvage item set itself is rolled per machine." But the mission-generation-authority half of
   M12b (`:159-191`) was never built — `grep -rn 'RefreshStandaloneGeneratorEncounters\|CombatGeneratorKey\|scenario_gen' src/`
   returns exactly one hit, a doc comment at `src/PBAndJ.Core/Net/PassengerRules.cs:47` (verified;
   build artifacts excluded). Either that sentence is a real unbuilt prerequisite or it is stale
   prose superseded by ship-the-fight (M12b·1/·2, built 2026-08-09). ✅ **SETTLED 2026-08-21 by
   L2: verdict (b), stale prose** — the dated verdict is in `m12-concurrent-management.md`
   §"VERDICT 2026-08-21", and the replacement is not "M12d is free": the priced salvage list is
   rolled per machine post-combat (`EquipmentUtility.cs:2763`) and generation authority would not
   have fixed it. M12d owns it (`m12d-plan.md`) — with the review's q9 caveat on its client half.

---

## 2. The lane map

| id | name | touches (concrete) | depends on | writes code? | concurrency |
|---|---|---|---|---|---|
| **L1** | M12c build (stages A→D) | Core: `src/PBAndJ.Core/Net/{LobbySaves.cs, LobbySaveWrites.cs, HostSession.Lobby.cs, HostSession.Scenario.cs, HostSession.Turn.cs, PbjEffect.cs, PbjRuntime.cs, Seams.cs, PbjProtocol.cs}`; Mod: `src/PBAndJ.Mod/Net/CombatGameBridge.Handoff.cs`, new `src/PBAndJ.Mod/Net/CheckpointGlue.cs`, `src/PBAndJ.Mod/Net/SaveVisibilityPatches.cs`, `src/PBAndJ.Mod/ModEntry.cs`; `mod/metadata.yaml`, `wire-surface.lock`; tests | nothing (build-ready) | YES | runs now; owns **merge window W1**; must not overlap L3's build |
| **L2** | Settle the M12b→M12d gate, then scope M12d | `decompiled/` (read), `docs/design/m12-concurrent-management.md` (append verdict), new `docs/design/m12d-plan.md` | nothing (read + docs) | NO | runs now, collides with nothing in `src/` |
| **L3** | M17 stage 2: reconstruct plan → 3rd refutation → build | Plan phase: new `docs/design/m17-stage2-plan.md` only. Build phase: `src/PBAndJ.Core/Net/{UnitSnapshot.cs, PbjMessageCodec.cs?, PbjProtocol.cs}` (WIRE_FILES), `src/PBAndJ.Mod/Net/{CombatGameBridge.Snapshot.cs, KeyframePlayer.Destruction*.cs}`, new patch glue, `mod/metadata.yaml`, `wire-surface.lock`; tests | plan: nothing; build: 3rd refutation done AND W1 closed | plan NO, build YES | plan runs now; build is **merge window W2**, strictly after W1 |
| **L4** | Rig instrumentation + the one-run runbook + doc debt | new `src/PBAndJ.Mod/Net/DebriefProbeGlue.cs`, `src/PBAndJ.Mod/ModEntry.cs` (1-line registration), new `docs/notes/rig-run-1-0.md`, `docs/notes/overworld-recon.md` (nightfall transcription) | nothing | YES (probe glue only, wire-neutral) | runs now; only collision is the `ModEntry.cs` registration line vs L1 stage C (trivial, sequence the merges) |
| **R0** | Mini rig reading: stall + 1-machine round-trip + re-entry edge | the rig, no repo files (results into `docs/notes/rig-run-1-0.md`) | L1 PR-C deployed | NO | serial (rig is a single resource) |
| **R1** | THE comprehensive two-instance run | the rig | L1 complete, L3 build merged, L4 merged, R0 read | NO | serial |
| **L5** | M12d build | scoped by L2; expect `PbjMessage.cs`/`PbjMessageCodec.cs` (WIRE_FILES) for barrier messages, salvage glue, ownership tags | L2 verdict + refutation, L1 merged (rollback floor), likely **window W3** | YES | serial after R1 (or after W2 at earliest) |

**Explicit collisions:**

- **L1 vs L3 (build): `PbjProtocol.cs`, `mod/metadata.yaml`, `wire-surface.lock`.** Both move the
  wire-surface hash — `Seams.cs` and `UnitSnapshot.cs` are both in `WIRE_FILES` (`Makefile:43-47`,
  verified: `PbjProtocol.cs Seams.cs` close the list at `:47`). One merge window each, never shared.
- **L1 vs L4: `src/PBAndJ.Mod/ModEntry.cs`** — each adds one probe/glue registration line. Trivial;
  whichever merges second rebases.
- **Everything vs the split queue:** `ManagementProbeGlue.cs` (827), `OverworldProbeGlue.cs` (572),
  `VfxProbeGlue.cs` (514), `DestructProbeGlue.cs` (619), `DestructionPlayback.cs` (688),
  `ConnectScreenGlue.cs`, `ActuatorGlue.cs`, and 🔴 `PbjMessage.cs`/`PbjMessageCodec.cs`/
  `ScenarioPayload.cs`/`Keyframes.cs` are all on the modularization queue. **No file a 1.0 lane
  touches may be split while that lane is in flight.** The queue is hygiene, not a blocker (§7).

---

## 3. The serial spine — what genuinely cannot be parallelized, and why

1. **W1 before W2 (M12c's wire window before M17 stage 2's).** Both PRs move
   `wire-surface.lock` + `mod/metadata.yaml` and bump versions; `check-wire-surface`
   (`Makefile:160`) forces bump+re-record in the same commit. Two writers to one lock = two
   windows. *Reason: file/lock collision.* Order justified in §6.
2. **M17 stage 2 build after its third refutation.** The adversarial-review rule is project law, and
   this plan has already been wrong twice (round 2 refuted round 1's headline). *Reason: process
   gate + the plan text is lost and must be reconstructed first.*
3. ~~**M12c stage D after R0.** The plan's own §9.3 names the re-entry edge
   (`PbjRuntime.ObserveCombatEdge`, `src/PBAndJ.Core/Net/PbjRuntime.cs:117`, called from `:82`,
   verified) as the single largest hole: nobody has verified a mid-combat reload drives it
   false→true. Stage D's design forks on the answer (free resume via the edge vs an explicit exit
   before reload).~~ 🔴 **CORRECTED 2026-08-21: the edge question is answered by the decompile,
   not the rig** (`m12c-stage-d-fork.md` §2, verified by the review: `TryLoading` pops to
   `"mainmenu"` — `DataHelperLoading.cs:237-239` — and `LoadingStart` refuses at `:259` unless it
   became the menu; the pump is a `Heartbeat.Update` postfix that M11d already proved live across
   loads; the outcomes are exhaustive). **Stage D still waits for R0, but for a different
   reading**: the fork doc's §5 — whether a client's `UnitAssignments` survive a host checkpoint
   reload (the edge re-runs `HandleCombatExited`/`Entered`, which drops assignments and `Reassign`s)
   — a **two-instance** reading; R0 gains that tail. L4's `pbj.combat-edge` counter rides along as
   the cheap confirmation of the pump-liveness premise, and the reading is **destructive** (it ends
   the session's fight), so it goes last. *Reason: still an evidence dependency, on a different
   question.*
4. **M12d scoping after L2's verdict.** Whether M12d needs mission-generation authority is the
   difference between a milestone and two. *Reason: evidence dependency.*
5. **M12d build after M12c merged.** M12c's checkpoint is M12d's rollback floor by design
   (`m12-concurrent-management.md:352-355`), and the disconnect story ("a machine that never clicks
   never commits", `:637-641`) is survivable only because of it. *Reason: semantic dependency; also
   both plausibly touch `PbjMessage.cs`.*
6. **All rig work is serial with itself.** Two instances is the machine's ceiling
   (`tools/game-instance.sh` header, enforced), the rig is one physical resource, and deploy
   requires both instances closed. *Reason: hardware lock.*
7. **The cadence constant N after R0's stall number.** Design open question 6
   (`m12-concurrent-management.md:709`) decides it; shipping N=1 blind risks a per-turn hitch.
   *Reason: evidence dependency (small — N=1 ships as default, R0 confirms or a one-line Core PR
   adjusts).*

**What is genuinely parallel right now:** L1 (code), L2 (evidence+docs), L3 plan phase (docs), L4
(wire-neutral probe glue + docs). Four lanes, disjoint files except one registration line.

---

## 4. The checklists

Legend: 🧑 = requires a human (eyes/click/Steam) — not agent-closable, route around it.
Every PR: branch → PR → merge; never commit to `main`; never `git commit` unless the user asked.
Every code step: tests first, red before green; `make dist` must exit 0 (read the tail AND the exit
code — a grep for success strings has already banked a failure as a pass once).

### L1 — M12c: session-owned combat checkpoints

Source of truth: memory `m12c-plan.md` + `m12c-plan-full.md` (read BOTH in full before starting).
The plan is already refuted (six kills, recorded); do not re-derive it. It was written at
`21b780f`; PRs #48–#50 landed since.

- [ ] **A0. Re-verify the plan's citations by member** on today's tree before writing anything:
      the `TryCommit` gap (verified today: `HostSession.Turn.cs:174` `OrdersApplied` log, `:175`
      `committedTurn = barrier.Turn`, `:176` `CommitTurnEffect` — unchanged), the `ScenarioSlot`
      grep-derived 7-site list in plan §3.1, `CanSave` refusals (`decompiled/`
      `DataManagerSave.cs:94-149` UNVERIFIED today — re-read), `LobbySaveWrites.NameForRead`'s
      **middle** (plan §9.7 flagged it unread: the interaction between per-turn
      `DataManagerSave.saveName` mutation and M11d's read redirection — read it now, it is one
      method).
- [ ] **PR-A — stage A, the reserved slot (Core, wire-neutral).** Tests first:
      `CheckNewName("combat_turn") == Reserved`, `IsOffered` false, `IsProtectedFromOverwrite`
      true, lobby-select refused, both `HostSession.Scenario` arms exclude it, picker excludes it.
      Then `LobbySaveNames.CheckpointSlot = Prefix + "combat_turn"` in `LobbySaves.cs` beside
      `ScenarioSlot`, and the guard at each of the 7 behavioural sites the plan enumerates
      (`LobbySaves.cs` IsReserved/IsOffered, `LobbySaveWrites.cs` IsProtectedFromOverwrite,
      `HostSession.Lobby.cs`, `HostSession.Scenario.cs` ×2, `SaveVisibilityPatches.cs`). ⚠️ Reserve
      the **unprefixed** form or the guarding arm is unreachable and fails the 100% gate (the M11b
      trap, plan §3.1). Gate: `make dist` exit 0.
- [ ] **PR-C — stages B+C together: the effect, the seam, the glue (merge window W1).** They travel
      together because `IPbjGameBridge.WriteCheckpoint` in `Seams.cs` is the wire-surface move and
      splitting B from C would leave either an effect no arm consumes or an interface member only a
      fake implements across a window boundary. Contents:
      - Tests first (Core): the **ordering assertion** — `WriteCheckpointEffect` strictly after the
        last `ApplyOrderEffect`, strictly before `CommitTurnEffect`, on a filled barrier; **absent**
        on unfilled barrier, refused commit, and N-turn skips; cadence arithmetic (N in Core, default 1).
      - `PbjEffectKind.WriteCheckpoint` + `WriteCheckpointEffect(int turn)` in `PbjEffect.cs`
        (deliberately NOT a wire file, `Makefile:40-42`).
      - Emit in `HostSession.TryCommit` between `HostSession.Turn.cs:174` and `:175`.
      - `IPbjGameBridge.WriteCheckpoint(int turn)` in `Seams.cs` — full-line comments only (the
        hash strips only full-line comments, `Makefile:336`).
      - `PbjRuntime.Execute` arm — the switch dispatches on TYPE patterns (`case SendEffect send:`),
        not on `PbjEffectKind`; any grep written to prove the arm exists must first be shown to
        match an existing arm (vacuous-guards sighting 13).
      - Mod glue `CheckpointGlue.cs` patterned on `CombatShipGlue.cs` `Write`
        (`src/PBAndJ.Mod/Net/CombatShipGlue.cs`): `if (!CanSave(false)) { log WHICH refusal; return; }
        DoSave(CheckpointSlot, Normal, null, -1, false)`. Synchronous, no polling, no
        `delayUntil` (plan §3.3 gives the reasons — keep them as comments).
      - **The stall instrument ships inside this glue** (see R0): wrap the `DoSave` in a stopwatch,
        log `pbj checkpoint: turn=<n> ms=<x> writes=<total-this-session>`, and register
        `pbj.checkpoint-stat` printing attempts / refusals-by-reason / writes / last-ms / last-turn.
      - `PbjProtocol.ModVersion` 0.22.0 → 0.23.0 + `mod/metadata.yaml`, then
        `make record-wire-surface`, same commit. If any new member lands in a split-family file,
        `make record-split-grouping` with the placement justified in the PR.
      - Gate: `make dist` exit 0; `make peer-selftest` ALL PASS **unchanged** — that it is unchanged
        is itself the evidence no protocol byte moved.
- [ ] **R0 happens here** (see §5) — its re-entry-edge answer decides stage D's shape, its stall
      number confirms N.
- [ ] **PR-D — stage D, resume.** 🔴 **CORRECTED 2026-08-21** (old text said the host offers
      `CombatOfferMessage(CheckpointSlot, digest, turn)`; L1's trace shows the checkpoint never
      crosses the wire — on the re-entry edge `HandleCombatEntered` raises `ShipCombatEffect` and
      `CombatShipGlue.Write` re-ships the reloaded state as the **scenario** slot). Two branches,
      decided by R0's two-instance tail (`m12c-stage-d-fork.md` §4-5): **branch A** — reload +
      shipped relaunder, wire-neutral, console command + paragraph; costs a visible combat-end on
      every client and **re-dealt unit ownership**. **Branch B** — rollback-aware resume: new
      message + protocol bump, its own merge window (ordered against W2, never shared); preserves
      ownership. If the R0 reading shows units move (which `Reassign` makes the default
      expectation), branch B is mandatory. `make dist` proves the hash unmoved (A) or the
      re-record is deliberate (B).
- [ ] 🧑 **Stage D two-machine verification is part of R1**, not a lane step.

### L2 — Settle the M12b→M12d gate, then scope M12d

Evidence lane. Writes no production code. Its output is a verdict + a build plan, both refuted
before anyone codes them. ⚠️ This is PR #50's territory: the claim being tested went stale once
already inside this very document family. **State the wiring, not the intent, and date every claim.**

- [ ] **B1. Map every route to `RefreshStandaloneGeneratorEncounters` and mark each HOST/CLIENT/BOTH.**
      Starting evidence (verified 2026-08-21): the four call sites are
      `decompiled/DataHelperLoading.cs:408` (post-load, only inside the
      `!IsFeatureUnlocked("feature_base_standalone_ops_unlocked")` branch),
      `decompiled/PhantomBrigade.Data/OverworldPointUtility.cs:449` (the design doc says `:451`;
      the code says `:449` — believe the code) inside `OnCombatCompletionLate`,
      `decompiled/CIViewOverworldRoster.cs:1830` (sim-lock exit; M12a suppresses the client's route
      into sim locks — verify the suppression covers the exit, not just the entry),
      `decompiled/PhantomBrigade.DebugConsole/ConsoleCommandsBasecrawler.cs:100` (console).
      **The pivotal fact to verify:** `OnCombatCompletionLate` has exactly two non-console callers —
      `CIViewOverworldDebriefing.cs:2818` and `OverworldCombatOutcomeProcessingSystem.cs:353`
      (both verified) — and M17's review established a client never runs
      `OverworldCombatOutcomeProcessingSystem` and never reaches the debriefing (UNVERIFIED today —
      re-verify from the decompile, by mechanism not by quote). If that holds, the design's
      "both machines re-roll divergently after every co-op mission" (`m12-concurrent-management.md:175-176`,
      written 2026-08-08, pre-M12b·2) is **stale for the shipped path**: only the host re-rolls,
      and the client's contracts are divergent-but-frozen relics it cannot act on (M12a suppresses
      `EngageSite`, `PassengerRules.cs`).
- [ ] **B2. Trace what the salvage screen actually reads, host and client.** The design's own
      `savedOutput` chain (`m12-concurrent-management.md`, "✅ SOLVED" block): loot is pre-rolled at
      scenario generation into `CombatDescription.savedOutput`, rides the save, and post-combat
      generation is skipped when present (`EquipmentUtility.cs:1153-1156`, UNVERIFIED — re-read).
      Question to answer with wiring: after ship-the-fight, does the machine that runs the
      debriefing hold the host's `savedOutput` bytes? Does a client ever open a debriefing today at
      all, and under M12d's design, does it need to?
- [ ] **B3. Write the verdict** into `docs/design/m12-concurrent-management.md` §Sequencing as a
      dated correction (the PR #50 shape: quote the old sentence, state the test that was run, give
      the new claim with citations), and one of:
      (a) *M12b generation authority is NOT needed for M12d* — M12d needs host-authoritative salvage
      data (savedOutput via shipped bytes) + the host-computed budget broadcast the design already
      mandates; or (b) *it IS needed* — then scope it as its own pre-M12d work item with the
      replication set the design lists (`CombatGeneratorKey`, `CombatUnitLevel`, `FactionBranch`,
      `CombatEscalationLevel`, `UpdateCombatDescription` output, `:186-188`).
- [ ] **B4. Scope M12d** into `docs/design/m12d-plan.md` on top of the verdict: the ownership tags
      (parts via `customTags`; subsystems via inheritance + the `(kind, serial)` side table for
      loose ones), the pool arithmetic with per-group `costMultiplier`, the `SalvageBarrier`
      (third barrier; a departing peer must not satisfy it; edge-triggered), the host-broadcast
      budget, the symmetric commit via the confirmation modal's public `Invoke()` (measured
      drivable end to end, design table), rollback-to-checkpoint on disconnect. Decide whether new
      `PbjMessageType`s are needed (almost certainly yes → protocol v11, window W3).
- [ ] **B5. Adversarial refutation pass** on the M12d plan (Fable-5, project law) before any code.
- [ ] The post-commit determinism question (design q7, `:711`) gets its **instrument** from
      L4 and its partial reading from R1; the two-machine comparison lands during M12d verification.
      🔴 **CORRECTED 2026-08-21: there is no `FinishDebriefing`** — zero hits across the decompile,
      case-insensitive, pattern proven by `SalvageFinish` biting. The commit is the private
      `CIViewOverworldDebriefing.SalvageFinish` (declared `:2772`), driven externally via the
      public `OnStageNextExternal()`. `m12d-plan.md` §2.7/§8-Q4 and
      `m12-concurrent-management.md:666` + open question 7 owe the same rename.

### L3 — M17 stage 2: the wrecked-unit cluster

- [ ] **C1. Reconstruct the stage 2 plan** into `docs/design/m17-stage2-plan.md` from memory
      `m17-review-findings.md` + `client-wreck-pose-lost.md` (the scratchpad rewrite is gone —
      the third time this project pays for scratchpad plans; that is why the reconstruction goes in
      the tree). Must carry, at minimum: the `Filter`-postfix suppression on
      `CombatUnitWreckingSystem` + `CombatUnitDestructionEffectSystem` (string-name form —
      `Filter` is `protected`); leave `CombatUnitWreckingSyncSystem` unpatched (its Execute is an
      overlay refresh a client wants); the `EndCombatWithOutcome` prefix — 🔴 **CORRECTED 2026-08-21: REQUIRED, not
      defense-in-depth** (the reconstructed plan's KILL 1 + §7A, verified by the review: bit 4 has
      a second, content-driven producer at `CombatScenarioTransitionSystem.cs:68`, and
      `CombatForceExecution`/`cm.execute-by-turns` reach the first one on a client too), with a
      predicate excluding `Closed`/`Faulted` (KILL 2 — a prefix armed after a fault makes a
      single-player continuation unwinnable forever). ⚠️ The prefix disables the client's console
      exit — which is `cm.force-victory`/`cm.force-defeat` (**there is no `cm.end-combat-*`**;
      `ConsoleCommandsCombat.cs:71/:80` under `[CommandPrefix("cm.")]`) — so the plan's
      `pbj.force-end` escape hatch ships with it, and the runbook is told;
      **strike `frameIntegrity` from the v10 list** (M16 already ships it — round 2's finding);
      the re-scoped stage 3 fields `deathStatus`/`knockedOut`/`ejected` (no reactive systems);
      possibly part `chargeCount`/`isSalvageable`; stage 4 stays cut; the `isWrecked` lifecycle at
      combat end (both answers falsify a different claim — the plan must pick one and say which
      claim dies); `OnHandleInactiveUnitCollision` best-effort, not invariant; do NOT patch
      `CombatPartWreckingSystem` — 🔴 **CORRECTED 2026-08-21: not because "dead code fails the
      100% gate"; that names the wrong unit.** `Makefile:200` covers `src/PBAndJ.Core` only and
      `:220` puts `src/PBAndJ.Mod` in `UNCOVERED_PROJECTS`, so a dead Harmony patch compiles,
      deploys, never runs, and nothing tells you — strictly worse than a build failure. The
      conclusion survives on the correct mechanism: its trigger `EquipmentMatcher.Wrecked.Added()`
      is a component no client path adds, and the mitigation for silent Mod glue is a counter that
      can read zero, not a test. One v10 break total.
- [ ] **C2. Third refutation pass** (Fable-5) on the reconstructed text. The first two passes each
      killed a headline; assume this one finds something too.
- [ ] **C3. Build (merge window W2, strictly after W1).** Wire: new `UnitSnapshot` fields + codec +
      `PbjProtocol` protocol v9→v10 + ModVersion 0.23.0→0.24.0 + `mod/metadata.yaml` +
      `make record-wire-surface`, one commit. Apply side in `KeyframePlayer.Destruction*.cs`; the
      client-side counters the run will read (applied-wrecked count, tracker count) ship with it.
      ⚠️ `UnitSnapshot.cs`, `PbjMessage.cs`, `PbjMessageCodec.cs`, `Keyframes.cs` are 🔴 queue
      files — no splits in flight. Gate: `make dist` exit 0; `peer-selftest` ALL PASS (it WILL
      change here — the protocol moved; that is expected and the PR says so).
- [ ] 🧑 **Eyes-verification of the corpse behaviour rides R1** (counters are agent-readable;
      "looks dead and stays dead" is a human reading, as stage 1's was).

### L4 — Rig instrumentation, the runbook, and doc debt

All probe glue is Mod-side `[ExcludeFromCodeCoverage]`, wire-neutral, ships any time. House style:
`src/PBAndJ.Mod/Net/DestructProbeGlue.cs` (a probe states in its own doc block which
indistinguishable-from-a-chair outcomes it separates). **Every probe obeys the twenty-four-times
rule: the vacuity guard goes on the INSTRUMENT (a canary), and a zero prints its own alternative
hypothesis.** A probe that cannot state what its zero means does not ship.

- [ ] **D1. `DebriefProbeGlue.cs`** — `pbj.debrief-probe`. 🔴 **CORRECTED 2026-08-21: the original
      spec's "per-group `savedOutput` fingerprint" described a list that does not exist.** Two
      different things are called groups: the screen's `salvageGroups`
      (`CIViewOverworldDebriefing.cs:427`, one per unit, keyed by `unitPersistentID`, **no
      `savedOutput` field** — element declared `:55`) and the scenario's `rewardGroupsCollapsed`
      (`CombatDescription.cs:37`, keyed by reward key, where `savedOutput` lives —
      `CombatRewardGroupCollapsed.cs:12`). Quoting one count as the other compares two machines on
      the wrong axis. The shipped probe prints both, separately labelled, plus `salvageBudgetLast`,
      an inventory fingerprint, a computed hash canary, and a distinct sentence for each way a zero
      can arise — audited by the review and adopted as the spec. *Zero meaning:* as the probe
      itself prints it.
- [ ] **D2. Confirm the re-entry-edge observable.** The reading R0 needs is "did
      `ObserveCombatEdge` produce exit-then-enter across `pbj.combat-load`". Check whether NetLog
      already prints distinguishable lines for `CombatExitedEvent`/`CombatEnteredEvent` on the
      host; if not, add an edge counter to an existing probe (`pbj.drive-state` or
      `pbj.checkpoint-stat`). *Zero meaning:* no edge pair after a load = either the pump did not
      run across the load or `InCombat` never went false — print live `InCombat`
      (`CombatGameBridge.cs:46-47`) beside the counter so the two are separable.
- [ ] **D3. Sweep the existing run-inventory probes for the zero-prints-its-hypothesis property**
      (`pbj.ow-probe`/`-sample`/`-watch`, `pbj.vfx-probe`, `pbj.mg-*`): where a zero is ambiguous,
      add the one line. `pbj.vfx-probe` already prints `presimulated=` beside its other counters
      (`VfxProbeGlue.cs:475`, verified) — confirm a total-scanned figure sits beside it so `0/0`
      (nothing scanned) is distinguishable from `0/N`.
- [ ] **D4. Write `docs/notes/rig-run-1-0.md`** — the §5 runbook, as an executable checklist with
      exact `tools/drive.sh` commands, expected outputs, and the zero-meaning line per reading.
      R0's results get appended to it, then R1's.
- [ ] **D5. Doc debt (non-gating): transcribe the nightfall chain** into
      `docs/notes/overworld-recon.md` from `SkyPatches.cs`'s doc comment and
      `OverworldProbeGlue`'s header, numbers included (`cycleHour` 18.800 host / 20.906 client,
      ambient 1.06/1.72) — the probe-sweep section (`overworld-recon.md:457-495`) names this as
      half its own exit condition. State the TEST, not the observation (sighting 24).
- [ ] PR the lot; rebase after L1 PR-C if `ModEntry.cs` conflicts.

### L5 — M12d build (gated: L2 verdict + refutation, M12c merged, **and §8 q9's stage D0**)

L2's B4 plan now exists (`docs/design/m12d-plan.md`, verdict (b), seven kills). 🔴 **CORRECTED
2026-08-21 (review §3): it is not buildable as scoped.** Its client half (D4/D5 — postfix-apply,
screen driving, symmetric commit) presumes the client's debriefing opens; no machinery, shipped or
planned, ever opens it (see §8 q9), and its §2.1 "let `PrepareUnitForSalvage` run on every
machine" contradicts its own M1 reading's hypothesis — the method never fires on a client. The
plan owes a **stage D0** (design the client's debriefing entry, or remove the client screen from
the design) plus its refutation before D4/D5 are real. Fixed constraints it must honor: tests-first at 100%; pool enforcement is 100% mod code (the vanilla backstop is a
`LogWarning`, not a refusal); the budget is host-computed-once and broadcast; `(kind, serial)`
identity with "the host assigns the identity clients quote back"; never write `livery`; the
barrier is edge-triggered and a departing peer must not satisfy it; new messages = protocol v11 in
window W3 with `make record-wire-surface`.

---

## 5. The comprehensive rig test

**The organizing goal: one well-instrumented two-instance session that closes every open
measurement that is closable, in one sitting.** Two sessions total, not scattered readings:

### R0 — the mini reading (after PR-1 + PR-2 deploy; one instance, **plus a two-instance tail**)

Exists because two L1 decisions fork on its answers and waiting for R1 would serialize the lanes.
(*Corrected 2026-08-21: "mostly one instance" no longer holds — the stage-D fork reading in item 3
needs a client holding units, so the session's second instance joins for the tail.*)

1. **The checkpoint stall** (design q6): drive a fight to planning phase, `pbj.execute` several
   turns, read `pbj checkpoint: turn=N ms=X writes=K` per turn. Record the save-folder census
   beside the number — the cost scales with lifetime save count (`RefreshSaveHeaders` re-parses
   every save's metadata, design `:403`), so the reading without the census is not a measurement.
   *Zero meaning:* `ms=0` with `writes>0` = the timer wrapped nothing (suspicious — DoSave zips);
   `writes=0` = the path never ran — `pbj.checkpoint-stat` prints refusals-by-reason, and
   `attempts=0` means the effect never reached the bridge (check the NetLog `OrdersApplied` line
   beside it).
2. **Checkpoint → pressable Execute**. 🔴 **CORRECTED 2026-08-21 — two blockers stood between
   this reading and reality, absorbed into PR-1 (review §C3):** `pbj.combat-load` takes no
   argument and loads `pbj_combat_test` (`SaveLoadGlue.cs:44`, `:23`), so PR-1 adds
   `pbj.checkpoint-load`; and `SaveLoadGlue.beforeSave` was armed only by `pbj.combat-save`
   (`:38`), so without PR-1's arming in `CheckpointGlue.Write` the load prints
   `"(no pre-save capture this session — diff skipped)"` (`:60`) and this reading never runs.
   ✅ **SHIPPED in PR #51 — and the prescription above was corrected during implementation.** The
   capture is keyed **by slot**, not written into the single shared `beforeSave` field: one field
   with two writers would have made `pbj.combat-save` → commit a turn → `pbj.combat-load` diff the
   *scenario* slot's restored actions against the *checkpoint's* capture and print a confident
   `DIFF` that means nothing — breaking M3a's probe while adding M12c's, the same silent shape as
   the two blockers themselves. The capture is also taken **after** the `CanSave` gate, not at the
   top of `Write`: above the gate it would describe a checkpoint that was never written, and outside
   the `try` it could throw inside `NetGlue.Pump` and end networking for the process.
   With both absorbed: load the checkpoint with `pbj.checkpoint-load`, confirm the action diff
   reports the planned set intact and Execute is pressable. *Zero meaning:* the glue prints both counts; read them, not
   the diff's silence.
3. **The re-entry edge + the stage-D fork reading — one load, not two, and it goes LAST.**
   🔴 **CORRECTED 2026-08-21** (review §C4; runbook §6.1-6.2): reading 2's load *is* the edge
   event, and observing it is destructive on a host (`HandleCombatExited`: assignments dropped,
   `CombatEndMessage` broadcast, then re-ship + `Reassign`). The edge itself is decided from the
   decompile (spine item 3); `pbj.combat-edge-watch`/`pbj.combat-edge` ride along as the
   pump-liveness confirmation. **The reading that actually decides stage D needs the second
   instance:** client owns units → host reloads the checkpoint → does the client end up owning
   the same units (`m12c-stage-d-fork.md` §5 — assert against `UnitAssignments`, and beware the
   small-roster re-deal landing on the same answer by luck). *Zero meaning:* the probe prints its
   own five-way verdict.

### R1 — the one comprehensive run (after L1 complete + L3 build + L4 merged)

**Pre-flight, in order:**

- [ ] `make deploy` exit 0 with BOTH instances closed (deploy `rm -rf`'s a mod folder whose DLL a
      running instance holds open — `tools/game-instance.sh` header).
- [ ] Steam CLIENT running; the game itself NOT launched via Steam (the Steam-launched instance
      cannot be driven; two script instances is the ceiling and the script enforces it).
- [ ] `tools/game-instance.sh 2` (host), then `tools/game-instance.sh 3` (client);
      `tools/window-arrange.sh` (both windows land on top of each other otherwise — cost a playtest once).
- [ ] Sanity: `tools/drive.sh 2 "pbj.net-status"` and same on 3 — a TIMEOUT here is an unreachable
      instance, not a failed reading.
- [ ] Host at a clean overworld, **no debrief view pending** (PB has no view stack; a pending
      screen re-drops onto the next battle).
- [ ] Session up, both in one fight via the shipped M12b·2 path (the M16 route: `cm.kill-enemy` is
      the guaranteed kill; ⚠️ the `EndCombatWithOutcome` prefix ships with W2, so the CLIENT's
      console exit — `cm.force-victory`/`cm.force-defeat`, **there is no `cm.end-combat-*`**
      (corrected 2026-08-21) — is dead there: plan combat exit via host victory, or the
      `pbj.force-end` escape hatch W2 ships alongside the prefix).
- [ ] Both HUDs woken (`pbj.select-unit <index>` on each) or `pbj.execute` refuses
      indistinguishably from a stalled barrier (M16 trap list).

**The reading order** (overworld readings before the fight, combat readings during, resume last,
so no reading destroys a later one's preconditions):

| # | reading | closes | instrument | what a ZERO means |
|---|---|---|---|---|
| 1 | overworld divergence, both machines idle + host drives | recon measurement 2 | `pbj.ow-probe`/`pbj.ow-sample` on both | identical fingerprints = PASS; an empty sample row = wrong game state, which the probe prints — read the state field, not the emptiness |
| 2 | loadout-change writes, live session | recon measurement 5 | `ManagementProbeGlue` drive + `pbj.mg-serials` before/after | no delta = either the change wrote nothing or the change never applied — the drive command's own return value says whether the click landed |
| 3 | concurrent edits + host-initiated combat entry | recon measurement 6 | `pbj.ow-watch` (`EnterCombat` patch) on both — ⚠️ *corrected 2026-08-21: it is a toggle; ARM IT IN PHASE 1 (before the fight), read it here* | no watch line on the client = the client never took the entry path (expected) — the HOST line is the positive control; if BOTH are silent the patch never applied |
| 4 | per-turn checkpoint cadence + stall under two-machine load | design q6 (confirm R0) | `pbj.checkpoint-stat` on host | attempts=0 = effect not reaching bridge (see R0); refusals>0 names the refusal |
| 5 | turn digests agree all session | continuous canary | existing `StateDigest` per-turn logs | a digest line ABSENT is not agreement — count the lines against the turn count |
| 6a | the three Harmony patches resolved and applied | M17 stage 2 — the silent-failure mode | `pbj.wreck-patches` on the client (no fight needed) | `resolved=False` = the string-name attribute is wrong or the game moved the member. `resolved=True owners=0` = the target is fine and **our patch class never applied** — Mod is outside the coverage gate, so nothing else can tell you |
| 6b | cascade suppression fired: host `cm.kill-enemy`, then client probe | M17 stage 2 acceptance | `pbj.destruct-probe`'s `cascade: filtered=/passed=/suppressing=` | `filtered=0 passed=0` = **the Filter was never called at all** (patch dead, or nothing was wrecked). `filtered=0 passed>0` = the patch applied and the predicate was false — read `suppressing=` on the same line before concluding anything |
| 6c | the ECS flag landed: client `wreckFlags` vs the host's wrecked count | M17 stage 2 acceptance | `pbj.destruct-probe`'s `wreckFlags: set=/cleared=/refused=` | `set=0` with host-wrecks>0 = the apply path is dead. `refused>0` names an exception in the log. ⚠️ `wrecksPlayed`, `frozen` and `wreckFlags` are **three different questions** — a wreck-visual counter moving is not evidence stage 2 ran |
| 6d | 🧑 the enemy tracker corrects | M17 stage 2's headline buy | the user's eyes on instance 3's unit-tab row, plus the probe's per-unit `wrecked=` list | n/a — human reading, and the counter half cannot substitute: `set=N` says the ECS moved, not that the UI redrew. **This is the reading that tests the explicit `RedrawUnitTabs`** |
| 6e | no modal dialog, no frozen debris | M17 stage 2 — what the suppression buys | zero exceptions in the client `Player.log` during combat; eyes on a corpse's core for stuck fragments | a clean log is **not** evidence the cascade was suppressed, only that it did not throw. 6b is that evidence; read them together |
| 6f | the escape hatch works: `pbj.force-end victory` on the client | M17 stage 2 — the console exit the prefix closes | the command's own return string + `pbj.net-status` | the command names its branch: `BYPASSED`, `prefix was NOT ARMED`, `NOT IN COMBAT`, `BAD ARGUMENT` or `THREW`. A silent return is impossible by construction, which is the point |
| 7 | 🧑 client corpse stays collapsed through planning (eyes) | M17 stage 2 (stage 1's property, re-confirmed under stage 2) | the user watches instance 3 | n/a — human reading. Named because `OnHandleInactiveUnitCollision` and stage 1's freeze touch the same views and nobody has seen them together |
| 8 | `presimulated` during varied fights | advanced-particle-blocks decision | `pbj.vfx-probe` (exists, `VfxProbeGlue.cs:428-475`) | 0 beside a non-zero scanned-total, across ≥3 varied fights, retires the feature; 0/0 = the probe ran outside combat |
| 9 | host victory → host debriefing fingerprints | design q7 (partial) | `pbj.debrief-probe` before + after commit | entered=False = wrong moment **before** the commit only — *corrected 2026-08-21: `SalvageFinish` calls `TryExit` as its second act, so `entered=False` is CORRECT after the commit; the post-commit fingerprint comes from the inventory section, and the probe prints the stage on the same line* |
| 10 | checkpoint resume, two machines: host reloads `pbj_combat_turn`, client re-offered, same turn, same plan | M12c stage D acceptance | NetLog offer/load lines + **`pbj.net-status`** (*corrected 2026-08-21: `pbj.status` does not exist*) turn on both + edge counter | client not re-offered = the edge never fired (D2's counter separates pump-dead from level-stuck) |
| 11 | 🧑 M10 Leave-button swap: with the session up, the user clicks Multiplayer and eyeballs Host/Join → Leave | the last stale M10 status | eyes | n/a — human reading; non-gating ride-along |

**Explicitly NOT in this run:** the nightfall chain (already measured 2026-08-15; owed a
transcription, not a rerun — L4 discharged it); `SalvageFinish` two-machine determinism
(*corrected 2026-08-21: formerly written `FinishDebriefing`, which does not exist*; needs M12d
machinery — and see §8 q9: today no machinery gets a client into that screen at all);
cross-session pose-digest comparison (invalid by construction — the sim is non-deterministic).

Results are appended to `docs/notes/rig-run-1-0.md` the same day, numbers inline — never to a
scratchpad.

---

## 5b. 🆕 How the rest of this roadmap gets executed (adopted 2026-08-22)

Everything below runs as a **planner/executor loop**, adapted from the sister project's and recorded
in memory as `planner-executor-loop`. The shape, and the three ways ours differs:

1. **A PERSISTENT Fable planner owns this file.** Not the conversation, not a scratchpad.
2. **A Fable refutation pass sits between planner and executor.** Their loop has none; ours needs
   one, because a plan that refutes none of its own premises is treated as unread here.
   ⭐ **Ask the reviewer to BREAK it, not check it — and give them the thing, not the diff.**
3. **Owner questions are batched BEFORE execution** and written back here as decisions, so later
   executors read them rather than re-asking. (q9 is the worked example: §8 carries the decision.)
4. **Opus executors run in PARALLEL, in isolated worktrees** — theirs are serial because they share
   a tree. Scope is stated as an **exclusion list** naming which later PR owns each excluded thing,
   and each executor's report ends in **NEW WORK DISCOVERED**.
5. 🔴🔴 **Every report goes BACK to the SAME planner.** This is the invariant, and **the one this
   roadmap's own first run broke** — the planner returned once and never saw a lane report, which is
   why the client-debriefing gap (§8 q9), where four lanes each held a piece, surfaced only in final
   review instead of at lane 2.
6. **A cycle closes when the planner absorbs the report, not when the PR merges.** The merge is the
   middle of the cycle.

### 🆕 This plan is FINISHABLE WITH HOLES

Some items here cannot be closed by any agent — they need the two-instance rig, a human's eyes, or a
click. **A hole does not stall the plan.** Each is written with four fields: the measurement stated
precisely enough that the taker need not interpret it; ⭐ **who can take it**; what each outcome
changes; and **what may proceed under which assumption**. The open holes today are **R0**, **R1**
(including R1·10b, the q9 decider), and the **post-reboot GPU ladder** in
`docs/notes/gpu-wedge-forensics.md`.

⭐⭐ **Budget them:** *reach for the offline rig first, and spend the scarce human-gated measurement
on what the rig cannot see.* The sister project lost a play session to three readings that reached
the code and **could not tell a fixed rule from the broken one it replaced**.

---

## 6. Merge-window schedule

🔴 **REPLACED 2026-08-21 by the review** (old table kept in git history; changes: PR-A and PR-C
were built and gate-checked as ONE branch and land as one PR; that PR absorbs the two R0 blockers;
stage D's branch decision moved behind R0's two-instance tail; W2 owes `record-split-grouping`,
which the old row omitted; M12d gains a D0 gate — §8 q9):

| order | PR | lane | wire cost | owes |
|---|---|---|---|---|
| ✅ 1 | **MERGED as #51.** **PR-1: M12c stages A+B+C — WINDOW W1** (built, integration-verified) **+ absorbs R0 blockers B-1/B-2**: `pbj.checkpoint-load`, `beforeSave` armed by `CheckpointGlue.Write` | L1 | wire-surface hash moves (`Seams.cs`); ModVersion **0.23.0**; protocol stays v9 | `make record-wire-surface` + `record-split-grouping` same commit (both already done in the lane); re-run `dist` + `peer-selftest` after absorbing the blockers |
| ✅ 2 | **MERGED as #52.** PR-2: DebriefProbe + edge counter + runbook + nightfall transcription (built, integration-verified) — fix `rig-run-1-0.md:180`'s `cm.end-combat-*` before merge | L4 | none (verified on the merged tree) | `make dist`; rebase on PR-1 (`ModEntry.cs` adjacency only — the trial merge showed no textual conflict) |
| 3 | PR-3: docs (this PR) — this file's corrections + the review + L2's two docs carrying their owed fixes (`SalvageFinish` rename ×2, `TryToDestroyCombatSite`, the `:268` defeat-gate correction, m12d-plan stage D0) | — | none | doc-only |
| — | **R0** (deploy, mini reading **+ two-instance tail**) — 🧑 **HOLE: the user, at the rig.** ⚠️ Blocked behind the post-reboot GPU ladder (`gpu-wedge-forensics.md` §5); single-instance headless is confirmed so R0·1's stall reading no longer needs the desktop | — | — | numbers into the runbook; decides N and stage D's branch |
| 4 | PR-4: M12c stage D — branch A (wire-neutral) or branch B (**its own wire window**, ordered against W2, never shared) | L1 | A: none — `make dist` proves the hash unmoved · B: protocol bump + re-record | branch per R0's tail |
| 5 | **PR-5: M17 stage 2 — WINDOW W2** 🔴 **R1 MUST RUN FIRST** — this PR's `EndCombatWithOutcome` prefix closes the only route R1·10b (the q9 reading) can use, and the `bypassOnce` hatch that would re-open it ships in this same PR | L3 | wire **v10** + ModVersion **0.24.0** (UnitSnapshot + codec) | 🧑 user authorizes the build (plan §9; 3rd refutation already paid); `make record-wire-surface` **and `record-split-grouping`** same commit; `Closed`/`Faulted` predicate; `pbj.force-end` |
| — | **R1** (deploy, the comprehensive run) — 🧑 **HOLE: the user, at the rig.** ⚠️ Needs the PAIR question answered first (two headless compositors with games in them — unproven, see `gpu-wedge-forensics.md`). **R1·10b must precede PR-5.** | — | — | closes §5's table (with its four dated corrections) |
| 6+ | M12d PRs — **gated on m12d-plan stage D0 + refutation** (§8 q9); then **WINDOW W3** (likely protocol v11) | L5 | per L2's plan | `make record-wire-surface` per wire PR |
| last | **1.0**: `make package` (adds `check-game-hash peer-selftest check-no-drive-channel`, `Makefile:484`) — release ships the mod zip only, never the peer | — | — | 🧑 the user cuts the release |

**Why W1 before W2:** M12c is build-ready today with its refutation already paid; M17 stage 2 owes
a plan reconstruction and a third refutation before any code, so its window arrives later at no
cost. M12c also unblocks the R0 evidence and M12d's floor — it is the critical path; M17 stage 2
is not on anyone's dependency chain (M12c does NOT gate on it — plan KILL 2). Whichever PR enters
its window second rebases and re-runs the full gate on the rebased tree; green is never inherited.

---

## 7. Explicitly NOT on the path to 1.0

- **The split queue (12 source files)** and the #32/#35 grouping re-examination — hygiene. A split
  landing in a file a 1.0 lane touches is a manufactured merge conflict; the queue WAITS.
- **M17 stage 3 beyond the three re-scoped fields, and stage 4** — stage 3 was re-scoped into the
  v10 break (`deathStatus`/`knockedOut`/`ejected` only), stage 4 was cut (`destroyed` is write-only
  in vanilla; status ticks need their own suppression story). Recorded in `m17-review-findings.md`.
- **Host-drop resume** — under host-only checkpoints there is no route (plan §9.4) and pricing one
  reopens the client-checkpoint question. Post-1.0 unless the user pulls it in (§8 Q1).
- **Advanced particle blocks as a feature** — 1.0 needs the READING (R1 #8); the ship/cut decision
  it feeds is content polish, not a blocker.
- **A release before 1.0 / remote-friend compatibility** — settled by the release policy; local
  two-instance playtesting is the verification route.
- **Client self-victory** — ~~impossible by construction~~ 🔴 **CORRECTED 2026-08-21: unreachable
  on the ordinary path; two content-driven routes exist.** The gate at
  `CombatScenarioStateSystem.cs:365` is `flag3 && allowOutcomeVictory` where `flag3` is context
  bit 4 (`:228`); bit 4 has TWO producers — `CombatExecutionEndSystem.cs:59` (host-only, traced:
  sole `Simulating` writer `SimulationTimeSystem.cs:63` ← `TurnSystem.cs:36` ← `ConfirmExecution`
  ← `CommitTurnEffect`, sole emitter `HostSession.Turn.cs:176`) and
  **`CombatScenarioTransitionSystem.cs:68`** (content-driven; and `CombatForceExecution`/
  `cm.execute-by-turns` reach the first producer on a client too). Same gate wraps the defeat arm
  (`:268` is inside `if (flag3)` at `:229`) — a client cannot self-DEFEAT on the ordinary path
  either. Closed by W2's required `EndCombatWithOutcome` prefix. Full chain:
  `m17-stage2-plan.md` §7-KILL-1/§7A, spot-verified by the review.
- **The `pbj_` frame-integrity 1f-reinstall on reload** — same bytes both machines, digest inputs
  unaffected; one line in PR-C's body, not a fix (plan §3.5).

---

## 8. Open questions the plan could not settle

| # | question | cheapest settling experiment |
|---|---|---|
| 1 | Is host-drop resume in 1.0's scope? | 🧑 Ask the user one sentence. If yes: price a client-side mirror of the host's checkpoint bytes (it reopens plan KILL 2 — say so in the pricing). |
| 2 | Does a mid-combat reload drive `ObserveCombatEdge` false→true? | ✅ **ANSWERED 2026-08-21 from the decompile** (`m12c-stage-d-fork.md` §2, review-verified): yes or the load refuses — exhaustive. R0's edge counter is the confirmation, not the decider; the live question moved to the fork reading (spine item 3). |
| 3 | Does M12d need the unbuilt M12b generation-authority half? | L2 B1–B3 — decompile reads only; the probable answer (client reaches neither `OnCombatCompletionLate` caller) is already half-evidenced above. |
| 4 | Do M12d's messages share a window with anything? | Answered by L2 B4; the default is NO — v10 does not stay open waiting for an unscoped milestone. |
| 5 | What does a per-turn save cost, and what is N? | R0 reading 1, with the save-folder census recorded beside the milliseconds. |
| 6 | Do completed/disposed actions survive to the checkpoint gap? | R0 reading 2's action diff — the glue already prints both counts (M3a's diff is the precedent). |
| 7 | Does the per-turn `DataManagerSave.saveName` mutation interact with M11d's `NameForRead`? | ✅ **ANSWERED 2026-08-21 by L1**: it cannot — `LoadingStart` passes the key explicitly and `NameForRead`'s `IsGameGeneratedSaveName` clause leaves the checkpoint's unprefixed name alone; pinned by `NameForRead_DoesNotRedirectTheCheckpointSlotsUnprefixedName` (in PR-1's tests). |
| 8 | Is `SalvageFinish` (*corrected 2026-08-21: formerly the nonexistent `FinishDebriefing`*) deterministic given synced inputs? | Partial: R1 reading 9 fingerprints the host's commit. Full: two-machine comparison during M12d verification — **which now waits on q9**. |
| 9 | 🔴 **NEW 2026-08-21 (review §3) — how does a CLIENT ever enter the debriefing?** No shipped or planned machinery feeds the client's post-combat chain: it cannot resolve its own combat on the ordinary path (§7), `CombatEndMessage` leaves it standing in the fight (`ClientSession.Dispatch.cs:228-246`), any load from combat is a campaign teardown, and W2's prefix closes even the content routes. M12d's D4/D5 presume the screen opens; today nothing opens it. | m12d-plan needs a **stage D0**: relay the host's outcome and drive the client's combat end + debriefing entry in place (new wire semantics + a deliberate prefix bypass), or make the host the sole committer with clients sending claims. One sentence from the user on which shape 1.0 wants would size it. L2's M1 reading stays as the confirmation — its expected value is `exec=0` on the shipped path, permanently, not "before M17 stage 2". |
| 10 | Does shipped scenario content use `CombatForceExecution` or `transitionMode: OnExecutionEnd` (the two content routes §7 names)? | One grep over the game install's Configs (the YAML is not in this repo; `vendor/` holds only `Managed/`). Not a blocker — the prefix closes both routes either way. |

---

*Written by the roadmap lane, 2026-08-21. This file supersedes no design doc; it sequences them.
When a lane finishes, tick the box here in the same PR — a checklist nobody updates is prose, and
prose is the least-checked thing in the commit.*
