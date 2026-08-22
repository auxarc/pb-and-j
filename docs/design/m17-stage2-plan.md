# M17 stage 2 — the wrecked-unit cluster: the reconstructed plan, and its third refutation

Status: **written 2026-08-21 at `main` = `1708a0b`, tree clean. NOT BUILT. No build authorization.**
Mod 0.22.0, wire v9 (`src/PBAndJ.Core/Net/PbjProtocol.cs:202`, `:226`; `mod/metadata.yaml`;
`wire-surface.lock:3`).

This file is the **reconstruction** of a plan that was written, reviewed twice, rewritten, and then
lost with the session scratchpad it lived in. That is the third plan this project has lost to a
scratchpad, which is why this one is in the tree. Its inputs are the surviving memory
`m17-review-findings.md` (rounds 1 and 2), `client-wreck-pose-lost.md` (stage 1, **built and
merged**, PR #22), `m16-part-integrity-built.md`, and `docs/design/road-to-1-0.md` §4 L3 / §5 / §6.

**Every `file:line` below was opened by hand on 2026-08-21 unless the line is marked
`⚠️ UNVERIFIED`.** Decompiled paths are namespace-nested — the memories cite basenames, and
`CombatScenarioStateSystem.cs:365` is really
`decompiled/PhantomBrigade.Combat.Systems/CombatScenarioStateSystem.cs:365`. Citations rot; every
one below also names the **member**, so navigate by member and re-derive the number before quoting
it onward.

---

## 0. What is verified, and by what — the standing ground

### 0.1 The mechanism holds, and it is now checked from the DLL a third time

`vendor/Managed/Entitas.dll`, decompiled **in place** (`ilspycmd -t Entitas.ReactiveSystem\`1` and
`-t Entitas.Collector\`1`; sighting 23 — take both readings from the dll's own directory):

```
public void Execute() {
    if (_collector.count == 0) return;
    foreach (TEntity e in _collector.collectedEntities)
        if (Filter(e)) { e.Retain(this); _buffer.Add(e); }
    _collector.ClearCollectedEntities();
    if (_buffer.Count == 0) return;
    try { Execute(_buffer); } finally { ...Release(this); _buffer.Clear(); }
}
```

and `Collector<TEntity>.ClearCollectedEntities()` releases **every** collector retain before
clearing. So an all-false `Filter`:

- never invokes the abstract `Execute(List)`,
- clears the collector regardless,
- leaks nothing and leaves no partial list.

A Harmony **postfix forcing `__result = false`** is a real, narrow off-switch. Precedent already
shipping and playtest-proven: `src/PBAndJ.Mod/Net/ExecutionPatches.cs:111` patches the
`protected override Execute` of a `ReactiveSystem<CombatEntity>`.

⚠️ **`Filter` is `protected`**, so the attribute needs the **string-name** form
`[HarmonyPatch(typeof(T), "Filter")]`, never `nameof`. Each of the two target types declares exactly
one `Filter`, so there is no overload ambiguity to resolve
(`CombatUnitWreckingSystem.cs:29-36`, `CombatUnitDestructionEffectSystem.cs:30-37`).

### 0.2 The systems DO run on a client — verified end to end, not assumed

This was the load-bearing assumption nobody had traced. It is now traced:

| step | citation |
|---|---|
| `Heartbeat.Update` ticks the controller | `decompiled/PhantomBrigade/Heartbeat.cs:76` → `_gameController.OnUpdate()` |
| the top-of-stack state is updated | `decompiled/PhantomBrigade/GameController.cs:391` (`m_stateStack[last].OnUpdate()`) |
| a state's `OnUpdate` executes every `Systems` it holds | `GameController.cs:95-100` — `foreach (Systems s in m_systems) s.Execute();`, **with no `Simulating` condition anywhere** |
| `"combat"` holds `CombatSystems` | `GameController.cs:183` |
| the three wrecking systems are in it | `CombatSystems.cs:36`, `:38`, `:109` |

⇒ On **any** machine in the `"combat"` game state — host or client — `CombatUnitWreckingSystem`,
`CombatUnitDestructionEffectSystem` and `CombatUnitWreckingSyncSystem` have their `Execute()` called
every frame. Setting `isWrecked` on a client fires the whole cascade. The `Simulating` gates that
save a client elsewhere are **inside** particular systems, not at the Feature level.

Same trace also proves `OverworldSystemsPermanent` runs *during combat*: `GameController.cs:179`
registers it under `"gameLate"` with `SetAlwaysActiveAfterStack(true)`, and `GameController.cs:395-399`
updates every such state after the stack top. That matters in §3.3.

### 0.3 The component sweep — grep the COMPONENT, not one accessor spelling

Round 1 grepped `.Added()` and missed `CombatUnitWreckingSyncSystem`'s `.AddedOrRemoved()`. This
sweep goes at the component, three ways, with the pattern shown to bite:

- `PersistentMatcher.Wrecked` (any accessor): **exactly 3 hits**, all `CreateCollector` —
  `CombatUnitDestructionEffectSystem.cs:27`, `CombatUnitWreckingSystem.cs:26`,
  `CombatUnitWreckingSyncSystem.cs:15`.
- The matcher is `Matcher<PersistentEntity>.AllOf(107)` (`PersistentMatcher.cs:1751-1762`) and
  `Wrecked = 107` (`PersistentComponentsLookup.cs:222`). **Zero** uses of
  `PersistentComponentsLookup.Wrecked` anywhere — and that zero is not vacuous: the bare
  `PersistentComponentsLookup.` prefix has **111** hits, so the pattern can match.
- Every composite matcher on the persistent context (`PersistentMatcher.AllOf/AnyOf`, 16 sites;
  `Matcher<PersistentEntity>.AllOf` outside `PersistentMatcher.cs`, 0 sites) was listed and read.
  **None mentions `Wrecked`.**
- Control that the *shape* of the grep bites at all: `Matcher\.Wrecked` across the whole decompile
  returns **4** — the three above plus `CombatPartWreckingSystem.cs:26`'s `EquipmentMatcher.Wrecked`,
  a different context.

⇒ Three reactive consumers of the unit-level flag, and no fourth. `CombatUnitWreckingSyncSystem`'s
whole `Execute` is an `IsGameState("combat")` guard plus `CIHelperOverlays.OnUnitWrecked`
(`:25`, `:34`) — an overlay refresh a client **wants**. **Leave it unpatched.**

### 0.4 What is in the tree today

- The wire already carries `IsWrecked` and `WreckedAt`: captured at
  `src/PBAndJ.Mod/Net/CombatGameBridge.Snapshot.cs:103`, carried at
  `src/PBAndJ.Core/Net/UnitSnapshot.cs:335`/`:358`, coded at
  `src/PBAndJ.Core/Net/PbjMessageCodec.cs:809`/`:863`/`:888`, and it drives `DestructionPlayback`.
- **Nothing in `src/PBAndJ.Mod` writes the ECS flag.** Every `isWrecked` hit in `src/` is a read, a
  doc comment, or a probe (`DestructProbeGlue.cs:128`, `:344`; `ActuatorGlue.cs:186`).
- **Zero `"Filter"` Harmony patches in `src/`** (`grep -rn '"Filter"' src/` → nothing; the same
  grep shape finds 11 other string-name patch attributes, so it bites).

---

## 1. The patch set

Four Harmony members, all in one new `src/PBAndJ.Mod/Net/WreckingPatches.cs`
(`[ExcludeFromCodeCoverage]`, house style as `WeaponLightPatches.cs` / `ExecutionPatches.cs`).

### 1.1 Suppress the two damaging cascades — POSTFIX on `Filter`, string-name form

```csharp
[HarmonyPatch(typeof(CombatUnitWreckingSystem), "Filter")]
internal static class Patch_CombatUnitWreckingSystem_Filter
{
    private static void Postfix(ref bool __result)
    {
        if (WreckingPatches.SuppressCascade) { __result = false; }
    }
}
// identical shape for CombatUnitDestructionEffectSystem
```

- **Postfix, not prefix.** A prefix that skips the original would have to invent the host's answer;
  a postfix only ever *narrows* a true to a false, which is the whole semantic we want.
- **`ref bool __result`**, not a return value — the original is `protected override bool Filter`.
- **`SuppressCascade` is a live-client predicate, evaluated per call**, never a patch-time decision:
  the patch is static and applies to the host's instance too. See §1.4.

What each suppression buys, read line by line out of `CombatUnitWreckingSystem.Execute`
(`:38-124`):

| line | what it would do on a client | why we do not want it |
|---|---|---|
| `:70` | `CombatActionEvent.DestroyAllActions` | the corpse's local action set is the host's to decide |
| `:74` | `ReplaceUnitFrameDefects(n+1)` | a **serialized** field, locally invented, that vanilla uses at `ScenarioUtility.cs:3965-3973` to decide whether a unit is destroyed for good |
| `:84`, `:92` | `CIViewDialogConfirmation.ins.Open(...)` | a **modal pause dialog** mid-playback, on a UI with no view stack |
| `:103` | `ReplaceCrumpleTime(f)` | the only remover is `CombatExecutionEndLateSystem.cs:66-69`, whose trigger is `CombatMatcher.Simulating.Removed()` (`:28`) — **a client never removes it**, so the component would be permanent litter and `hasFrozenPuppet` would never be set |
| `:116-123` | `effectsProc.effectsOnDestruction` | arbitrary content functions on a machine that is replaying, not simulating |

and out of `CombatUnitDestructionEffectSystem.Execute` (`:39-173`): `:102-122` creates up to 30
real `CombatEntity` projectiles per wreck with `TimeToLive`, `SimpleMovement` and `SimpleForce` — and
the systems that move and expire them are simulation-gated, so on a client that is **thirty frozen
fragments at the unit's core, per wreck, for the rest of the fight**.

Two lines of that cascade we *do* want, and they are repaid by hand in §2:
`visualManager.OnUnitDestruction()` (`:64` — already ours since M15) and
`AddScenarioStateRefreshContext(OnUnitDisabled)` (`:108`).

### 1.2 `CombatUnitWreckingSyncSystem` — NOT patched

Its `Execute` (`:23-37`) is `IsGameState("combat")` + `CIHelperOverlays.OnUnitWrecked` per unit.
That is the crash-overlay refresh, and a client wants it. Leaving it unpatched is also what makes
un-wrecking work: `Wrecked.AddedOrRemoved()` (`:15`) fires on the **clear** too, and the two
`.Added()` systems do not.

### 1.3 Do **NOT** patch `CombatPartWreckingSystem`

Its trigger is `EquipmentMatcher.Wrecked.Added()` (`:26`) — the *equipment* flag. No client path
sets it: M15/M16 drive part visuals and per-part integrity without ever adding the component
(`KeyframePlayer.Destruction.Apply.cs` header). A patch there would never run.

⚠️ **The roadmap's stated reason for this is wrong and §7 kills it.** See KILL 3.

### 1.4 `ScenarioUtility.EndCombatWithOutcome` — PREFIX, and it is **required**, not defence in depth

```csharp
[HarmonyPatch(typeof(ScenarioUtility), nameof(ScenarioUtility.EndCombatWithOutcome))]
internal static class Patch_ScenarioUtility_EndCombatWithOutcome
{
    private static bool Prefix() => !WreckingPatches.SuppressCombatEnd;
}
```

`nameof` is correct here — the method is `public static`
(`decompiled/PhantomBrigade/ScenarioUtility.cs:3574`).

It **is** the sole chokepoint: `ReplaceCombatResolved` has exactly one caller in the whole
decompile, `ScenarioUtility.cs:3586`, inside this method. The mod never calls it (zero hits in
`src/`), and suppressing it strands nothing — a client's exit is `CombatEndMessage` →
`StopKeyframesEffect` (`src/PBAndJ.Core/Net/ClientSession.Dispatch.cs:234-241`).

Round 2 demoted this to defence-in-depth on the strength of "a client cannot self-victory".
**§7 KILL 1 and §7A both show that is contingent, not structural** — KILL 1 by finding a second
producer of the gating bit, §7A by finding two content/console routes into the producer round 2
itself named — **and stage 2's own repayment (§2.5) is one of the things that makes it
contingent.** The prefix ships.

### 1.5 The two predicates — the one place a wrong `if` costs the whole feature

```csharp
// true only while a live client session owns combat outcomes
internal static bool SuppressCombatEnd =>
    NetGlue.HasSession
    && NetGlue.Runtime?.Session is ClientSession c
    && c.State is not (ClientSessionState.Closed or ClientSessionState.Faulted);

// same predicate today; kept separate because §7 KILL 5 may move one of them
internal static bool SuppressCascade => SuppressCombatEnd;
```

`NetGlue.HasSession` is `src/PBAndJ.Mod/Net/NetGlue.cs:68`; `IsHost` at `:81` reads the session's
type rather than a remembered flag, and this follows it. `ClientSessionState` is
`src/PBAndJ.Core/Net/ClientSession.cs:18-37`; `State` at `:132`.

🔴 **The `Closed`/`Faulted` exclusion is not tidiness, it is a correctness requirement.**
`ClientSession.Fault` (`ClientSession.cs:278-292`) says in its own comment: *"A lost host must never
leave the local execute button disabled — the player continues single-player from here."* After a
fault or a `Bye` (`Dispatch.cs:320-322`) the human keeps playing that fight **alone**, sets
`Simulating` themselves, and reaches `CombatExecutionEndSystem` normally. A prefix that stayed armed
would make that fight **unwinnable and unlosable for ever**. See §7 KILL 2 — this is a defect the
round-1/round-2 text would have shipped.

`NetGlue.Runtime` does not exist as an internal accessor today; adding one is a one-line change to
`NetGlue.cs` beside `HasSession`/`IsHost`. ⚠️ `NetGlue.cs` is a **split-family** file — see §6.

---

## 2. The apply path — where the flag is actually set

### 2.1 Where it goes

Beside `DriveWreck`, in `src/PBAndJ.Mod/Net/KeyframePlayer.Destruction.Apply.cs`, which already has
both callers and is already the file that owns "the host's wreck, drawn here":

- `ApplyDestruction(Target)` `:271` → `DriveWreck(target.Visuals, hidden, true)` `:286` — the
  in-window wreck moment.
- `ApplySettled(DestructionUpdate)` `:122` → `DriveWreck(unitVisuals, hidden, wreck.Wrecked)` `:165`
  — the no-ramp settle, and **the only path that can carry `false`**, i.e. revival.

New member, called immediately after each `DriveWreck`:

```csharp
private static void DriveWreckFlag(PersistentEntity persistent, CombatEntity? unit, bool wrecked)
```

### 2.2 What it does, in order, and why each line is there

1. **`persistent.isWrecked = wrecked;`** — the point of the milestone. Idempotent: Entitas's flag
   setter early-returns when the value is unchanged (`PersistentEntity.cs`, the `isWrecked` property
   pair), so a repeat costs nothing and fires no collector.
2. **`persistent.isFunctional = !wrecked;`** — vanilla's own wreck path writes both, one line apart:
   `CombatActionEvent.cs:31` `isFunctional = false;` then `:32` `isWrecked = true;`, and the un-wreck
   at `ScenarioUtility.cs:3992-3993` writes both back. `Functional` has **no collector anywhere**:
   `PersistentMatcher.Functional` exists as a property (`PersistentMatcher.cs:813`) and has **zero**
   references, while the same grep shape returns 16 hits for other persistent matchers. It is read
   at `ActionUtility.cs:678`/`:783` and 90-odd other sites. One free bit of correctness.
3. **`if (wrecked) UnitUtilities.OnHandleInactiveUnitCollision(unit);`** — **best-effort, explicitly
   NOT an invariant.** See §2.3.
4. **`CIViewCombatMode.ins.RedrawUnitTabs();`** — once per batch, not per unit. See §2.4.
5. **`CombatUtilities.AddScenarioStateRefreshContext(ScenarioStateRefreshContext.OnUnitDisabled);`**
   — once per batch. See §2.5. 🔴 **This line and §1.4's prefix ship together or not at all**
   (§7 KILL 1).
6. Counters: `WreckFlagsSet`, `WreckFlagsCleared`, `WreckFlagsRefused` — the whole body inside
   `try/catch`, exactly as `ApplyDestruction`'s wreck arm already is (`:279-294`).

### 2.3 `OnHandleInactiveUnitCollision` — best-effort, and here is precisely why

`decompiled/PhantomBrigade.Data/UnitUtilities.cs:2692-2752`. Read in full:

- `:2700` — it **self-guards on `isWrecked`** and returns if the flag is not set. So it is inert
  before stage 2, and callable only *after* line 1 above. Order is load-bearing.
- `:2707` — `combatView.OnHitCollidersEnabled(false)`, which touches only colliders on
  `LayerMasks.puppetRagdollLayerID` (`CombatView.cs:94-107`).
- `:2739-2740` — disables the trigger collider off the animation view's `impactor`.
- `:2743-2751` — **permanently removes that collider from `combatView.colliders`.** Nothing restores
  it; `CombatUnitRevive` does not, and revival is a real wire path here.

**And our own machinery undoes half of it.** `CombatView.OnVisibility(true)` sets
`collider.enabled = visible` on **every** collider still in the list (`CombatView.cs:79-89`), and
`KeyframePlayer.Visibility` calls exactly that at `:146` and `:192`. So after the next visibility
restore, the ragdoll hit colliders are live again while the trigger collider — removed from the list
— stays dead for good.

⇒ **Call it, expect nothing from it, and never write a test that asserts a collider state.** Its
honest value is one frame of parity with vanilla, plus the permanent trigger removal. Round 1
described it as a clean call; it is not.

⚠️ It also does **not** stop click-selection: that raycast uses `LayerMasks.unitSelectionMask`
(`InputCombatUnitSelectionUtility.cs:41`), a different layer.

### 2.4 The redraw — the headline does not arrive without it

`CIViewCombatMode.RedrawUnitTabs` (`:271-...`) is what draws the friendly/allied/enemy unit tab
lists, and its **very first per-unit filter** is:

```
if (item.isHidden || !item.isUnitDeployed || item.isDestroyed || item.isWrecked) continue;   // :295
```

— a direct `isWrecked` test, not routed through `IsUnitActive`. **This is the enemy tracker M16
photographed showing six dead enemies alive.**

But nothing in the reactive cascade redraws it. Vanilla pairs the flag write with an explicit call,
one line later: `CombatActionEvent.cs:32` then `:33 CIViewCombatMode.ins.RedrawUnitTabs();`. There
is a self-healing path — `CombatUILinkTimeline.Execute` calls it whenever an action's
owner/start/duration changes and `!combat.Simulating` (`:43-46`), which on a client is always — but
that fires on the *next action change*, not now. Vanilla's shape is one call; take it.

The view defers safely when it is not entered (`:273-276` sets `unitRedrawScheduled`), so calling it
outside unit-selection mode is free.

### 2.5 The dropped `OnUnitDisabled` poke — one line, and it is not free

Suppressing `CombatUnitWreckingSystem` drops its `AddScenarioStateRefreshContext(OnUnitDisabled)`
(`:108`), so kill-target scenario objectives would still never refresh on a client. The repayment is
literally that one call.

`AddScenarioStateRefreshContext` **bitwise-ORs into an accumulator**
(`decompiled/PhantomBrigade/CombatUtilities.cs:798-802`) which `CombatScenarioStateSystem` reads and
clears on the next refresh (`:207-208`). `OnUnitDisabled` is `0x20`
(`ScenarioStateRefreshContext.cs:14`). Harmless on its own.

🔴 **Not harmless in combination.** §7 KILL 1.

### 2.6 What stage 2 buys **today** — the honest list

**Headline: the enemy tracker count.** `CIViewCombatMode.cs:295`, above. This is the exact artefact
M16 photographed (host VICTORY at `wrecked=6`, client still counting six alive).

Also real:

- **In-world markers.** `CombatUILinkInWorldMarkers.cs:42` tests `!linkedPersistentEntity.isWrecked`
  directly.
- **The crash-overlay widget.** `CIHelperOverlays.cs:286`, `:1241`, `:1341` all go through
  `IsUnitActive`, and `CombatUnitWreckingSyncSystem` (left unpatched) drives the refresh.
- **The execute-readiness warning.** `CIViewCombatExecution.cs:224` skips units failing
  `IsUnitActive`, so a client's own wrecked mech stops counting as "has no orders yet".
- **Targeting lists.** `CIViewCombatMode.cs:300`'s `IsUnitActive` arm.
- **`IsUnitActive` itself:** `ScenarioUtility.cs:6397` signature, `:6415`
  `if (inactiveIfUnitWrecked && unitPersistent.isWrecked) return false;`, that parameter
  **defaulting to true**. ~20 UI sites consult it.
- **Groundwork for M12c**: a client's mid-combat checkpoint save now records `wrecked: true`, which
  `DataManagerSave.cs:2254` reads straight back on reload.

**Overstated and struck:**

- 🔴 **"Selectable" is wrong.** Click-selection is a physics raycast
  (`InputCombatUnitSelectionUtility.AttemptSelectionByScreenPoint:40-43`) filtered by
  `IsSelectable` (`:15-38`), which tests `isHidden`, `isDestroyed`, `hasPosition` and
  `flag_untargetable` — **and consults neither `IsUnitActive` nor `isWrecked`.** A client's corpse
  stays clickable. Say so before a playtest "finds" it.
- 🔴 **Roster divergence is not closed.** `FreeOrDestroyCombatParticipants` (`ScenarioUtility.cs:3948`)
  destroys **enemy** participants unconditionally at `:4003-4010`, never consulting `isWrecked`; the
  defect-limit branch (`:3965-3973`, destroy at `:4000-4002`) is player-faction, non-disposable only.
  M16 saw six wrecked **enemies** diverge. Closing that needs outcome processing mirrored.
- 🔴 **Frame integrity is not fixed by the flag.** `EquipmentUtility.cs:620-623` short-circuits a
  wrecked or salvageable unit to `0f`, but its two callers
  (`OverworldCombatOutcomeProcessingSystem.cs:200` ⚠️ UNVERIFIED line, `CIViewOverworldDebriefing`)
  are outcome-processing paths a client never runs. **And it does not matter** — M16 already ships
  the host's value every snapshot (§3.2).

**And what stage 2 explicitly does NOT fix** (stage 1's list, still true): a unit already wrecked at
combat start, a boneless unit, an aborted window, and non-mech views. The crumple→freeze path lives
in `CombatExecutionEndLateSystem`, which only a simulating host runs (`:28`).

---

## 3. Wire v10 — the field list

**One break, not two.** Stage 2 and the **re-scoped** stage 3 ship together; this project
deliberately pairs unrelated features into a single break (M15 paired the unit wreck with the
per-part dissolve; M14 stage B paired trails with weapon lights).

### 3.1 Struck, with reasons

| field | struck because |
|---|---|
| ~~`HasFrameIntegrityValue` / `FrameIntegrity`~~ | **M16 already ships it, value and presence, every snapshot.** `CombatGameBridge.Snapshot.cs:103-113` captures → `UnitSnapshot.cs:430` `HasFrameIntegrity` carries → codec `PbjMessageCodec.cs:888` → apply writes `ReplaceUnitFrameIntegrity`. Round 1 would have duplicated it. |
| ~~`isWrecked` / `wreckedAt`~~ | already on the wire since M15 (`UnitSnapshot.cs:335`, `:358`). Stage 2 needs **no new bit for the flag itself** — only an apply path. |
| ~~pilot `statValues`~~ | the cascade. `CombatPilotStatReactionSystem` triggers on `PersistentMatcher.PilotStatValues` (`:26`) and on `hp <= 0` locally computes `isFunctional = false` / `isKnockedOut = true` (`:67-68`), invents a death cause via `PilotUtility.DeclareDeath(entity, "trauma")` (`:89`), runs `ActionUtility.ConcussEntity` (`:103`) and `CombatUtilities.ForceEjection` (`:120`), and raises **the same modal dialog** §1.1 spends a patch suppressing. A second, permanent-Feature system also collects it: `OverworldPilotStatReactionSystem.cs:18`. |
| ~~subsystem `destroyed`~~ | **stage 4, cut.** Write-only in vanilla — even the game's own loader discards it. ⚠️ UNVERIFIED today; round 2's reading, not re-taken. |
| ~~`unitStatusStates` / `unitStatusBuildups`~~ | **stage 4, cut.** `UnitStatusUpdateSystem` triggers on `CombatMatcher.SimulationTime`, so a client locally ticks and decays every synced status, fighting each next snapshot. ⚠️ UNVERIFIED today. |
| ~~`unitFrameDefects`~~ | see §3.4 — deliberately NOT synced, and that is a decision, not an omission. |

### 3.2 What v10 actually adds

Three pilot bits and one string, on `UnitSnapshot` (one pilot per unit via `entityLinkPilot`, so the
unit snapshot is the right home and no new message type is needed):

| new field | ECS target | why it is safe |
|---|---|---|
| `PilotDead` (bool) + `PilotDeathCause` (string, ≤32) | `AddDeathStatus(time, cause)` — `PilotUtility.cs:2728` | consumed by `IsUnitActive:6427` (`persistentEntity.hasDeathStatus`) |
| `PilotKnockedOut` (bool) | `isKnockedOut` | `IsUnitActive:6431`. **No matcher `PersistentMatcher.KnockedOut` exists at all** |
| `PilotEjected` (bool) | `isEjected` | `IsUnitActive:6427`. `PersistentMatcher.Ejected` has exactly one reference — a non-reactive `IGroup` in `OverworldCombatOutcomeProcessingSystem.cs:32`, a system a client never triggers (`CombatOutcomeProcessing.Added()`, `:36`) |

⚠️ **`DeathStatus` DOES have a reactive system, and the memory said it did not.** §7 KILL 4 —
the conclusion survives, the reason does not.

🔑 **Write the components, never the helpers.** `AddDeathStatus`/`ReplaceDeathStatus` and the two
flags directly. `PilotUtility.DeclareDeath` (`:2715-2760`) additionally fires `OnPilotEvent`, zeroes
two pilot stats and runs combat-state focus events — the same class of cascade stage 2 exists to
avoid.

**Deferred to M12c's own break, not v10:** part `chargeCount` and `isSalvageable`. Both are stored
and loaded and **neither has any collector** — `EquipmentMatcher.ChargeCount` (`:153`) and
`EquipmentMatcher.Salvageable` (`:587`) exist as properties with zero references, against 16
reference sites for other matchers of the same shape. They are cheap and they are *M12c's* need, not
stage 2's; folding them in now would be scope this plan cannot justify.

### 3.3 The version arithmetic

`PbjProtocol.Version` 9 → **10**; `ModVersion` **0.23.0 → 0.24.0** (L1's W1 takes 0.23.0);
`mod/metadata.yaml` `ver` to match — `check-mod-version` (`Makefile:116-124`) fails the build if the
two disagree.

### 3.4 The one field deliberately left divergent, said out loud

`unitFrameDefects` is **serialized** and the host increments it at `CombatUnitWreckingSystem.cs:74`.
Suppressing the cascade means a client's copy never moves. That is **intentional**: it is the host's
number, and a client that computed its own would produce a *different* one (its baseline is whatever
the loaded scenario save held). It is read at `ScenarioUtility.cs:3965-3973` to decide permanent
destruction — a decision only the host makes. If M12c's checkpoint ever needs it, it is one more int
in M12c's own break, and it should be priced there.

---

## 4. The two decisions

### 4.1 The `isWrecked` lifecycle at combat end — **DO NOT CLEAR IT**

Both answers were said to falsify a plan claim. Here is the evidence that breaks the symmetry.

**The horn that is simply FALSE: "the client carries wrecked units onto its own overworld."**

A client cannot carry them, because it cannot reach its own overworld without a **campaign
teardown**:

- `DataHelperLoading.TryLoading:237` sets `game.isTeardownOfCampaignRequested = true` and pops to
  `"mainmenu"` for **any** load started outside the main menu.
- `TeardownCampaignSystem` triggers on `GameMatcher.TeardownOfCampaignRequested.Added()` (`:47`) and
  its `Execute` ends with `DestroyEntitiesInGroup(persistentGroup)` (`:78`) — **every persistent
  entity, including every unit that received the flag.**
- The mod has **no** code that returns a client to its own overworld: `grep -rn
  'LoadOverworld\|ReturnToBase\|LeaveCombat' src/PBAndJ.Mod` returns nothing, and the same grep shape
  finds `ProbeGameStates` in `OverworldProbeGlue.cs:112`, so it bites. The client sits in the combat
  game state after `CombatEnd` (`ClientSession.Dispatch.cs:234`: `State = Lobby`, `HostIsFighting =
  false`, execute held) — M16 observed exactly this — and a human loads their own save to leave.

⚠️ **The memory's correction is itself half-right.** "A client never returns to the overworld" is
wrong; it lives on its own overworld between fights (M12a). But **it never returns to the overworld
*from inside a host's fight* without destroying the persistent context on the way.** That is the
sentence that settles this, and it is the one nobody had written.

**The horn that is REAL, and it is the fault case.**

`StopKeyframes` — hence `ClearDestruction` — fires on three events, not one:
`CombatEnd` (`Dispatch.cs:235`), `Bye` (`:321`), and `Fault` (`ClientSession.cs:289`). On the last
two, **the human keeps playing that fight single-player** (`SetExecutionLockEffect(false)`, with the
comment saying so). They will set `Simulating`, reach `CombatExecutionEndSystem`, and the
all-hostiles-inactive count at `CombatScenarioStateSystem.cs:369-383` will run — consulting
`IsUnitActive`, which consults `isWrecked`. **Clearing the flag would resurrect every corpse into
that count and make the fight unwinnable.**

⇒ **Decision: `ClearDestruction` clears our held sets and the frozen map, and leaves the ECS flag
alone.**

**The claim that dies, stated plainly:** *"`ClearDestruction` forgets everything M17 touched, so
combat end is a clean slate."* It is no longer true. Stage 2 writes ECS state that deliberately
outlives our own bookkeeping, and the thing that reclaims it is `TeardownCampaignSystem.cs:78`, not
us. That sentence belongs in `ClearDestruction`'s comment beside the existing ordering note
(`KeyframePlayer.Destruction.Apply.cs:81-89`), because the next reader's instinct will be to
"tidy up".

**And the M12c benefit survives**, for the same reason: a checkpoint taken mid-fight records
`wrecked: true`, and `DataManagerSave.cs:2254` reads it straight back.

⚠️ **Residual, accepted and named:** if a client leaves a host's fight to the **main menu** without
loading anything, the flags sit on a persistent context that is about to be torn down anyway. If a
future milestone ever gives a client an in-place route from a host's combat to its own overworld,
this decision must be re-taken — the teardown is the whole argument.

### 4.2 The rig's escape hatch — priced, and it costs one probe command

**First, a correction that matters operationally.** There is **no `cm.end-combat-*` command.** The
brief, and `road-to-1-0.md:239`, both name one. `grep -rn '"end-combat' decompiled/` returns
nothing, and the same grep shape lists 40+ commands in `ConsoleCommandsCombat.cs`, so it bites. The
real commands are:

- `cm.force-victory` — `ConsoleCommandsCombat.cs:71-78`
- `cm.force-defeat` — `ConsoleCommandsCombat.cs:80-87`

both of which call `ScenarioUtility.EndCombatWithOutcome` behind a `CombatStateCheck()`. A runbook
that says `cm.end-combat-*` sends an operator to a command that does not exist, and the failure
looks like a wedged instance. **Fix the roadmap line in the same PR.**

**The cost, stated:** with §1.4's prefix armed, both commands become silent no-ops **on the client**.
On the **host** they are untouched (the predicate requires a `ClientSession`), so the R1 pre-flight's
"plan combat exit via host victory only" already works.

**The price, and it is small:** add to `DestructProbeGlue`

```
pbj.force-end <victory|defeat>
```

which sets a `bypassOnce` flag, calls `EndCombatWithOutcome(outcome, early: true)`, and clears it in
a `finally`. `[ExcludeFromCodeCoverage]`, wire-neutral, registered the way
`SaveLoadGlue.RegisterConsoleCommands:70-76` registers its pair. That restores a *deliberate*
escape hatch while leaving the *accidental* content-driven routes closed — which is the whole point
of the prefix.

**How the rig works without it, in priority order:**

1. **Host victory.** `cm.kill-enemy` on the host (M16's guaranteed kill) then let the outcome
   resolve, or `cm.force-victory` on the host. The client leaves on `CombatEndMessage`. This is the
   normal path and R1 §5 already specifies it.
2. **`pbj.force-end` on the client**, when the run needs the client out first (e.g. testing the
   interregnum).
3. **Kill the session.** `Bye`/fault drops the predicate to false and both `cm.force-*` come back.

---

## 5. Tests-first breakdown

Coverage reality, corrected: the 100% line/branch/method gate covers **`src/PBAndJ.Core` only**
(`Makefile:200` `COVERED_PROJECTS`). `src/PBAndJ.Mod` is in `UNCOVERED_PROJECTS` (`Makefile:220`)
because Unity/Harmony types cannot load in a test host. See §7 KILL 3 — this changes what "dead code
fails the build" means.

### 5.1 Core — provable, and therefore where the logic must live

Every decision that can be a pure function must be one, so the gate can see it.

| test | what it pins | mutation that must be seen to fail |
|---|---|---|
| `UnitSnapshotTests` — pilot fields round-trip | ctor stores and returns all four | swap `PilotKnockedOut`/`PilotEjected` in the ctor |
| `PbjMessageCodecTests` — v10 round-trip | codec order and the string length cap | drop the cause string; reorder two bools |
| `PbjMessageCodecTests` — cause string > cap | refusal, not truncation | raise the cap |
| `DestructionPlaybackTests` — `Held.Wrecked` survives `SettleWindow`, dies on `Clear` | stage 1's coupling, unchanged | invert either |
| **`DestructionPlaybackTests` — the flag decision is a Core predicate** | `ShouldHoldWreckFlagAcrossCombatEnd` returns **true**, with §4.1's reasoning in its doc block | flip it |
| `ClientSessionTests` — `Faulted`/`Closed` are not suppressing states | §1.5's predicate, as a Core-side enum test | add `Faulted` to the suppressing set |

🔑 **`SuppressCombatEnd`'s state set is the one part of §1.5 that can live in Core** — a
`static bool ClientOwnsCombatOutcome(ClientSessionState)`. Put it there. The Mod predicate then
reads `NetGlue.HasSession && session is ClientSession c && ClientOwnsCombatOutcome(c.State)` and the
only untested part is the two-term null check.

⚠️ **Every new Core member must be reachable from a test, or `make dist` fails.** A Core helper
written for a Mod patch that never runs is the one genuine "dead code fails the build" case here.

### 5.2 Mod glue — unmeasurable, so it must be *thin*

`WreckingPatches.cs`, `DriveWreckFlag`, the `pbj.force-end` command, and the counters. Nothing in
them may contain a decision Core could have made. The rule to hold to: **if you find yourself
writing an `if` in Mod, ask whether its condition belongs in Core.**

### 5.3 Rig — folded into R1, each with its zero meaning

These slot into `road-to-1-0.md` §5's R1 table as reading **6** (which today says only "applied
count, tracker count, corpse not orderable/targetable"). Replace it with 6a–6e.

| # | reading | instrument | what a ZERO means |
|---|---|---|---|
| 6a | **cascade suppression fired**: host `cm.kill-enemy`, then client `pbj.destruct-probe` | new `cascade: filtered=<n> passed=<n>` counter on the two `Filter` postfixes | `filtered=0` with `passed=0` = **the Filter was never called at all** — the patch did not apply, or nothing was wrecked; `filtered=0` with `passed>0` = the patch applied and the predicate was false (check `pbj.net-status` for a live client session). The two counters print side by side and the probe prints the live predicate beside them. |
| 6b | **the flag landed**: client `wreckFlagsSet` vs host wrecked count | `pbj.destruct-probe` `wreckFlags: set=<n> cleared=<n> refused=<n>` beside the existing `wrecked=<bool>` per unit | `set=0` with host-wrecks>0 = the apply path is dead. `set=N, refused=0`, and `wrecked=True` on N named units, is the pass. `refused>0` names the exception. |
| 6c | 🧑 **the enemy tracker corrects** | the user's eyes on instance 3's unit-tab row, plus `pbj.destruct-probe`'s per-unit `wrecked=` list as the machine-readable half | n/a — human reading. The counter half cannot substitute: `set=N` says the ECS moved, not that the UI redrew. **This is the reading that tests §2.4.** |
| 6d | **no modal dialog, no frozen debris** | zero exceptions in the client `Player.log` during combat; visual check for fragments at a corpse's core | a clean log is **not** evidence the cascade was suppressed — it is evidence it did not *throw*. 6a is the evidence. Read them together. |
| 6e | 🧑 **corpse stays collapsed through planning** (stage 1's property, re-confirmed under stage 2) | the user watches instance 3 | n/a — human reading. Named because §2.3's `OnHandleInactiveUnitCollision` and stage 1's freeze touch the same views and nobody has seen them together. |
| 6f | **the escape hatch works**: `pbj.force-end victory` on the client | the command's own return string + `pbj.net-status` | a silent return = the bypass flag never cleared or the prefix is unconditional; the command prints which branch it took. |

⚠️ **A wreck-visual counter moving is NOT evidence stage 2 ran.** Stage 1 already paid for that:
`wrecksPlayed` and `frozen` answer different questions, and `wreckFlagsSet` is a third. All three
must be read, and the pre-existing arithmetic (tanks are never slept, hidden units are never posed)
still applies to the first two.

⚠️ **Do not read 6a/6b during a `pbj.fx-hold`** — the hold clamps the cursor so the window never
finishes, which is exactly what M16 used to make the settle path observable, and it will make these
counters legitimately stand still.

🧑 items for the run sheet: **6c, 6e**, plus the release click. Everything else is agent-readable.

---

## 6. Merge window W2

Strictly after L1's **W1**. Both PRs move `wire-surface.lock` and `mod/metadata.yaml`;
`check-wire-surface` (`Makefile:160`) forces bump-and-re-record **in the same commit**, so two
writers need two windows. `UnitSnapshot.cs`, `PbjMessageCodec.cs` and `PbjProtocol.cs` are all in
`WIRE_FILES` (`Makefile:43-47`).

**The version bump:** protocol v9 → **v10**, `PbjProtocol.ModVersion` **0.23.0 → 0.24.0**,
`mod/metadata.yaml` `ver: 0.24.0`. `peer-selftest` **will** change — the protocol moved; the PR body
says so rather than treating it as a surprise.

**What PR-E owes, in one commit:**

```
make record-wire-surface        # Makefile:326 — mandatory, the hash moves
make record-split-grouping      # Makefile:270 — see below, and the roadmap omits it
make dist                       # exit 0, and READ THE TAIL, not a grep for success words
make peer-selftest              # ALL PASS at protocol v10
```

🔴 **`make record-split-grouping` is missing from `road-to-1-0.md` §6's W2 row and it is owed.**
`split-grouping.lock` records 15 families / 145 part files / **1541 members** (`:11`), and
`KeyframePlayer` is one of them — `KeyframePlayer.Destruction.Apply.cs` currently contributes 4
recorded members. Adding `DriveWreckFlag` to it changes the map, and `check-split-grouping`
(`Makefile:267`) is a hard dependency of `dist` (`Makefile:357`). The same applies to the
`NetGlue.Runtime` accessor in §1.5 — `NetGlue` is also a locked family. **Either re-record, or put
the accessor somewhere outside a family and say why.**

⚠️ **No split may land in `UnitSnapshot.cs`, `PbjMessageCodec.cs`, `Keyframes.cs`,
`DestructionPlayback.cs` (688 lines, queued), `DestructProbeGlue.cs` (619, queued) or any
`KeyframePlayer.*` part while W2 is in flight.** The queue is hygiene and it waits.

Whichever PR enters its window second rebases and re-runs the **full** gate on the rebased tree.
Green is never inherited.

---

## 7. The third refutation pass — run against the text above

Rounds 1 and 2 each killed a headline. This pass killed five claims, three of which were in my own
reconstruction before I checked them. Each entry names the evidence that killed it.

### 🔴 KILL 1 — "a client cannot self-victory" is **contingent, not structural**, and §2.5's repayment is what makes it contingent

Round 2's kill of round 1 rests on: bit 4 (`OnExecutionEnd`) gates the count at
`CombatScenarioStateSystem.cs:365`, and *"Bit 4's only substantive producer is
`CombatExecutionEndSystem.cs:59`, triggered on `Simulating.Removed()` — and a client never sets
`Simulating`."*

**There is a second producer.** `grep -rn "AddScenarioStateRefreshContext" decompiled/` returns 15
sites; two pass `OnExecutionEnd`:

- `CombatExecutionEndSystem.cs:59` — host only, as round 2 said.
- **`CombatScenarioTransitionSystem.cs:68`** — reached from a collector on
  `CombatMatcher.ScenarioTransitionRefresh.Added()` (`:25`), i.e. from
  `combat.isScenarioTransitionRefresh = true`, and it adds bit 4 whenever the current step's
  `transitions.transitionMode == ScenarioTransitionEvaluation.OnExecutionEnd` (`:66`).

Who sets `isScenarioTransitionRefresh`? Seven sites, and **two are content-driven and reachable on
a client**: `CombatScenarioStateSystem.cs:356` (inside the state-evaluation loop, which is *not*
gated on flag3) and `PhantomBrigade.Functions/CombatStateValueChange.cs:39`.

And the contexts **accumulate by bitwise OR** (`CombatUtilities.cs:798-802`) until consumed
(`CombatScenarioStateSystem.cs:207-208`). So `OnUnitDisabled | OnExecutionEnd` in one window ⇒
`flag3` true ⇒ line `:365` passes ⇒ the count at `:369-383` runs ⇒ with every hostile now `isWrecked`,
`num4 == 0` ⇒ `EndCombatWithOutcome(Victory)` on a **client**.

⇒ **Three consequences, all binding:**
1. Round 2's headline stands as "a client cannot self-victory *through the ordinary route*", not as
   a structural impossibility. `road-to-1-0.md:429` files it under "impossible by construction" —
   **that line is now wrong** and should be softened in the same PR.
2. §1.4's prefix is **required**, not defence in depth.
3. **§2.5's poke and §1.4's prefix ship together or not at all.** Adding the poke while omitting the
   prefix is the exact combination that opens the door. That coupling is written into §2.2 step 5.

⭐ Found by refusing to accept "only producer" without running the grep at the **component**
(`AddScenarioStateRefreshContext`, all 15 sites) rather than at the claim.

⭐⭐ **Independently corroborated by §7A**, which reaches the same conclusion from the opposite
end: bit 4's *round-2-named* producer is itself reachable on a client through content
(`CombatForceExecution`) and the console. Two content routes, one bit.

### 🔴 KILL 2 — the prefix as written would have made a post-fault fight unwinnable **for ever**

Round 1 and round 2 both describe the `EndCombatWithOutcome` prefix as "no-ops on a client", with no
predicate. Take that literally and pair it with the two other paths through `StopKeyframes`:
`ByeMessage` (`ClientSession.Dispatch.cs:320-322`) and `Fault` (`ClientSession.cs:288-292`). Both
release the execution lock, and `Fault`'s comment states the intent: *"A lost host must never leave
the local execute button disabled — the player continues single-player from here."*

That human then simulates locally, `CombatExecutionEndSystem` fires, bit 4 is set legitimately, the
victory count runs — **and the prefix eats the outcome.** The fight can never end. Nothing in the
plan text priced this.

⇒ §1.5's predicate must exclude `Closed` and `Faulted`, and the reason must be in the code, not just
here. This is also the second half of decision 4.1's argument, which is why the two sections share it.

### 🔴 KILL 3 — "dead code fails the 100% gate" is a claim about the **wrong unit**

`road-to-1-0.md:238` and the surviving memory both justify "do NOT patch `CombatPartWreckingSystem`"
with *"a patch there is dead code, and dead code fails the 100% gate."*

`Makefile:200` sets `COVERED_PROJECTS := src/PBAndJ.Core`. `Makefile:220` sets
`UNCOVERED_PROJECTS := src/PBAndJ.Net src/PBAndJ.Mod`, with the stated reason that Mod's Unity and
Harmony types **cannot load in a test host**. A Harmony patch is Mod code. **It does not fail
`make dist`. It compiles, deploys, never runs, and nothing tells you.** That is strictly worse than
a build failure, and the mitigation is different: a patch needs a **counter that can read zero**
(§5.3, 6a), not a test.

The *conclusion* survives — do not patch `CombatPartWreckingSystem`, because
`EquipmentMatcher.Wrecked.Added()` (`:26`) is a component no client path adds — but the stated
mechanism is false and must not be repeated. **Dead *Core* code fails the gate; dead Mod glue is
silent.** This is sighting 21's shape exactly: an invariant aimed at the wrong unit.

### 🔴 KILL 4 — "`deathStatus`/`knockedOut`/`ejected` have no reactive system" is false for `deathStatus`

The stage-3 re-scope rests on that sentence. `PersistentMatcher.DeathStatus` has exactly one
reference and it is a **collector**: `OverworldPilotUILinkSystem.cs:18`,
`AnyOf(..., PersistentMatcher.DeathStatus).AddedOrRemoved()`. And it is not dormant during combat —
`OverworldSystemsPermanent.cs:19` holds it, and `GameController.cs:179` registers that Feature with
`SetAlwaysActiveAfterStack(true)`, which `GameController.cs:395-399` honours on every frame,
including while `"combat"` is on top.

**The conclusion survives, for a reason that had to be found rather than assumed:** the system's
`Execute` (`:30-51`) is four `IsEntered()`-guarded UI refreshes —
`CIViewBasePilotInfoExtended`, `CIViewBaseBriefingV2`, `CIViewBasePilots`, `CIViewOverworldRoster`
— none of which is entered during combat. The cost is a no-op loop.

⇒ Sync the three fields, but **write the components directly** (§3.2), and record in the plan that
`DeathStatus` has a live collector whose Execute is inert *in this game state* — because if a future
milestone ever shows a base view over combat, that inertness ends.

⭐ Same failure mode as the `.Added()` miss in round 1, one layer up: the claim was "no reactive
system", and the check that was actually run was narrower than the claim.

### 🔴 KILL 5 — setting `isWrecked` on a client re-routes `ActionUtility.CrashEntity` into the trap stage 1 spent a build learning to avoid

`ActionUtility.CrashEntity` (`:657`) reads, in order: a `crashable` check (`:659`), an
`IsUnitActive(..., inactiveIfUnitWrecked: false)` early-out (`:664-667`), and then

```
if (linkedPersistentEntity.isWrecked) { UnitUtilities.OnUnitNonfunctional(unitCombat); return; }   // :669-673
```

`OnUnitNonfunctional` (`UnitUtilities.cs:2667-2689`) sets `enableInternalCollisionsOnKill = true`,
`enableAngularLimitsOnKill = true`, `mode = PuppetMaster.Mode.Active` and `state = State.Dead` —
**the exact call `client-wreck-pose-lost.md` documents as a trap, whose refutation was verified by
hand at `PuppetMaster.cs:423-431`, and whose absence is what makes stage 1's fix small.** Today
`isWrecked` is false on a client so this branch is unreachable; **stage 2 makes it reachable.**

How bad is it? The 12 callers of `CrashEntity` were listed and the reachable ones checked:

- `ActionPlaybackSystem` (5 sites) — its `Execute` opens `if (combat.Simulating && IDUtility.IsGameLoaded())` (`:93`). Unreachable on a client.
- `CombatCrashingSystem` — operates on units with `isCrashing`, which only `CombatActionEvent.OnCrashStart` sets, itself reached only from `CrashEntity`. Self-closing.
- `EquipmentUtility` (2), `CombatTriggerEventSystem`, `CombatTriggerEventHelper`, `CombatActionEvent` — all damage/trigger paths a non-simulating client does not drive.
- `ConsoleCommandsCombat.cs:688` — `cm.crash-unit`, human-driven. Real.
- ⚠️ **`Area/AreaSimulatedChunk.cs:187` → `OverlapUtility.OnAreaOfEffectAgainstUnits` → `EquipmentUtility.cs:3105 CrashEntity`** — level-destruction physics. **UNVERIFIED whether a client's collapsing building reaches it.** This is the one live thread.

⇒ Not a blocker, and **not a reason to skip stage 2** — but it must be in the plan, in the code
comment, and in the runbook, because if a client's corpse ever starts ragdolling under its own
physics after this ships, **this is the first place to look and nobody would otherwise know.**
Cheapest verification is §8's item 1.

### What this pass did **not** kill

The central `Filter` mechanism (§0.1), the string-name requirement, the leave-`WreckingSyncSystem`-alone
decision, the wire-v10 strikes, and stage 4 staying cut all survived re-examination unchanged. Saying
so is part of the pass: five kills out of a text this size is a real result, and claiming more would
be inventing them.

---

## 7A. ADJUDICATION — does a client ever set `Simulating`? (2026-08-21, lane L3)

Lane L2 reported the **opposite** reading of `CombatScenarioStateSystem.cs:365` from the one this
plan inherited, and the whole of §1.4 and §7 KILL 1 rests on it. Both accounts agree on the wiring
down to `Simulating.Removed()`. They disagree on one question, so that is the question I read.

**Verdict: the M17 round-2 memory is RIGHT on the fact, and L2 is RIGHT that the gate is not
structurally host-only — but L2's evidence for it does not support it, and the correct evidence is
a different route entirely.** Neither account is wholly right, and the resolution is the same
conclusion this plan already reached in §7 KILL 1, reached again from the opposite end. §1.4 stands
unchanged: **the prefix ships.**

### Step 1 — every writer of `Simulating`, matched at the COMPONENT

`Simulating` is a flag component, index **216** (`CombatComponentsLookup.cs:442`), so the only
write shape is the property setter (`CombatEntity.cs:2723-2745`; context-level wrapper
`CombatContext.cs:554-567`). Checked for the other shapes anyway —
`AddSimulating|RemoveSimulating|ReplaceSimulating|isSimulating` returns **zero across the whole
decompile**, and that zero is *not* vacuous: the identical grep shape returns **92** hits for
`.isDestroyed = `, so it bites on a flag component that is written.

**`.Simulating = ` has exactly three hits, and one is the context wrapper itself:**

| site | value |
|---|---|
| `CombatContext.cs:567` | the wrapper's own `CreateEntity().Simulating = true` |
| **`SimulationTimeSystem.cs:63`** | `true` |
| **`SimulationTimeSystem.cs:129`** | `false` |

⇒ **`SimulationTimeSystem` is the sole writer, both sides.** Nothing else in the game sets or clears
it, and nothing in `src/` writes it at all (`grep -rn 'Simulating' src/` — 18 hits, every one a read
or a comment).

Collectors on it, for completeness (`CombatMatcher.Simulating`, any accessor — 9 hits against a
control of 281 `CombatMatcher.` hits): one `.Added()` (`CombatUILinkSimulationStart.cs:38`) and
seven `.Removed()`, including `CombatExecutionEndSystem.cs:35` and
`CombatExecutionEndLateSystem.cs:28`.

### Step 2 — `SimulationTimeSystem.cs:59-63`, the setter, read in full

```
if (!Mathf.Approximately(f, f2) && f <= f2) {      // :59   f = simulationTime, f2 = simulationTargetTime
    if (!combat.Simulating) { combat.Simulating = true; }   // :61-63
```

`SimulationTimeSystem` is an `IExecuteSystem` in `CombatSystems` (`CombatSystems.cs:21`), so it
**does** run on a client every frame (§0.2's trace). ⇒ **"A client never sets `Simulating`" is not a
structural property of the system. It reduces entirely to: does a client's
`simulationTargetTime` ever get ahead of its `simulationTime`?**

Writers of `SimulationTargetTime` outside the generated context/entity code — four:

| site | what it writes | reachable on a client? |
|---|---|---|
| `CombatLoadingSystem.cs:79` | `0f` | yes, and `0 == 0` ⇒ no simulation |
| `CombatBootstrap.cs:56` | `0f` | yes, same |
| `DataManagerSave.cs:2829` | `combat2.time` — **and `:2828` writes `SimulationTime` to the same `combat2.time`** | yes; the two are **equal**, so `Mathf.Approximately` holds and nothing simulates. `:2823` also `Deactivate()`s `TurnSystem`'s collector across the load |
| **`TurnSystem.cs:36`** | `currentTurn.i * turnLength.i` | **only if `currentTurn` advances** |

⇒ The whole question collapses to **one** further question: *does a pb-and-j client's `currentTurn`
ever advance?*

### Step 3 — `currentTurn`, and the mod's own wiring

In-fight, exactly one game path advances it: `CombatUtilities.ConfirmExecution`
(`CombatUtilities.cs:50`, ending `:106-107` `ReplaceCurrentTurn(currentTurn + turnsAdvanced)`).
The other three writers are `0`-setters at load/bootstrap (`CombatLoadingSystem.cs:83`,
`CombatBootstrap.cs:49`) and the save loader (`DataManagerSave.cs:2830`, with `TurnSystem`
deactivated).

`ConfirmExecution` has four callers: the Execute button (`CIViewCombatExecution.cs:260`), the debug
console (`ConsoleCommandsCombat.cs:202`), scenario content
(`PhantomBrigade.Functions/CombatForceExecution.cs:14`), and the mod.

The mod's side:

- The **button is intercepted**: `ExecutionPatches.cs:27-35` prefixes
  `CIViewCombatExecution.CheckAndAttemptExecution` and returns `false` whenever `NetGlue.HasSession`
  — a client presses Execute and sends a Ready instead.
- The mod's only call is `CombatGameBridge.CommitTurn` (`CombatGameBridge.Turn.cs:73-95`), reached
  only from `PbjRuntime.cs:252-255` handling a `CommitTurnEffect`.
- **`CommitTurnEffect` has exactly one emitter in the whole tree: `HostSession.Turn.cs:176`.** A
  `ClientSession` never emits it, and `ClientSession.Dispatch.cs:127-129` discards the outcome event
  with the comment *"Clients never commit."*

⇒ **A pb-and-j client's `currentTurn` does not advance through any path the mod drives, so
`TurnSystem` never fires, so `simulationTargetTime` never exceeds `simulationTime`, so
`SimulationTimeSystem.cs:63` never runs, so `Simulating` is never set — and therefore never
removed.** The round-2 memory's fact is correct, and this is the chain it was asserting without
tracing.

The tree already believed this in four places and none of them had traced it either:
`Seams.cs:149`, `CombatGameBridge.Snapshot.Apply.cs:16` and `:26` (*"Safe only because a client
never sets `combat.Simulating`"* — the **licence** for hard-writing transforms), and
`ReplayProbeGlue.cs:162-163`. ⭐ It is now traced, and `ApplySnapshot`'s safety argument rests on
the same fact.

### Step 4 — L2's evidence, examined: what `ExecutionPatches.cs:111` actually implies

L2 reads *"the mod's own `ExecutionPatches.cs:111` exists **because** a client reaches the
execution-end systems."* That is an inference about intent. Read as wiring, it says the opposite:

- The patch is `[HarmonyPatch(typeof(CombatExecutionEndLateSystem), "Execute")]`, postfix, guarded
  on `NetGlue.HasSession` — which is true on **either** kind of session. Being *guarded for both* is
  not being *reached on both*.
- Its own comment (`:107-109`) explains it in **host** terms: *"ConfirmExecution advanced
  `currentTurn` before the sim ran, so it already shows the next turn. HostSession supplies the real
  one."* Only a machine that ran `ConfirmExecution` has that problem.
- Its body posts `NetGlue.PostLocalTurnComplete()` (`:120`), and
  **`ClientSession.Dispatch.cs:122-125` explicitly throws that event away**: *"A client does not
  simulate, so its own execution-end hook carries no authority. The host's TurnComplete drives us."*
  A patch whose only output the client-side session discards by design is not evidence the client
  reaches it.
- The neighbouring comment names the real reason the *host* needs a patch rather than trusting its
  own barrier (`:68-73`): `CombatForceExecution` and the console **bypass the barrier**, so the host
  learns about those advances here.

⇒ **The patch is reached on the HOST.** L2's premise that "any simulating machine reaches
`Simulating.Removed()`" is true and vacuous for a client; the patch is not the counter-example.

### Step 5 — where L2 is nevertheless RIGHT, and it matters

The property is **contingent on content and on the console, not structural.** `ConfirmExecution`'s
two bypass routes exist on a client too:

- **`CombatForceExecution`** — an `ICombatFunction` any scenario YAML can invoke
  (`PhantomBrigade.Functions/CombatForceExecution.cs:14`).
- **`cm.execute-by-turns`** — `ConsoleCommandsCombat.cs:202`.

`ExecutionPatches.cs:74-101` exists precisely because these routes are real; it **detects** the
resulting turn advance (`NetGlue.NotifyExternalTurnAdvance`, `:100`) and does not **prevent** it. If
scenario content force-executes on a client, that client simulates, reaches `Simulating.Removed()`,
and `CombatExecutionEndSystem.cs:59` produces bit 4 after all — and `road-to-1-0.md:429`'s
*"impossible by construction"* is then false.

⭐ **This is §7 KILL 1's conclusion reached by a second, independent route.** KILL 1 found a
*different* producer of bit 4 (`CombatScenarioTransitionSystem.cs:68`, content-driven); this
adjudication finds a *content-driven route to the same producer round 2 named*. Two independent
content routes to one bit is a much stronger case than either alone.

### The verdict, in one line each

- **"A client never sets `Simulating`" — TRUE**, and now traced end to end rather than asserted:
  `SimulationTimeSystem.cs:63` ← `TurnSystem.cs:36` ← `ConfirmExecution` ← `CommitTurnEffect` ←
  `HostSession.Turn.cs:176` only.
- **"`ExecutionPatches.cs:111` proves a client reaches the execution-end systems" — FALSE.** It is a
  host-side hook with a session-wide guard, and the client discards its output by design.
- **"`:365` gates on a bit only the host produces" — TOO STRONG.** It gates on a bit only the host
  produces *on the ordinary path*; two content-driven routes reach it on a client.
- **Net effect on this plan: none, and that is the point.** §1.4's prefix was already promoted from
  defence-in-depth to required by KILL 1, and §2.2 step 5 already couples the `OnUnitDisabled` poke
  to it. This adjudication supplies a second reason for the same decision.

**Owed elsewhere, not written by this lane:** `road-to-1-0.md:429` (§7, *"Client self-victory —
impossible by construction"*) should be softened to *"unreachable on the ordinary path; two
content-driven routes exist"*, with a pointer here. **That file is shared and L3 did not touch it.**

**⚠️ UNVERIFIED, and the cheapest experiment if anyone wants certainty rather than a chain:** no
shipped fight has been checked for a `CombatForceExecution` block or an `OnExecutionEnd`
transition mode. One grep over the game's scenario YAML for `CombatForceExecution` and
`transitionMode: OnExecutionEnd` would say whether the contingency is live in practice or only in
principle. It costs one grep and it is **not** a blocker: the prefix closes both routes either way.

---

## 8. The UNVERIFIED register, with the cheapest verification for each

| # | claim | why unverified | cheapest verification |
|---|---|---|---|
| 1 | ~~a client's level destruction can reach `CrashEntity` (KILL 5)~~ | ✅ **CLOSED 2026-08-21 during the build — see §10** | done statically; the rig ride-along is now optional confirmation, not the closure |
| 2 | subsystem `destroyed` is write-only in vanilla (stage 4 cut) | round 2's reading, not re-taken today | read `UnitUtilities.CreateSubsystemsFromSave` and grep `destroyed` in `DataBlockSavedSubsystem` — one grep, agent-cheap |
| 3 | `UnitStatusUpdateSystem` triggers on `CombatMatcher.SimulationTime` (stage 4 cut) | round 2's reading, not re-taken | open `UnitStatusUpdateSystem.GetTrigger` — one file |
| 4 | `OverworldCombatOutcomeProcessingSystem.cs:190-199` / `:200` line numbers | quoted from memory; the member was verified, the lines were not | navigate by member (`Execute`), re-derive |
| 5 | `PuppetMaster.cs:423-431` (KILL 5's downstream) | stage 1 verified it 2026-08-18; not re-opened today | open it if KILL 5's thread turns out live |
| 6 | that no *other* game-state stack combination puts a base view over combat (KILL 4's residual) | only the four `IsEntered()` guards were read, not every route into those views | not worth paying now; re-check if a base view is ever shown in combat |

---

## 9. Build gate

**Build authorization for stage 2 comes from the user, after this plan is reviewed.** This document
writes no code. When it is authorized, the order is: Core tests red → Core green → Mod glue →
`make dist` (read the tail **and** the exit code) → `record-wire-surface` + `record-split-grouping`
in the same commit → branch → PR → merge in W2, after W1.

*Written by lane L3, 2026-08-21, at `main` = `1708a0b`. Superseded only by a later refutation, and a
later refutation should say which of §7's five kills it re-opened.*

---

## 10. BUILD NOTES (2026-08-21) — what the build found, kept, and had to correct

Written by the build lane against this document. **Five of the plan's decisions were checked at the
code face and every one held.** What follows is only the deltas.

### 10.1 ✅ KILL 5's open thread is CLOSED, and the answer is the opposite of the one feared

**The guard the plan hoped for does not exist.** `AreaSimulatedChunk.OnCollisionEnter`
(`decompiled/Area/AreaSimulatedChunk.cs:133-193`) is a Unity physics callback with **no `Simulating`
condition anywhere in the file** — its only guard is `colliderToPointMap.ContainsKey(thisCollider)`,
and the AoE call sits unguarded at `:187`, exactly where the plan said. Read literally, §8 item 1
would have come back "thread live".

**It is closed one level down instead, by the very flag stage 2 sets.**
`OverlapUtility.OnAreaOfEffectAgainstUnits` builds its hit list at
`decompiled/PhantomBrigade.Data/OverlapUtility.cs:317-325` and admits a unit only when
`linkedPersistentEntity != null && !linkedPersistentEntity.isWrecked` (**`:320`**); the `CrashEntity`
call at `:480` iterates nothing else. **A wrecked unit is invisible to the whole AoE path.** Setting
`isWrecked` does not open `AreaSimulatedChunk.cs:187` — it shuts it. That is a stronger closure than
a `Simulating` guard, because it does not depend on simulation state at all.

⚠️ **The plan's downstream citation is wrong and harmlessly so.** It routes
`:187 → OnAreaOfEffectAgainstUnits → EquipmentUtility.cs:3105 CrashEntity`. `EquipmentUtility` is at
`decompiled/EquipmentUtility.cs`, **not** under `PhantomBrigade.Data/`, and `:3105` is a
weapon-damage caller on a different path. The real chain is
`AreaSimulatedChunk.cs:187 → OverlapUtility.cs:280 → OverlapUtility.cs:480`.

### 10.2 🔴 A SECOND ROUTE THE PLAN NEVER NAMED, and it is the one that stays open

Grepping the *component* rather than the claim (the ⭐⭐ rule) turned up a second caller from the
same `AreaManager`: **`OverlapUtility.CheckUnitsOnDestroyedPoint`** (`:32-88`), reached from
`AreaManager.ApplyDestructionToPoint:2701` and `AreaManager.CreateSimulatedPoint:3063`.

Read with the guard *above* it, which is the whole point:

```
if (data.classTag == UnitClassKeys.turret) {
    if (!linkedPersistentEntity.isWrecked) { ... OnDestruction ... }        // :73  guarded
}
else if (!value.isCrashing && !value.hasCurrentDashAction && !value.hasUnitImpaledStatus) {
    ActionUtility.CrashEntity(value, ..., bypassCrashableCheck: true);      // :79-82  NOT guarded
}
```

A **wrecked non-turret** standing on a destroyed floor tile therefore does reach `CrashEntity`, and
`bypassCrashableCheck: true` skips the first early-out. This is the live residue of KILL 5.

**How reachable is it?** Only if this client's own `AreaManager` destroys a point. The six producers
of area damage were listed and read:

| producer | reachable on a pb-and-j client? |
|---|---|
| `CombatCollisionSystem.cs:446` | needs `projectile.hasInflictedImpact` on a collision-event entity — simulation work |
| `BeamProjectionSystem.cs:371` | guarded by `beamEnt.hasInflictedImpact`; **`InflictedImpact` has zero hits in `src/`** (control: `NetGlue` returns 74), so mod-injected beams cannot damage the level |
| `TriggerEventDelegates.cs:22` | trigger collisions, not driven by a non-simulating client |
| `DataManagerSave.cs:3402` | load-time `ApplyDamageFromList` — no unit is wrecked yet |
| `CombatCreateDamage.cs:42` | **scenario content**, an `ICombatFunction` |
| `ConsoleCommandsCombat.cs:1314`, `CIViewInternalCombatTools.cs:366` | debug console / internal tools |

⇒ **Unreachable through any path this mod drives; reachable in principle through scenario content or
the debug console** — precisely the contingency class §7A already prices for `CombatForceExecution`.
Not a blocker. It is written into `DriveWreckFlag`'s remarks as the first place to look if a
client's corpse ever ragdolls, which is what §7 KILL 5 asked for.

⚠️ Also worth carrying: `grep -rn 'AreaManager|areaManager|AreaVolumePoint' src/` returns **0**
against a control of **74** for `NetGlue`. The mod does not touch level destruction at all.

### 10.3 Corrections to this document that were already made on `main`

`3a160c4` is later than the `1708a0b` this plan was written at, and the review lane had already
landed three of the plan's "owed elsewhere" items. **They were not re-done:**

- §4.2's *"fix the roadmap line"* — `road-to-1-0.md` already says *"there is no `cm.end-combat-*`"*.
- §6's *"`record-split-grouping` is missing from §6's W2 row"* — the W2 row already names it.
- §7 KILL 1's *"`road-to-1-0.md:429` should be softened"* — already reads
  *"~~impossible by construction~~ 🔴 CORRECTED"*.

What §5.3 asked for and was **not** yet done: R1 reading 6 was still a single row. It is now 6a–6f
in `road-to-1-0.md` §5 and in `docs/notes/rig-run-1-0.md`.

🔴 **And that runbook carried a claim stage 2 falsifies.** `rig-run-1-0.md`'s R1·6 block said *"a
client's own ECS reads zero for `wrecked` by design, so read L3's applied counter, not the ECS
column."* True until this PR; from this PR the ECS column **is** the reading. Sighting 19's shape —
prose that goes stale into a wrong instruction — caught only because §5.3 sent the build into that
file. Corrected in place with the date and the reason.

### 10.4 Two things the plan under-specified, decided here and named

1. **`AddDeathStatus` takes a `time` this wire does not carry.** §3.2 lists the field as
   `PilotDead + PilotDeathCause` and no float. The component is `{ float time; string cause; }` and
   `time` is **serialized** (`DataHelperSaveSerialization.cs:368-369`). Decision: **write `0f`**, and
   say so, for exactly §3.4's reason — it is the host's number on the host's clock, a client that
   invented one would put a different value in its own checkpoint, and nothing reads it during a
   fight (the two readers are the extended pilot-info and overworld-debriefing views).
2. **§2.2 lists the batch calls as steps 4 and 5 of `DriveWreckFlag` while also saying "once per
   batch, not per unit".** `ApplyDestruction` is called inside a per-target, per-frame loop, so
   those two cannot live in the per-unit method. Split into `FlushWreckFlagBatch`, called once after
   the target loop in `Advance` and once after the wreck loop in `ApplySettled`, and inert unless a
   flag actually changed.

### 10.5 One member is more ornamental than the plan implies

`DestructionState.ShouldHoldWreckFlagAcrossCombatEnd` is a constant-`true` property. Nothing
*branches* on it — a branch would be dead code, since `ClearDestruction` simply does not touch the
ECS flag. It is a documented decision with a Core test that pins it and a comment in
`ClearDestruction` pointing at it. That is what §5.1 asked for, and it is worth knowing it buys
documentation and a mutation target rather than behaviour.

### 10.6 🔴 The mutation run found TWO defects in the build's own new tests, and one in the harness

Every guard §5.1 names was mutation-checked **one mutation at a time** (sighting 18), and three
things came back that would otherwise have shipped as green-over-nothing.

**The harness first.** Its first run reported *"exit=2, failing tests (0)"* for all six mutations —
which reads as "the tests do not bite". It was reading `stdout` only, and xUnit writes `[FAIL]` to
`stderr`. ⭐ Caught by disbelieving the zero and re-running one mutation by hand with the streams
merged. **A zero was a claim about the parse, not about the tests.**

**Defect 1 — `Decode_SnapshotWithAnOverlongDeathCause_Throws` passed with the cap check deleted.**
The hand-built frame stopped after the cause string and omitted the last two bools, so the reader
ran off the end of the buffer and `Decode` threw `PbjProtocolException` for **framing**. The
assertion could not tell that from the cap refusing. Fixed by completing the record; re-proven by
deleting the check and watching the test go red.

**Defect 2 — the cap VALUE was pinned by nothing.** Every cap test built its string as
`new string('x', MaxPilotDeathCauseLength + 1)`, so raising the constant raised the tests with it:
**32 → 64 failed nothing at all.** They pinned the mechanism ("the check consults the cap"), never
the number, and §5.1's named mutation *"raise the cap"* was a no-op against them. Fixed with a
literal `Assert.Equal(32, ...)`.

⚠️ **And the first attempt at that mutation was itself a bad control** — sighting 15's shape.
Raising the cap to **4096** collides with `PbjWriter.MaxStringLength`, which is exactly 4096, so a
`cap + 1` string trips the *writer's* limit: the encode test passed for the wrong reason and the
decode test failed for the wrong reason. The pin now also asserts `cap * 4 < MaxStringLength` so the
two limits cannot drift into each other again.

**Final state — eight mutations, eight bites, each by its own test:**

| mutation | caught by |
|---|---|
| swap `PilotKnockedOut`/`PilotEjected` in the ctor | `Constructor_RetainsEveryPilotField`, `Constructor_KnockedOutIsSeparateFromDead` |
| reorder two bools on the codec write side | `RoundTrip_Snapshot_PreservesThePilotPerUnit` |
| drop the cause string from the wire | that plus both cap round-trips |
| raise the cap 32 → 64 | `MaxPilotDeathCauseLength_IsThirtyTwo` |
| neuter the encode cap check | `Encode_SnapshotWithAnOverlongDeathCause_Throws` |
| neuter the decode cap check | `Decode_SnapshotWithAnOverlongDeathCause_Throws` |
| flip `ShouldHoldWreckFlagAcrossCombatEnd` | `ShouldHoldWreckFlagAcrossCombatEnd_IsTrue` |
| let `Faulted` own the combat outcome | `ClientOwnsCombatOutcome_IsFalseOnceTheSessionIsOver(Faulted)` |

### 10.7 What still needs the rig

Nothing in stage 2 has run in a game. `make dist` cannot see a Harmony patch that failed to apply,
so **reading 6a (`pbj.wreck-patches`) is the first thing to take** and it needs neither a fight nor
a second instance. The static half of that proof is done: the three attributes were read out of the
**built** `PBAndJ.Mod.dll` with `ilspycmd` (decompiled in place, sighting 23) and carry the right
target type and member name, and each target declares exactly one matching member in the decompile.
What no static check can establish is that Harmony bound them at load — that is `owners=` on 6a.
