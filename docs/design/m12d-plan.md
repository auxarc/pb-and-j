# M12d — assigned gear and the salvage screen: the build plan

Written **2026-08-21** by lane L2 at `main` = `1708a0b`, mod 0.22.0, wire v9. No production code was
written to produce it. It sits on top of, and is only valid because of, the dated verdict at the end
of `docs/design/m12-concurrent-management.md`.

> ⚡ **RE-SPECIFIED 2026-08-22 (backlog item B4), design only, still no production code — and then
> REFUTED AND REVISED the same day (round 2).** Under the user's shared-budget decision
> (`docs/design/backlog-2026-08-22.md` §0.3) and its F1 correction, **§2.4–§2.6, §3's unit list,
> §4, §6's D1–D5 rows, §7 (KILL 8–17), §8 Q1 and the new §9 and §10 have been rewritten.**
> **Round 2 killed five of round 1's own claims** (KILL 12–17) — a document that has been refuted
> twice is worth more than one that reads cleanly, and the corrections are recorded in place.
>
> Six things a reader must not carry over from earlier text:
> **(1) `SalvagePool` does not exist** — it is `SalvagePrice` + `SalvageLedger` + `SalvageBarrier`;
> **(2) the `Invoke()`/`available` question is SETTLED — `Invoke()` bypasses the flag entirely**
> (§2.5.2); **(3) the group cost multiplier is `0.35`, not `0.4`**
> (`CIViewOverworldDebriefing.cs:2686`); **(4) the confirmation modal is NOT the commit gate** —
> it has six routes past a barrier, and the gate is a prefix on `SalvageFinish` (§2.6.1);
> **(5) prices are captured at screen time and stay HOST-LOCAL** — they do not exist at manifest
> time, and they never cross the wire (§2.5.2); **(6) a local click does NOT write
> `salvageSelection`** — the capture prefixes suppress the write until the host admits the claim
> (§2.5.5).
> The state of the rest of the world also moved: **W2 merged**, so `main` is mod 0.24.0 / wire
> **v10** (`PbjProtocol.cs:217`, `:260`), and M12d's window is v10 → **v11**.
>
> **Round 3 ran and found NO architectural defect** — admission, the ledger, the barrier,
> suppress-until-admission and the single-choke gate all survived deliberate attack, and the writer
> enumeration was independently confirmed exact. It killed seven downstream claims (KILL 18–24),
> **five of them in or beside the wire table**, so **§4 was re-derived from scratch under one rule:
> every row names its PRODUCER MOMENT and its CONSUMER.** That rule retro-catches all four wire
> defects the three rounds found. **Message count: 6 → 7 → 8 → 7**, tail `SalvageRelease = 39`.
>
> ⚠️ Two more things not to carry over: **(7) `SalvageUnready` does not exist** — no sender, and
> its barrier member would have been dead Core code; **(8) prices never cross the wire** — no
> consumer.
>
> ⏭️ **No round 4.** The remaining risk is in readings, not in text: **NW-k (do free drops reach
> the grid?) gates D4**, and **NW-i's RTT fork gates the capture prefixes**. Everything else goes to
> the rig.

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

Host computes once and broadcasts. Non-negotiable, and the reason is stronger than the design doc's.
**Every citation in this section was re-opened 2026-08-22 (item B4) and every one bit.**

- It is one `int` assembled inside **`CIViewOverworldDebriefing`, member `SalvageRedraw`** (declared
  `:2097`; the budget arithmetic runs `:2123-2208`) from preset × difficulty + per-group offsets +
  base and per-unit `salvage_bonus` + world-memory modifiers, and **two of the modifiers are
  consumables removed as the screen opens** (`RemoveMemoryFloat` at `:2168` and `:2183`). Reopening
  yields a different number.
- 🆕 **And the vanilla computation has a defect a reimplementation would not reproduce:** the
  *consumable* multiplier's contribution is computed from `value` — the **non-consumable** memory's
  out-var, bound at `:2154` — while the consumable's own value is discarded (`out var _` at
  `:2162`, and `value` used again at `:2165`). Whatever that is, it is what ships. A mod-side
  recomputation would disagree with the game on any save where those two memories differ.

⇒ **Never derive the budget independently on any machine.** Host reads its own
`salvageBudgetLast` after the screen opens and broadcasts the integer.

**The same rule, generalised, is what §2.5 below leans on:** anything vanilla computes from data
tables or from a defect gets *captured and shipped*, never recomputed. The only vanilla arithmetic
this plan reimplements in Core is the three-arm multiplier rule at `EquipmentUtility.GetSalvageCost`
`:1622` / `:1640-1643` — three arms, spelled out in §2.5.2, and the reason it is reimplemented
rather than captured is that the coverage gate has to be able to see the mutation that breaks it.

### 2.5 One shared budget, and where it is actually enforced ▸ ✅ **DECIDED 2026-08-22. Equal pools are DEAD.** Re-specified 2026-08-22 (item B4).

🔴 **The sentence this section opened with, and what killed it.** It read:

> *"Equal integer pools, one per present player, remainder discarded."*

**Killed by this plan's own KILL 7** (§7): *"Equal pools, remainder discarded, is obviously right"*
— not a technical claim at all, **and it can make an item unclaimable by anyone that a solo player
could afford.** That objection was escalated as §8 Q1 rather than built on; the user answered it on
2026-08-22 and answered it against equal pools. Recording *why* it died matters more than recording
that it did: the plan refuted its own premise, and the decision is that refutation being accepted.

**The decision, stated as wiring.** Every participant sees and draws from **the same**
`salvageBudgetLast`. Selections replicate (§2.1's correct-after model, unchanged). Nobody commits
until the barrier releases (§2.6). There is no per-player pool, no remainder, and no reservation
arithmetic — the contended resource is one integer that everyone can see.

#### 2.5.1 What vanilla actually enforces — three breaks, all re-derived 2026-08-22

The decision as first relayed said *"vanilla's own `salvageCostValid = salvageCostTotal <=
salvageBudgetLast` gate does the enforcing."* **It does not.** The three breaks compound, and the
third is the one no amount of care in the first two can fix.

**Break 1 — the redraw, and the one real refusal it is not.**
`salvageCostValid` is computed inside **`CIViewOverworldDebriefing`, member `SalvageRefreshBudget`**
(declared `:2351`; the assignment is `:2368`), and its consequences there (`:2369-2378`) are
**UI only**: `salvageButtonFinish.available`, `salvageBudgetButtonDetails.available`, a label
colour, the finish-label string, a tooltip hue, a fill amount.

⚠️ **A correction to the correction, and it matters for §2.6:** vanilla *does* have exactly one
genuine refusal, and the backlog's F1 wording ("vanilla enforces nothing") overshoots by omitting
it. **`OnSalvageFinish`** (declared `:2741`) reads `salvageCostValid` at `:2757` and, when it is
false, logs `"Can't proceed, last salvage calculation is not valid"` (`:2759`) and never opens the
confirmation modal. That is a real gate. It is useless to us for three separate reasons, each
derived rather than asserted:

1. It fires on the **local player's own click**, before any barrier exists — it can only ever see
   that machine's view of the merged set, which Break 3 shows is stale.
2. It is **conditional on the fight having salvage at all**: the whole check sits inside
   `if (outcomeVictoryLast && salvageEntries.Count > 0)` (`:2755`); the `else` arm calls
   `SalvageFinish()` **directly, with no modal and no budget check whatsoever** (`:2766-2768`).
3. It refuses by **falling out of the method with a log line** — a silent stall from the driver's
   point of view, which §2.7 names as the real hazard rather than lost loot.

And past that point vanilla stops looking. The commit funnel's own over-budget arm **lets the
process continue by design**: `EquipmentUtility`, member `ProcessSalvageSelections` (declared
`:1805`), `:1903-1905` — `if (budget < costTotal) { Debug.LogWarning($"Salvage operation cost
{costTotal} exceeds budget {budget} | … letting the process continue..."); }` and then transfers the
parts anyway. The warning text says out loud that it is not a gate. **Derivation, not assertion:**
the identifier `budget` occurs **four times at three sites** in the whole of
`ProcessSalvageSelections` — `:1805` (the signature), `:1836` (a log), and `:1903` + `:1905` (the
comparison and its warning string). It is never used to refuse anything. *(Pattern non-vacuity: the
scan is `budget` over `EquipmentUtility.cs:1805-1940` and it returns those four lines, so a zero
would have meant a broken pattern, not a silent member.)*

**Break 2 — `Invoke()` versus `available`: ✅ SETTLED 2026-08-22, and it bypasses the flag entirely.**
This was carried as **UNVERIFIED** in both §2.5 and §2.6 and in backlog §B4. It is now closed by
opening the classes:

- `CIButton.callbackOnClick` is a **`UICallback`** field (`decompiled/CIButton.cs:37`).
- **`UICallback`** (`decompiled/UICallback.cs`, member `Invoke`, `:143-184`) holds a `type` enum and
  six `Action` fields and **no reference to the button that owns it**. `Invoke()` switches on `type`
  and null-checks the delegate. It *cannot* consult `available` even in principle.
- The `available` gate lives in exactly two `CIButton` members and nowhere else: **`OnPressEvent`**
  (declared `:512`, gate at `:527`) and **`ForceClick`** (declared `:657`, gate at `:660`).

⇒ **`someButton.callbackOnClick.Invoke()` is that button's click handler with the UI gate removed.**
Vanilla's UI availability is not a backstop on the drive path — not even weakly.

Two corollaries the design uses rather than merely noting:

- **`CIButton.ForceClick(bool)` is the gated alternative and is the wrong tool here.** It honours
  `available` — but on failure it is a bare `return` (`:660`), i.e. a **silent no-op**, which is a
  stall. It also has an escape hatch, `interactiveUnavailable` (`:87`), which the debriefing never
  touches: `interactiveUnavailable` occurs **0 times** in `CIViewOverworldDebriefing.cs` (pattern
  control: the identifier appears in 2 files under `decompiled/`, so the scan bites), leaving the
  field at its `bool` default. ⇒ driving `salvageButtonFinish` through `ForceClick` on an
  over-budget machine would do nothing, silently, forever. **Do not drive the finish button.**
- The release point §2.6 uses, `CIViewDialogConfirmation.buttonConfirm.callbackOnClick.Invoke()`,
  reaches `CIViewDialogConfirmation.OnConfirm` (wired in `Awake` at
  `decompiled/CIViewDialogConfirmation.cs:48`; the member is declared `:126`), which does
  `TryExit()`, sets both buttons unavailable, and calls `callbackOnConfirm?.Invoke()` — for us,
  `SalvageFinish`. **It performs no budget check of any kind.**

**Derivation that nothing downstream re-checks:** `salvageCostValid` occurs **14 times** in
`CIViewOverworldDebriefing.cs` and **0 times** inside the body of `SalvageFinish` (`:2772-2900`).
Fourteen hits elsewhere in the same file is the positive control that makes that zero mean
something. ⇒ between the modal opening and `ProcessSalvageSelections` (`:2825`) there is **no
vanilla check at all**, and the barrier's release point sits downstream of every one of them.

**Break 3 — staleness, and why it is structural.** Between a replicated claim's arrival and the next
local redraw, `salvageCostValid` is stale. Two peers claiming **different** items, each locally
under budget at its own click, can be jointly over budget: neither machine's `salvageCostTotal`
included the other's pending choice, because the choice had not yet replicated. First-to-host
resolves *item* conflicts only — nothing in the shape as previously written is an arbiter for
**budget admission**. No amount of forced redrawing fixes this, because the window is between two
machines, not between two frames on one machine.

#### 2.5.2 ⇒ Enforcement is host-side claim admission, in Core

**(a) A claim that would push the merged total over the budget is REJECTED at the host when it
arrives** — refused, not repaired later, and refused by exactly the machinery that already refuses a
stale-version claim. **(b) merged-total-within-budget is a PRECONDITION of barrier satisfaction**,
checked before any machine invokes the commit. Neither can be a UI state; the UI is a mirror of
(a). This replaces stage D3's dead `SalvagePool`.

**The pricing model, derived — and vanilla has two of them.**

| where | multiplier used | member |
|---|---|---|
| the **screen's** cost total (what `salvageCostValid` compares) | the group's own `costMultiplier` | `ProcessSalvageGroupChoices` (declared `:2519`), `GetSalvageCost(entity, dismantle, group.costMultiplier)` at `:2547`, accumulated `:2548` |
| the **funnel's** cost total (what the warning at `:1903` compares) | hard-coded `1f` for every participant part and subsystem | `ProcessSalvageSelections` `:1889`, `:1897`, via `ProcessSalvageOfEntity` (declared `:1647`, cost at `:1660`) |

They disagree, and the funnel's warning text says as much ("outdated cost evaluation in this code").
⇒ **the mod mirrors the SCREEN's model**, because that is the number the player is shown and the
number `salvageCostValid` is built from. The funnel's number is not enforced by anything and is
therefore not a specification of anything.

The group multiplier is **not** 0-or-1 and is **not** 0.4 — the design doc's 0.4 was carried
UNVERIFIED and is wrong. Derived: `RefreshSalvageGroupsForFaction` (declared `:2552`) sets
`costMultiplier = (flag4 ? 0.35f : 1f)` at `:2686`, where `flag4` (`:2682`) is *the group's unit is
`Phantoms` faction and not `isDisposableOutsideCombat`*. ⇒ **a friendly unit's own gear is priced at
0.35; everything else at 1.0.** And `GetSalvageCost` applies it in three arms
(`EquipmentUtility.cs`, declared `:1616`): return `0` when `costMultiplier <= 0f` (`:1622`);
`RoundToInt(costMultiplier * num)` when `< 1f` (`:1640-1643`); otherwise `num` unchanged.

Scrap and recover are **separately priced**, four raw values deep: `costTakeIntact` /
`costTakeDamaged` / `costDismantleIntact` / `costDismantleDamaged`, selected by
`entity.isWrecked` and the `dismantled` flag, from `DataShortcuts.sim.salvageCostsPart` keyed by
rating for parts (`:1634`) and from flat `…Subsystem…` fields for subsystems (`:1638`).
**A scrap is not free.**

##### 🔴 The capture MOMENT — corrected 2026-08-22 (round 2). Prices are SCREEN data, not manifest data.

The round-1 draft put `rawRecover`/`rawScrap`/`costMultiplier` in `SalvageManifest`. **That is not
buildable**, and the reason is a moment, not a payload:

- The manifest is built in the `PrepareUnitForSalvage` postfix. That call is
  `EquipmentUtility.PrepareUnitForSalvage(item2, persistentEntity, flag6)` at
  `decompiled/PhantomBrigade.Overworld.Systems/OverworldCombatOutcomeProcessingSystem.cs:225`,
  inside the `if (currentScenario.coreProc.lootingUsed)` arm at `:223`, and it runs **before**
  `TriggerPostCombatRewards` (`:230`). **No debriefing screen exists yet.**
- `group.costMultiplier` is minted at `CIViewOverworldDebriefing.cs:2686`, inside
  `RefreshSalvageGroupsForFaction` (declared `:2552`), which runs inside `SalvageRedraw` (declared
  `:2097`). **Screen time.** At `:225` there is no `SalvageGroupData` and no multiplier to capture.
- Recomputing `flag4`'s predicate at `:225` would violate §2.4's own *captured and shipped, never
  recomputed* rule **in the very message that rule was written for**. Rejected on those grounds.
- ⚠️ And the raw pairs, which *are* nominally capturable at `:225`, must not be captured there
  either: `GetSalvageCost` selects `costTake*`/`costDismantle*` by **`entity.isWrecked`**
  (`:1634`, `:1638`), and the screen prices `isWrecked` **live at each redraw**. Capturing at `:225`
  freezes an input the screen re-reads. Nothing in this plan proves `isWrecked` is stable across
  that gap — and a drift would land on the canary below as a **false abort**, i.e. as a fault in the
  instrument rather than a fault in the world.

⇒ **The split is by MOMENT, and the message boundary follows it:**

- **`SalvageManifest` (postfix time) carries identity only** — the bit-set of what is salvageable,
  per `(unitKey, SalvageKey)`. No prices, and 🔴 **no free-drop identities** — see below.
- **The prices are captured host-locally and DO NOT CROSS THE WIRE.** Round 2 put them in a message;
  round 3 asked who consumes them and the answer is nobody. Admission is host-side (§2.5.3), so only
  the host prices a claim, and it prices from its own capture. A client's grid shows costs its own
  vanilla `GetSalvageCost` computed — from the same ECS state the manifest exists to reconcile — and
  those are **redraw-stable**. ⇒ shipping price rows would be **dead payload in a v11 message**.
  `SalvagePrices` stays a Core type populated on the host; `SalvageBudget` keeps its old name because
  it is once again carrying exactly one `int`.
- **The capture site is a Harmony postfix on `SalvageRedraw` (`:2097`)**, walking the private
  `salvageGroups` list — each `SalvageGroupData` carries `entities` (`:63`) and `costMultiplier`
  (`:57`; ⚠️ `:84` is a *different* `costMultiplier`, on the unrelated `GridEntryDebriefingSalvage`
  struct declared `:70` — round 2 cited the wrong one) — and calling
  `GetSalvageCost(e, dismantled: false, 1f)` and `GetSalvageCost(e, dismantled: true, 1f)` per
  entity. Same moment as the screen, same `isWrecked`, no drift by construction.

##### 🔴 The SAME moment bug, one level up: free drops do not exist at manifest time either

Round 2 fixed the prices and left `SalvageManifest` claiming *"the host-minted identities for free
drops"*. **That is the identical defect, in the row that costs a protocol version.** Derived:

- `PrepareUnitForSalvage` is called at `OverworldCombatOutcomeProcessingSystem.cs:225`, **inside a
  `foreach` over participant units** that closes at `:227`.
- `TriggerPostCombatRewards` (declared `EquipmentUtility.cs:1128`) runs **after that loop exits**, at
  `:230`, inside a `try`.
- The free drops are *created* inside it: `PrepareRewardsFromSavedOutput` (declared `:1182`, reached
  from `:1156`) mints parts at `:1214` (`ReplaceSalvageSelection(false)` at `:1217`) and subsystems
  at `:1236` (`:1239`).

⇒ **when the last manifest-building postfix fires, no free drop exists.** And §3's patch list named
no patch on either member, so **no instrument in this plan could observe free-drop creation at all.**

**Resolution: cut free drops from v11 and move them to D6.** Not merely because it is cheapest —
because they are the half §0 already established is *not* the priced list. `PrepareRewardsFromSavedOutput`
attaches them with `AttachPartToInventory(equipmentEntity, inventoryPersistent)` (`:1218`) — to the
**player base inventory**, not to a participant unit — while the screen's groups are built by
`RefreshSalvageGroupsForFaction` (declared `:2552`) walking participant units. The budget is spent on
the unit-mounted list; free drops are not on it.
⚠️ **The residual is named rather than assumed away:** whether a free drop nonetheless *surfaces* on
the salvage grid is not established here. **Rig reading M2's fingerprint is already keyed by unit and
would show it** as an entry belonging to no unit group. If M2 shows free drops on the screen, D6
becomes a blocker for D5 instead of an independent half — recorded as a fork, not resolved by
assertion.

**⇒ The split between what is captured and what is reimplemented, and why.**

- **Captured and shipped: the raw pairs and the multiplier** (above). The rating lookup, the
  intact/damaged selection and the part-vs-subsystem tables stay on the game side, where they are
  data-driven and version-fragile, and are never reimplemented (§2.4's rule).
- **Reimplemented in Core: `SalvagePrice.For(int raw, float multiplier)`, three arms, mirroring
  `:1622` and `:1640-1643` exactly.** Not because Core needs the arithmetic — it could ship the
  final price — but because `src/PBAndJ.Core` is the covered project (`Makefile:200`), and a
  mutation that drops the multiplier arm must be able to turn a test **red**. Ship it as a
  captured final price and the "drop the multiplier" mutation has nothing in Core to mutate: the
  test would be **vacuous**, and would read exactly like a safeguard while guarding nothing.
- 🔴 **The rounding is NOT VERIFIABLE IN THIS TREE and is therefore a MEASUREMENT, not an
  assumption.** Vanilla's middle arm is `Mathf.RoundToInt(costMultiplier * (float)num)` — a
  **single-precision product**, then Unity's rounding. `Mathf` is UnityEngine core and is **absent
  from `decompiled/`** (pattern control: `Vector3.cs` is absent too, while `CIButton.cs` is present,
  so the scan works and the absence is the decompile's scope, not a bad pattern). Core must
  therefore reproduce the product **in `float`** before widening, and its rounding mode must be
  *observed*, not assumed to be `MidpointRounding.ToEven`. ⇒ **stage D2's probe owes a rounding
  table**: for each of a set of chosen `(raw, 0.35f)` pairs straddling `.5` boundaries, print
  `Mathf.RoundToInt(0.35f * (float)raw)`. **T2's expected values are populated from that table, not
  hand-derived in decimal.** A hand-derived T2 would be a test that agrees with our arithmetic and
  disagrees with the game — and the canary would then abort every session, blaming the world.
- **The canary goes on the instrument, not on the input.** The host has both numbers:
  `ledger.MergedTotal` and the private `salvageCostTotal` (`:468`) that a postfix on
  `SalvageRefreshBudget` (`:2351`) reads back. **They must be equal.** See §2.5.6 for why "abort on
  mismatch" is *not* sufficient and what replaces it.


##### 🔴 One postfix, three holes: the budget's APPLY, the redraw that re-mints it, and the early `SalvageState`

Round 2 specified captures with no applies. Three holes of one shape, and **one mechanism closes all
three**, which is the reason they are written together.

**Hole 1 — `SalvageRedraw` re-fires and re-mints a DIFFERENT budget.** Derived:
`RedrawLastStage` re-runs `SalvageRedraw()` whenever `stageLast == DebriefingStage.Salvage`
(`:838-843`), and `OnStagePrev`'s general arm decrements the stage and calls it (`:752-757`) — so
**Salvage → Rewards → Next → Salvage is two clicks** and re-enters the stage. Each run resets
`salvageBudgetLast = 0` (`:2123`) and re-walks the memories — but the *first* run already
**consumed** `world_auto_salvage_multiplier_consumable` and `world_auto_salvage_offset_consumable`
via `RemoveMemoryFloat` (`:2168`, `:2183`). ⇒ **on any save where either was nonzero, the second run
mints a different budget.** Concrete damage on the **host**: `salvageCostValid` is then computed
against the new local budget while admission still uses the ledger's, and `OnSalvageFinish:2757`
refuses by silently returning ⇒ **a host that is under the ledger's budget cannot ready at all.**
That is precisely the stall §2.7 names, manufactured by our own screen.

**Hole 2 — the budget has no apply on a client.** `salvageBudgetLast` is `private` (`:431`) and
round 2 never listed it. Nothing wrote the host's broadcast into a client's view, so the client's
vanilla `SalvageRedraw` computed **its own** budget from **its own** memories and gated finish on it
— which is §2.4's rule ("never derive the budget independently on any machine") being violated by
the vanilla screen we chose not to intercept.

**Hole 3 — a `SalvageState` that arrives before the client's screen opens is dropped.** `:2043`'s
`if (ins == null) return;` is cited in §2.5.3 as a hazard and then not handled — **and the client
opens late by construction**, which is D0's entire subject.

⇒ **The mechanism: the `SalvageRedraw` postfix is authoritative on both machines.** On every run,
after the vanilla body:

1. **The ledger's `Budget` is latched first-wins** — captured from the host's *first* `SalvageRedraw`
   after screen open and immutable for the rest of the screen. Later runs never re-broadcast.
2. **The postfix overwrites `salvageBudgetLast` with the ledger's budget**, on host and client
   alike, then calls `SalvageRefreshBudget()` again — because `SalvageRedraw`'s own tail already
   called it (`:2309`) with the number we are replacing. This closes holes 1 and 2 with one write:
   the host stops drifting on re-entry, and the client stops deriving.
3. **The latest `SalvageState` is buffered and applied here** if it arrived before the screen
   existed. Safe to make "latest wins" because `SalvageState` is a **full snapshot, not a diff**.
4. `salvageBudgetLast` therefore joins the private-member list (§9 NW-b).

⚠️ **What the overwrite does NOT fix, stated rather than glossed:** the client's own
`SalvageRedraw` still *executes* `RemoveMemoryFloat` on **its** consumable memories (`:2168`,
`:2183`) before the postfix runs. We correct the number; we do not prevent the side effect. Whether
a client's world memory diverging matters is an M12b/M12c question (the host is authoritative for
campaign state), but it is a **real divergence introduced by opening the screen** and it is filed as
new work rather than assumed benign.

#### 2.5.3 Host-side claim admission — the checks, in order, with the order derived

A `SalvageClaim` carries `{ Version, Choices }` where `Choices` is a **non-empty set** of
`(Key, Choice ∈ Skip/Recover/Scrap)` pairs — one *gesture*, not one item; §2.5.5 derives why, and
§2.5.4 derives what it costs. The host's `SalvageLedger` holds the budget, a monotonic `Version`, a
`Sealed` flag, and per key a `(Choice, OwnerPeerId)` row.

**"Owned" is defined here, because the round-1 draft used the word without defining it and shipped a
gameplay defect through the gap (§7 KILL 12).** A key is **owned by P** iff it has a row whose
`Choice` is **not** `Skip` and whose owner is P. **`Skip` is the absence of ownership, not a kind of
it** — so accepting a `Skip` **deletes the row** rather than writing `(Skip, P)`. Returning the
budget without returning the item would let the first peer to touch an item lock every other peer
out of it forever.

On arrival the host evaluates, **stopping at the first failure**:

| # | check | outcome on failure | why it sits here |
|---|---|---|---|
| 0 | the ledger is not `Sealed` (§2.5.4) | `Closed` | a claim that arrives after `SalvageRelease` must not mutate anything; every later check would pass it |
| 1 | claimant is a barrier participant | `UnknownParticipant` | an unknown peer's version number is meaningless; mirrors `LobbyBarrier.SetReady` `:164-167` |
| 2 | `claim.Version` vs `ledger.Version` — `<` ⇒ stale, `>` ⇒ ahead | `Stale` / `NeedsResync` | the claim names the selection set it was formed against; admitting it against a different one prices a gesture the player never saw. Three-arm shape copied from `LobbyBarrier.SetReady` `:168-175`, and `NeedsResync` means what it means there (only the host mints versions, the stream is ordered) rather than what it means on `TurnBarrier` |
| 3 | `Choices` is **well-formed**: no key appears twice **and** every key is in the host-minted manifest | `Malformed` / `UnknownKey` | the manifest is host-minted (§2.2), so at a current version an unknown key is a protocol fault. 🆕 **The duplicate arm is round 3's:** `{(X, Recover), (X, Scrap)}` is a well-formed *set* that passes 0–2, gives check 5 an undefined `TotalIfApplied`, and reaches an accept arm whose result depends on **iteration order**. None of the three capture members (§2.5.5) can produce one, so it is a protocol fault, not a policy question — **reject, do not define last-write-wins** |
| 4 | no key in `Choices` is **owned by another peer** (per the definition above) | `Reserved` | needs the keys' rows, so after 3. Before 5 so that one act gets one deterministic reason: price a foreign key first and the same claim would be reported `OverBudget` or `Reserved` depending on unrelated peers' spending |
| 5 | `ledger.TotalIfApplied(Choices) <= ledger.Budget`, the whole gesture **atomically**. ⚠️ `TotalIfApplied` **replaces** the claimant's existing row for a key, it does not add to it — changing your own Recover to Scrap costs the *difference*, not the sum | `OverBudget` | 🔴 **the reason this re-spec exists.** The only place jointness is ever evaluated, and it is evaluated on the **merged** set. Atomic because a partially-admitted "recover this unit" is a gesture the player never made |
| 6 | `Choices` changes at least one row | `NoChange` | 🆕 see below — this is a *rejection* precisely so that it does not advance the version |
| — | accept | `Accepted` | apply every pair (`Skip` deletes its row; otherwise write `(Choice, claimant)`), `Version++`, broadcast `SalvageState`, `SalvageBarrier.AdvanceTo(Version)` |

**Check 2's reason was wrong in round 1 and is corrected above.** It said *"a stale claim may quote a
key from a manifest we have since replaced"* — **an event that never occurs in this design.** The
manifest is minted once, at `:225`, and never replaced; `ledger.Version` advances on acceptances
only. The real reason is the one now in the table, and it is the same reason `LobbyReadyMessage`
carries its selection (`PbjMessage.cs:884-888`): *without it, a message sent just before the set
changed is counted as an answer about the new set.*

**Check 6, and the derivation the accept arm owes.** A table that classifies a population owes a
derivation of the population, and the round-1 accept row absorbed two inputs it never named:

1. **Post-release claims** — no `Sealed` state existed, so a claim arriving after `SalvageRelease`
   passed every check, mutated the ledger, bumped the version, cleared everyone's ready and
   broadcast to machines that had already committed and exited the screen — where
   `OnSalvageDecision`'s `if (ins == null) return;` (`:2043`) **drops it silently**. Phantom
   ownership that no machine ever applies and no transfer ever honours. ⇒ **check 0**.
2. **No-op claims** — a `Skip` on a key with no row, or a re-claim of your own key with the same
   choice. All checks passed, and the accept arm still did `Version++` + `AdvanceTo` + broadcast.
   **A double-clicking or toggling player could hold the barrier open indefinitely**, because every
   no-op cleared every peer's ready. ⇒ **check 6**, and it must be a rejection rather than a
   silently-successful no-op, because the version must not move.

⇒ the accept arm's population is now stated rather than assumed: **exactly those claims that are
un-sealed, from a participant, at the current version, wholly within the manifest, touching no
foreign-owned key, affordable as a whole, and changing at least one row.** Everything else has a
named outcome.

**What goes back on rejection.** Nothing is broadcast (the state did not change); the host replies
to the claimant alone with **`SalvageClaimResult { Version, Outcome, FirstOffendingKey? }`**. On
acceptance the host broadcasts `SalvageState` to everyone **and then** sends the claimant its
`Accepted` result on the same ordered stream, so by the time the claimant reads the result its state
is already current. The result is sent on **every** path, not only failures: without it the claimant
cannot distinguish "still in flight" from "rejected", since a rejection produces no broadcast — and
a succeed-silently arm is one more branch for the coverage gate to fail on.

#### 2.5.4 The race, and what the global version counter is actually for

Two peers, budget 100, item X costs 60, item Y costs 60.

- **t0** — both hold `Version = 7`, ledger empty; both screens legitimately show `0/100`.
- **t1** — A sends `Claim{7, {X: Recover}}`; B sends `Claim{7, {Y: Recover}}`. Both were locally
  valid: each machine priced only its own pending gesture. **This is Break 3, live.**
- **t2** — A's claim reaches the host first (arrival order on the host's ordered inbound queue is
  the whole of "first-to-host"). Checks 0–4 and 6 pass; check 5: `60 ≤ 100`. Accept. `Version → 8`.
  Broadcast `SalvageState{8, X→A}`; `Result{8, Accepted}` to A.
- **t3** — B's claim arrives quoting `Version = 7`. **Check 2 fires first: `Stale`.** B re-renders
  from the `V=8` state and re-submits at `V=8` — and *now* check 5 prices it against the merged set:
  `60 + 60 = 120 > 100` ⇒ **`OverBudget`**. Y is refused.
- **Same item instead of different items:** B's re-submission of X at `V=8` stops at check 4,
  `Reserved`, and B's UI marks X as A's.

🔴 **The round-1 derivation of the global counter was WRONG, and the conclusion survives for a
different reason.** Round 1 argued that per-key versions would let both t1 claims be current, so
*"nothing is stale, both pass, and the joint over-budget hole is back"*. **False, and the
walkthrough above refutes it:** check 5 is evaluated **at the host, over the merged ledger, at
arrival**. Under per-key versions B's claim would simply skip check 2 and be refused at check 5 as
`OverBudget` instead of `Stale`. **Check 5 is total under either versioning scheme** — that is what
makes it the enforcement point, and it would be a poor enforcement point if it were not.

**What genuinely needs one global counter is the BARRIER, not the budget.** A `SalvageReady` must
name **one number that identifies the whole selection set it agreed to** — that is the entire reason
`LobbyReadyMessage` carries `SelectionVersion` (`PbjMessage.cs:884-888`) and the reason
`AdvanceTo` clears every ready. Under per-key versions there is no such number: a peer would have to
name a vector, and "the set I agreed to" would be unrepresentable in the message. Secondary benefit:
one counter gives every rejection a single coherent `Version` to re-render against.

**The amplifier, with its mechanism corrected.** The review filed this against
`OnSalvageDecisionUnit`; **that member is not the one that does it.** The real one, opened here:

- `OnSalvageDecisionUnit` (declared `:1883`) writes the selection **inline** over `value.entities`
  (`:1900`, `:1904`) and calls `SalvageRefreshBudget()` **once**, at `:1909`.
- **`OnSalvageDecisionShift`'s UNIT branch** (declared `:1928`; the branch is `mode == Mode.Unit`)
  loops **`OnSalvageDecision(entity2.id.id, decisionID)`** over every entity in the group at
  `:1967` — and since each `OnSalvageDecision` ends in `ins.SalvageRefreshBudget()` (`:2069`), one
  row-cycle gesture on an N-item unit costs **N redraws** in vanilla itself.

Either way the mod-side hazard is the same and it is why §2.5.3's claim is a **set**: capture one
gesture as **one claim**, and an N-item unit costs one message. Capture per entity and it costs N
same-version claims — 1 accepted, N−1 `Stale`, resubmit — **O(N²) messages and N ready-clears** for
a single click. The batch is not an optimisation; it is what keeps check 2 from turning a normal
gesture into a livelock.

#### 2.5.5 🔴 Where a claim COMES FROM — the largest piece round 1 did not write

Round 1 said remote choices are *applied* through the public statics and never said how a **local
click becomes a claim**. Three things make that gap load-bearing rather than an omission.

**(i) The public statics are not the click surface.** Derivation, by enumerating every writer of the
`salvageSelection` component in the view — `grep -n 'ReplaceSalvageSelection\|AddSalvageSelection\|
RemoveSalvageSelection' CIViewOverworldDebriefing.cs` returns **ten sites in three members**
(pattern control: the same identifier stem `SalvageSelection` hits ten files under `decompiled/`,
including `PhantomBrigade.Game/SalvageSelection.cs` itself, so a zero would have meant a broken
pattern):

| member | sites | routes through `OnSalvageDecision`? |
|---|---|---|
| `OnSalvageDecisionUnit` (declared `:1883`) — reached from the public `…Unit` statics `:1868`, `:1873`, `:1878` | `:1900`, `:1904` | ❌ **no — writes inline** |
| `OnSalvageDecisionShift` (declared `:1928`) — **item branch** (the `else` at `:1974`) | `:1989`, `:1993`, `:1997`, `:2002`, `:2006`, `:2010` | ❌ **no — writes inline** |
| `OnSalvageDecisionShift` — **unit branch** (`:1935-1972`) | — | ✅ yes, N times, at `:1967` |
| `OnSalvageDecision` (declared `:2041`) — reached from the public statics `:2026`, `:2031`, `:2036` | `:2054`, `:2058` | ✅ (is it) |

⇒ **a mod that patches only `OnSalvageDecision` sees neither the item-mode mouse path nor the
whole-unit gesture.** ⚠️ And the review's own proposed remedy — "intercept `OnSalvageDecision` and
`OnSalvageDecisionShift`'s item branch" — **is still incomplete: it misses `OnSalvageDecisionUnit`
(`:1883`) entirely**, which is exactly the member §2.6 cites as a drive-path entry point.

**(ii) ⇒ Capture at the GESTURE layer: all three members, not the two publics.** The three members
above are precisely the three player gestures — *set this item*, *set this whole unit*, *cycle this
row*. A Harmony **prefix** on each converts the gesture into one `SalvageClaim` carrying the set of
`(Key, Choice)` pairs it would have written. That is the same granularity §2.5.3's batch needs, and
it is the reason the batch exists.

**(iii) 🔴 The reentrancy guard — and the nested case that makes it subtle.** Apply and capture must
be separated or the design **livelocks**: an applied `SalvageState` re-enters the same members,
each re-emitting a claim, each acceptance broadcasting more applies. The guard is an
apply-scope flag: while the mod is applying a remote `SalvageState`, the capture prefixes
**suppress claim emission and let the vanilla body run**.

⚠️ **Round 2's derivation of this guard was internally stale and round 3 caught it.** It argued the
scope must be depth-counted *"because the gestures nest"* — citing `OnSalvageDecisionShift`'s unit
branch calling `OnSalvageDecision` N times (`:1967`). **Under suppression that nesting cannot
happen**: a suppressed outermost body never executes, so it never reaches `:1967`. The guard
survives, but as the **apply-mode flag** only — while the mod is applying a remote `SalvageState`
through the public statics, the capture prefixes must not re-emit. One boolean scope around the
apply, not a depth counter around the capture.

🔴 **The cost round 2 never stated: a suppressing prefix must REIMPLEMENT vanilla UI logic.** A
prefix that returns false must still emit *the set of pairs the body would have written* — and the
body is where those pairs are computed:

- **`OnSalvageDecisionShift`, item branch** (`:1985-2011`): a **direction-dependent three-state
  cycle**, two mirrored ladders over `hasSalvageSelection` and `salvageSelection.dismantle`.
- **`OnSalvageDecisionShift`, unit branch** (`:1946-1963`): a majority tally over the group
  (`num`/`num2`/`num3`), then `int a = ((num <= num2 || num <= num3) ? ((num2 > num && num2 > num3) ? 1 : 2) : 0);`
  and `a.OffsetAndWrap(!forward, 0, 2)`.

⇒ **that is vanilla UI logic recomputed mod-side**, and §2.4's own rule says captured-not-recomputed.
**The resolution is the one `SalvagePrice` already used:** the cycle is a *pure function of three
booleans* (`hasSelection`, `dismantle`, `forward`) and the tally a pure function of four integers —
so **both move to Core as `SalvageCycle`**, where the coverage gate sees them and T18 enumerates all
six item-cycle input states exhaustively. It is a reimplementation either way; putting it in Core is
what stops it being a **silent** one in `UNCOVERED_PROJECTS`.

⚠️ **And it mis-cycles during the RTT window.** The cycle reads *admitted* state, so a second click
before the round trip completes re-emits the **same** choice instead of advancing to the next one.
⇒ **NW-i's "no visible effect" was wrong, not merely understated: the gesture does not advance.**
Corrected at §9.

**(iv) The local write ordering, which decides whether the canary is usable at all.** Vanilla writes
the component on the click, *then* redraws. If the mod lets that stand and emits a claim in
parallel, then on the host `salvageCostTotal` includes the un-admitted choice while
`ledger.MergedTotal` does not — **§2.5.6's canary would block release on the host's own first
click — and, in the round-1 abort-on-mismatch shape, abort the barrier outright. A false fault by
construction.**

⇒ **Decision: the capture prefixes SUPPRESS the vanilla write and return false.** No local
`salvageSelection` write happens until the host's `SalvageState` comes back and is applied through
the normal apply path — on the host too, which is what keeps host and client on one code path
(§2.1's correct-after model, and §7 KILL 3's "one code path instead of two"). The player's own
machine is not a special case.

- The **`pending` render** of §2.5.4 is therefore a *decoration on the grid element*, not a written
  selection — it must not touch `salvageSelection`, or it re-creates the divergence it was added to
  paper over.
- The **cost of this decision, stated:** a local click has no visible effect until the round trip
  completes. On a loopback host that is sub-frame; on a client it is one RTT. If that proves
  unacceptable at the rig, the alternative is to let the local write stand and **make the canary
  version-aware over the admitted set only** — which is strictly more machinery. Recorded as the
  fork it is, with the cheap option chosen first.

#### 2.5.6 The canary must HALT, not merely DETECT — and prove its own liveness

Round 1 said a mismatch "aborts the barrier". That is a **detect** semantic, and this project has
paid for the difference: **a postfix that never fires produces no mismatch, ever.** The canary
lives on private `SalvageRefreshBudget` in `src/PBAndJ.Mod`, which is in `UNCOVERED_PROJECTS`
(`Makefile:220`) — a dead patch there is **silent**, and eternal agreement is exactly what a dead
patch looks like from the release gate.

⇒ **Release requires a POSITIVE, VERSION-FRESH reading, not the absence of a complaint:**

- the canary records a sample `(ledgerVersionAtSample, salvageCostTotal)` every time the postfix
  fires;
- `SalvageRelease` is sent only if a sample exists **whose `ledgerVersionAtSample` equals the
  current `ledger.Version`** and whose total equals `ledger.MergedTotal`;
- **a missing or stale sample BLOCKS release and says so by name.** Silence is a refusal, not a
  pass.

**And the patch set owes a count.** The mod already owns exactly the right liveness instrument and
round 1 never cited it: `ActuatorGlue.DriveState()` (declared `src/PBAndJ.Mod/Net/ActuatorGlue.cs:543`)
prints a patch count because *"a half-applied patch set is this mod's worst failure mode and is
otherwise invisible — `ModLink.OnLoad` wraps `PatchAll` in try/finally with no catch, so one
throwing patch silently drops every patch after it"* (`:535-542`). ⇒ **D2/D5 must extend that count
to the new patches** (`SalvageRedraw`, `SalvageRefreshBudget`, `SalvageFinish`, and the three
capture prefixes), and `pbj.salvage-probe` prints it. Without that, every guard in §2.5 is one
silent `PatchAll` failure away from being decorative.

#### 2.5.7 Tests owed — each with the RED it must produce before the fix exists

New Core files, `tests/PBAndJ.Core.Tests/Net/`, alongside `LobbyBarrierTests.cs` /
`LoadBarrierTests.cs`. Written first; each row states what fails, and how, on an empty
implementation.

| # | test | expected RED before the fix |
|---|---|---|
| T1 | **merged-cost arithmetic over N peers against one budget** — 3 peers, 7 keys, mixed recover/scrap, assert `MergedTotal` equals the hand-computed sum and is independent of which peer chose what | `MergedTotal` returns 0 (or throws on a missing member); the assertion on the sum fails |
| T2 | **the multiplier cases** — one `1.0` group and one `0.35` group, **with expected values taken from D2's captured `Mathf.RoundToInt` table (§2.5.2), never hand-derived**; includes `multiplier <= 0f ⇒ 0` | `SalvagePrice` does not exist; every arm fails |
| T3 | **scrap and recover are separately priced** — same key, two choices, two different totals | fails as soon as one arm is wired to the other's raw value |
| T4 | **over-budget refusal** — a claim whose `TotalIfApplied` exceeds the budget by 1 returns `OverBudget` and **leaves the ledger byte-for-byte unchanged** (version too); **boundary: a budget of 0 refuses every non-`Skip` claim** (NW-c) | admission accepts everything; version advances; state mutates |
| T5 | **claim-conflict resolution** — two peers, one key: first accepted, second `Reserved`, owner unchanged | second claim overwrites the first |
| T6 | **stale-version rejection** — `Version-1` ⇒ `Stale`, `Version+1` ⇒ `NeedsResync`, neither mutates | both accepted |
| T7 | **check ORDER** — a claim that is simultaneously stale *and* over budget *and* for a foreign key returns `Stale`; a current claim for a foreign key that is also over budget returns `Reserved`; a post-release claim that is *also* valid returns `Closed` | returns whichever arm happens to be first |
| T8 | **barrier: a departing peer does not satisfy it** — 2 participants, 1 ready, remove the unready one ⇒ `IsSatisfied` is **false** | `IsSatisfied` is true, exactly as `LobbyBarrier.RemoveParticipant` (`:142-146`) is documented to allow |
| T9 | **barrier is edge-triggered and re-arms** — release fires once per version; `AdvanceTo` clears every ready | release fires every poll |
| T10 | **release precondition** — barrier satisfied but merged total over budget ⇒ **no release** | releases |
| **T11** | 🆕 **a skip frees the ITEM, not just the budget** — A claims X, A skips X, **B claims X ⇒ `Accepted`**, owner B | B gets `Reserved`; the item is locked to its first toucher forever (§7 KILL 12) |
| **T12** | 🆕 **checks 1 and 3 have cases of their own** — a non-participant claim ⇒ `UnknownParticipant`; a claim naming a key absent from the manifest ⇒ `UnknownKey`; neither mutates | both accepted (round 1 listed neither, so "delete check 1" and "delete check 3" broke no test — §7 KILL 15) |
| **T13** | 🆕 **no-op claims do not move the version** — `Skip` on an unowned key, and a re-claim of your own key with the same choice, both ⇒ `NoChange`, `Version` unchanged, **no ready cleared** | both accepted; every double-click clears the barrier and it never closes |
| **T14** | 🆕 **post-release claims and readies are `Closed`** — seal the ledger, then submit a claim and a ready that would otherwise be valid | both accepted; the ledger mutates after the machines have exited |
| **T15** | 🔁 **REPLACED in round 3 — re-ready on version advance.** A peer with a latched finish-intent re-emits `SalvageReady` when the version advances, **with no further click**; a peer without the intent emits nothing; a ready at a superseded version is refused | the ready fires once and never again ⇒ **the barrier can never be satisfied after any peer's claim is accepted** (§2.6.1 item 2) |
| **T17** | 🆕 **the commit gate latches both ways** — `Holding` refuses; `Released` permits **exactly one** call; every call after that refuses forever | the gate passes every call once released ⇒ the post-release **double commit** of §2.6.1 item 1 |
| **T18** | 🆕 **duplicate-key claims are `Malformed`** — `{(X, Recover), (X, Scrap)}` is rejected and mutates nothing | accepted, with an outcome that depends on set iteration order |
| **T19** | 🆕 **own-key choice change REPLACES** — a claimant changing its own Recover to Scrap moves the total by the **difference**, not the sum | `TotalIfApplied` adds; the peer is refused `OverBudget` for an edit that costs less than what it already holds |
| **T20** | 🆕 **`SalvageCycle`** — all **six** item-cycle input states (`hasSelection` × `dismantle` × direction) enumerated exhaustively against `:1985-2011`, and the unit-branch tally + `OffsetAndWrap` against `:1962-1963` | the cycle does not exist; a suppressing prefix cannot say what the body would have written (§2.5.5) |
| **T21** | 🆕 **the budget is latched first-wins** — a second capture carrying a different value does not move `ledger.Budget` | the re-entry budget of §2.5.2 hole 1 overwrites it mid-screen |
| **T22** | 🆕 **a `SalvageState` buffered before the screen opens is applied on open, latest-wins** | it is dropped at `:2043` and never replayed — and the client opens late by construction |
| **T16** | 🆕 **release needs a positive, version-fresh canary sample** — no sample ⇒ no release; a sample at `Version-1` ⇒ no release; a matching sample at `Version` ⇒ release | releases on silence, which is what a dead patch produces (§2.5.6) |

**Mutations — the failure modes written as code, not as sentences.** Each is applied to the finished
implementation; each must turn at least one test above red, and the row names which.

| mutation | must break |
|---|---|
| **swap dismantle/take costs** — map `Recover` to `rawScrap` and vice versa | T3 (and T1's sum) |
| **drop the multiplier** — delete `SalvagePrice.For`'s `< 1f` arm | T2, and T4 via the 0.35 group's totals |
| **hand-derive T2's rounding** — replace the captured table with decimal `Math.Round` on a `double` product | T2, on the boundary rows — **and this mutation is the reason the table is captured** |
| **let a departing peer satisfy the barrier** — drop the `Aborted` latch, i.e. revert to `LobbyBarrier`'s shape verbatim | T8 |
| **accept a stale version** — change check 2's `<` arm to fall through | T6, T7 |
| **delete check 0** (the `Sealed` gate) | T7, T14 |
| **delete check 1** / **delete check 3** | T12 — 🆕 in round 1 these broke **nothing**, which is why T12 exists |
| **delete check 6** (treat no-ops as acceptances) | T13 |
| **write `(Skip, P)` instead of deleting the row** | T11 |
| **emit the ready only once** (revert to "the first time it fires") | T15 |
| **let the commit gate pass after `Committed`** | T17 |
| **accept a duplicate-key claim** (drop check 3's well-formedness arm) | T18 |
| **make `TotalIfApplied` ADD instead of REPLACE** for the claimant's own key | T19 |
| **invert the cycle direction** in `SalvageCycle` | T20 |
| **let a later budget capture overwrite the latched one** | T21 |
| **drop the buffered-state replay** | T22 |
| **make the canary detect-only** — release when no mismatch has been *seen*, rather than when a fresh matching sample *exists* | T16 |
| 🔴 **admit a claim that exceeds the remaining budget** — delete check 5 | **T4 and T10 only.** T1/T2/T3 stay green: the arithmetic is still right, it is simply never consulted. **This is the point.** The enforcement defect is TEMPORAL — it is about *when* a number is looked at — and every arithmetic test in this table is structurally unable to see it. That is why check 5 gets its own test and its own mutation row rather than being folded into the cost tests. *(Verified by the round-2 review: deleting check 5 turns T4 and T10 alone red, exactly as claimed.)* |

**Two gaps, stated as decisions rather than left as sentences that read like safeguards.**

1. The **price capture** — the `SalvageRedraw` postfix calling `GetSalvageCost(entity, dismantled,
   1f)` and reading `group.costMultiplier` — lives in `src/PBAndJ.Mod`, in `UNCOVERED_PROJECTS`
   (`Makefile:220`), so a mutation there is **silent**: no test can be made to fail. It is covered
   instead by §2.5.6's positive-liveness canary, the `DriveState()` patch count, and D2's probe.
   **All three are weaker than a red test and are recorded as such**, not dressed up as equivalent.
2. The **capture prefixes and the reentrancy scope** (§2.5.5) are Mod-side for the same reason and
   carry the same silence. The instrument that reaches them is D2's probe printing, per gesture,
   `claimsEmitted=` and `applyDepth=` — a **rig** reading, not a gate. The one thing that *is*
   gate-visible is that the ledger cannot be driven into a loop by well-formed input, which is
   T13's job.

### 2.6 The barrier and the commit ▸ commit half REWRITTEN round 2 (2026-08-22)

**`SalvageBarrier` is the FOURTH barrier in the codebase, not the third** — corrected 2026-08-22:
`TurnBarrier.cs`, `LobbyBarrier.cs` (191 lines) and `LoadBarrier.cs` (182 lines) all exist today.
`LobbyBarrier` is still the shape to copy, and it carries the traps:

- **edge-triggered, not level-triggered** — release fires once per version;
- **a departing peer must not satisfy it**;
- a version counter so a stale ready is recognisable (`LobbySelection.Version`, the lobby's
  analogue).

Four deliberate divergences from `LobbyBarrier`, each with its reason:

1. **Its counter is the ledger's version, not a selection version.** Readiness must clear whenever
   the merged selection set changes, or a peer that agreed to a 90-point set is counted as agreeing
   to a 100-point one. Since §2.5.3 advances `ledger.Version` on every acceptance, the host calls
   `AdvanceTo(ledger.Version)` there — which is `LobbyBarrier.AdvanceTo` (`:185-189`) verbatim,
   `ready.Clear()` included. (Check 6 exists so that a *no-op* claim does not do this — §2.5.3.)
2. 🔴 **`RemoveParticipant` latches an `Aborted` flag.** `LobbyBarrier.RemoveParticipant`
   (`:142-146`) *can* satisfy the barrier — its own remark says so — and `LoadBarrier.Drop`
   (`:111-120`) has the same shape. For salvage that is wrong: §2.7's answer to a disconnect is
   **roll back**, not commit-without-them. ⇒ `IsSatisfied` becomes
   `participants.Count > 0 && ready.Count >= participants.Count && !Aborted`, and only `AdvanceTo`
   clears the latch. This is a divergence, not an oversight — and mutation "let a departing peer
   satisfy the barrier" (T8) is what stops that sentence being a comment asserting an invariant.
3. **Satisfaction is a precondition, not the trigger.** The host sends `SalvageRelease` only when
   `IsSatisfied` **and** `ledger.MergedTotal <= ledger.Budget` **and** §2.5.6's canary returns a
   **positive, version-fresh** sample. Release then **seals** the ledger (check 0).
4. 🔴 **It has NO `Unready` member — and that is a divergence from both siblings, decided on
   purpose.** `TurnBarrier.Unready` (`:97`) and `LobbyBarrier.Unready` (`:182`) both exist, and the
   protocol carries `Unready = 11` and `LobbyUnready = 25` for them (`PbjMessage.cs:23`, `:37`).
   Round 2 copied the pattern; **round 3 showed the message has no sender** (§2.6.1 item 5), and a
   ready here is cleared by `AdvanceTo` on every accepted claim anyway. ⚠️ **The reason this must be
   a decision and not a leftover:** `src/PBAndJ.Core` is the covered project (`Makefile:200`), so an
   `Unready` member with no caller is **dead Core code and a build failure** — not the silent
   no-op it would be in `src/PBAndJ.Mod` (`Makefile:220`). Copying the sibling barrier verbatim
   would have failed the gate.

#### 2.6.1 🔴 The commit gate is on `SalvageFinish`, NOT on "we control who invokes the button"

Round 1 said the confirmation modal is *"a natural waiting-for-the-others holding state"*.
**That is false**, and every counter-example below was opened in `CIViewDialogConfirmation.cs`
(146 lines) on 2026-08-22:

| # | route past the barrier | evidence |
|---|---|---|
| 1 | **The player just clicks Confirm.** `Open` arms the modal live: `buttonConfirm.available = true` (`:96`, re-set `:104-105`), `buttonCancel.available = true` (`:97`). A peer who has readied can commit **unilaterally, before release**, using the button vanilla put under their cursor | `:96-105` |
| 2 | **Nav Forward.** `Awake` binds `this.TryAddFallbackNode(OnCancelInput)?.SetLinkCallback(CINavDir.Forward, OnConfirm)` — `OnConfirm` as a **direct method reference**, so it bypasses `available` for the same reason `Invoke()` does (§2.5.2) | `:50` |
| 3 | **Escape becomes Confirm.** `OnCancelInput` (`:75-85`) calls `OnCancel()` only when `inputHintCancel.gameObject.activeSelf`; otherwise it calls **`OnConfirm()`**. ⚠️ **The obvious mitigation — hide the cancel button — converts Escape into a commit.** Named here so nobody reaches for it | `:75-85` |
| 4 | **Cancel does not un-arm the callback.** `OnCancel` (`:134-140`) exits and clears the buttons but **never clears `callbackOnConfirm`**. With no un-ready message, a peer who readies then cancels is **still counted ready**, and on release the stale callback commits what they cancelled — possibly after they changed their selections | `:134-140` + divergence 4 above |
| 5 | **The dialog is a shared singleton.** `public static CIViewDialogConfirmation ins` (`:7`), input context `"SharedConfirmationDialog"` (`:9`). Release invokes whatever `callbackOnConfirm` **currently** holds; any other dialog opened between ready and release repurposes it | `:7`, `:9`, `:98` |
| 6 | **No way to re-ready.** Every acceptance calls `AdvanceTo`, clearing readies — but a peer holding the modal has only two exits, confirm (route 1) or cancel (route 4). **Stall-shaped**, and §2.7 names stall as the real hazard | §2.5.3 accept row |

⇒ **The gate belongs on `SalvageFinish`, the choke point, not on the button.** Derivation:

- `SalvageFinish` (declared `:2772`) has **exactly two references** in the whole view — `:2763`
  (passed to `CIViewDialogConfirmation.Open` as `callbackOnConfirm`) and `:2768` (the direct call on
  the no-modal arm). A Harmony **prefix on `SalvageFinish` catches both**, including the delegate
  route, because the prefix runs on the method rather than on whoever holds the reference.
- Every finish gesture funnels into `OnSalvageFinish` first: `rewardButtonFinish` (`:540`),
  `salvageButtonFinish` (`:541`), `OnStageNext`'s salvage arm (`:806`), and
  `OnStageNextExternal()` (`:760`) → `ins.OnStageNext()` (`:764`). (`OnSalvageFinishFromRewards`,
  declared `:2737`, is an **empty body** — it is not a route.)
- `SalvageFinish` carries heavy, irreversible world mutation on **both** arms — province ownership
  refresh (`:2786`), `ClearActionsAfterCombat` (`:2817`), `OnCombatCompletionLate` (`:2818`),
  and `ProcessSalvageSelections` (`:2825`). It is exactly the line that must not be crossed twice or
  early.

⇒ **The shape, replacing round 1's:**

1. 🔴 **A prefix on `SalvageFinish` gates in BOTH directions — round 2 gated only one.**
   It refuses while the local machine has not received `SalvageRelease` for the current ledger
   version, **and it refuses again, permanently, once the release-driven call has run** (a latched
   local `Committed` state). **Round 2's one-directional gate permitted a post-release DOUBLE
   commit**, and the crossing is reachable: a readied peer re-clicks finish while its ready is in
   flight → `:2757` passes → the modal opens armed with `callbackOnConfirm = SalvageFinish` →
   release arrives → the mod calls `SalvageFinish` directly (**commit 1**) → **the modal is still
   open**, because the mod's direct call never touches the dialog and `SalvageFinish`'s own
   `TryExit()` (`:2775`) is `CIView.TryExit` on the *debriefing* (`CIView.cs:177`), not on the
   dialog — it even exits two other views explicitly (`:2828`, `:2830`) and never that one → the
   player clicks Confirm → **commit 2**. `SalvageFinish` has **no internal guard**: `:2773-2776` is
   tutorial advance, `TryExit()`, audio, straight into world mutation. A second run re-executes
   `ClearActionsAfterCombat` (`:2817`), `OnCombatCompletionLate` (`:2818`),
   `ProcessSalvageSelections` (`:2825`) and `FreeOrDestroyCombatParticipants` (`:2826`).
   §2.6.1's own sentence said this is *"the line that must not be crossed twice or early"* — round 2
   enforced **early** only.
2. 🔴 **Ready is a LATCHED LOCAL INTENT that re-arms itself, not an event emitted once.**
   Round 2 said the prefix *"emits `SalvageReady{Version}` the first time it fires"*. **That
   livelocks**, and route 6 of the table below diagnosed exactly this shape against the old design:
   §2.5.3's accept arm calls `AdvanceTo(Version)` on **every** acceptance by **any** peer, clearing
   every ready — so A confirms, B's claim is accepted, A's ready is cleared, A re-clicks, the prefix
   fires again and emits nothing. The barrier can never be satisfied.
   ⇒ the first `SalvageFinish` call **latches a local finish-intent**, and the mod emits
   `SalvageReady{Version}` **whenever it holds that intent and is not already ready at the current
   version** — including automatically on every `SalvageState` that advances the version, with no
   further click. The player clicks once; the protocol re-ready is the mod's job.
3. **One gate covers both the modal arm and the no-modal arm** — the missing half of round 1's NW-a
   (§9), whose "suppress and ready" had no release action on the arm where no modal exists.
4. **The modal is reduced to display.** It may open (it is the vanilla feedback that the click
   registered) but it is no longer load-bearing: routes 1–3 above now land on a prefix that holds,
   and route 1 after release lands on the `Committed` latch. **Nothing is driven through
   `buttonConfirm`.**
5. 🔴 **`SalvageUnready` is DELETED, not specified — round 2 invented a message with no sender.**
   Under this shape the ready is emitted when `SalvageFinish` fires, which is **after**
   `CIViewDialogConfirmation.OnConfirm` has already `TryExit`'d the dialog (`:126-131`) — so
   **cancel always precedes any ready** and the cancel hook could never un-ready anything. Round 2's
   justification cited route 4, describing the gate shape that same round replaced: **stale inside
   its own revision.** The hook would also have sat on the *shared singleton's* cancel path, firing
   for any unrelated dialog cancelled while a salvage ready was outstanding — inheriting route 5's
   problem into the fix for route 4.
   **And nothing needs it:** every selection change produces a claim, every acceptance advances the
   version, and every version advance clears the ready anyway (divergence 1). Un-ready is *redundant
   with the version bump*. **What would resurrect it:** a deliberate "cancel my ready" affordance in
   the UI, which M12d does not add. Recorded so its absence is a decision, not an oversight.
6. **Release = clear the local gate, call `SalvageFinish` directly, then latch `Committed`**, not
   via any button or callback. That sidesteps route 5's singleton entirely: the mod never depends on
   what `callbackOnConfirm` happens to hold.

**What survives from round 1, unchanged:** §2.5.2's finding that `callbackOnClick.Invoke()` bypasses
`available`. It is *why* the button cannot be the gate — the design no longer relies on that bypass,
it is now a hazard the design routes around.

**Drive-path entry points, all re-verified 2026-08-22** — but see §2.5.5, which shows they are **not
the vanilla click surface**: `OnSalvageRecover/Scrap/Skip(int)` at `:2026`, `:2031`, `:2036`; the
`…Unit(int)` trio `OnSalvageRecoverUnit` / `OnSalvageScrapUnit` / `OnSalvageSkipUnit` at `:1868`,
`:1873`, `:1878`; `OnStageNextExternal()` at `:760`. (End-to-end drivability measured 2026-08-08 —
**UNVERIFIED here** — but the entry points themselves are real and were opened.)

**Also outside the funnel:** `TransferStandaloneRewards` (`EquipmentUtility.cs`, declared `:1733`,
sole caller `CIViewOverworldReward.cs:157` — two hits total under `decompiled/`, one of them the
declaration, so "sole caller" is derived) is a second, budget-free transfer path for non-combat
reward popups. Ownership must follow it too — stage D6.

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
- `SalvageManifest` — the host's bit-set: per `(unitKey, SalvageKey)`, `salvageable`. **Identity
  only** — prices moved out in round 2 because they do not exist at manifest time (§2.5.2). Pure
  data + a merge/apply function returning "what to change".
- `SalvagePrices` — 🆕 the screen-time price rows: per key `rawRecover` / `rawScrap` /
  `costMultiplier`. ⚠️ **Core-only, populated on the host, never serialised** — round 3 cut it from the wire for want of a consumer.
- `SalvagePrice` — 🆕 **replaces `SalvagePool` (2026-08-22, B4).** `For(int raw, float multiplier)`,
  the three arms of `EquipmentUtility.GetSalvageCost` (`:1622`, `:1640-1643`) and nothing else. It
  lives in Core **because the coverage gate must be able to see the "drop the multiplier" mutation**
  — §7 KILL 11.
- `SalvageLedger` — 🆕 the shared budget, the global `Version`, the `Sealed` flag, the per-key
  `(Choice, Owner)` rows with **`Skip` deleting rather than owning** (§2.5.3), `MergedTotal` /
  `TotalIfApplied` over a **set**, and the **seven-check** admission order of §2.5.3 (checks 0–6).
  **This is where budget enforcement lives**; per-player pools and remainders are gone.
- `SalvageBarrier` — the state machine, mirroring `LobbyBarrier` with the three divergences of §2.6
  (ledger version, latched `Aborted`, satisfaction-as-precondition).
- Message types + codec arms (§4).
- The `HostSession`/`ClientSession` dispatch arms and the `PbjEffect` kinds that carry the work out.

**Mod (`src/PBAndJ.Mod/Net/`, every type `[ExcludeFromCodeCoverage]`):**

- The `PrepareUnitForSalvage` postfix (capture on host, apply on client).
- Reading `salvageBudgetLast` off the live view and reporting it.
- Driving selection through the public statics; releasing the confirm modal.
- 🆕 The **`SalvageRedraw` postfix** capturing the raw prices — `GetSalvageCost(e, false, 1f)` /
  `GetSalvageCost(e, true, 1f)` per entity and the group's `costMultiplier`, walking the private
  `salvageGroups`. **Uncovered by construction** (`Makefile:220`), so it is guarded by §2.5.6's
  positive-liveness canary, the `DriveState()` patch count and D2's probe, not by a test — stated
  as the weaker instruments they are (§2.5.7).
- 🆕 The `SalvageRefreshBudget` postfix that records the version-fresh canary sample (§2.5.6).
- 🆕 **The three capture prefixes** — `OnSalvageDecision` (`:2041`), `OnSalvageDecisionUnit`
  (`:1883`), `OnSalvageDecisionShift` (`:1928`) — with the outermost-wins reentrancy scope, which
  suppress the local write and emit one `SalvageClaim` per gesture (§2.5.5).
- 🆕 **The `SalvageFinish` prefix** — the single commit gate covering both the modal and the
  no-modal arm, gating in **both** directions: hold until `SalvageRelease`, then latch `Committed`
  so a still-open modal cannot drive a second commit (§2.6.1).
- 🆕 **The `SalvageRedraw` postfix** — overwrite `salvageBudgetLast` from the ledger, re-run
  `SalvageRefreshBudget()`, latch the budget first-wins, and replay a buffered `SalvageState`
  (§2.5.2). One postfix, three holes.
- 🆕 Extending `ActuatorGlue.DriveState()`'s patch count to all of the above (§2.5.6).
- The probe (§5) and every `Contexts.sharedInstance` read.

⚠️ **Two existing Core invariants M12d must not break:**
1. `PbjMessage`'s constructor is `protected` and `Type` is `abstract` **so the test suite can define
   an out-of-range subclass and reach `PbjMessageCodec`'s `default:` arm**
   (`src/PBAndJ.Core/Net/PbjMessage.cs:63-66`). Do not seal either.
2. Messages carry no validation; sessions do (`docs/design/networking.md`).

---

## 4. The wire: yes, a break — and its own window

**RE-DERIVED FROM SCRATCH in round 3 under one hard rule, because three of round 2's eight rows
changed under review and a table that churns cannot open a merge window:**

> 🔴 **Every row must name its PRODUCER MOMENT and its CONSUMER** — the moment some machine can
> first compute the payload, and the code on the far side that reads it. A row that cannot name both
> is not a message.

That rule is not decoration. Applied backwards it catches **every wire defect the three review
rounds found**: round 2's price rows had no producible moment (the multiplier is minted at screen
time, the manifest is built at `:225`); round 3's free-drop field had the same defect one level up
(`TriggerPostCombatRewards` runs at `:230`, after the manifest loop closes at `:227`); round 3's
price rows had no consumer (admission is host-side, the client prices its own grid); and
`SalvageUnready` had no producer at all (cancel always precedes ready). **Four defects, one missing
column.**

**Baseline, recounted here rather than inherited.** The enum ends at `ReplayAssets = 32`
(`src/PBAndJ.Core/Net/PbjMessage.cs:56`); W2's v10 moved `PbjProtocol.Version` (`:217`) and
`ModVersion` to `"0.24.0"` (`:260`) **without adding a type**. The body holds **32** `Name = N,`
entries whose ordinals are exactly `1…32` — enumerated, not assumed — so the next free ordinal is
`33`.

**The count, and its history: 6 → 7 → 8 → 7.** It is back to seven, and the two moves that got it
there are the two the rule forced: `SalvageScreen` shrank to `SalvageBudget` (prices have no
consumer, so they never leave the host), and `SalvageUnready` is gone (no producer).

| # | type | producer MOMENT (the earliest point the payload exists) | consumer (the code that reads it) | payload |
|---|---|---|---|---|
| 33 | `SalvageManifest` | host, the `PrepareUnitForSalvage` postfix — `OverworldCombatOutcomeProcessingSystem.cs:225`, inside the participant loop that closes `:227` | client's manifest apply, before its screen opens; the host's own `SalvageLedger` keys off the same set | the salvageable bit-set per `(unitKey, SalvageKey)` + host-minted identities for **pre-existing gear only**. 🔴 **No free drops** — they are created at `:230`→`:1214`/`:1236`, after this moment (§2.5.2); D6 |
| 34 | `SalvageBudget` | host, the **first** `SalvageRedraw` postfix (`:2097`) after the screen opens — the budget is not final until `:2208` | **the `SalvageRedraw` postfix on every machine**, which overwrites the private `salvageBudgetLast` (`:431`) and re-runs `SalvageRefreshBudget()` (§2.5.2) | one `int`, latched first-wins (T21) |
| 35 | `SalvageClaim` | client, a capture prefix on one of the three gesture members (§2.5.5) | host `SalvageLedger` admission, checks 0–6 (§2.5.3) | `{ Version, Choices: non-empty, duplicate-free set of (Key, Choice) }` |
| 36 | `SalvageState` | host, on an **accepted** claim only | every machine's apply path (the public statics), **and the `SalvageRedraw` postfix** for a state that arrived before the screen existed (T22) | ledger `Version` + merged selections with owners + per-peer spend (**display**, not a limit) |
| 37 | `SalvageClaimResult` | host, on **every** admission decision | the claimant's UI: clears `pending`, renders `reserved`/`over budget` against the offending row | `{ Version, Outcome, FirstOffendingKey? }` — `Accepted / Closed / UnknownParticipant / Stale / NeedsResync / Malformed / UnknownKey / Reserved / OverBudget / NoChange` |
| 38 | `SalvageReady` | client, whenever it holds a latched finish-intent and is not already ready **at the current version** — re-emitted automatically on every version advance, with no second click (§2.6.1 item 2) | host `SalvageBarrier.SetReady` | `{ Version }` — carried for the reason `LobbyReadyMessage` carries its selection (`PbjMessage.cs:884-888`) |
| 39 | `SalvageRelease` | host, when `IsSatisfied` **and** merged total ≤ budget **and** a version-fresh canary sample exists (§2.5.6) | every machine's `SalvageFinish` gate: clear, call, latch `Committed` (§2.6.1 item 1) | `{ Version }`; sending it **seals** the ledger (check 0) |

⇒ **the enum tail moves `ReplayAssets = 32` → `SalvageRelease = 39`.**

**What is deliberately NOT on the wire, each with the reason the rule produced:**

| not a message | why |
|---|---|
| per-key `rawRecover` / `rawScrap` / `costMultiplier` | **no consumer.** Only the host prices a claim; a client's grid is priced by its own vanilla `GetSalvageCost` from the ECS state the manifest already reconciles, and those prices are redraw-stable. `SalvagePrices` is a **Core type populated host-locally**, never serialised |
| `SalvageUnready` | **no producer.** Ready is emitted from `SalvageFinish`, which runs after `OnConfirm` has already exited the dialog (`:126-131`), so cancel always precedes ready; and a version advance clears readies anyway (§2.6.1 item 5) |
| free-drop identities | **no producible moment** at 33's producer, and no priced role — created at `:230` and attached to the base inventory (`:1218`), not to a participant unit. D6 |
| per-player pools / remainder / an allowance | dead with the shared-budget decision (§2.5) |

**Dead with the pool split:** every per-player allowance field. `SalvageState` still carries
per-peer spend, but as **display**, not as a limit; the only limit is §2.5.3 check 5.

`PbjMessage.cs` and `PbjMessageCodec.cs` are **both** `WIRE_FILES` (`Makefile:43-47`; the list is
one `:=` spanning `:43-47` and both names are on `:44`). `Seams.cs` moves too if `IPbjGameBridge`
gains apply/capture members — also a `WIRE_FILE` (`Makefile:47`).

⇒ **Protocol v10 → v11, ModVersion bump, `make record-wire-surface` in the same commit, in M12d's
own merge window W3.** M12c owns W1 (`Seams.cs`); M17 stage 2 owned W2 (`UnitSnapshot.cs` + codec)
and **has merged** — v10 and 0.24.0 are on `main`. Two writers to one `wire-surface.lock` is two
windows, always.

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
| **D1** | **Core, wire-neutral.** `SalvageKey`, `SalvageOwner` (tag format + `pbj_` prefix rule), `SalvageManifest` (bit-set + apply-diff, **identity only** — §2.5.2 moved prices out; round 3 cut free drops), `SalvagePrices` (**Core-only, never serialised**), `SalvageCycle`, 100% tests. No game types. | none | `make dist` exit 0 (read the tail AND the exit code) |
| **D2** | **Mod probe, wire-neutral.** `SalvageProbeGlue`: the `PrepareUnitForSalvage` postfix in *capture-only* mode, plus `pbj.salvage-probe` printing M1–M3, **per key `rawRecover`/`rawScrap`/`multiplier`**, 🔴 **the `Mathf.RoundToInt` rounding table T2 is populated from** (§2.5.2 — `Mathf` is absent from `decompiled/`, so the rounding is a MEASUREMENT), **the patch count** (§2.5.6), and per gesture `claimsEmitted=`/`applyDepth=`; plus `pbj.owner-tag`/`pbj.owner-read` for M5. Ships before any authority code so the milestone is scoped on numbers. | none | `make dist`; every zero prints its own alternative hypothesis or the probe does not ship |
| **— R** | **Rig: M1–M5.** Folded into R1 if the timing works; otherwise its own short session. **D3's shape forks on M3.** | — | numbers into `docs/notes/rig-run-1-0.md` |
| **D3** | **Core, wire-neutral. RE-SPECIFIED 2026-08-22 (B4) — `SalvagePool` is dead.** Three units: `SalvagePrice.For(raw, multiplier)` (the three arms of `GetSalvageCost` `:1622`/`:1640-1643`); `SalvageLedger` (budget, global `Version`, `Sealed`, per-key `(Choice, Owner)` with `Skip` deleting, `MergedTotal`, `TotalIfApplied` over a set, and the **seven-check admission order** (checks 0–6) of §2.5.3); `SalvageBarrier` (edge-triggered, version = the ledger's, **`RemoveParticipant` latches `Aborted`**, **no `Unready`** — §2.6 divergences 2 and 4); `SalvageCycle`; `SalvageCommitGate`. Tests **T1–T22** and all **eighteen** mutations of §2.5.7, **red first**. Tested against a fake bridge. | none | `make dist`; every mutation turns a named test red, or the mutation row is wrong |
| **D4** | **WINDOW W3 — the wire.** The **seven** message types `33`–`39` (§4, **re-derived from scratch in round 3** under the producer-moment/consumer rule — `SalvageClaimResult` is the only net addition; prices and `SalvageUnready` were cut for having no consumer and no producer), codec arms, `PbjProtocol` v10→v11, `IPbjGameBridge` capture/apply members in `Seams.cs`, ModVersion bump, `mod/metadata.yaml`, `make record-wire-surface` **in the same commit**. Host/client dispatch arms + effects. | **v11** | `make dist` exit 0; `make peer-selftest` WILL change and the PR body says so |
| **D5** | **Mod, the screen.** Apply the manifest in the postfix; broadcast the budget off the live view; **capture** local gestures with prefixes on all three writers — `OnSalvageDecision`, `OnSalvageDecisionUnit`, `OnSalvageDecisionShift` — suppressing the local write, emitting one claim per **outermost** gesture (§2.5.5); apply remote choices **through the public statics, never by writing `salvageSelection`** — that is what forces the redraw (§2.5.3); a postfix on `SalvageRefreshBudget` recording the **version-fresh canary sample** (§2.5.6); 🔴 **one prefix on `SalvageFinish`** as the commit gate for BOTH arms, release by calling it directly — never through the shared confirmation dialog (§2.6.1); 🔴 **the `Committed` latch** so release cannot be followed by a second commit (§2.6.1 item 1); the ready re-armed automatically on every version advance; the `SalvageRedraw` postfix that overwrites `salvageBudgetLast`, latches the budget first-wins and replays a buffered `SalvageState`; the `DriveState()` patch count extended; rollback-to-checkpoint on disconnect. | none | `make dist`; hash proven unmoved |
| **— R** | **Rig: M6 + two-machine acceptance.** Identical salvage lists, identical commits, reserved markers, a stall survived. 🧑 partly eyes. | — | into the runbook |
| **D6** | **Assigned gear, the other half.** Loose-subsystem `(kind, serial)` side table riding the transfer; ownership through `TransferStandaloneRewards` (`EquipmentUtility.cs:1733`); the general-inventory claim granularity (design q4). **Cuttable without blocking the screen.** | possibly one more | `make dist` |

**Order note:** D3 before D4 deliberately — the barrier and the arithmetic are pure Core and can be
written, tested and refuted before W3 opens, so W3 carries only mechanical work. (Written 2026-08-21 as
"while W2 is still open"; W2 has since merged, and the ordering argument does not depend on that.)

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

**KILL 8 — "vanilla's `salvageCostValid` gate does the enforcing under one shared budget."** Dead,
2026-08-22, in three compounding ways, all in §2.5.1: it is a **redraw**; its one genuine refusal
(`OnSalvageFinish:2757`) fires on the local click, is skipped entirely on the no-salvage arm
(`:2755`, `:2766-2768`), and refuses by logging; and it is **stale by construction** across two
machines. The funnel logs over-budget and transfers anyway (`:1903-1905`; `budget` is used three
times in the whole member and never to refuse). ⇒ enforcement is host-side claim admission.
**This kill is what the whole of §2.5 is.**

**KILL 9 — "a direct `callbackOnClick.Invoke()` might still respect the button's `available` flag."**
Dead, and it was carried as **UNVERIFIED** in three places until 2026-08-22. `UICallback`
(`decompiled/UICallback.cs`, member `Invoke`, `:143-184`) has no reference to its button; the flag
is read only in `CIButton.OnPressEvent` (`:527`) and `CIButton.ForceClick` (`:660`). **The drive
path has no vanilla backstop whatsoever** — which strengthens host-side admission rather than
weakening it, exactly as §2.5 predicted it would either way.

**KILL 10 — "`SalvageBarrier` can just be `LobbyBarrier` again."** Dead. `LobbyBarrier`'s
`RemoveParticipant` (`:142-146`) is *documented* to be able to satisfy the barrier, and
`LoadBarrier.Drop` (`:111-120`) shares the shape. Under §2.7 a departure means **roll back**, so
salvage needs a latched `Aborted` — §2.6 divergence 2, with mutation T8 to keep the sentence honest.
It is also the **fourth** barrier, not the third: `TurnBarrier.cs`, `LobbyBarrier.cs`,
`LoadBarrier.cs` all exist today.

**KILL 11 — "ship the final salvage price on the wire; Core needs no arithmetic."** Dead on the
coverage gate, not on taste. `src/PBAndJ.Mod` is in `UNCOVERED_PROJECTS` (`Makefile:220`) and
`src/PBAndJ.Core` is the covered project (`Makefile:200`). Ship a final price and the "drop the
multiplier" mutation has nothing in Core to mutate — the multiplier test becomes **vacuous while
reading exactly like a safeguard**. §2.5.2 therefore splits the difference: raw pairs + multiplier
on the wire, the three-arm rule in Core, a runtime canary over the uncovered capture.

**— ROUND 2 (2026-08-22): the round-1 re-spec itself took an adversarial pass, and five of its own
claims died. They are recorded here rather than quietly patched, because the pattern matters: every
one of them was a place where a *conclusion* was right and a *mechanism* was invented.**

**KILL 12 — "check 4 plus a uniform accept row resolves conflicts."** Dead, and it was a **gameplay
defect**, not a protocol one. Writing `(Skip, P)` on an accepted skip, then refusing every foreign
claim, locks an item to its first toucher **forever**: the budget comes back, the item does not.
"Owned" was used throughout and defined nowhere. §2.5.3 now defines it (`Skip` is the *absence* of
ownership and **deletes** the row) and **T11 is the test round 1 did not have** — its absence is why
this shipped through a table that looked complete.

**KILL 13 — "per-key versions would re-open the joint over-budget hole."** Dead. Check 5 is
evaluated at the host over the merged ledger **on arrival**, so it is total under either versioning
scheme — the round-1 walkthrough proved that against its own derivation and nobody noticed.
**The conclusion (one global counter) survives for the real reason: the BARRIER needs one number a
ready can name**, exactly as `LobbyReadyMessage` needs its `SelectionVersion`
(`PbjMessage.cs:884-888`). Rewritten at §2.5.4. ⚠️ Check 2's stated reason was invented in the same
way — it described a manifest replacement **this design never performs** — and is corrected in the
same table.

**KILL 14 — "the confirmation modal is a natural holding state."** Dead, six ways, all opened in
`CIViewDialogConfirmation.cs` and tabulated at §2.6.1: the modal is armed live (`:96-97`,
`:104-105`), nav-Forward calls `OnConfirm` by direct reference (`:50`), Escape *becomes* confirm
when the cancel hint is hidden (`:75-85`), cancel never clears `callbackOnConfirm` (`:134-140`),
`ins` is a shared singleton (`:7`, `:9`), and a peer whose ready was cleared has no way to re-ready.
⇒ **the gate moved to `SalvageFinish`**, which has exactly two references (`:2763`, `:2768`) and
catches both from one prefix. **The lesson is the general one: a UI object that another system can
also drive is not a lock.**

**KILL 15 — "the test set is derived."** Dead as stated. Round 1's T1–T10 contained **no
`UnknownParticipant` case and no `UnknownKey` case**, so "delete check 1" and "delete check 3" broke
**nothing** — failing the table's own stated standard while reading as if it met it. The 100% branch
gate would have forced coverage at build time, but the *plan* claimed a derivation it did not have.
T12–T16 close it. **This is the twenty-something-th sighting of the same shape: an instrument whose
completeness was asserted rather than checked.**

**KILL 16 — "the canary aborts on mismatch."** Dead: that is a **detect** semantic, and a postfix
that never fires produces eternal agreement. The patch lives in `UNCOVERED_PROJECTS`
(`Makefile:220`), so its death is silent. §2.5.6 replaces it with a **positive, version-fresh
sample** whose absence blocks release, and pulls in the instrument the mod already had and this plan
never cited — `ActuatorGlue.DriveState()`'s patch count (`src/PBAndJ.Mod/Net/ActuatorGlue.cs:535-542`,
member declared `:543`), which exists because *"a half-applied patch set is this mod's worst failure
mode and is otherwise invisible."*

**KILL 17 — "prices ride the manifest."** Dead on a **moment**, not a payload.
`PrepareUnitForSalvage` runs at `OverworldCombatOutcomeProcessingSystem.cs:225`, before any screen;
`costMultiplier` is minted at `CIViewOverworldDebriefing.cs:2686`, inside `SalvageRedraw`. Round 1
specified a message that **could not be built as written** — and the fix that first suggested itself
(recompute `flag4` host-side) would have violated §2.4's own never-recompute rule *in the very
message that rule was written for*. §2.5.2 moves prices to screen time, which also removes an
unproven `isWrecked` drift that would have landed on the canary as a false abort.

**— ROUND 3 (2026-08-22): no architectural defect.** Host-side admission, the shared ledger, the
barrier, suppress-until-admission and the single-choke-point gate all survived deliberate attack,
and the ten-site/three-member writer enumeration was independently re-derived and confirmed exact.
**Everything round 3 killed was downstream of the architecture — and five of the seven were in or
beside the wire table**, which is why §4 was re-derived under a rule instead of re-checked.

**KILL 18 — "the manifest carries the host-minted identities for free drops."** Dead: **the same
moment bug round 2 fixed for prices, left standing one level up, in the row that costs a protocol
version.** `PrepareUnitForSalvage` fires at `OverworldCombatOutcomeProcessingSystem.cs:225` inside a
loop closing `:227`; `TriggerPostCombatRewards` runs **after** it at `:230`; the drops are minted
inside `PrepareRewardsFromSavedOutput` at `:1214`/`:1236`. And §3's patch list named **no patch on
either member**, so nothing in the plan could have observed the creation. Cut from v11 → D6, with
the residual (do free drops surface on the grid?) filed as rig reading M2 rather than assumed.

**KILL 19 — "the commit gate holds."** Dead in one direction. It refused *early* and permitted a
post-release **double commit** — the mod's direct `SalvageFinish` call never closes the still-open
modal, and `SalvageFinish` has no internal latch (`:2773-2776`). §2.6.1 item 1 adds the `Committed`
latch and T17. **Round 2 wrote the correct sentence — "must not be crossed twice or early" — and
implemented half of it.**

**KILL 20 — "the prefix emits `SalvageReady` the first time it fires."** Dead: a **four-word
livelock.** `AdvanceTo` clears every ready on every acceptance, so a cleared peer re-clicks and
emits nothing. **Route 6 of §2.6.1's own table diagnosed this exact shape against the design it
replaced** — and then the replacement reproduced it. Ready is now a latched intent that re-arms
itself (T15).

**KILL 21 — "`SalvageUnready` is needed."** Dead: **a message with no sender.** Under the new gate
the ready is emitted from `SalvageFinish`, after `OnConfirm` has already exited the dialog
(`:126-131`), so cancel always precedes ready; its cited justification described the gate shape the
same round replaced — **stale inside its own revision**. Deleted. ⚠️ And the corresponding
`SalvageBarrier.Unready` member would have been **dead Core code**, i.e. a build failure
(`Makefile:200`) rather than the silent no-op it would be in Mod (`Makefile:220`).

**KILL 22 — "capture and apply are the only lifecycle."** Dead three ways, all one mechanism short:
`SalvageRedraw` **re-fires** on stage re-entry (`:838-843`, `:752-757`) and re-mints a *different*
budget because the first run consumed the consumables (`:2168`, `:2183`) — stalling the **host**
through `:2757`; the budget had **no apply** on a client (`salvageBudgetLast` is private, `:431`);
and a `SalvageState` arriving before the client's screen was dropped at `:2043` **while the client
opens late by construction**. §2.5.2's postfix closes all three.

**KILL 23 — "prices must ride a message."** Dead on the consumer side, one round after the producer
side. Only the host prices claims. Cut → `SalvageScreen` becomes `SalvageBudget` again.

**KILL 24 — "the reentrancy scope is needed because the gestures nest."** Dead as *reasoning*, alive
as *mechanism*: under suppression the outermost body never runs, so it never reaches `:1967`. The
scope survives as the apply-mode flag. **The real cost round 2 never stated** is that a suppressing
prefix must reimplement the item cycle (`:1985-2011`) and the unit tally + `OffsetAndWrap`
(`:1962-1963`) — now `SalvageCycle` in **Core**, so the reimplementation is checked rather than
silent (T20).

**SURVIVED ROUND 3, verified by the review rather than by me:** the `SalvageFinish` single-gate
premise (two references, and the `:2763` delegate is a bare method group created fresh at every
`Open`, long after `PatchAll`, so a prefix intercepts both); `ProcessSalvageSelections` has exactly
one in-game caller; the finish-funnel list; the `salvageCostValid` counts (14 in file, 0 inside
`:2772-2900`); the 0.35 mint; the canary's screen-open sample; and **NW-f's rounding measurement,
judged honest** — Core is written to match a captured table, so disagreement is impossible by
construction rather than deferred. *(Scheduling note from round 3: that measurement needs any Unity
runtime, not specifically the rig.)*

**SURVIVED ROUND 2, verified by the review rather than by me:** the check **order** (every failure
outcome is a non-mutating rejection, so a swap changes only diagnosis — which is exactly what the
"why" column claims); the **check-5 asymmetry** (deleting check 5 turns T4 and T10 alone red); the
**cost-model choice** (mirror the screen); the **capture/reimplement split**; the **enum density**
`1..32`.

⚠️ **Two round-2 findings I did NOT absorb, because the code says otherwise:**

1. The review filed the O(N²) amplifier against **`OnSalvageDecisionUnit`**, saying it "loops
   `OnSalvageDecision` over every entity in a group". **It does not** — it writes inline (`:1900`,
   `:1904`) and refreshes **once** (`:1909`). The member that really does it is
   **`OnSalvageDecisionShift`'s unit branch**, looping `OnSalvageDecision` at `:1967` (and therefore
   redrawing N times). The hazard is real; the mechanism is a different member, and §2.5.4 names the
   right one.
2. The review's remedy for the capture gap — "intercept `OnSalvageDecision` and
   `OnSalvageDecisionShift`'s item branch" — **is itself incomplete**: it misses
   `OnSalvageDecisionUnit` (`:1883`), which is reached from the very public statics §2.6 lists as
   drive-path entry points. §2.5.5 enumerates **all ten write sites in three members** and patches
   all three.

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
| 1 | Equal pools, or one shared budget with reservations? | ✅ **ANSWERED 2026-08-22 by the user: ONE SHARED BUDGET, barrier-gated.** ~~"🧑 One sentence to the user."~~ Asked and answered. Equal pools died to **this plan's own KILL 7** — an item unclaimable by anyone that a solo player could afford. And "with reservations" did not survive either: there is no reservation arithmetic, just one visible integer plus the barrier. §2.5 is rewritten to the decision, **including the finding the decision as relayed did not carry** — vanilla does **not** enforce the budget at the moment that matters (`SalvageRefreshBudget` only sets UI availability; `ProcessSalvageSelections` logs over-budget and continues), so enforcement is host-side claim admission **plus** merged-total as a precondition of barrier release. §2.5 is now buildable, and **stage D3 is too as of 2026-08-22**: B4 re-specified it as `SalvagePrice` + `SalvageLedger` + `SalvageBarrier` (§2.5.2–§2.5.7, §2.6), with the seven-check admission order, tests T1–T22 and eighteen mutations including the temporal one. `SalvagePool` is deleted from the plan. **Two refutation passes have run and are folded in** (§7 KILL 12–17 and KILL 18–24, twelve claims killed across them); round 3 found **no architectural defect** and the plan is closed to further passes. ⏭️ What still gates D4 is a **reading**, not a review: NW-k. |
| 2 | Is the `combat_salvage_drop_chance` roll reached in a real co-op campaign fight? | Rig M3. If `featuresChecked=True`, D3's manifest shrinks to roster reconciliation. |
| 3 | Can an M12c combat checkpoint be loaded from the **overworld** state a stalled debriefing leaves a machine in? | One instance, one fight, checkpoint, finish the fight, then `pbj.combat-load` the checkpoint slot from the overworld. Read the action diff. Rides R0's tail at near-zero cost. |
| 4 | Is `SalvageFinish`'s post-commit work deterministic given synced inputs? (design q7, `CIViewOverworldDebriefing.cs:2780+`) | Rig M6 — **and it is not closable before D5**, because it needs both machines to commit. Recorded as blocked rather than guessed. |
| 5 | Does a client today actually open a debriefing after a shipped fight? | Rig M1. This is the single reading that most changes M12d's size, and it costs one console call on the client at R1 reading 9. |
| 6 | Claim granularity for the general inventory outside salvage (design q4) | Deferred to D6; needs no answer to ship the screen. |

---

## 9. New work discovered by the B4 re-specification (2026-08-22, rounds 1–3)

Each row is something no stage covered as written. None is a blocker for the refutation pass; all
of them are blockers for calling D5 done.

| # | what | who it lands on |
|---|---|---|
| **NW-a** | ✅ **RESOLVED in round 2 — it was half a fix.** The reading stands: `OnSalvageFinish:2755` gates the modal path and the `else` (`:2766-2768`) calls `SalvageFinish()` on the click with no modal and no budget check, on **every defeat** and every empty-list victory. Round 1 specified suppress-and-`SalvageReady` but **never specified the release ACTION on that arm** — §2.6's release was modal-shaped, and no modal exists there. §2.6.1's single `SalvageFinish` prefix covers both arms and releases by calling the method directly | stage D5 (in its row) |
| **NW-b** | **The canary needs private members.** `salvageCostTotal` is `private` (`:468`), `SalvageRefreshBudget` is private (`:2351`), `salvageGroups` is private. Three Harmony patches on private members, none previously listed | stage D5 |
| **NW-c** | **A defeat may have a budget of zero and still reach the screen.** Derived answer: check 5 refuses every non-`Skip` claim. Now a **boundary case in T4**, not an inference | ✅ folded into §2.5.7 T4 |
| **NW-d** | **The two cost models diverge and only one is enforced.** The screen prices with the group multiplier (`ProcessSalvageGroupChoices:2547`), the funnel with a hard-coded `1f` (`:1889`, `:1897`). ⚠️ **Precision corrected in round 2:** the warning at `:1903` fires **iff the 1f-priced sum exceeds `budget`** — not "whenever a 0.35 group is involved". By the same arithmetic **it can fire in vanilla single-player**, which is stronger evidence of benignity than round 1's framing. Enforcing the funnel's model instead would refuse purchases the player was shown as affordable | rig runbook / R-M6 |
| **NW-e** | **The message list is seven types, `33`–`39`** (§4). It moved 6 → 7 → 8 → **7** across three review rounds, every move forced by a producer-moment or consumer question — which is why §4 now states that rule in the section and applies it as a column | stage D4 |
| **NW-f** | 🔴 **The rounding rule is unverifiable in this tree.** `Mathf` is absent from `decompiled/` (control: `Vector3.cs` absent, `CIButton.cs` present). Core must reproduce a **float** product and an **observed** rounding mode. ⇒ D2's probe owes a rounding table over `.5` boundaries, and T2 is populated from it. A hand-derived T2 would agree with our arithmetic and disagree with the game — and the canary would then abort every session, blaming the world | stage D2, then D3 |
| **NW-g** | 🔴 **The claim SOURCE was the largest unwritten piece and is now §2.5.5.** Ten `salvageSelection` write sites in **three** members; two of the three bypass `OnSalvageDecision` entirely. Needs three capture prefixes, an outermost-wins reentrancy scope (the gestures **nest** — `:1967`), and the decision that local writes are **suppressed** until admission, without which §2.5.2's canary false-aborts on the host's own first click | stage D5 |
| **NW-h** | **The `pending` render must not touch `salvageSelection`.** It is a grid-element decoration; writing the component re-creates the divergence it was added to hide. Stated because the natural implementation is the wrong one | stage D5 |
| **NW-i** | ⚠️ **CORRECTED in round 3 — it is worse than "no visible effect".** The cycle members read *admitted* state, so a second click inside the RTT window **re-emits the same choice instead of advancing**: the gesture does not progress, it repeats. Sub-frame on a loopback host; one RTT on a client. If the rig finds it perceptible the fallback is optimistic local writes plus a canary scoped to the admitted set — **strictly more machinery, and now a closer call than round 2 thought**, since suppression's own cost rose by two reimplementations (§2.5.5). Fork recorded, cheap branch still chosen first, **tripwire named: D5 settles this before the capture prefixes are written** | rig acceptance, then D5 |
| **NW-j** | 🆕 **A client's `SalvageRedraw` consumes ITS OWN consumable world memories.** §2.5.2's postfix corrects the *number*, but the vanilla body has already run `RemoveMemoryFloat` on `world_auto_salvage_multiplier_consumable` and `..._offset_consumable` (`:2168`, `:2183`) on that machine. The host is authoritative for campaign state, so this is probably benign — but it is **a real divergence introduced by opening the screen**, and "probably benign" is not a derivation | M12b/M12c question; flagged at D5, measured at R-M6 |
| **NW-k** | 🆕 **Do free drops surface on the salvage grid?** KILL 18 cuts them from v11 on the derivation that they are attached to the base inventory (`:1218`), not to a participant unit, while groups are built from participant units (`:2552`). **If M2's per-unit fingerprint shows entries belonging to no unit group, D6 becomes a blocker for D5** instead of an independent half | rig M2, before D4 opens |
| **NW-l** | 🆕 **`SalvageCycle` is a SECOND reimplementation of vanilla logic in Core**, joining `SalvagePrice`. Both are there because the coverage gate cannot see `src/PBAndJ.Mod`. That is now a **pattern** worth naming: *when a suppressing patch must reproduce what the suppressed body would have computed, the reproduction belongs in Core.* Cheap here; it will not always be | noted for D3, and for the next suppressing patch anyone writes |

---

## 10. Corrections owed to the backlog (`docs/design/backlog-2026-08-22.md`)

**The scaffolding this item was briefed with was wrong in two places, and the next reader of §B4
must not follow it.** Recorded here because this document cannot edit the backlog (the planner owns
it), and a correction nobody can find is not a correction.

| # | what the backlog says | what the code says |
|---|---|---|
| 1 | §B4: *"The barrier (`SalvageBarrier`) is **unchanged**: edge-triggered, departing peer must not satisfy it, version counter (copy `LobbyBarrier.cs`, 191 lines)."* | **Copying `LobbyBarrier` would have shipped the departing-peer hole the same sentence forbids.** `LobbyBarrier.RemoveParticipant` (`:142-146`) *can* satisfy the barrier — its own remark says so — and `LoadBarrier.Drop` (`:111-120`) shares the shape. The instruction's two halves contradict each other; §2.6 divergence 2 resolves it with a latched `Aborted` and T8. **`LobbyBarrier` also has an `Unready` member (`:182`) the instruction did not mention, and §2.6.1 shows it is required.** |
| 2 | §0.3: *"vanilla's own `salvageCostValid …` gate does the enforcing"* | Already flagged as 🛠F1 in the backlog itself, and §2.5.1 now carries the full three-break derivation — **plus a correction to F1's own wording**: vanilla *does* have one genuine refusal (`OnSalvageFinish:2757`), it is simply at the wrong moment, conditional on `:2755`, and silent. |

⚠️ **The general shape, since it has now happened twice on this item:** a briefing sentence that
names a mechanism ("copy this file", "this gate enforces it") reads as settled and is the least
likely thing to be re-derived. **Both of this item's briefing errors were mechanism claims.**

---

*Written by lane L2, 2026-08-21. §2.4–§2.6, §4, §6 D1–D5, §7 KILL 8–17, §8 Q1, §9 and §10
re-specified 2026-08-22 by lane B4 under the shared-budget decision
(`docs/design/backlog-2026-08-22.md` §0.3) and the F1 refutation, then **revised the same day after
an adversarial review that refuted five round-1 claims**. Every `file:line` in the re-specified
sections was opened in this working tree on 2026-08-22; two inherited numbers were found rotted by
one line and corrected in place, one inherited figure (the `0.4` group multiplier) was found
**wrong** and is now `0.35`, and two round-2 findings were themselves refuted with code and are
recorded at §7 rather than absorbed. **Round 3 (same day) found no architectural defect, killed
seven downstream claims (KILL 18–24), and forced the §4 re-derivation; the plan is now closed to
further review passes and open to the rig.**
It supersedes no section of
`docs/design/m12-concurrent-management.md`; it is conditioned on that file's dated verdict and
corrects §M12d only where the corrections are recorded there, in place, with dates.*
