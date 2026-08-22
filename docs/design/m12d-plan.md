# M12d — assigned gear and the salvage screen: the build plan

Written **2026-08-21** by lane L2 at `main` = `1708a0b`, mod 0.22.0, wire v9. No production code was
written to produce it. It sits on top of, and is only valid because of, the dated verdict at the end
of `docs/design/m12-concurrent-management.md`.

**Citation rule, inherited:** every `file:line` here was opened in this working tree on the date
above unless marked **UNVERIFIED**. `decompiled/` paths rot when the game updates; `src/` paths rot
when a split lands. Every citation also names the member — **navigate by member and re-derive the
number before quoting it onward.**

---

## 0. The verdict this plan stands on

**M12b's unbuilt mission-generation-authority half does NOT gate M12d.** Verdict (b), settled
2026-08-21 — see `m12-concurrent-management.md`, "✅ VERDICT 2026-08-21".

Two claims that were load-bearing before this lane ran are now dead, and this plan is written
against their corpses rather than against them:

- 💀 **"The salvage item set problem is solved by `savedOutput`, so M12b subsumes it."** True for the
  *free reward groups* (`EquipmentUtility.cs:1153-1156` routes to `PrepareRewardsFromSavedOutput`
  whenever `savedOutput != null`, and `ScenarioSetupUtility.cs:724` makes it non-null for every
  group at scenario setup). **False for the priced, unit-mounted list** — the one the budget is
  spent on — which is built five lines earlier by `PrepareUnitForSalvage`
  (`OverworldCombatOutcomeProcessingSystem.cs:225`) from local ECS state plus a
  `UnityEngine.Random.Range(0f, 1f)` draw at `EquipmentUtility.cs:2763`.
- 💀 **"A client never reaches the post-combat chain, so there is nothing to reconcile."** Nothing in
  `src/` suppresses any link of it (grep proven non-vacuous — see the verdict section), and M12d's
  own design *requires* a client debriefing, because the commit funnel
  `EquipmentUtility.ProcessSalvageSelections` (`:1805`) runs over each machine's own local entities.

⇒ **M12d owns host-authoritative salvage, end to end.** It is bigger than the design doc's
"it shrank twice over" paragraph implied, and the plan below is sized for that.

---

## 1. What actually diverges, stated as wiring

Everything post-combat is one `Execute` on whichever machine resolved its own combat
(`OverworldCombatOutcomeProcessingSystem`, trigger `PersistentMatcher.CombatOutcomeProcessing.Added()`
at `:38`). Inside it, in order:

| line | what it does | diverges? | why |
|---|---|---|---|
| `:217` | `flag6 = ScenarioUtility.IsUnitActive(...)` per enemy unit | **YES** | local end-of-combat ECS state; M16 measured host VICTORY / client still fighting (UNVERIFIED here — from the M16 memory, not re-run) |
| `:223` | `if (currentScenario.coreProc.lootingUsed)` | no | scenario data, same bytes both machines |
| `:225` | `PrepareUnitForSalvage(unit, sitePersistent, flag6)` | **YES** | see below |
| `:230` | `TriggerPostCombatRewards(...)` | **no** | `savedOutput` rides the shipped save |
| `:346` | `CIViewOverworldDebriefing.EnterAfterCombat(...)` | n/a | sole caller of the debriefing's after-combat entry |
| `:353` | `OnCombatCompletionLate` (only when `debriefingUsed == false`) | — | the `:2818` route is the normal one |

`PreparePartForSalvage` (`EquipmentUtility.cs:2718`) sets each part's `isSalvageable` from:

- `part.isWrecked` and `DifficultyUtility.GetFlag("combat_salvage_allows_destroyed")` (`:2753-2757`);
- `unitPersistent.isUnitSalvageExempted || (!part.isWrecked && unitEscapes)` (`:2749-2752`);
- **a live `UnityEngine.Random.Range(0f, 1f)` against `combat_salvage_drop_chance`**
  (`:2758-2767`), reached only when `!OverworldUtility.AreFeaturesChecked() && !IsUnitFriendly(unit)`;
- tag rules on the part and its subsystems (`:2769-2787`).

⇒ **Two independent divergence sources, one stochastic and one from roster state. Neither is
downstream of anything M12b would have replicated.**

### The one structural fact that makes this cheap

`PrepareUnitForSalvage` never creates or destroys entities. It sets flags on entities that already
exist, and those entities come from the **shipped save** — the same bytes on both machines
(`CombatShipGlue.Write` → `DataManagerSave.DoSave(LobbySaveNames.ScenarioSlot, …)`,
`src/PBAndJ.Mod/Net/CombatShipGlue.cs:206-207`; restore at
`DataManagerSave.cs:2554-2558` for the combat description and the part/unit restore path around
`:2717-2733`).

⇒ **M12d replicates a bit-set, not a list of items.** The host broadcasts *which of the parts both
machines already have* are salvageable, and each machine writes that onto its own entities. No
entity is created from the wire. This is the whole design.

---

## 2. The mechanism

### 2.1 Authority: correct after, do not suppress

🔴 **CORRECTED 2026-08-21 by the cross-lane review — this section's premise does not hold on a
client.** "Let `PrepareUnitForSalvage` run on every machine, then overwrite the flags" assumes the
method fires on a client. It never does: the whole post-combat chain hangs off `ReplaceCombatResolved`
(sole producer `ScenarioUtility.cs:3586`, inside `EndCombatWithOutcome`), which a client cannot reach
on the shipped path — and M17 stage 2's required prefix closes the content-driven routes too. So on a
client there is nothing to postfix, no screen to drive, and no funnel to commit. **This section is
correct for the HOST and unbuildable for a client until §D0 below settles the entry.** The M1 reading
in §5 should expect `exec=0` **permanently on the shipped path**, not "before M17 stage 2" — stage 2
moves that answer in the other direction. See `road-to-1-0-review.md` §3.

⚠️ **Do not patch `PrepareUnitForSalvage` away on the client.** Its first act is
`unit.ReplaceEntityLinkPersistentParent(inventory.id.id)` and `unit.isUnitSalvageable = true`
(`EquipmentUtility.cs:2709-2710`) — the unit reparenting the whole debriefing depends on.
Suppressing it wholesale breaks the screen for the reason that has nothing to do with salvage.

**Instead: let it run on every machine, then overwrite the flags from the host's bit-set before the
screen opens.** The window is between `:225` and `:346` in the same `Execute` — but a Harmony
postfix on `PrepareUnitForSalvage` is a cleaner seam, because it fires per unit and the mod already
patches by string name elsewhere.

On the host the same postfix *captures* rather than applies. One code path, one flag.

### 2.2 Identity

- **Unit-mounted gear:** `(kind, serial)`, where the serial came out of the shipped save. Both
  machines restore the same serial (`CreatePartEntityBase(..., partSerialized.serial)` on the
  non-regeneration branch, `DataManagerSave.cs:2728`).
  ⚠️ **Not guaranteed:** the version-regeneration branch at `:2717-2725` mints a *fresh* serial from
  a per-process counter. Same save ⇒ same counter seed and, if iteration order is stable, the same
  sequence — **an alignment to measure (M4), never to rely on.**
- **Everything minted after the split** (free drops from `savedOutput` — created with **no serial
  override**, `EquipmentUtility.cs:1214` and `:1236`): **the host assigns the identity clients quote
  back.** The design doc's rule, kept.
- ⚠️ **The key needs the kind.** Two counters (`DataHelperStats.cs:14, :16`) whose live ranges
  overlap; parts and subsystems share the salvage list.

### 2.3 Ownership tags — and the `DataBlockSavedSubsystem` hole, re-verified

**Verified against the decompile today, not inherited:**

| | parts | subsystems |
|---|---|---|
| saved class | `DataBlockSavedPart` — **has** `customTags` | `DataBlockSavedSubsystem` — **eight fields, no tags**: `serial, blueprint, livery, destroyed, fused, salvageable, inventoryAdded, favorite` |
| written | `DataHelperSaveSerialization.cs:1313-1324` | `CreateSavedSubsystem` `:1328-1346` — never reads tags |
| restored | `DataManagerSave.cs:2744-2746` | `serial` survives: `CreateSubsystemEntity(blueprint, livery, inventoryAdded, serial)`, `DataManagerSave.cs:1905` |

So a `pbj_owner_<peer>` tag on a subsystem is **silently dropped by every save, and the transfer to
a client is a save.** (Measured 606 in / 0 out, twice, including across a restart — **UNVERIFIED
here**; that is the 2026-08-08 run recorded in the design doc, not something this lane re-ran.)

**The riders/standalone analysis holds — re-verified line by line:**

- `ProcessSalvageSelections` (`EquipmentUtility.cs:1805`) walks
  `GetPartsInUnit` and, when `part.isSalvageable`, processes the part and **`continue`s past its
  subsystems** (`:1887-1901`). Riders are never listed or priced separately ⇒ **their ownership is
  the part's ownership**, stored in the tag that provably round-trips.
- The loose-inventory loops price at `costMultiplier: 0f` (`:1844`, `:1862`) and
  `GetSalvageCost` returns 0 for `costMultiplier <= 0f` (`:1616-1645`) ⇒ **free drops and loose
  inventory never touch the budget.**
- ⇒ **The subsystem tag hole does not touch the salvage budget.** Confirmed.

🆕 **And a structural fact the design doc does not have, which makes inheritance the natural
mechanism rather than a workaround:** an installed subsystem is serialised *inside* its part, as
`DataBlockSavedPart.systems : Dictionary<hardpoint, DataBlockSavedSubsystem>`
(written `DataHelperSaveSerialization.cs:1306-1312`, restored via `UnitUtilities.CreateSubsystemsFromSave`
at `DataManagerSave.cs:2733`). The save file already expresses "this subsystem belongs to that part".
Inheritance needs **no new storage at all** — not a side table, not a message, nothing.

⇒ **The `(kind, serial)` side table is for LOOSE inventory subsystems only, and it is NOT on the
salvage screen's critical path.** The first live salvage screen contained zero subsystems across 12
groups (design doc, 2026-08-08, **UNVERIFIED here**). The side table belongs to the *assigned gear*
half, where the same probe counted 606. **It is deferred to stage D6 and can be cut without
blocking the screen.**

### 2.4 The budget

Host computes once and broadcasts. Non-negotiable, and the reason is stronger than the design doc's:

- It is one `int` computed at screen open from preset × difficulty + per-group offsets + base and
  per-unit `salvage_bonus` + world-memory modifiers (`CIViewOverworldDebriefing.cs:2117-2210`), and
  **two of the modifiers are consumables removed as the screen opens** (`RemoveMemoryFloat` at
  `:2168` and `:2183`). Reopening yields a different number.
- 🆕 **And the vanilla computation has a defect a reimplementation would not reproduce:** the
  *consumable* multiplier's contribution is computed from `value` — the **non-consumable** memory's
  out-var — while the consumable's own value is discarded (`out var _` at `:2162`, used at
  `:2165`). Whatever that is, it is what ships. A mod-side recomputation would disagree with the
  game on any save where those two memories differ.

⇒ **Never derive the budget independently on any machine.** Host reads its own
`salvageBudgetLast` after the screen opens and broadcasts the integer.

### 2.5 The pool split ▸ ✅ **DECIDED 2026-08-22 — ONE SHARED BUDGET. Equal pools are DEAD.**

🔴 **The sentence this section opened with, and what killed it.** It read:

> *"Equal integer pools, one per present player, remainder discarded."*

**Killed by this plan's own KILL 7** (§7): *"Equal pools, remainder discarded, is obviously right"*
— not a technical claim at all, **and it can make an item unclaimable by anyone that a solo player
could afford.** That objection was escalated as §8 Q1 rather than built on; the user answered it on
2026-08-22 and answered it against equal pools. Recording *why* it died matters more than recording
that it did: the plan refuted its own premise, and the decision is that refutation being accepted.

**The decision, stated as wiring.** Every participant sees and draws from **the same**
`salvageBudgetLast`. Selections replicate (§2.1's correct-after model, unchanged). Nobody commits
until the barrier releases (§2.6, unchanged). There is no per-player pool, no remainder, and no
reservation arithmetic — the contended resource is one integer that everyone can see, and the
barrier is what makes contention resolvable rather than racy.

🔴 **The correction the decision as first relayed did NOT carry, and it is the load-bearing one.**
The relay said *"vanilla's own `salvageCostValid = salvageCostTotal <= salvageBudgetLast` gate does
the enforcing."* **It does not.** Read on 2026-08-22:

- `salvageCostValid` is computed inside **`CIViewOverworldDebriefing`, member
  `SalvageRefreshBudget`** (`decompiled/CIViewOverworldDebriefing.cs:2351`), and its only
  consequences at `:2368-2378` are **UI**: `salvageButtonFinish.available = salvageCostValid`,
  `salvageBudgetButtonDetails.available`, a label colour, the finish-label string, a tooltip, and a
  fill amount. It is a **redraw**, not an admission check. Anything that reaches the commit without
  passing through that redraw is unenforced.
- The commit funnel's own over-budget arm **lets the process continue by design**:
  `EquipmentUtility`, member `ProcessSalvageSelections` (declared `:1805`), `:1903-1906` —
  `if (budget < costTotal) { Debug.LogWarning($"Salvage operation cost {costTotal} exceeds budget
  {budget} | … letting the process continue..."); }` and then transfers the parts anyway. The
  warning text says out loud that it is not a gate.

⇒ **Enforcement is 100% mod code** — which this section already said and the relay lost. Concretely,
it must live in **two** places, not one: **(a) host-side claim admission** (a claim that would push
the merged total over the budget is refused at the host when it arrives, not repaired later), and
**(b) the merged total as a precondition of barrier release** (the barrier does not release while
the merged selection set is over budget). Neither can be a UI state; the UI is a mirror of (a).

⚠️ **UNVERIFIED, and B4 must open it before designing around it:** §2.6 proposes releasing the
per-machine commit with `buttonConfirm.callbackOnClick.Invoke()`. **Whether invoking the callback
directly bypasses the button's `available` flag is not established here** — `available` is set on
the button object, and a direct `Invoke()` on the delegate plausibly ignores it. If it does bypass,
the vanilla UI gate is not even a backstop on the drive path, which strengthens (a)+(b) rather than
weakening them; if it does not, the drive path can fail silently on an over-budget machine. **Open
the button class and settle it** — do not assume either way.

⚠️ Per-group `costMultiplier` is not 0-or-1 — a recovered player frame priced at 0.4 while enemy
units priced at 1.0 (design doc, 2026-08-08, **UNVERIFIED here**), and `GetSalvageCost` applies the
multiplier only when `< 1f` (`:1640-1643`). **Still true and still needed** under one shared budget:
the merged total (a)+(b) enforce is a sum over each group's own multiplier, and scrap and recover
are separately priced (`costDismantle*` vs `costTake*`, each intact/damaged) — a scrap is not free.

⇒ **Stage D3's `SalvagePool` is dead as specified and must be re-specified or removed. That design
work is item B4 of `docs/design/backlog-2026-08-22.md`, not this correction's** — this section
records the decision and the enforcement finding; B4 writes the replacement and refutes it.

### 2.6 The barrier and the commit

Third barrier in the codebase; it inherits `TurnBarrier`/`LobbyBarrier`'s traps
(`src/PBAndJ.Core/Net/LobbyBarrier.cs`, 191 lines, is the shape to copy):

- **edge-triggered, not level-triggered**;
- **a departing peer must not satisfy it**;
- a version counter so a stale ready is recognisable (`LobbySelection.Version`, the lobby's
  analogue).

Commit is per-machine and the funnel is single (`CIViewOverworldDebriefing.cs:2825` calls
`ProcessSalvageSelections`; `SalvageFinish` is reached from `OnSalvageFinish` at `:2741`, wired to
`salvageButtonFinish.callbackOnClick` at `:541`). The game's own confirmation modal is the natural
per-player confirm, with the barrier releasing `buttonConfirm.callbackOnClick.Invoke()` on each
machine once everyone has agreed (⚠️ **UNVERIFIED, flagged 2026-08-22, and B4 must settle it by
opening the button class: whether a direct `callbackOnClick.Invoke()` bypasses the button's
`available` flag.** `available` is what `SalvageRefreshBudget` sets from `salvageCostValid`
(§2.5), so if `Invoke()` ignores it, the vanilla UI gate is not even a backstop on the drive
path — which is an argument for host-side admission, not against this mechanism) (drivability measured end to end 2026-08-08 — **UNVERIFIED here**,
but the public entry points are real: `OnSalvageRecover/Scrap/Skip(int)` at `:2026, :2031, :2036`,
the `…Unit(int)` trio at `:1868, :1873, :1878`, `OnStageNextExternal()` at `:760`).

**Also outside the funnel:** `TransferStandaloneRewards` (`EquipmentUtility.cs:1733`, sole caller `CIViewOverworldReward.cs:157`) is a second,
budget-free transfer path for non-combat reward popups. Ownership must follow it too — stage D6.

### 2.7 Disconnect

Roll back to M12c's checkpoint. Never forfeit, never redistribute, never auto-recover. The real
hazard is a **stall**, not lost loot: `SalvageFinish` is click-driven, so a machine that never
clicks never commits.

⚠️ **This is weaker than the design doc claims and the plan says so.** See §6 KILL 5.

---

## 3. Core versus Mod — and why the coverage gate decides it

**TDD with 100% line/branch/method coverage is a hard deploy blocker in this repo.** The consequence
that shapes this plan is not "write tests"; it is that **a branch nothing can reach fails the
build.** `CombatShipGlue`'s own doc block records the precedent: a `CanSave` guard in Core "would be
a branch nothing could reach, which the coverage gate turns into a build failure"
(`src/PBAndJ.Mod/Net/CombatShipGlue.cs:19-20`).

⇒ **The rule for M12d: Core decides *what*; Mod decides *whether the game will let it*.** Every
"can we?" against live game state lives Mod-side.

**Core (`src/PBAndJ.Core/Net/`, tested at 100%, `tests/PBAndJ.Core.Tests`):**

- `SalvageKey` — the `(kind, serial)` value type, ordering, formatting.
- `SalvageOwner` — the `pbj_owner_<peerId>` tag format and parser. ⚠️ **`pbj_` prefix is mandatory**:
  the tag namespace is live (`flag_no_damage`, `flag_no_loss` are read by `CombatDamageSystem`).
- `SalvageManifest` — the host's bit-set: per `(unitKey, SalvageKey)`, `salvageable` and the
  group's `costMultiplier`. Pure data + a merge/apply function returning "what to change".
- `SalvagePool` — the integer arithmetic: budget → per-player pools, remainder, per-group multiplier
  costs, `dismantle` vs `take`, over-budget detection.
- `SalvageBarrier` — the state machine, mirroring `LobbyBarrier`.
- Message types + codec arms (§4).
- The `HostSession`/`ClientSession` dispatch arms and the `PbjEffect` kinds that carry the work out.

**Mod (`src/PBAndJ.Mod/Net/`, every type `[ExcludeFromCodeCoverage]`):**

- The `PrepareUnitForSalvage` postfix (capture on host, apply on client).
- Reading `salvageBudgetLast` off the live view and reporting it.
- Driving selection through the public statics; releasing the confirm modal.
- The probe (§5) and every `Contexts.sharedInstance` read.

⚠️ **Two existing Core invariants M12d must not break:**
1. `PbjMessage`'s constructor is `protected` and `Type` is `abstract` **so the test suite can define
   an out-of-range subclass and reach `PbjMessageCodec`'s `default:` arm**
   (`src/PBAndJ.Core/Net/PbjMessage.cs:63-66`). Do not seal either.
2. Messages carry no validation; sessions do (`docs/design/networking.md`).

---

## 4. The wire: yes, a break — and its own window

**M12d needs new `PbjMessageType`s.** The enum currently ends at `ReplayAssets = 32`
(`src/PBAndJ.Core/Net/PbjMessage.cs:56`). Proposed additions:

| type | direction | payload |
|---|---|---|
| `SalvageManifest = 33` | host → clients | the authoritative bit-set + per-group `costMultiplier` + the host-minted identities for free drops |
| `SalvageBudget = 34` | host → clients | the single `int`, read from the host's live screen |
| `SalvageClaim = 35` | client → host | one selection change: key, `recover`/`scrap`/`skip` |
| `SalvageState = 36` | host → clients | merged selections + per-peer spend, so another player's picks render as `reserved` |
| `SalvageReady = 37` / `SalvageRelease = 38` | both ways | the barrier |

`PbjMessage.cs` and `PbjMessageCodec.cs` are **both** `WIRE_FILES` (`Makefile:43-47`; line 44 lists
them). `Seams.cs` moves too if `IPbjGameBridge` gains apply/capture members — also a `WIRE_FILE`
(`Makefile:47`).

⇒ **Protocol v9 → (M17 stage 2's v10) → v11, ModVersion bump, `make record-wire-surface` in the same
commit, in M12d's own merge window W3.** M12c owns W1 (`Seams.cs`); M17 stage 2 owns W2
(`UnitSnapshot.cs` + codec). Two writers to one `wire-surface.lock` is two windows, always.

**M12d cannot share W2.** Not for a file reason but for a process one: its messages have not been
designed in detail, let alone adversarially refuted, and v10 does not stay open waiting for an
unscoped milestone (`road-to-1-0.md` §8 Q4).

---

## 5. What M12d needs from the rig

Each of these is written to fold into the single comprehensive run in `road-to-1-0.md` §5, as extra
readings on the **host-victory / debriefing** leg (that run's reading 9). Each carries the line the
project's twenty-four-sightings rule demands: **what a zero would mean.**

| # | reading | instrument | what a ZERO means |
|---|---|---|---|
| **M1** | does the CLIENT run `OverworldCombatOutcomeProcessingSystem` after a shipped fight? | postfix counter on its `Execute`, printed beside `combatResolved=<bool>` and `gameState=<string>` | `exec=0, combatResolved=False` = the client never resolved its own combat (roster divergence — expected before M17 stage 2). `exec=0, combatResolved=True` = it resolved but never re-entered `"overworld"`, which is what `OverworldCombatCompletionSystem:21` gates on. **Print all three or the two cases are one reading.** |
| **M2** | salvage-list divergence, host vs client, same fight | fingerprint of the built list — sorted `(kind, serial, preset/blueprint, isWrecked, cost)` — on each machine, printed beside `lootingUsed=<bool>` and `units=<n>` | `entries=0` with `exec=0` is M1's answer, not a list defect. `entries=0` with `exec=1` and `lootingUsed=False` = the scenario has no looting (`OverworldCombatOutcomeProcessingSystem:223`), a scenario-selection problem, not a mod one. `entries=0` with `lootingUsed=True` is the real signal. |
| **M3** | is the drop-chance roll even reached? | print `featuresChecked=<bool>` (`OverworldUtility.AreFeaturesChecked`) and `dropChance=<float>` beside M2 | `dropChance=0` with `featuresChecked=True` = the roll is short-circuited in this campaign and the **only** divergence source is roster state — a materially cheaper M12d, so this reading can shrink the milestone. `dropChance=0` with `featuresChecked=False` = every enemy part refused, so `entries` must be 0 too; if it is not, the instrument is pointed at the wrong thing. |
| **M4** | is `(kind, serial)` machine-portable across the shipped save? | `pbj.mg-serials` on both machines right after the client loads the host's fight, printed beside a **regenerated-part count** | identical fingerprints = portable for pre-existing gear. A *difference* is only interpretable next to the regeneration count (`DataManagerSave.cs:2717-2725` mints fresh serials) — without it, "different" and "N parts were rebuilt" are two hypotheses for one number. |
| **M5** | subsystem tag loss on the **transfer** path specifically | tag N parts and M subsystems on the host, ship the fight, count on the client; print the pre-save counts beside the post-load ones | `subsystemsTagged=0, partsTagged>0` = the known hole confirmed on the real path. **Both zero** = the tag write never ran — an unrun write and a lost tag both print 0 on the far side, so the near-side counts must be in the same line. |
| **M6** | `SalvageFinish` post-commit determinism (design q7) | fingerprint of world memory / province ownership / `ClearActionsAfterCombat` effects, before and after commit, **on both machines** (`CIViewOverworldDebriefing.cs`, `SalvageFinish` declared `:2772`; `isProvinceOwnershipRefresh` `:2786`, `ClearActionsAfterCombat` `:2817`, `OnCombatCompletionLate` `:2818`) | `entered=False` = wrong moment; L4's `pbj.debrief-probe` prints it. ⚠️ **One machine measured twice is not this reading** — the question is host-vs-client on one run, and it cannot be taken until stage D5 makes both machines commit. Recorded as not-closable earlier rather than faked. |

M1–M5 are readable on the R1 run as extra `pbj.` calls at reading 9. **M6 waits for D5** and gets its
own two-instance session; the plan says so instead of pretending.

---

## 6. Stages — one PR each

| stage | contents | wire | gate |
|---|---|---|---|
| **D0** | 🔴 **DESIGN ONLY — added 2026-08-21, and D4/D5 are not real until it lands.** Settle how a client ever enters the debriefing (see §D0 below). Output is a design + a refutation pass, not code. | n/a | adversarial review pass, per project law |
| **D1** | **Core, wire-neutral.** `SalvageKey`, `SalvageOwner` (tag format + `pbj_` prefix rule), `SalvageManifest` (bit-set + apply-diff), 100% tests. No game types. | none | `make dist` exit 0 (read the tail AND the exit code) |
| **D2** | **Mod probe, wire-neutral.** `SalvageProbeGlue`: the `PrepareUnitForSalvage` postfix in *capture-only* mode, plus `pbj.salvage-probe` printing M1–M3 and `pbj.owner-tag`/`pbj.owner-read` for M5. Ships before any authority code so the milestone is scoped on numbers. | none | `make dist`; every zero prints its own alternative hypothesis or the probe does not ship |
| **— R** | **Rig: M1–M5.** Folded into R1 if the timing works; otherwise its own short session. **D3's shape forks on M3.** | — | numbers into `docs/notes/rig-run-1-0.md` |
| **D3** | **Core, wire-neutral.** `SalvagePool` arithmetic (per-group `costMultiplier`, dismantle/take, remainder) and `SalvageBarrier` (edge-triggered, departing peer does not satisfy, version counter). Tested against a fake bridge. | none | `make dist` |
| **D4** | **WINDOW W3 — the wire.** The six message types, codec arms, `PbjProtocol` v10→v11, `IPbjGameBridge` capture/apply members in `Seams.cs`, ModVersion bump, `mod/metadata.yaml`, `make record-wire-surface` **in the same commit**. Host/client dispatch arms + effects. | **v11** | `make dist` exit 0; `make peer-selftest` WILL change and the PR body says so |
| **D5** | **Mod, the screen.** Apply the manifest in the postfix; broadcast the budget off the live view; drive selection through the public statics; release `buttonConfirm.callbackOnClick.Invoke()` on barrier satisfaction; rollback-to-checkpoint on disconnect. | none | `make dist`; hash proven unmoved |
| **— R** | **Rig: M6 + two-machine acceptance.** Identical salvage lists, identical commits, reserved markers, a stall survived. 🧑 partly eyes. | — | into the runbook |
| **D6** | **Assigned gear, the other half.** Loose-subsystem `(kind, serial)` side table riding the transfer; ownership through `TransferStandaloneRewards` (`EquipmentUtility.cs:1733`); the general-inventory claim granularity (design q4). **Cuttable without blocking the screen.** | possibly one more | `make dist` |

**Order note:** D3 before D4 deliberately — the barrier and the arithmetic are pure Core and can be
written, tested and refuted while W2 is still open, so W3 opens with only mechanical work left in it.

---

## 6b. 🔴 D0 — the client's debriefing entry (added 2026-08-21; blocks D4/D5)

**The gap, stated as wiring.** Nothing shipped or planned feeds a client's post-combat chain:

1. It cannot produce the outcome. The chain hangs off `ReplaceCombatResolved` (sole producer
   `ScenarioUtility.cs:3586`, inside `EndCombatWithOutcome`), consumed by
   `OverworldCombatCompletionSystem` only when the **same machine** transitions in place to
   `"overworld"`. A client reaches neither the victory nor the defeat arm on the ordinary path —
   both sit inside the `if (flag3)` bit-4 gate (`CombatScenarioStateSystem.cs:229`).
2. `CombatEndMessage` leaves it **standing in the loaded fight** with the execute lock deliberately
   held (`ClientSession.Dispatch.cs:228-246`).
3. It has **no in-place route to its own overworld**: any load from inside combat is a campaign
   teardown (`TryLoading` → `TeardownCampaignSystem` → `DestroyEntitiesInGroup(persistentGroup)`),
   which destroys the very entities the salvage flags live on.
4. **M17 stage 2 then closes even the accidental routes** — after W2 the `EndCombatWithOutcome`
   prefix means a client can never open a debriefing by itself, content routes included.

⚠️ Every fact above was already known — item 3 is `m17-stage2-plan.md` §4.1, written for the
opposite purpose (why `isWrecked` needs no clearing). **Four lanes and the roadmap each held a piece
and nobody joined them.** That is the failure mode of briefing several agents from one document.

**The two shapes, and they are genuinely different milestones.** This is a product decision, not a
technical one, and it is escalated rather than assumed:

- **Shape A — drive the client into its own debriefing.** The mod relays the host's outcome and
  drives the client's combat end + debriefing entry in place. Needs new wire semantics, and must
  coexist with the M17 prefix through an explicit bypass — the `bypassOnce` shape
  `m17-stage2-plan.md` §4.2 already sketches for the rig's escape hatch. Keeps the vanilla salvage
  UX and both players' agency in the screen. Costs: a second bypass path through a patch whose whole
  job is to be unconditional, and a client running a screen over a combat scene **PB has no view
  stack to restore from** — unmeasured, assume no until the rig says otherwise.
- **Shape B — host as sole committer.** No client debriefing at all. The host runs the only
  debriefing; clients send claims over the wire and see the result. Sidesteps the entry problem
  entirely and needs no bypass. Costs: the salvage UX is rebuilt mod-side rather than driven, the
  client's experience is a custom screen rather than the game's, and D5 stops being "drive the
  public statics" and becomes real UI work.

✅ **DECIDED 2026-08-21 by the user: measure first.** Neither shape is chosen; the deciding reading
is **R1·10b** in `docs/notes/rig-run-1-0.md`, and it turned out to need **no new code** —
`cm.force-victory` calls `EndCombatWithOutcome` directly, bypassing the bit-4 gate that blocks the
ordinary path (`decompiled/PhantomBrigade.DebugConsole/ConsoleCommandsCombat.cs`, members
`ForceVictory` and `CombatStateCheck`; verified in the decompile). **That decision stands.**

🔴 **CORRECTED 2026-08-22 — the ordering constraint this section claimed does not exist.** The
sentence was:

> *"🔴 **Ordering constraint this creates:** the reading **must be taken before M17 stage 2 merges**
> — that PR's prefix closes the route it uses, and the `bypassOnce` hatch that would re-open it
> ships in the same PR. ⇒ **R1 before W2.**"*

Read literally it refutes itself: **closed and re-opened in one commit is not closed.** The two
routes were derived side by side on 2026-08-22 and are **call-for-call identical** for this
reading — `pbj.force-end victory` (`src/PBAndJ.Mod/Net/DestructProbeGlue.cs`, member `ForceEnd`)
takes the same `IsGameState("combat")` guard, sets `WreckingPatches.BypassCombatEndOnce`, makes the
**same** `ScenarioUtility.EndCombatWithOutcome(resolved, early: true)` call synchronously, and
clears the flag in a `finally`; the prefix short-circuits on that flag
(`src/PBAndJ.Mod/Net/WreckingPatches.cs`, member `SuppressCombatEnd`). Same method, same arguments,
same precondition ⇒ no argument for ordering survives except "the hatch has never been driven",
which is a risk, not a constraint.

⇒ **The user ruled W2 FIRST (2026-08-22). M17 stage 2 merged as PR #60** (`main` = `d3a3b3b`, mod
0.24.0, wire v10). **R1·10b is taken through `pbj.force-end`**, in the same sitting as R1·6a–6f,
which need stage 2 deployed anyway. Full derivation: `docs/design/backlog-2026-08-22.md` §D.
Nothing about D0's *question* changes — only the console command that asks it.

⚠️ Item 4 above ("M17 stage 2 then closes even the accidental routes") is now **present tense**:
after #60 a client can never open a debriefing by itself, content routes included. That makes D0
mandatory rather than likely, and it is why the backlog schedules a **stage D0** ahead of D4/D5.

**What does not change either way:** D1–D3 (Core, wire-neutral) stand as written, and §5's M1–M3
readings remain the right instruments — M1's expected answer is now `exec=0` permanently.

---

## 7. Self-refutation pass

Project law (`adversarial-plan-review`): a plan that refutes none of its own premises is unread.
Seven claims died writing this; the ones that survived are noted with what would kill them.

**KILL 1 — "The client never reaches the debriefing, so the gate is stale for that reason."**
This is the premise this lane was handed. **Dead.** Nothing in `src/` suppresses the chain (grep
proven non-vacuous against `OverworldSkySystem`=4 and `CombatExecutionEndLateSystem`=4, both really
patched), and `road-to-1-0.md` §7's mechanism — "`CombatScenarioStateSystem.cs:365` gates on a bit
only the host produces" — is not what `:365` does: `flag3` is
`ScenarioStateRefreshContext.OnExecutionEnd` (`:228`), raised by `CombatExecutionEndSystem.cs:59` on
`Simulating.Removed()`, which any machine that simulates a turn reaches. **The verdict was
re-derived by a route that does not need this premise.**

**KILL 2 — "`savedOutput` solves the item set."** Dead; it solves the free half only. §0.

**KILL 3 — "Suppress the client's `PrepareUnitForSalvage`."** Dead on reading `:2709-2710`: its
first act is the unit reparenting the debriefing needs. Replaced by *let it run, then correct*,
which is also one code path instead of two.

**KILL 4 — "`(kind, serial)` is a machine-portable key."** Demoted, not dead. It is portable for
gear that came out of the shipped save on the non-regeneration branch (`DataManagerSave.cs:2728`),
and **not** portable for anything minted after the split — `savedOutput` drops mint with no serial
override (`EquipmentUtility.cs:1214`, `:1236`), and the regeneration branch (`:2717-2725`) mints
from a per-process counter. The design's "the host assigns the identity clients quote back" rule is
therefore **load-bearing, not a belt-and-braces**. M4 measures how much of the cheap path survives.

**KILL 5 — "M12c's checkpoint makes a salvage stall survivable."** Partly dead. The checkpoint is a
*combat-turn* save; the stall happens *after* combat, in the debriefing. Rolling back discards the
fight's outcome and makes both players **re-fight the last turn and the ending**. That is a real
cost the design doc's one-liner hides. Worse, it is **unmeasured whether a combat checkpoint can be
loaded from the overworld state the stalled machines are in** — `pbj.combat-load` has only ever been
exercised from inside combat. ⇒ §8 Q3.

**KILL 6 — "Build the `(kind, serial)` side table in stage 1."** Dead. Installed subsystems are
already nested inside their part in the save format (`DataBlockSavedPart.systems`), so inheritance
needs no storage; the loose ones are a base-inventory problem, and the first live salvage screen had
zero subsystems in it. The side table moved to D6 and is now explicitly cuttable. **The 606→0 number
is real and was pointing at the wrong milestone half.**

**KILL 7 — "Equal pools, remainder discarded, is obviously right."** Not a technical claim at all,
and it can make an item unclaimable by anyone that a solo player could afford. Escalated to the
user as §8 Q1 rather than built on.
✅ **AND IT LANDED, 2026-08-22: this objection is the reason equal pools are dead.** The user's
ruling — one shared budget, barrier-gated — is this KILL accepted, not a preference expressed over
it. §2.5 and §8 Q1 carry the decision. **The self-refutation pass is the thing that changed the
design here**; that is worth more than the row it closed.

**SURVIVED, with what would kill it:**

- *"M12d needs its own wire window."* Killed only if the messages are designed and refuted before W2
  opens — they are not, and §8 Q4 of the roadmap already says v10 does not wait.
- *"Replicating a bit-set is enough; no entity is created from the wire."* Killed if the two
  machines' **participant sets** differ, not just their flags — `ProcessSalvageSelections` iterates
  `ScenarioUtility.GetParticipantUnitsPersistent()` (`EquipmentUtility.cs:1873`, inside `ProcessSalvageSelections` declared at `:1805`). M2's fingerprint
  is keyed by unit precisely so this shows up as a *missing unit*, not as a wrong flag.
- *"The subsystem tag hole does not touch the budget."* Killed if a future salvage screen contains
  priced subsystems; the riders `continue` (`:1887-1901`) says it cannot while the parent part is
  salvageable, and the one edge case (a subsystem on a **non**-salvageable part, priced at `1f`,
  `:1898`) resolves through the parent part's tag anyway.

---

## 8. Open questions this plan could not settle

| # | question | cheapest settling experiment |
|---|---|---|
| 1 | Equal pools, or one shared budget with reservations? | ✅ **ANSWERED 2026-08-22 by the user: ONE SHARED BUDGET, barrier-gated.** ~~"🧑 One sentence to the user."~~ Asked and answered. Equal pools died to **this plan's own KILL 7** — an item unclaimable by anyone that a solo player could afford. And "with reservations" did not survive either: there is no reservation arithmetic, just one visible integer plus the barrier. §2.5 is rewritten to the decision, **including the finding the decision as relayed did not carry** — vanilla does **not** enforce the budget at the moment that matters (`SalvageRefreshBudget` only sets UI availability; `ProcessSalvageSelections` logs over-budget and continues), so enforcement is host-side claim admission **plus** merged-total as a precondition of barrier release. §2.5 is now buildable; **stage D3's `SalvagePool` is not** — its re-spec is backlog item B4. |
| 2 | Is the `combat_salvage_drop_chance` roll reached in a real co-op campaign fight? | Rig M3. If `featuresChecked=True`, D3's manifest shrinks to roster reconciliation. |
| 3 | Can an M12c combat checkpoint be loaded from the **overworld** state a stalled debriefing leaves a machine in? | One instance, one fight, checkpoint, finish the fight, then `pbj.combat-load` the checkpoint slot from the overworld. Read the action diff. Rides R0's tail at near-zero cost. |
| 4 | Is `SalvageFinish`'s post-commit work deterministic given synced inputs? (design q7, `CIViewOverworldDebriefing.cs:2780+`) | Rig M6 — **and it is not closable before D5**, because it needs both machines to commit. Recorded as blocked rather than guessed. |
| 5 | Does a client today actually open a debriefing after a shipped fight? | Rig M1. This is the single reading that most changes M12d's size, and it costs one console call on the client at R1 reading 9. |
| 6 | Claim granularity for the general inventory outside salvage (design q4) | Deferred to D6; needs no answer to ship the screen. |

---

*Written by lane L2, 2026-08-21. It supersedes no section of
`docs/design/m12-concurrent-management.md`; it is conditioned on that file's dated verdict and
corrects §M12d only where the corrections are recorded there, in place, with dates.*
