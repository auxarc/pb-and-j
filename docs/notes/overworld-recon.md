# Overworld recon — what a campaign does when nobody is driving it

**M12 recon, single-party pass run 2026-08-07 on a running game** (mod 0.9.0, game 2.2.2-b8339,
save "TWO POINT OH BABY"). Driven by the throwaway `pbj.ow-*` commands in
`src/PBAndJ.Mod/Net/OverworldProbeGlue.cs`. **That probe is scheduled for deletion** — when it goes,
this file is the only copy, exactly as `ngui-surface.md` is for the UI probes.

Everything below marked ✅ was observed on a running game. Everything marked ⏳ is still a
decompile read or is unrun, and must not be built on.

---

## ✅ The campaign clock is stopped unless something is driving it

`docs/design/campaign-coop.md` justified the host-only tactical map with "the overworld runs a
*continuous* clock". **That is wrong**, and the correction is not "the clock runs while travelling"
either — that was the first draft's overcorrection. Measured:

| State | `simulationTimeScale` | `simulationTime` over 60 s | `Time.timeScale` |
|---|---|---|---|
| Idle in `overworld`, base stationary | **0.000** | **frozen** at 620.9059, 60/60 samples | 0.000 |
| Travelling | 1.000 | 621.07 → 636.91, then froze when travel ended | 1.000 |
| Time skip (`ow.sim-lock`) | **20.000** | 696.9 → 756.97 in ~9 s | **2.000** |
| In `basecrawler` (management) | 0.000 | frozen | **0.660** |

So the accurate statement is: **the campaign clock advances only while the base is travelling or a
time skip is running.** A time skip is a *stationary* base with a fast clock, which is why the
`isBaseMoving` reading alone is not the whole rule.

**`Time.timeScale` is not a proxy for the campaign clock.** It reads 2.000 during a 20× time skip —
`OverworldSimulationTimeSystem` clamps it to `[0, 2]` — and 0.660 in `basecrawler`, where
`BaseCrawlerTimeSystem` owns it. Read `overworld.simulationTimeScale` for the campaign rate and
`Time.timeScale` for nothing at all.

### ✅ A stopped clock is NOT a stopped game — the autosave proves it

`SettingUtility.autoSaveTimer`, sampled across the same runs:

- idle in `overworld` at scale 0 — **flat at 0.0** for 60 s
- travelling — 0.3 → 32.1
- during the time skip — 38.1 → 44.1
- **in `basecrawler` at scale 0 — 50.0 → 63.25**

That last row is the one that matters. `OverworldTimedAutosaveSystem:15-25` accumulates on
`TimeCustom.unscaledDeltaTime`, and its zero-scale early return also demands
`IsGameState("overworld")` — so sitting in the management screens keeps the timer running and will
eventually **fire a real save** with the campaign clock stopped. It is both a divergence source
between two machines and noise in any save-diff measurement.

---

## ✅⚠️ The state split, and the limit it puts on "watch the host drive"

`GameController:177-183` registers the systems across separate states, and the two halves of base
rendering do not live together:

- `PositionLinkSystem` (renders the base) → `OverworldSystemsPermanent`, always active
- `OverworldRangeSystem` (the bridge that feeds it) → `OverworldSystems`, **only in state `overworld`**
- the management screens → state `basecrawler`

Observed stack in the management screens: `game gameLate mainmenu overworld basecrawler` — additive,
with `basecrawler` on top. `overworld` is *in* the stack but its systems are not ticking.

**Measured consequence** — the same mirror write, run twice:

| Run in | `position` after write | `positionDetectedLast` (what renders) | `isPositionUnchecked` |
|---|---|---|---|
| `overworld` | (5.0, **13.3**, 5.0) | **caught up within 1 s**, clock at scale 0 | cleared |
| `basecrawler` | (10.00, 13.32, 10.00) | **stuck at (5.00, 13.32, 5.00)** | still True |

So: **a client can be driven around the map by an external write with its clock stopped — but only
while it is looking at the map.** In the workshop the write lands in the ECS and renders nothing,
and the ground-snap does not run either. The user's goal of "full UI access *and* watch the host
drive" is therefore not two states at once; it is **catch-up on returning to the map**, which the
overworld row shows works, since the position is already correct when the bridge starts ticking again.

Note the Y coordinate: the overworld write passed y=33.3 and the base settled at 13.3.
`isPositionUnchecked = true` → `OverworldPositionValidationSystem` snapped it to the ground. **Send
X and Z; let the receiving machine find its own Y.**

---

## ✅ The mirror recipe works, and it is the game's own

Cribbed from `ConsoleCommandsOverworld:893-901` rather than invented, and confirmed to propagate to
the rendered transform **with the campaign clock at scale 0**:

```
OverworldUtility.StopMovement(base);
base.ReplacePosition(v);
base.ReplacePositionTarget(v);      // NOT optional — OverworldMovementSystem:254-272 drags
base.isPositionUnchecked = true;    //   Position back toward a stale PositionTarget
overworld.ReplaceSimulationTime(overworld.simulationTime.f);   // self-replace kicks the collectors
```

The rendered value does **not** move in the same frame — `OverworldRangeSystem` is reactive, so it
lands on the next tick. A caller that reads back immediately will conclude, wrongly, that it failed.

---

## ✅ View singletons are dormant, not absent

At the **main menu, before any campaign is loaded**, all five checked views are already constructed:
`CIViewBaseWorkshopV2`, `CIViewOverworldRoster`, `CIViewOverworldRoot`, `CIViewOverworldNav`,
`CIViewPauseRoot` — every one non-null, none entered. Same finding as M11's load probe made for the
load screen: **views persist and are built up-front, not on demand.**

In `overworld`: Roster, Root and Nav entered. In `basecrawler`: Root and Nav still entered, Roster
not.

## ✅ `CanSave()` in the states the two-instance measurement needs

True in idle `overworld` and in `basecrawler`; false at the main menu. The decompile's other refusals
(`DataManagerSave:94-149` — during a time skip via `hasSimulationLockCountdown || isCloaked`,
debriefing, tutorial, loading, combat) were not each exercised.

---

---

## ✅ Base control has ONE funnel, and it identifies the player base itself

`pbj.ow-watch`, run in the overworld across map clicks, time-scale taps and a mission start. All
three patches fired; nine events, no misses:

- **`OverworldUtility.OrderMovementToPosition` — 6 hits, every one `playerBase True`.** Every player
  movement order went through it, matching the three static call sites
  (`CIViewOverworldNav:1060`, `CIViewOverworldProcess:854`, `OverworldMoveToOrderSystem:128`).
  **This is the suppression point for M12a: one Harmony prefix, not a sweep.** It is also where the
  clock restarts — the method unpauses from `simulationTimeScaleBackUp` directly, which is why the
  movement hits carry no accompanying `SetPreferredTimeScale`.
- **`OverworldTimeUtility.SetPreferredTimeScale` — 3 hits** (0.00, 0.00, 1.00), from the time-scale
  buttons. A separate entry, but note it routes through `RefreshTimeScale`, which derives the scale
  from `isBaseMoving` — so on a client that cannot move, the time buttons are **already inert**.
  `CIViewOverworldRoot.ForceTimeScale:291-307` is the bypass that would still need closing (⏳ not
  exercised here).
- **`ScenarioSetupUtility.EnterCombat` — 1 hit, in state `overworld`.** Observable, and the hook
  point for propagating a host-initiated mission start to a client.

---

## ✅ Two real game instances on one machine — it works

Verified 2026-08-07. `tools/second-instance.sh` builds and launches the rig; both of the things the
plan refused to record as fact are now observed:

- **A second concurrent `SteamAPI.Init()` on the same appid SUCCEEDS.**
  `Steam | Initialized | App ID: 553540` in the second instance's own log, process stable past the
  title screen, no `SteamAPI_Init() failed` and no `Shutting down because RestartAppIfNecessary`.
  This was the single unverified step the whole rig rested on — the game hard-quits on a failed init
  (`Heartbeat.cs:13-15` → `SteamHelper.cs:68-73`), so there was no degraded mode to fall back to.
  `SteamAppId`/`SteamGameId` plus a `cd` into the game directory with `steam_appid.txt` present is
  what satisfies it.
- **Wine follows a host symlink for the Mods directory.** The second prefix's
  `AppData/Local/PhantomBrigade/Mods` is a symlink at the first prefix's, and the second instance
  loaded `pb-and-j / 0.9.0` through it. One `make deploy` therefore serves both instances, which is
  what makes a `ModVersion` mismatch between them structurally impossible.
- **The second instance resolves its own user folder** (`Saves:`, `Settings:` under its own prefix),
  so the two do not fight over the save folder or `Player.log`.

⚠️ **Do not read "the mod loaded" as "the instance survived."** `Application.Quit()` takes effect at
end of frame, so `ModManager` loads the mod even on the failing path. The evidence is the
`Steam | Initialized` line and a process that is still alive, not the mod banner.

---

## ✅ M11 verified two-party, and 62 KB crossed between two real games

Run 2026-08-07 on the rig, with the client's copies of every `pbj_` campaign **deleted first** so the
transfer could not pass for the wrong reason. Host selected `pbj_fromsp`; from the two logs:

- `offered scenario 'pbj_fromsp' (62,076 bytes, 32a3ae4e) to peer #1` → client
  `requesting the host's scenario (32a3ae4e)` → `received | 2 files, 62,076 bytes` → `written`.
  **M9's byte transfer between two real games, unproven since it was written, is now proven.**
- **`Unavailable` never appeared** — M11e's transfer-on-selection did its job.
- `lobby 2/2 ready` → `loading 'pbj_fromsp' on 2 machine(s)` → `load OK` from both →
  `load complete | 2 of 2 machine(s) are in`. M11d's synchronised load, two-party.
- Both machines set the multiplayer-campaign bit.

Two cosmetic defects found in passing, neither affecting the run:

1. The client logs `scenario written to 'pbj_fromsp' — run pbj.combat-load to enter it`. M9-era
   wording leaking into the campaign flow; nobody should run `pbj.combat-load` here.
2. `this campaign is multiplayer — saves will stay in the pbj_ namespace` is logged **twice on both
   machines**, suggesting `MultiplayerCampaign.Enter` fires twice. Worth chasing before M12a builds
   on that bit.

---

## ⭐ The measured divergence — and the one that actually threatens the design

Both machines loaded the same bytes, then each drove its own base. `save drift1`, three untouched
minutes, `save drift2`, all four `content.zip`s extracted and diffed.

**Nothing drifts when nobody acts.** Intra-machine, `drift1` → `drift2` differs in exactly one file
(`world.yaml`) and only in `timeReal` fields — wall-clock counters, not campaign state. 47 of 48
files byte-identical after three idle minutes, on **both** machines. This confirms the stopped-clock
finding independently of the probe, at the level of the serialised save.

**Cross-machine, after independent driving: 5 files of 47.**

| File | What differs |
|---|---|
| `OverworldEntities/internal_mobilebase.yaml` | base position, rotation, `world_auto_time_*` |
| `world.yaml` | `time` (624.2073 vs 624.9213), base position, `timeReal` |
| `scenario_gen_contract_generic_01/02/03.yaml` | **752 / 663 / 894 differing lines** |

⚠️ **The contracts are the finding.** They are not perturbed — they are *different missions*:
`areaKey: main_military_camp_02` vs `_01`, `biomeKey: sandy_03` vs `sandy_02`, different
`spawnKeyPlayer` and spawn groups, across 50–87% of each file.

And the mechanism is now pinned down: **the transferred save contains no contract entities at all.**
Source save = 44 files, post-load = 47 (+3, exactly the contracts). They do **not** regenerate while
idle (host `drift1` ≡ `drift2`). So each machine **rolls its own missions once, after the load**, and
the rolls disagree.

**Why this outranks base position for M12a.** A divergent base position is a cosmetic mismatch the
mirror in §"the mirror recipe" already fixes. A divergent contract means that when someone selects a
mission, the two machines build **different combat scenarios** — different map, different spawns —
which would break the combat path M4–M7 already made work. Mission generation therefore has to be
host-authoritative or resynced, and that is a constraint on M12a's design, not a polish item.

This is also direct evidence for the design doc's open question 4 (write-back cadence): the save
transfer already carries everything that diverges, and a post-load resync of these five files would
close the whole gap without a per-entity protocol.

---

## ⭐⭐ Per-unit ownership is NOT sufficient — measured, two machines, first attempt

The design doc's answer to open question 1 was "the likely answer is the one combat already uses —
per-unit ownership, host validates". **That is refuted.**

Protocol: both machines in one loaded campaign, host edits **Longbow** (`pb_mech_01`), client edits
**Guardian-9** (`pb_mech_02`) — *different* units, the case the doc treats as safe — then both
`save edit1`, extract, diff.

What a single loadout edit touches, isolated by an intra-machine diff (`drift2` → `edit1`):

| File | Change |
|---|---|
| `Units/pb_mech_0X.yaml` | the edited unit — **correctly confined**, one file per editor |
| `OverworldEntities/internal_mobilebase.yaml` | **the shared parts inventory**, 245 changed lines |
| `core.yaml` | `state: overworld` → `basecrawler` — an artefact of saving from the management screen |
| `world.yaml` | time |

**The mobile base entity holds the parts inventory, and equipping moves an item out of it.** So a
loadout edit is never confined to a unit; it always mutates shared state.

**The collision, on the first try, with no attempt to provoke it:** both machines removed
**serial 2249, `wpn_heat_repeater_01`** from the base inventory. Host equipped it on Longbow; client
equipped it on Guardian-9. One physical item, two owners, neither machine aware.

```
host:   serial 2249 -> pb_mech_01 (Longbow)
client: serial 2249 -> pb_mech_02 (Guardian-9)
```

**Consequences for M12a, all of them load-bearing:**

- Per-unit ownership partitions `Units/` perfectly — that half of the doc's instinct is correct and
  the measurement confirms it. **The conflict is not in the units.** It is in the pool they draw
  from, and no per-unit rule can see it.
- The two saves **cannot be merged**. One edit has to lose. That means a **claim protocol** — a
  client asks the host for item `2249`, the host grants or refuses, and the client's UI has to be
  able to *show* a refusal — not a replication protocol.
- This is the scenario co-op exists for (two players kitting out their own machines at the same
  time), so it is not an edge case to defer.
- ⚠️ It also means an optimistic client-side edit is a lie the moment two people are in the
  workshop. The M10c lesson applies directly: **a silent success is indistinguishable from a silent
  failure**, and here the failure is invisible until a save is compared.

**Round 2 (both machines editing the same unit, Longbow) touched the identical two files** —
`Units/pb_mech_01.yaml` and `internal_mobilebase.yaml`. The same-unit case adds nothing the
different-unit case did not already show, which is the point: **the worst outcome came from the
configuration the design assumed was safe.**

## ⏳ Still unrun — do not treat as answered

- **Whether the management UI can actually be driven (measurement 5) — PARTIAL, and the first
  reading of it was a measurement error.** Both passes watched `CIViewBaseWorkshopV2`, saw
  `entered: False` throughout, and concluded the management UI had never been opened. **It had.**
  `CIViewBaseWorkshopV2` is the *crafting* workshop; the mech loadout screen is
  `CIViewBaseLoadout`, and that is what was open. The probe now reports the whole surface —
  `CIViewBaseLoadout`, `CIViewBaseParts`, `CIViewBaseInventory`, `CIViewBasePilots`,
  `CIViewBaseCustomizationRoot`, `CIViewBaseWorkshopV2` — all six of which expose an `ins`
  singleton. What remains genuinely unmeasured is what a loadout **change** writes, and whether any
  of it works with a live session. Left for the two-instance rig, where that condition is reachable.

  ⚠️ **The general trap:** picking one class as a proxy for a whole feature and reporting its
  absence as the feature's absence. The screen was on the user's monitor while the log said it was
  closed.
- **Divergence between two machines (measurement 2)** and **concurrent edits / host-initiated combat
  entry (measurement 6)** need the two-instance rig.
- **`Time.timeScale` flicker.** `OverworldSimulationTimeSystem:178-187` sets `Time.timeScale` from
  `simulationTime` rather than the scale on every `ReplaceSimulationTimeScale` — a game bug read in
  the decompile. Not observed here, because nothing in this pass wrote the scale.

## Traps — readings that looked settled and were not

- **"The overworld runs a continuous clock."** Written into the design doc as fact and used to
  justify the whole host-only-map split. Measured: 60 consecutive samples with the clock frozen.
- **"`ReplacePosition` renders, like combat's `PositionLinkSystem`."** The overworld
  `PositionLinkSystem` collects on **`PositionDetectedLast`**, not `Position`
  (`PositionLinkSystem:17,32-35`). Writing the component the name suggests would have rendered
  nothing, and the basecrawler row above is what that failure looks like.
- **`pgrep -f PhantomBrigade.exe` reports the shell that runs it**, because the pattern matches its
  own command line. It said the game was running when nothing was. Bracket the pattern
  (`"[P]hantomBrigade.exe"`), as `tools/second-instance.sh` now does.
