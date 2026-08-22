# Next cycle — R1, and what it decides

Cut 2026-08-22 at `main` = `2e3a059`, tree clean. **Short by design.** The previous cycle's plan
(`backlog-2026-08-22.md`) grew 512 → ~1100 lines in a day and closed itself as that day's *record*;
this file is its successor and cites it rather than absorbing it. If this one starts needing
maintenance, it has become a handoff — cut another.

**The whole cycle is one attended rig sitting.** No agent can take it. Everything agent-doable was
done and merged (#60–#66).

---

## 0. State, so nobody re-derives it

`main` **`2e3a059`** · mod **0.24.0** · wire **v10** · **2125 tests at 100%** · split grouping
**1586** · `make dist` exit 0 · `peer-selftest` ALL PASS (11 scenarios, protocol v10, mod 0.24.0).

**Closed since the last cycle:** M17 stage 2 shipped (W2, #60). q9 → measure. q10 → both content
routes live in shipped YAML. **q6 → N = 1 stands** (§9.3 of the runbook: the header walk is
≈814 µs/directory, linear, and a 1 s hitch needs ~700 saves against a self-limiting ~20).

**Still open and NOT this cycle's business:** B11, B12, B13, B15, B16 (small, agent-doable, queued).

---

## 1. The sitting

**Runbook: `docs/notes/rig-run-1-0.md` §4. Follow it; it is current.** This file only says what the
runbook cannot: why the sitting is shaped the way it is.

- **Desktop pair, two instances.** The GPU ladder does **not** gate this — that question is about
  *headless* pairs only, and desktop pairs are proven for the rig's entire life.
- **One sitting, not two.** R1·10b is taken through `pbj.force-end`, alongside 6a–6f which need
  stage 2 deployed anyway. The old "R1 must precede W2" rule was refuted by derivation
  (`cm.force-victory` and `pbj.force-end` make the identical call behind the identical guard, and
  the prefix patches `EndCombatWithOutcome` itself, so the bypass is structural).
- 🔴 **6f and 10b are ONE ACT, not two numbered readings.** `pbj.force-end victory` **ends the
  fight**; it is not a probe. Taken where the table numbers it, it destroys the preconditions of
  6d, 7, 9, 10 and 10b. Phase 4, last: `pbj.debrief-probe` → `pbj.force-end victory` →
  `pbj.debrief-probe`. The return string is 6f; the probe pair is 10b.
  ⭐ **The general rule: a reading whose instrument MUTATES the thing being read cannot be
  scheduled by its number.** R0 already obeyed it (readings 2+3 as one load, last).
- ⚠️ **`pbj.force-end` has never executed against a real game.** R1·6f is its first flight. Its five
  branches return distinct strings and it fails loudly by construction; the *class* is proven
  (`pbj.net-status` registers through the identical `TryAddCommand` path and was read over the drive
  channel throughout R0). If its return string does not surface, THREW/NOT ARMED are silent — check
  that first, before believing any 6f result.

**What R1·10b decides:** M12d stage D0's shape. `present=True entered=True` ⇒ Shape A (vanilla
salvage UX). Otherwise Shape B (host sole committer, screen rebuilt mod-side). Nothing in M12d's
build starts until this lands.

---

## 2. Also owed at the rig, cheaply, while you are there

- **The stage-D fork reading** (H-3) — ownership survival across an M12c resume. Two instances.
- **NW-k** — do free drops reach the salvage grid? Gates M12d's D4. One look at the grid after a
  fight that produced them; M2's per-unit fingerprint is the instrument.
- **NW-i** — the RTT fork for M12d's capture prefixes. Settle before those prefixes are written.
- **The single-frame-stall ceiling** (H-12) — the unit every timeout in this codebase actually
  depends on, measured so far only for the checkpoint write (433/455 ms). Worst frame gap during
  entry/save.

---

## 3. Decisions waiting on the user, none blocking the sitting

| # | question | shape of the answer |
|---|---|---|
| H-7 | campaign ownership after host migration | A "the fight ends the session" (recommended, and H1/H2 are identical under all three) · B survivor's campaign consumes it · C old host's on return |
| H-8 | N>2 host migration | REFUSE (recommended). At N>2 the mechanism is *under-specified, not wrong* — only *which* client promotes is missing |
| H-11 | H1 vs H1-lite | H1-lite promotes from the scenario slot the client already holds: **no message type, no protocol bump, no wire window**, but resumes the fight from its start. **H1 is M18's only window-needing stage**, so this decides whether a W3 slip can push M18 out of 1.0 |
| H-13 | should `tools/cite-check` gate `dist`? | Gating the build on prose line numbers fails every edit above a cited line. Policy call |
| B5b·2 | — | ✅ done; the bulk-save gate is discharged |

---

## 4. The rules this cycle inherits, in one place

- **A reading whose instrument mutates what it reads is an ACT, ordered by what it destroys.**
- **Do not record a number without reading its `ZERO:` line.** Every runbook reading has one.
- **A grep returning zero is a claim about your PATTERN** — control it against a known-present hit.
- **Read the exit code AND the tail.** Never grep a build for success words.
- **`dmesg` is permission-denied here and returns nothing** — use `journalctl -k`; match `NVRM`, and
  ignore the r8169 `XID 609` decoy.
- **Teardown reaps by exact name with a canary** — `pkill -f 'gamescope -W'` matches nothing.
- ⚠️ **`make deploy` takes >10 min wall here.** Budget it.
- ⚠️ **`grep -c` exits 1 on zero matches**, so `RUNNING=$(... | grep -c ...)` under `set -e` aborts
  precisely when the machine is clean (B16).

Prior cycle: [`backlog-2026-08-22.md`](backlog-2026-08-22.md) · Runbook:
[`../notes/rig-run-1-0.md`](../notes/rig-run-1-0.md)
