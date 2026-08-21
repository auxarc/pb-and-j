# M12 — concurrent management, planned from measurements

Status: **designed 2026-08-07, adversarially reviewed and corrected 2026-08-08, nothing built.**
Every claim here rests on `docs/notes/overworld-recon.md`, which records two real game instances
running one campaign. Where this document and `campaign-coop.md` disagree, this one is newer and
measured.

**The 2026-08-08 review refuted six premises of the first draft**, every refutation confirmed by
hand against the decompile before being written here. The corrections are inline and marked
**⚠️ REVIEW**. The shape of the error was the same one this project has now paid for six times: a
path traced correctly and then generalised one step too far — parts to "equipment", a movement
funnel to "base control", one generation site to "generation".

M11 ended the moment everyone was loaded into the same save. M12 is everything after that.

## What the recon changed

Three assumptions the earlier design was built on turned out to be wrong, and two problems nobody had
anticipated turned out to exist. Read `overworld-recon.md` before building any of this; the short
version:

- The overworld clock is **not** continuous — it advances only while the base travels or a time skip
  runs. A client sitting still does not drift: 47 of 48 save files byte-identical after three idle
  minutes.
- The overworld renderer collects on **`PositionDetectedLast`, not `Position`**, and its feeder only
  runs in game state `overworld`. The management screens are `basecrawler`.
- **Per-unit ownership is not sufficient.** The shared parts inventory lives on the mobile base, so
  every equip mutates shared state. Two machines editing *different* mechs both claimed weapon
  serial 2249.
- **Generated contracts are rolled per machine** and diverge wholesale — different `areaKey`,
  `biomeKey`, spawn points. Two players disagree about what a mission is.
- Player *movement orders* have a **single funnel**: `OverworldUtility.OrderMovementToPosition`.
  ⚠️ **REVIEW: base control does not.** See M12a.

## The principle this milestone is built on

**The system never decides on a player's behalf what a player should decide.** Not loot, not
ownership, not "who gets the odd point". Where a decision cannot be made — a peer dropped, two people
want the same item — the design **preserves the moment** so the humans can still make it, rather than
resolving it quietly. Every fallback below rolls back rather than auto-assigns.

The corollary, learned the hard way twice in this project (M10c's connect screen, and the double
claim above): **a refusal must be visible before the fact.** An optimistic local success that is
silently reversed is the failure mode to design out.

---

## M12a — the client as a passenger

**The user's stated target:** full UI access, cannot drive the base, and can watch the host drive it.

**Build:**

- **Suppress movement orders on a client.** One Harmony prefix on
  `OverworldUtility.OrderMovementToPosition` — three call sites, all player input
  (`CIViewOverworldNav.cs:1060`, `CIViewOverworldProcess.cs:854`,
  `OverworldMoveToOrderSystem.cs:128`), always identifying the player base. It also restarts the
  clock from `simulationTimeScaleBackUp`, so suppressing it suppresses both.

- ⚠️ **REVIEW: that prefix is necessary and nowhere near sufficient.** The first draft said the base
  had one control funnel and the time-scale buttons were already inert. Both were refuted:

  - **A simulation lock bypasses `RefreshTimeScale` entirely.** `OverworldTimeUtility.RefreshTimeScale`
    only acts `if (IsGameState("overworld") && !overworld.hasSimulationLockCountdown)` — so the
    `isBaseMoving` derivation is skipped in exactly the case that matters, and
    `OverworldSimulationLockActivationSystem` writes the time scale directly instead. **A client can
    advance its own clock without ever issuing a move order.**
  - **Camp is a clock-advancing button on the client's screen.** `CIViewOverworldRoster.OnCampInitiated`
    (`:1022`) spends supplies and calls `ReplaceSimulationLockCountdown(f, duration, "camp")`.
  - **Retreat both locks the clock and moves the base**, outside the funnel:
    `OverworldUtility.TryRetreatToResupplyBase` starts a `"resupply"` lock and a
    `SimulationLockReposition`.
  - **Sim-lock exit re-rolls every generated contract** — `CIViewOverworldRoster.cs:1829-1830` wipes
    `world_gen_visited_` memory and calls `RefreshStandaloneGeneratorEncounters()`. One client camp
    click destroys whatever M12b synchronised. This is the single sharpest coupling the first draft
    missed: **M12a's suppression list is load-bearing for M12b**, not merely cosmetic.
  - **Other base-position writers exist** and the mirror must tolerate them rather than assume they
    cannot fire: post-combat `OffsetFromInteraction`, and the event functions
    `TeleportBaseToPosition` / `TeleportBaseToEntityName`.
  - `CIViewOverworldRoot.ForceTimeScale` is **not** console-only — `DataHelperLoading.cs:391`,
    `CIViewOverworldPointList.cs:193` and `CIViewPauseRoot.cs:566` all call it. All three pass `0f`,
    so it is materially harmless, but the sentence was wrong and the tutorial path sets `3f`.

  **So the client suppression set is: movement orders, camp, retreat, and mission engagement** (see
  M12b — the client's own interaction dialog can call `EnterCombat` once the mirror puts its base in
  range). Each is a refusal that must be **visible before the fact** per the principle — a greyed
  button with a reason, not a silently swallowed click.
- **Mirror the host's base**, using the game's own teleport recipe
  (`ConsoleCommandsOverworld.cs:893-901`, and `pbj.ow-mirror` is a working implementation):
  `StopMovement`, `ReplacePosition`, **`ReplacePositionTarget`** (not optional — `OverworldMovementSystem`
  drags position back toward a stale target), `isPositionUnchecked = true`, and
  `ReplaceSimulationTime(same value)` to kick the reactive collectors while paused.
- **Send X and Z only.** The receiving machine's ground-snap finds its own Y — observed correcting
  33.3 to 13.3.
- **Accept catch-up, do not fight it.** In `basecrawler` the write lands but does not render. The
  position should be right when the player returns to the map. ⚠️ **REVIEW: that is an inference,
  not a measurement.** `GameController.PopUntilState` calls `DeactivateReactiveSystemsSelective()`,
  and Entitas' `Collector.Deactivate` **clears collected entities** — so a kick issued in
  `basecrawler` is discarded before it can execute, and catch-up depends instead on
  `OverworldBootstrap.Enable`'s own self-replace firing on state re-entry. The mechanism exists and
  is plausible; it is also exactly the one-entry-point-traced shape that has burned this project
  repeatedly. **Measure it on the rig before building on it.**

- **Never write the host's `simulationTime` value.** The mirror re-replaces the *same* value
  deliberately. Verified at the Entitas source level (`vendor/Managed/Entitas.dll`): `replaceComponent`
  raises `OnComponentReplaced` with no value-equality short-circuit, so a same-value replace does
  collect — and ~20 overworld systems collect on `SimulationTime`. With an unchanged value they run
  at dt = 0. Writing the host's value would run all twenty with a real dt. This is the overworld
  cousin of the standing "never advance `combat.simulationTime` on a client" rule.

**Verification:** two instances, client in the workshop while the host drives, client returns to the
map and the base is in the right place; client cannot issue a move order, camp, retreat, or engage a
site; client's clock stays at scale 0 throughout — **including across a host time skip and a client
attempt to camp.**

**⚠️ Known incomplete, by design.** Only the base is mirrored. Patrol positions, site
spawn/destruction, detection state and contract expirations are all `SimulationTime`-reactive and
advance on the host while the frozen client's do not. The recon's "nothing drifts when nobody acts"
was measured with nobody acting; M12a's whole premise is that the host acts. First visible failure:
the host's target expires or a site is destroyed and the client still displays it. M12a is the
passenger seat, not world sync — name the gap rather than discovering it in play.

### The suppression set is four unverified gates (added 2026-08-09, after M12a shipped)

`PassengerGlue` is now the largest concentration of `return false` prefixes in the mod — four of the
eight, against `ExecutionPatches`' one. See [The patch surface](networking.md#the-patch-surface) for
why that matters: a suppression prefix *is* the extension point, so there is no version of it that
cannot be lost, and a patch pass that aborts partway leaves an arbitrary subset of these live.

Two things follow specifically for M12a:

- **Only one of the four has a backstop, and it is a partial one.** The base mirror overwrites a
  client that moved itself, so a dead `OrderMovementToPosition` prefix self-corrects on the next
  mirror write. Nothing corrects a client camp, a client retreat, or a client-side `EnterCombat` —
  and a camp click *re-rolls every contract M12b synchronised*. Those three are exactly where the
  "verify the effect, not the gate" rule needs an instance: the host should notice a client whose
  clock advanced or whose contract set changed, rather than the client being trusted to have
  refused.
- **The greyed button and the prefix are independent mechanisms.** The greying is the
  visible-before-the-fact affordance the milestone principle demands; the prefix is the boundary.
  Neither implies the other is alive, which is the same shape as the standing rule that client-side
  enforcement is UX and the real check lives elsewhere — except that here there is no elsewhere,
  which is the whole argument for detection.

**The patch-surface assertion is owed now, and its number is not stable yet.** M12a took the mod to
36 patch classes over 32 distinct target methods. That figure was written expecting the throwaway
`OverworldProbeGlue` to go with M12a, which would have dropped it to 32 over 30 — two of its three
targets are also `PassengerGlue`'s.

⚠️ **The probe did NOT go, and this paragraph used to say its deletion was "due".** It was swept on
2026-08-21 against its own written exit condition and **kept**; see
[The rig](#the-rig-for-whoever-picks-this-up) below and the probe-sweep section of
`docs/notes/overworld-recon.md`, which is the file that decides this. So do not predicate the count
on the probe's removal. Count the patch set when the assertion is actually written, and expect
M12b's combat-entry work to have moved it again.

---

## M12b — mission-generation authority

**Why it is not optional and not last:** contracts diverge wholesale, so a selected mission would
build a different combat scenario on each machine — breaking the combat path M4–M7 already made work.
The salvage budget also derives from the mission preset, so **every pool in M12d is wrong until this
is fixed.**

**What was measured:** the transferred save contained **no** contract entities (44 files in, 47 after
load), and nothing regenerated while idle.

⚠️ **REVIEW: "generated once, post-load" is a property of that one save, not a law.**
`OverworldUtility.RefreshStandaloneGeneratorEncounters()` has four call sites, and the post-load one
is the least important:

1. `DataHelperLoading.cs:408` — post-load, but **only inside the
   `!IsFeatureUnlocked("feature_base_standalone_ops_unlocked")` branch** (`:398-410`). The 44→47
   measurement records that save's flag state. A save written after the flag is set already carries
   the `scenario_gen_contract_*.yaml` files and will not re-roll them at load.
2. `OverworldPointUtility.cs:451` — **after every combat**, on each machine, with unsynced
   `UnityEngine.Random`. Both machines re-roll divergently after every co-op mission.
3. `CIViewOverworldRoster.cs:1830` — after every sim-lock exit (camp, retreat, skip). See M12a.
4. `ConsoleCommandsBasecrawler.cs:100` — console.

**So a post-load file resync is not a whole-problem answer.** Divergence is re-introduced mid-session
at sites 2 and 3, where a resync means save + transfer + synchronised reload — unacceptable after
every fight. **Generation must be host-only and the result replicated, with the client's refresh
suppressed at all three runtime sites.** One fact makes replication tractable: generator hosts are
**name-addressed** (`"scenario_gen_" + key`, looked up through `IDUtility.GetOverworldEntity(name)`),
so identity survives the machine boundary; the component set to replicate is `CombatGeneratorKey`,
`CombatUnitLevel`, `FactionBranch`, `CombatEscalationLevel` plus the whole `UpdateCombatDescription`
output. **Considered and rejected:** syncing an RNG seed and letting both machines roll — it makes
every future divergence a silent one, and the project's own rule is that a refusal must be visible.

## M12b·1 — into combat together (planned 2026-08-08, reviewed, nothing built)

The last gap before two households can playtest a campaign end to end. **Ship the fight, do not
reproduce it:** contracts diverge, so a client told "start mission X" would build a different battle.
The host writes the loaded combat to `LobbySaveNames.ScenarioSlot`, offers it, and everyone loads the
same bytes — reusing M9's transfer (proven between two real games) and M11d's `TryLoading` callback.

**⚠️ The adversarial pass found a deadlock in the obvious version of this, and it would have cost a
playtest.** Verified by hand:

1. The host's combat edge fires and `HandleCombatEntered` broadcasts `CombatStart` **immediately**
   (`HostSession.cs`).
2. Every client sets `HostIsFighting = true` (`ClientSession.cs:445`).
3. The host then writes the fight and offers it.
4. `HandleScenarioOffer` opens with `if (HostIsFighting) return;` — **every client silently
   declines.** No bytes move, nobody loads, two people watch a spinner.

That guard is right in general: a client should not pull saves mid-fight. This is the one flow where
pulling *is* the point.

**The agreed shape (user's call, 2026-08-08): defer `CombatStart`, and use a separate offer.**

- The host's combat edge no longer broadcasts `CombatStart`. It writes the fight — **from glue, at a
  permitted moment**, because `CanSave(false)` refuses while `combat.isScenarioIntroInProgress`
  (`DataManagerSave.cs:136`), which is set in the same tick that makes `InCombat` true. Polling
  belongs in glue; a Core guard for it would be a branch nothing can reach, and the 100% gate turns
  that into a build failure.
- A **new** offer message carries the fight, so M9's `HostIsFighting` guard is untouched. The byte
  path itself needs nothing: `HandleScenario` has no such guard.
- Clients transfer if needed, load, and report in. `CombatStart` fires only when the entry barrier
  fills — so it keeps meaning "everyone is in the fight, plan your turn", which is what M11's turn
  barrier already assumes.

**⚠️ `LoadGlue` cannot load this save as written.** It requires `LobbyCatalogue.Contains`, and
`IsOffered` deliberately excludes `ScenarioSlot` (`LobbySaves.cs:352-355`), so every combat-entry
load would return `Unavailable` — silently, and it would read as a save problem. **Add a separate
entry point that keeps the digest check and skips the catalogue check.** Do not widen `IsOffered`:
that reopens the M11b trap where a save being rewritten underneath is simultaneously offered as a
campaign.

**Corrections to the first draft, all verified:**

- **`CombatEnteredEvent` already has a producer** — `PbjRuntime.ObserveCombatEdge:117-128` turns every
  `bridge.InCombat` flip into one, the mod pumps it every frame, and the selftest covers it.
  `HandleCombatEntered` is live production code. The draft claimed no producer existed; the grep had
  been scoped to `src/PBAndJ.Mod` and `tools/` and never looked in Core — the recorded grep trap,
  paid again.
- **`OnLoadingInitiate` has a fourth caller**, `CIViewCombatEnd.cs:298`, and of the three briefing
  sites only `:2052` is the campaign path; the other two are `standaloneMode`.
- **`autosave_before_combat` is conditional**, gated on `saveBeforeCombat && CanSave()` and skipped
  entirely on the `loadImmediately` path — so it is not the free complete save the draft assumed.
- **A new route out needs its own patch.** A pbj screen open when the load fires is closed by
  nothing; see [[no-view-stack-in-pb-ui]]. The draft did not mention it.

**Probe before the two humans sit down** — one host restart covers the first three:

1. Log `CanSave(false)` per pump from the combat edge until true, to measure the intro window.
2. Write the combat save at the first permitted pump and inspect it: turn 0? units present?
3. `TryLoading` that entry-moment save with the session live. M3a round-tripped a *mid*-combat save;
   an entry-moment one has never been tried.
4. Fix the `LoadGlue` catalogue refusal **first**, or probe 3 reports `Unavailable` and masquerades
   as a save problem.

**Named, not solved:** a load that dies *after* teardown leaves that player at the main menu,
campaignless but still connected — the completion callback is success-only, so nothing reports it and
recovery needs a reconnect. Decide what that player is told before the playtest, not during it.

## M12b·2 — the write itself, and four wedges the review found (built 2026-08-09)

The host glue that was missing. `HandleCombatEntered` now emits a **`ShipCombatEffect`**, dispatched
to `IPbjGameBridge.ShipCombat()` and landing in `CombatShipGlue.Arm()`; `CombatShipGlue.Tick()` runs
from the `Heartbeat.Update` postfix **immediately after `NetGlue.Pump()`**, polls `CanSave(false)`,
writes `LobbySaveNames.ScenarioSlot` and posts `LocalCombatReadyEvent`.

**Why an effect rather than the glue watching `InCombat` itself.** The pump executes its effects
synchronously, so an ask raised by this frame's combat edge has already armed the glue by the time
`Tick` runs. Ordering by construction rather than by frame timing — the glue cannot report a fight
the session does not yet know about.

**The probe list above is now built in rather than thrown away.** The glue names, once a second,
which of `CanSave`'s refusals is live; that *is* probe 1, permanently. `pbj.ship-fight` forces the
write by hand with no session, which is probes 2 and 3 on one machine. A hand-driven write never
posts to a session — see wedge D.

**The timeout counts machine-paced refusals only.** `CanSave` also refuses while a cutscene, a
tutorial or the debriefing is open (`DataManagerSave.cs:136,144`), and all three are paced by a
human. A first co-op fight is exactly where a combat tutorial appears, so counting reading time
against the 30s clock would fire the ship-failure path — which drops every peer — on the most likely
session there is.

### ⚠️ Four wedges in the merged M12b Core half, all confirmed by hand

Found reviewing the glue plan, not the glue. Each left the turn barrier or the entry barrier
unfillable, which in play looks like "Execute does nothing, for ever".

- **`RemovePeer` cleared every barrier except `combatEntry`.** A peer that dropped while loading the
  fight was awaited for the full 120s — with the host standing in the battle. Fixed beside the
  existing `load.Drop`.
- **Dropping from the entry barrier is not dropping from the fight.** A peer that reported a failed
  entry, or timed out, stayed in the registry: still dealt units by `ParticipantIds`, still awaited
  by the *turn* barrier every turn. `StartCombatForEveryone`'s own comment claimed this was handled;
  it was not. Now `DropFromTheFight` disconnects them with a reason at all three sites — failed
  report, timeout, and the ship-failure arm, where "starting alone" now means alone.
- **`HandleCombatExited` never cancelled `combatEntry`.** A report arriving after the host abandoned
  the fight completed the barrier and broadcast `CombatStart(-1)` from a host in `Lobby`. Cancelled
  now, and `HandleLocalCombatReady` additionally refuses to act outside `Planning`.
- **`ClientSession.Handle`'s default arm throws, and `NetGlue.Pump` turns a throw into "networking
  stopped" for the rest of the process.** So a `LocalCombatReadyEvent` reaching a client — a stray
  `pbj.ship-fight`, or a host that stopped hosting mid-write — cost the session. There is an ignore
  arm now, and the glue posts only while hosting.

### Two things named rather than fixed — ✅ BOTH NOW FIXED (2026-08-16, mod 0.18.0)

- **The combat-retry interregnum.** ✅ **Fixed.** Host retry leaves combat first, so clients got
  `CombatEnd` and an unlocked Execute that did nothing while they were still standing in the loaded
  fight, until the host re-entered and re-shipped. It re-converged without code, but the human saw
  something unexplained.
  **The fix is to hold the lock rather than release it** — `ClientSession`'s `CombatEnd` arm now
  emits `SetExecutionLockEffect(true)`. 🔑 The unlock was not merely cosmetic: `State` goes to
  `Lobby` and `HandleLocalReady` opens with `if (State != Planning) return;`, so the button handed
  back was **already dead and silent** — the same silent-refusal class M10c paid for. A held button
  is a refusal visible before the fact, which is the standing rule. `CombatStart` releases it on the
  host's return; `Bye` and `Reject` release it if they never come back. The log line says so out
  loud: *"host's combat ended — back to the lobby, holding execute until they return"*.
- **A >256 KiB fight re-transfers every time.** ✅ **Fixed.** Above `MaxPartBytes` the host's
  `ReadScenario` splits the content, and the digest mixed each part's *name* and per-file length, so
  the split digest could never equal the unsplit `SaveCatalogueGlue.Digest` the client checks "am I
  already holding this" against. `LoadGlue` compared unsplit-to-unsplit and still loaded, so it cost
  bandwidth rather than correctness. Fights measure ~119 KB today, so nothing in play had reached it.
  **`ScenarioPayload.ComputeDigest` now merges numbered content parts back into one logical
  `content.zip` before hashing**, by part index rather than by list position, tolerating gaps because
  the digest is computed in the constructor ahead of `Inspect`. **A payload that never split is
  digested exactly as before**, so only the previously-broken case moves — but that *is* a semantic
  break between mod versions under an identical layout, which is why `ModVersion` went to **0.18.0**
  while `PbjProtocol.Version` stayed at **6**.

**Residual, accepted:** `SaveFromECS` can return without writing while `DoSave` proceeds to
`SaveData` anyway, so "no throw" is a weaker success signal than it looks. `CanSave(false)` pre-covers
every guard reachable from here, so no failing case was constructed — but this is why inspecting the
written save for turn and units stays a mandatory probe rather than an optional one.

---

**Also unbuilt and belonging here:** what a client experiences when the host starts a mission.
⚠️ **REVIEW: `ScenarioSetupUtility.EnterCombat` is the wrong hook.** It is the *briefing-open*, not
the commit:

- It hard-guards `gameControllerStateCurrent.s != "overworld"` and returns with a `LogWarning`, so a
  client in the workshop — the exact case M12a endorses — **cannot replay it**.
- On success it pushes `basecrawler` and opens `CIViewBaseBriefingV2`. The real commit is
  `OnLoadingInitiate`, fired from briefing confirm (`CIViewBaseBriefingV2.cs:1955,2019,2052`), and
  there is a **cancel path back out** (`ScenarioUtility.cs:683-689`) which also moves the player base.
  Propagating on `EnterCombat` commits the client to a mission the host can still abandon.
- Scenarios with `coreProc.loadImmediately` skip the briefing entirely — **two transition shapes**.
- Its only non-console caller is the event function `OverworldEntityCombat.cs:15`, i.e. the site
  interaction dialog — **which the client's own UI can also trigger** once the mirror puts its base
  in range. Hence engagement suppression in M12a.

---

## M12c — session-owned combat autosaves

Automatic, written by the session, never surfaced to players except after a disconnect. This is the
floor that makes M12d's rollback principle affordable.

- **Rolling per-turn save.** Needs no new permission: the mod already sets `allowCombatSaves = true`
  at load and `CombatSave()` calls `CanSave(false)`. M3a already round-tripped a mid-combat save.
  **Save at the planning phase, never while `combat.Simulating`** — the `Simulating` refusal
  (`DataManagerSave.cs:128`) is state-gated, not player-gated, so planning passes.

  **⭐ The exact moment, found by using it (user, 2026-08-08): snapshot at Execute, after the turn
  barrier fills, before `Simulating` flips.** A combat save **keeps the queued orders for the turn**,
  so that save reloads into a fully-planned turn you can simply press Execute on again. That makes
  the checkpoint *re-executable* rather than merely restorable, and it is the natural shared instant
  — the barrier has just agreed, so every machine is provably at the same point with the same plan.
  It is also the last instant at which `CanSave` still says yes.

  **⚠️ The one thing it does not give you: the same outcome.** The sim is non-deterministic — that is
  why lockstep was ruled out on day one — so replaying the turn produces *an* outcome, not *the*
  outcome. For M12d's rollback that is acceptable and arguably correct under the principle: the
  humans get the decision back, they do not get a guaranteed rerun of the dice. Record it so nobody
  later reads "guaranteed good to reload" as "guaranteed to reproduce". The save is fully
  synchronous on the main thread, so there is no mid-write window. The host should order the write at
  a shared turn boundary so every machine's save is from the same turn. Two corrections from review:
  the two permission facts are **causally redundant** (`allowCombatSaves` is only consulted when
  `playerFacingSave` is true, `:107`, which the `CanSave(false)` path never sets); and `CanSave(false)`
  **also refuses while the debriefing is entered** (`:144`) — no saving from inside the salvage screen,
  which matters for M12d's rollback floor.
- **Resolved-window save.** `ScenarioUtility.cs:3586` sets `CombatResolved(outcome, early)`; the
  window closes at `CIViewCombatEnd.cs:353` / `OverworldCombatCompletionSystem.cs:25`.
  ⚠️ **REVIEW: mostly answered from the decompile, and the answer is no.**
  - **`CombatResolved` is not serialized** — zero hits in `DataHelperSaveSerialization.cs`. A save
    taken in that window records game state `combat` with the outcome dropped on the floor. It
    **cannot** restore into the debriefing unless the mod carries the outcome itself.
  - **`OnAfterCombatSaveUnchecked` has no callers in code** (`DataHelperLoading.cs:502` is the
    definition and the only hit), so it is not the sanctioned pattern the draft claimed; if it runs at
    all it is invoked by name from scenario config, in `overworld`, *after* salvage has committed.
  - **`DoSave`'s first line is `Co.StopAndClear(ref delayedSaveCoroutine)`** (`DataManagerSave.cs:426`),
    so a mod `DoSave` here **cancels vanilla's pending `autosave_after_combat`**.

  **Decision: drop the resolved-window save.** The per-turn save already made it optional; the review
  turned it from unknown-cost into known-negative. Rollback lands on the last planning-phase save and
  replays the turn.
- **⚠️ Reserved names, excluded from the lobby catalogue.** The M11b trap exactly: `pbj_combat_test`
  sat inside the namespace the catalogue claimed and would have been offered as a campaign while
  being rewritten underneath. A save rewritten every turn is the same shape and worse. `LobbySaveNames`
  owns the names; **reserve the unprefixed form** or the guarding arm is unreachable and breaks the
  100% gate while letting the colliding input through.
- `previewScreenshot: false`, off the critical path — this runs every turn. ⚠️ **REVIEW: the
  screenshot is the small half of the cost.** Every write is synchronous on the main thread and does
  a full `SaveFromECS`, ~47 YAML file writes, a whole-directory zip to temp plus copy-back, and then
  **`RefreshSaveHeaders`, which re-enumerates and YAML-parses `metadata.yaml` for every save in both
  the Normal and Internal folders** (`DataManagerSave.cs:551-556, 604-640`) — a cost that scales with
  the player's lifetime save count, not with this save. Measure the stall on the rig before committing
  to every turn; per-N-turns is the fallback.
- **`DoSave` mutates two globals every write:** `SettingUtility.autoSaveTimer = 0` and
  `DataManagerSave.saveName = <slot>` (`:433-434`). Anything reading "the current save name" — the
  game's or ours — sees the rolling slot afterwards. Digest-safe, though: the zip excludes
  `metadata.yaml`, so `DateTime.Now` stays out of content digests.
- **Accepted tradeoff:** surfacing a resume only on disconnect invites deliberate drops. Judged a fair
  price for robustness against accidents. Recorded so it is not re-litigated.

---

## M12d — assigned gear, and the salvage screen

**Ownership rides the save — for parts only.** `customTags` is a free-form `HashSet<string>`,
mutable at runtime and round-tripped through the save: written at
`DataHelperSaveSerialization.cs:1313-1321`, restored at `DataManagerSave.cs:2744-2746` (and the
restore survives the version-regeneration branch). A `pbj_owner_<id>` tag on a **part** reaches every
client through the transfer M11e already performs. ⚠️ Prefix with `pbj_`: the namespace is live,
`CombatDamageSystem` reads `flag_no_damage` and `flag_no_loss`. Nothing clears or rebuilds tags, and
every reader is a `Contains`, so an unknown tag is inert.

✅ **MEASURED 2026-08-08, and the refutation holds: 606 subsystem tags in, 0 out.** `pbj.mg-tag`
tagged **65 parts and 606 subsystems** in ECS; one save to `mod testing` and one load back, and the
probe read **parts 65, subsystems 0**. It survived a game restart too — the next session's first
probe read the same 65/0. This is no longer a decompile inference; it is a run.

⚠️ **REVIEW — and this is the worst failure shape in the plan: `DataBlockSavedSubsystem` has no
`customTags` field at all.** Its eight fields are `serial`, `blueprint`, `livery`, `destroyed`,
`fused`, `salvageable`, `inventoryAdded`, `favorite` — no tags, and `CreateSavedSubsystem` never
reads them. **A `pbj_owner_` tag on a subsystem works in ECS all session and is silently dropped by
the next save — and the transfer to a client *is* a save.** Subsystems are a large share of the base
inventory and of the salvage list. "No new message type, no side table" is true for parts and false
for subsystems.

**⭐ The domain shape, from the user 2026-08-08 and confirmed in the commit loops
(`EquipmentUtility.cs:1837-1901`): a subsystem is either a guaranteed standalone drop or a rider on a
part in the keep/scrap/skip list.** The code says it precisely:

```
foreach part in GetPartsInUnit(unit):
    if part.isSalvageable:
        ProcessSalvageOfEntity(part, 1f, ..., destroyWithoutSelection: false)
        continue                                   // <-- riders follow the part
    foreach subsystem in GetSubsystemsInPart(part):
        if subsystem.isSalvageable: ProcessSalvageOfEntity(subsystem, 1f, ...)
```

- **Riders need no ownership of their own.** When the part is salvageable it is processed and that
  `continue` skips its subsystems entirely — they are neither listed nor priced separately, they
  simply follow the part's decision. So ownership *is* part ownership, stored in the tag that
  provably round-trips. No extra machinery, and this is the common case.
- **Standalone drops are the inventory loops** (`GetPartsInInventory` / `GetSubsystemsInInventory`,
  `costMultiplier: 0f`, `transfer: true`, `destroyWithoutSelection: true`) — free, auto-selected at
  creation, kept unless explicitly skipped. These are the loose subsystems that genuinely need
  attribution, and **because they price at 0 they never touch the pool arithmetic.**
- **One edge case worth naming:** a subsystem riding a part that is *not* salvageable becomes
  individually selectable and priced at `1f`. Attribution still resolves through its parent part even
  though that part cannot itself be claimed.

**So the subsystem tag loss does not touch the salvage budget at all.** It touches who owns free
drops and loose inventory items — a smaller and cheaper problem than the 606→0 number suggested.

**The way through, in preference order — the identity survives even though the tag does not.**
`DataBlockSavedSubsystem` persists `serial`, and the restore passes it straight back
(`DataManagerSave.cs:1905` → `CreateSubsystemEntity(blueprint, livery, inventoryAdded, serial)`). So
a subsystem is still *nameable* across the transfer; only the place to write the owner is missing.

1. **Inheritance — subsystems take their parent part's owner.** A subsystem is either installed in a
   part (`GetEntitiesWithSubsystemParentPart`) or loose in inventory. Installed ones need no storage
   at all: the part already carries a tag that provably round-trips. This covers the large majority
   and costs nothing.
2. **A side table keyed by `(kind, serial)` for LOOSE inventory subsystems only.** Small, and it
   rides the transfer we already perform. ✅ **MEASURED 2026-08-08: serials are stable across the
   round trip, both kinds.** `pbj.mg-serials` before and after a save/load returned identical
   fingerprints — parts `count 77 | min 1300 | max 2840 | sum 195248`, subsystems
   `count 658 | min 445 | max 10011 | sum 4696955`. The side table is sound.

   **That run also settled why the key needs the kind.** The two counters sit far apart but their
   ranges **overlap**: parts span 1300–2840 while subsystems span 445–10011. Cross-kind collision is
   not a theoretical consequence of two counters, it is the arithmetic of these two live ranges.
3. **Scope ownership to parts and treat loose subsystems as shared** — acceptable only if measurement
   shows loose subsystems are rare in practice.

**Not an option: reusing a field that already persists.** `livery` is a persisted string on the saved
subsystem and is the obvious place to smuggle an owner id. It drives the item's appearance, so
writing to it corrupts what the player sees — the same class of mistake as the `customTags`
namespace, but visible.

**Scope note from the first live salvage screen:** it contained **zero** subsystems across 12 groups,
so this may not touch the salvage path at all. It remains fully live for the base inventory, where
the same probe counted 606.

**Identity:** ⚠️ **REVIEW: `serial` alone is not a key.** There are **two independent counters** —
`serialPartLast` and `serialSubsystemLast` (`DataHelperStats.cs:14-16`, `:200-210`) — both seeded
from the save's maxima, so a part and a subsystem routinely hold the *same* serial, and the salvage
list mixes both kinds. **The key is `(kind, serial)`.** It is also not stable across a round trip: a
part whose `version < generationVersionExpected` is rebuilt by `CreatePartEntityFromPreset` with a
**fresh** serial (`DataManagerSave.cs:2717-2725`). The rest of the draft's claim holds — the
allocator is a per-process counter, so anything minted after the split collides across machines, and
salvage mints items. **The host assigns the identity clients quote back.**

**⚠️ REVIEW — there is no shared list to pick from.** Salvage rewards are generated *per machine*,
post-combat, with `UnityEngine.Random`: `PrepareRewardsForSalvage` and its neighbours
(`EquipmentUtility.cs:1381+`) call `GetRandomEntry`, `Random.Range`, `RollRandomQuality`. Two
machines roll **different items, ratings and levels**.

**✅ SOLVED, and by a mechanism the game already ships — `savedOutput`. Do not seed the RNG.** The
chain, all read 2026-08-08:

1. **The game pre-rolls the loot at scenario generation, not after combat.**
   `ScenarioSetupUtility.UpdateCombatDescription` — which also takes a first-class **`scenarioSeed`**
   parameter — ends by calling `GenerateRewardOutputs` for every reward group (`:368-375`), writing
   concrete resources, parts (preset + level + rating) and subsystems (blueprint) into
   `group.savedOutput`.
2. **`savedOutput` rides the save.** The whole `CombatDescription` is cloned into the save through
   YAML (`DataHelperSaveSerialization.cs:1219-1222`), and `CombatRewardGroupSavedOutput` is a plain
   serialisable class hanging off it. **M11e's transfer already moves it — no new message type.**
3. **Post-combat generation is skipped when it is present.** `EquipmentUtility.cs:1153-1156` routes
   to `PrepareRewardsFromSavedOutput` instead of rolling.

**So M12b subsumes this: replicate the contract and the loot comes with it.** One mechanism, both
problems — which also removes the reason to treat M12d's reward set as separate work.

**⚠️ The one gap: `savedOutput` carries no serial.** `CombatRewardSavedPart` is `{level, rating,
preset}` and `CombatRewardSavedSubsystem` is `{blueprint}`, and `PrepareRewardsFromSavedOutput` calls
`CreatePartEntityFromPreset(...)` / `CreateSubsystemEntity(blueprint)` **with no serial override**
(`:1216, :1236`) — so each machine mints its own. Identical content in identical order from
counters seeded by the same save *will* mint matching serials, but that is an alignment to verify,
not to rely on: any other minting on one machine (a workshop craft) drifts the counters and every
later serial disagrees. **Keep "the host assigns the identity clients quote back"** as the belt to
this braces.

**Considered and rejected: seeding `UnityEngine.Random`.** It is mechanically available —
`Random.InitState`, and the game itself uses the capture/seed/restore idiom
(`ActionProjectionSystem.cs:1047-1236`, `WeaponCustomizerSimple.cs:48`) — but `UnityEngine.Random` is
process-global and shared with VFX, audio and every other system, so anything consuming a draw
between the seed and the roll desynchronises it, and the *call sequence* must match too, which
requires province level, faction branch and quality tables to already agree. It re-derives where
`savedOutput` transports. Re-derivation fails **silently**, which is precisely the failure mode this
milestone's principle exists to design out.

**The salvage screen:** the budget splits into **equal pools, one per present player, remainder
discarded**. Everyone picks from the (replicated) shared list, spending only their own pool. **Every
change is broadcast, and another player's picks show as `reserved`** — a refusal before the fact.
**Nobody leaves until all confirm.**

⚠️ **REVIEW: the vanilla backstop the pool split was justified by does not exist.**
`ProcessSalvageSelections` ends with a `Debug.LogWarning` — *"…letting the process continue…"*
(`EquipmentUtility.cs:1903-1905`), not a refusal. The only enforcement is UI-side,
`salvageCostValid = salvageCostTotal <= salvageBudgetLast` gating the finish button
(`CIViewOverworldDebriefing.cs:2368-2370`). **Pool enforcement is 100% mod code**; the integer-division
remainder is still worth keeping, but as tidiness, not as a safety net.

⚠️ **REVIEW: the budget is not a stable function of the mission preset.** `salvageBudgetLast` is one
`int`, but it is computed **once at screen open** (`CIViewOverworldDebriefing.cs:2111-2210`) from
preset × difficulty multiplier + per-salvage-group offsets + base and per-surviving-unit
`salvage_bonus` + world-memory modifiers — **two of which are consumable and removed as the screen
opens**. Reopening the screen yields a different budget. So **the host computes the pool number once
and broadcasts it**; machines must never derive it independently. M12b is necessary for this but not
sufficient.

⚠️ **REVIEW: `destroyWithoutSelection: true` does the opposite of what the draft assumed.** Every
generated reward is auto-selected at creation (`ReplaceSalvageSelection(newDismantle: false)` at
`EquipmentUtility.cs:1217, 1239, 1527, 1605`), and the flag is passed `true` only on the
`costMultiplier: 0f` site-inventory calls (`:1844, :1862`) and `false` on the priced unit-mounted ones
(`:1891, :1898`). So a player who never touches the screen **transfers their site loot for free**;
the flag destroys only items whose selection was explicitly removed by the UI's skip. Unselected
wreck parts are lost later via `FreeOrDestroyCombatParticipants`, a different mechanism. **"Silence
destroys loot" is wrong.** The rollback principle still stands, but the real disconnect hazard is
different and simpler: `FinishDebriefing` is click-driven, so **a machine that never clicks never
commits at all.**

**The commit is per-machine**, and the single funnel is real (one call site,
`CIViewOverworldDebriefing.cs:2825`): each machine runs its own `SalvageFinish` over its own local
entities, so the host cannot commit on everyone's behalf through it. The mod must merge selections
and have every machine commit symmetrically — which means a satisfied `SalvageBarrier` has to drive
each machine's debriefing from outside its own flow.

✅ **MEASURED 2026-08-08 — the whole flow is drivable, end to end, with no reflection into a private
method.** Driven entirely from the console on a live debriefing:

| Step | Entry point | Result |
| --- | --- | --- |
| Select an item | `OnSalvageRecover/Scrap/Skip(entityID)`, public static | selection `<none>`→`True`, the **view's own** `salvageCostTotal` 0→5 |
| Select a whole unit | `...Unit(unitID)`, public static | same, via the other redraw path |
| Advance the stage | `OnStageNextExternal()`, public static | reached the game's own confirmation modal |
| Commit | `buttonConfirm.callbackOnClick.Invoke()`, public `UICallback` | `Requesting province ownership refresh` → `Attempting to transfer salvage from generic_industry_1 (P-ID 22) to player base \| Last budget: 253 \| Victory: True`, both screens exited |

The commit is gated behind `CIViewDialogConfirmation`, whose `callbackOnConfirm` **is** `SalvageFinish`
(`OnSalvageFinish:2762`). `OnConfirm` is private, but the button's `callbackOnClick` is a plain
public `UICallback` and `Invoke()` is public — so the last link needs no private access. **M12d is
unblocked.**

**Design consequence worth keeping:** that modal is a natural home for the barrier. "Nobody leaves
until all confirm" can use the game's own confirmation dialog as the per-player confirm, with the
barrier releasing the `Invoke()` on each machine once everyone has agreed.

**Also outside the funnel:** `TransferStandaloneRewards` (`EquipmentUtility.cs:1735`, from
`CIViewOverworldReward.cs:157`) is a second, budget-free transfer path for non-combat reward popups.
Ownership tags must follow it too.

**Two corrections from the first live salvage screen (2026-08-08, 12 groups, budget 253):**

- **`costMultiplier` is not just 0 or 1.** The player's own recovered frame
  (`workshop_utl_unit_frame_2`) priced at **0.4** while every enemy unit priced at 1.0. So pool
  arithmetic must use each group's own multiplier, and "free site inventory" is not the only special
  case. `GetSalvageCost` applies the multiplier only when `< 1f` and returns 0 when `<= 0f`.
- **That screen's salvage list was parts-only — zero subsystems across all 12 groups.** So the
  subsystem tag loss above, while real, may not bite the *salvage* path at all; it remains fully live
  for the base inventory, where the same probe counted 606 subsystems. It also means this sample
  could not exercise cross-kind serial collision (reported 0), so `(kind, serial)` stands on the
  two-counter read, not on this measurement.
- Scrap and recover are **separately priced** (`costDismantle*` vs `costTake*`, each with an
  intact/damaged variant), so both move the budget — a scrap is not a free action.

**What the game does supply, all confirmed:** `salvageBudgetLast` is one `int`; selection is a
`SalvageSelection` component per entity carrying `dismantle`, and it is what the screen reads
(`CIHelperEquipmentSelectorSalvage.cs:167-182`); price is `GetSalvageCost` — note site-inventory items
are priced at **zero** (`costMultiplier: 0f` → `EquipmentUtility.cs:1622-1624`), so only unit-mounted
salvage spends a pool at all; and the commit funnel
`EquipmentUtility.ProcessSalvageSelections(host, inventory, budget, victory)` is genuinely the only
one.

**This is the third barrier in this codebase, not a new pattern.** `TurnBarrier` and `LobbyBarrier`
already encode "everyone must agree", and a `SalvageBarrier` inherits their traps: a departing peer
must not silently satisfy it, and the trigger must be an **edge, not a level**.

**On a disconnect:** roll back per the principle. Never forfeit, never redistribute, never
auto-recover. ⚠️ **REVIEW: the justification changes, the conclusion does not.** The concrete hazard
is not that silence destroys loot — silence *transfers* free site loot and leaves priced salvage to a
later, different mechanism. It is that `FinishDebriefing` is click-driven, so **a machine that never
clicks never commits**, and the barrier's real failure mode is a stall. Rollback to M12c's last
planning-phase save is what makes that stall survivable.

**Divergence does not stop at the commit.** `FinishDebriefing` then runs `customExitBehaviour`
functions, `ClearActionsAfterCombat`, `TryToDestroySite` and a province-ownership refresh, per machine
(`CIViewOverworldDebriefing.cs:2780+`). Even perfectly merged selections diverge afterwards unless
each of those is deterministic given synced inputs. **Unmeasured — check before declaring M12d done.**

---

## Sequencing

⚠️ **REVIEW: the first draft's independence claims were too generous.**

`M12a` is the visible win and is *mostly* independent — but its suppression of camp and retreat is
what stops a client re-rolling every contract, so **M12a is a prerequisite for M12b holding**, not
merely parallel to it.

**`M12b` gates `M12d`**, and by more than the budget: the salvage *item set itself* is rolled per
machine, so M12d needs replicated reward generation on top of replicated contracts.

`M12c` is code-independent of both, but its acceptance test — two machines resuming the same campaign
combat turn — needs shared combat entry, which is M12b's unbuilt transition. Until then M12c is
verifiable only through the M9 console flow. It should still land before `M12d`.

**`M12d` is the largest, and is no longer gated on open question 3** — the flow is measured drivable
end to end. It shrank twice over on 2026-08-08: `savedOutput` folds the reward-set problem into
M12b's contract replication, and the riders/standalone split means most subsystem ownership is
inherited from parts rather than stored.

## The rig, for whoever picks this up

`tools/game-instance.sh <N>` (was `second-instance.sh`) — prefixes `553540-pbj2` and `-pbj3` are both
set up. **Two instances is the ceiling and the script enforces it**, and the Steam-launched instance
is not one of the pair: it cannot be driven by the control channel. The Steam *client* must still be
running, because `SteamAPI.Init` failing is a hard quit. Drive either with
`tools/drive.sh <N> "<command>"`.
Steam's Play button becomes **Stop** once a manual instance registers, and it then controls that
instance, so start the Steam host first. Both instances share one `Mods` directory by symlink, so one
`make deploy` serves both; deploy with both closed. A second concurrent `SteamAPI.Init()` on the same
appid is verified to work.

The overworld probe (`pbj.ow-probe`, `-sample`, `-mirror`, `-watch`) is still registered, and it
**stays**.

⚠️ **This paragraph used to say it should be deleted when M12a lands, because "every finding is
already in `overworld-recon.md`". Both halves were wrong.** M12a landed long ago, and the findings
were not all there — that sentence predates `ProbeNightfall`. The probe was swept on 2026-08-21
against its own exit condition ("delete it once every finding is in `docs/notes/overworld-recon.md`")
and kept, for two reasons recorded in that file's probe-sweep section: the nightfall chain is still
not transcribed into it, and the probe is the instrument for the measurements it lists as unrun.

**`docs/notes/overworld-recon.md` decides when this probe can go, not this document.** Read the
verdict there rather than acting on anything said here — deleting the file on the strength of a
design doc would take a live instrument with it.

## Still unanswered

1. ~~Whether a save taken in the `CombatResolved` window restores into the debriefing.~~
   **Answered by review: no** — `CombatResolved` is not serialized. The window save is dropped from
   M12c.
2. ~~Whether `customTags` survives the save round-trip in practice.~~ **ANSWERED 2026-08-08 by
   running it.** Parts survive; subsystems do not — 65 parts in and out, 606 subsystems in and **0**
   out, across one save/load and again across a game restart. M12d's ownership scheme works for
   parts and needs a different mechanism for subsystems.
3. ~~Whether the management UI can be driven from outside its own flow.~~ **ANSWERED 2026-08-08:
   yes, end to end** — selection, stage advance, confirmation modal and commit, all from the console,
   all through public entry points. See the table in M12d. The `TryLoading` precedent held: the same
   question about the load screen was also a no-problem, and again one console command settled it.
4. Claim granularity for the general inventory outside salvage — per `(kind, serial)` (now measured
   stable, and cross-kind overlap makes the `kind` mandatory), or a coarser lease.
5. **New:** whether the basecrawler→overworld transition actually delivers the mirrored position,
   given that `PopUntilState` clears pending collectors (M12a).
6. **New:** what a per-turn save actually costs in main-thread stall, given `RefreshSaveHeaders`
   re-parses every save's metadata (M12c).
7. **New:** whether `FinishDebriefing`'s post-commit work is deterministic given synced inputs
   (M12d).
