# Replay handoff (M8) — reconnaissance

Paraphrased names/signatures only, same discipline as the other notes files. No game code is
reproduced here.

`docs/design/networking.md` §"Replay handoff (M8)" was written before `CombatReplayHelper` was read
end to end. This file records what a full reading found. **It corrects the design doc in several
places, and it also corrects a first draft of itself** — an adversarial review overturned five of six
findings in that draft, and each overturned item is kept below as a marked trap, because they are
the readings a careful person arrives at and they are wrong.

Everything below was verified against the decompiled source directly. Line numbers are
`decompiled/CombatReplayHelper.cs` unless stated.

---

## 1. The playback pose write is UNGUARDED — and it is not the function it looks like

There are two pose-applying functions and it is easy to study the wrong one.

- **`ApplyUnitPose` (`:1879`) is teardown-only.** Its single caller is `RestoreUnitForExecution`
  (`:781`), on the `SetReplayActive(false)` path. It has a length guard
  (`joints.Length == recordedBones.Count`, `:1895`) and skips silently when it fails.
- **`ApplyTimeToUnit` (`:1083`) is the per-frame playback path**, and its pose loop (`:1164–1243`)
  has **no length guard at all**. It takes `count4 = recordedBones.Count` (`:1168`) and indexes
  `joints[l]` / `joints2[l]` straight up to that bound (`:1181–1185`).

So during playback:

| Client bone list vs recorded joints | What happens |
|---|---|
| Client **longer** | `IndexOutOfRangeException`, every frame, inside the driver |
| Client **shorter** | Truncated write, silently, and misaligned if the order also differs |
| Same count, different order | Elbow onto knee, every frame |

The pose block is additionally gated on **`keyframesPoses.Count > 2`** (`:1165`), so a track with only
its start and end keyframes animates nothing at all, silently.

> **TRAP (first draft got this wrong).** Studying `ApplyUnitPose` leads to "a count mismatch is a
> silent whole-pose skip, so a length check defends nothing we would notice." That is exactly
> backwards for the path that matters: a mod-side length check is what stops a per-frame exception.
> Validate against `ApplyTimeToUnit`, not `ApplyUnitPose`.

## 2. Mechs and tanks use DIFFERENT visual managers, and the design doc analyses the tank one

Both implement `IUnitVisualManager`; `visualManager.GetRecordedBones()` dispatches between them.

- **`UnitVisualManagerSimple`** — used by `CombatTankAnimationView`. Its `RefreshRecordedBones`
  (`decompiled/UnitVisualManagerSimple.cs:2062–2150`) is the one the design doc describes: string-keyed
  `jointsLookup` dictionaries, a provably unreachable skinned-mesh block (`:2097–2104` — the guard
  requires `bones.Length == 0` and the loop then runs to `bones.Length`), and unnamed positional leg
  joints.
- **`UnitVisualManager`** — used by `CombatMechAnimationView` (`.visual`, `:14`). Its list
  (`decompiled/UnitVisualManager.cs:558–594`) is composed completely differently:
  1. `jointTorsoParentSpace`, if present;
  2. `body.bones[]` — the skinned mesh's bind order — filtered to exclude any name containing
     `"auto"` or `"finger"`, and the exact name `"joint_root"`;
  3. `jointWeaponLeftLocalReference`, then `jointWeaponRightLocalReference`, each if present;
  4. every `jointSyncLinks[].joint`.

**M8 is about mechs, so (2) is the list that matters.** This is better news than the design doc
implies. There is no dictionary-enumeration-order problem for mechs, and every entry is a **named
skeleton Transform**, so `transform.name` is a natural, stable identity key — no composed
`v{i}:{key}` / `leg{i}:{role}` scheme is needed. Two caveats: names must be unique within a unit
(verify, do not assume), and the filter is name-based, so a renamed bone in a future game build
shifts every index after it.

> **TRAP (first draft got this wrong).** The draft asserted the `UnitVisualManagerSimple`
> composition applied to mechs and designed a key scheme around it. Wrong class entirely. It also
> concluded from `ApplyUnitPose`'s `hasMechAnimationView` guard that tanks can be recorded but not
> posed — false, because playback goes through the interface, not that function. Tanks pose fine.

## 3. `CombatReplayHelper.holder` does NOT gate replayed VFX

`SetReplayActive(true)` calls `ins.holder.gameObject.SetActive(true)` (`:582`), and the class declares
`holderAssetsProjectiles` (`:48`) and `holderAssetsStandalone` (`:50`).

**Those two fields are dead.** They appear nowhere in `decompiled/` or `decompiled-firstpass/` outside
their own declarations. Nothing is ever parented under them.

Replayed assets live under the pool's own holder instead: `ApplyTimeToActiveAssetTracks` activates
with `parent: null` (`:1494`), and `AssetPoolUtility.ActivateInstance` only reparents when a parent is
supplied. Standalone tracks reparent to their own recorded parent or fall back to world space;
projectile and beam tracks set world transforms.

**So skipping `SetReplayActive` costs nothing in VFX.** The "call the pieces, not the whole" strategy
survives on this point.

> **TRAP (first draft got this wrong).** The draft called this "the most important correction" and
> concluded the holder probably gated all replayed VFX, pushing toward the `SetReplayActive(true)`
> fallback. The field names strongly suggest it. Two greps refute it.

## 4. The `SetReplayActive(true)` fallback does not exist on a client

Worth knowing precisely because §3 makes it unnecessary — but the design doc offers "just call
`SetReplayActive(true)` and accept its UI" as a legitimate cheaper stepping stone, and **on a client
that is a silent no-op.**

`SetReplayActive` returns immediately unless `IsReplayAllowed()` (`:577`), which needs all four of:

| Condition | On a client |
|---|---|
| `activationAllowed` | **False.** Set true at exactly one place, `:417`, inside `OnExecutionEnd` — which never runs on a client |
| UI mode is `Unit_Selection` | True |
| scenario `coreProc.replayUsed` | True with a shared save |
| `feature_combat_replay` unlocked | Depends on campaign progression |

The design doc's table claims `activationAllowed` is "True — set at `OnExecutionEnd`, which is
exactly when playback would start". That is host reasoning applied to a client. Reaching this path
means reflecting into `activationAllowed` anyway, at which point the piecewise approach is simpler.

## 5. `pauseUpdates` IS required — the FinalIK hazard does not need `LateExecute`

`LateUpdateUnit` runs manual FinalIK solves that write bones regardless of `animator.enabled`, and it
is gated only on `view.pauseUpdates` and `animator.gameObject.activeInHierarchy`
(`MechAnimationSystem.cs:164`). It has **two** entry points, not one:

1. `LateExecute` (`:137`) — gated on `lateExecuteUnconditional || lateExecuteRequested`. Both
   initialise false; the latter is set only by the reactive `Execute` (`:1231`) whose trigger is
   `CombatMatcher.SimulationTime` (`:69`).
2. **`UpdateAnimationsForAll` calls `LateUpdateUnit` directly** (`:97`), per unit, and the
   non-reactive `Execute` calls `UpdateAnimationsForAll(Time.deltaTime)` every frame whenever
   `!combat.Simulating && Time.timeScale > 0f` (`:130–133`).

On a client `Simulating` is always false, so path 2 hangs entirely on **`Time.timeScale`, which has
still never been observed on a client**. `pauseUpdates` is checked in both `LateUpdateUnit` (`:164`)
and `UpdateUnit` (`:1254`), so it is the **only single switch that blocks both paths**. Set it.

Still true and still worth avoiding: `SetReplayActive(false)` sets `lateExecuteUnconditional = true`
on its way out (`:648–652`; `experimentalUpdate` is false by default, `:58`), arming path 1 for the
rest of the combat.

> **TRAP (first draft got this wrong).** The draft traced only `LateExecute`, found both flags false
> on a client, and concluded the hazard was dormant and `pauseUpdates` merely belt-and-braces. It
> missed the direct call at `MechAnimationSystem.cs:97`. Tracing one entry point and stopping is how
> this project has been bitten before.

## 6. Playback writes to the ECS, which breaks the "presentation only" invariant

`ApplyTimeToUnit` (`:1288–1304`) walks the **client's own** equipment entities, derives a destruction
progress from its local `isWrecked` / `destructionTime`, and calls **`item.ReplaceDestructionProgress(...)`**
— a real component write — plus `visualManager.OnSocketDestructionChange(...)`.

Two consequences:

- The design doc's stated invariant that playback is "presentation only — view transform, never ECS"
  **is not true of the game's own playback path.** Either accept the write (it is a visual-progress
  float, and `DestructionProgress` is not a digest input — confirm that) or patch around it.
- Part destruction during playback is driven by **local** ECS state, not by the recording. If a
  client's equipment is never marked wrecked, replayed units never show parts breaking no matter
  what the host recorded. The recorded `keyframesDestructions` track is written (`:1914`) and **read
  nowhere** — dead, as is `keyframesEffectSliding` (`:1985`).

## 7. `ApplyTime` mechanics

Confirmed, and worse than first thought:

- `ApplyTime` is a **private instance** method (`:955`) reached through the **private static** `ins`
  (`:20`) — reflection needed for both.
- Its early-out compares `timeAppliedLast` against the static `previewTime` (`:957`), not the
  `timeRequested` argument. Worse, `:1080` stores `timeAppliedLast` as **turn-local** time
  (`max(0, timeRequested - turnStartTime)`, `:962`) while `:957` compares it against an **absolute**
  value. The units do not match, so for `turnStartTime > 0` the check essentially never fires.
  **Pass `timeCheck: false` and drive our own cursor.** Do not write `timeAppliedLast` externally —
  `ApplyTimeToLevel` (`:1334–1341`) reads it as local time to detect rewinds.
- It **clamps every request to `previewTimeLimit`** (`:961`), which defaults to **5f** (`:74`) and is
  otherwise set only in `OnExecutionEnd` (`:347`). `turnStartTime` (`:72`) is set only in
  `OnExecutionStart` (`:240`). **A client must seed both**, or playback is clamped to a 5-second
  window starting at zero.
- It already calls `CIViewCombatTimeline.ins.OnTimeChange(...)` itself; the design doc lists that as
  a separate element for us to drive, which would call it twice a frame.
- It dereferences `CIViewCombatExecution.ins`, `CombatStrikeHelper.ins`, `CombatSceneHelper.ins` and
  `PostprocessingHelper` (`:963–974`, `:1079`). Any null in a client's execution HUD throws inside
  the per-frame driver.
- `ApplyTimeToUnit` also dereferences `combatView.view` unguarded (`:1090`) and needs a linked
  persistent entity (`:1085–1088`).

## 8. Wire feasibility — yes, with an exclusion list

The recorded tracks are almost all plain data (ints, floats, strings, `Vector3`, `Quaternion`):
transform, pose, state, trail, beam, projectile keyframes; level snapshots and damage; simulated
structures; melee trajectories; UI popups. The live references that cannot cross a process boundary:

- `ReplayKeyframeUnitLight.firingTransform` — a `Transform`, consumed at `:1264`.
- `ReplayEntityAssetStandalone.parent` — a `Transform`; send `parentPresent = false` and it falls
  back to world space.
- `ReplayAdvancedParticleBlock.holder` / `systemRoot` / `systemsFull` / `systemEmissionModules` —
  re-resolved host-side in `OnExecutionEnd` (`:369–390`) by integer index, so re-resolvable
  client-side the same way. **Transferring `presimulated == true` with a null holder NREs** inside
  `Simulate`; send it false or re-resolve first.
- `AssetLinker` references are playback-transient and never part of recorded data.

Entity-ID coupling: `units` is keyed by combat entity ID and resolved through
`IDUtility.GetCombatEntity` (`:1010`); popup text carries `unitCombatID` (`:1046`). Projectile and
beam dictionary keys are **not** resolved during playback, only in teardown (`:702`, `:727`). So
**only unit IDs have to correspond across the two processes** — which is what the shared-save
requirement already buys.

## 9. Two ways received tracks get destroyed

- **`ClearData()`** is called by `CombatScenarioSetupSystem`
  (`decompiled/PhantomBrigade.Combat.Systems/CombatScenarioSetupSystem.cs:105`) and
  `TeardownCombatSystem` (`:56`). Tracks injected before scenario setup finishes are wiped.
- **If the client's combat context ever gains the `Simulating` flag**, `CombatUILinkSimulationStart`
  (trigger `Simulating.Added`, gated on `ScenarioUtility.predictionEnabled`, default true) calls
  `OnExecutionStart` on whatever machine it fires on: sets `recordingAllowed = true` (`:235`), stomps
  `turnStartTime` (`:240`), and **appends client-local keyframes into the existing `units` entries**
  (`:281–311`), corrupting the received tracks in place.

## 10. Smaller things

- Vanilla replay runs at `playbackSpeedTarget = 0.6f` (`:625`), not 1×. Choose our rate deliberately.
- `CombatReplayHelper.Update` (`:479`) requires `activeLast` to be true, so with the piecewise
  approach it will not drive anything — our own cursor must.
- `RestoreUnitForExecution` applies only the **last** pose keyframe (`:777–786`).
- Host-side pose density comes from `OnUnitSnapshot` callers, so ≥3 pose keyframes is normal; a
  degenerate one-sample turn silently animates nothing (see §1's `> 2` gate).

---

## Measured — `pbj.replay-probe`, host, 2026-08-03

Two passes on one instance: a 12-unit combat at turn 0 before any execution, and the same combat at
turn 1 after executing. Four of the five open questions are now answered from a running game rather
than a reading.

### `Time.timeScale` is 0 — and already 0 before anything executes

| | before execution | after execution |
|---|---|---|
| `Time.timeScale` | **0** | **0** |
| `simulationTime` / `currentTurn` | 0 / 0 | 5 / 1 |
| `Simulating` | False | False |
| `lateExecuteUnconditional` / `lateExecuteRequested` | False / False | False / False |

The interesting reading is the **first** column. `timeScale` is already 0 at turn 0, before the host
has ever passed through the `Simulating→false` transition that `SimulationTimeSystem` uses to zero
it. So combat entry itself leaves `timeScale` at 0, by a path a client also takes when it loads into
combat.

**Therefore the FinalIK hazard of §5 is almost certainly dormant on a client after all** — not for
the reason the first draft gave (which traced only one of two entry points and was rightly refuted),
but because the *other* entry point is gated on `Time.timeScale > 0f` and that is measurably 0 from
combat entry onward. Both `LateExecute` flags are also False in both passes.

**Still set `view.pauseUpdates = true`.** It is one assignment, it blocks both paths unconditionally,
and the inference above is from the host's value at combat entry rather than from a client's during
playback. Cheap insurance against the one part still unmeasured.

### Bone names are unique, and the count does not vary with loadout

Every mech: `UnitVisualManager`, **26 bones, 26 distinct, 0 duplicates, 0 nulls** — across
`w1_sr_shotgun`, `w2_mr_kinetic`, `w1_sr_smg`, `w1_mr_ar_shield`, `w3_sr_shotgun_shield`, the two
player mechs and both workshop frames. Every tank: `UnitVisualManagerSimple`, **3 bones**.

```
joint_torso_parentSpace, joint_pelvis_xyz, joint_torso_xy, joint_head_xy,
joint_right_arm_xyz, joint_right_forearm_x, joint_right_hand_palm_xyz,
joint_left_arm_xyz,  joint_left_forearm_x,  joint_left_hand_palm_xyz,
joint_right_thigh_xyz, joint_right_leg_x, joint_right_foot_xyz,
joint_right_foot_front_x, joint_right_foot_tongue_x, joint_right_foot_heel_x,
joint_left_thigh_xyz,  joint_left_leg_x,  joint_left_foot_xyz,
joint_left_foot_front_x,  joint_left_foot_tongue_x,  joint_left_foot_heel_x,
joint_left_weapon_local_xyz, joint_right_weapon_local_xyz,
joint_left_weapon, joint_right_weapon
```

Two consequences, both good:

- **`transform.name` is a sound identity key.** Zero duplicates anywhere in the sample.
- **The list looks loadout-independent** — 26 for every mech regardless of weapons or shield. That
  makes the count mismatch that would throw in §1's unguarded loop much less likely than feared.
  Not proof: `jointSyncLinks` contributes to the tail and simply did not vary here. Keep the key
  remap; it is now insurance rather than the load-bearing defence.

Tanks are recorded *and* posed (`joints=3` tracks appear in the volume table), which confirms §2's
correction — they are not excluded from playback.

### Volume: 253 KB per turn for 12 units

47 pose keyframes per 5-second turn. **34,216 bytes per mech per turn** (47 × 26 × 28), 3,948 per
tank. **253.2 KB total across 12 tracks.** No `RAGGED` markers — joint array lengths are consistent
within every track.

The design doc estimated ~44 KB per unit and ~1.5 MB for a 30-unit combat. Measured is ~34 KB per
*mech*, and tanks are an order of magnitude cheaper, so a 30-mech fight lands near ~1 MB. Still over
`MaxFrameLength` and still needing the per-unit chunking already planned — but this is a comfortable
size for a payload that arrives during the host's planning phase, and M9 already moves 65 KB
scenarios over the same path.

Also visible: `advParticles=3` on every mech, so the `ReplayAdvancedParticleBlock` holder
re-resolution of §8 is live work, not hypothetical.

### The `ApplyTime` singletons are all non-null

`timeline`, `execution`, `strike`, `scene`, `timeControl`, `postprocessing` — all `ok`, in both
passes, in the execution HUD. Guard the call site anyway, but §7's fear does not materialise here.

### `activationAllowed` behaves exactly as §4 predicts

`False` before execution, `True` after; `IsReplayAllowed` follows it `False → True`. On a client
`OnExecutionEnd` never runs, so it stays False forever and `SetReplayActive` stays a silent no-op.
`IsRecordingAllowed` is `False` even after execution, which re-confirms the M6-era rule that capture
must not gate on it.

Note `previewTimeLimit` reads **5** and `turnStartTime` **0** after execution — consistent with a turn
that ran 0→5, and confirming both are *turn-dependent* values a client must be told, not constants it
can assume.

---

## Measured on a real CLIENT, 2026-08-14 — the last question falls

Two game instances, a shared campaign, a shared fight and a shared turn, driven end to end through
`tools/playtest-m12b.sh`. The client received the host's keyframes and played them back; a sampler
in `ReplayProbeGlue`, pumped from the `Heartbeat` postfix and gated on `KeyframePlayer.IsPlaying`,
read `Time.timeScale` across **exactly** the playback window:

```
replay-probe playback window | frames=577 timeScale min=0 max=0 framesAboveZero=0
  simulating=False session=CLIENT | FinalIK hazard dormant — pauseUpdates is insurance
```

**577 frames, `timeScale` 0 on every one of them, on a genuine client.** Not one frame above zero.

So **the FinalIK hazard of §5 is dormant, measured rather than inferred.** `MechAnimationSystem`'s
non-reactive `Execute` is gated on `!Simulating && Time.timeScale > 0f`, and the second conjunct is
never true on a client during playback — so `UpdateAnimationsForAll` never runs, `LateUpdateUnit` is
never reached by that path, and the manual FinalIK solves never fight replayed bone writes.

**`view.pauseUpdates = true` still gets set.** It is one assignment, it closes both entry points
unconditionally, and it costs nothing. The measurement downgrades it from load-bearing to insurance;
it does not remove it. Sampling min/max rather than a single reading was deliberate: the hazard is
"was it *ever* above zero", and one non-zero frame would have been enough.

Also confirmed in the same run: **M6 keyframes reaching a real client through the M12b fight path** —
8 tracks, 384 keys, 5.00 s of motion, broadcast by the host and received intact.

---

## The Stage 3 adversarial pass, 2026-08-14 — it collapsed the design choice

Seven claims put up for refutation before the driver was written. Two came back refuted, three
partly. The two headline corrections were re-verified by hand before being acted on.

### The choice between "drive `ApplyTime`" and "write the bones ourselves" was not a choice

`units` is written in exactly one place, `OnExecutionStart` (`:293-294`), whose only caller is
`CombatUILinkSimulationStart.cs:69` — triggered by the ECS `Simulating` flag a client never gains.
`OnUnitSnapshot` only appends to entries that already exist (`:1782-1785`). So on a client the
dictionary is empty and `ApplyTime`'s unit loop (`:1002-1017`) iterates nothing: driving the game's
scrubber means **fabricating its own `ReplayUnit` graph** into a static §9 already says two systems
destroy.

And it would not even save the work. **`SleepPuppet` is reachable only through
`SetReplayActive → PrepareUnitForReplay`** (`:616`, `:762`, `:834`), which is precisely the call §4
shows a client cannot make. A client driving `ApplyTime` would have its bone writes overwritten by
its own idle animation exactly as a hand-rolled driver would. **`ApplyTime` is strictly more work for
the same problem**, so M8 writes the bones itself.

There is a middle path if the vanilla presentation is ever wanted: `ApplyTimeToUnit` is
`private static` (`:1083`), so it needs one reflected `MethodInfo` and no `ins`, no
`turnStartTime`/`previewTimeLimit` seeding, no singletons, no clamp and no tint. It still carries the
ECS write and the unguarded pose loop, and it still needs the sleep set.

### `pauseUpdates` is on `CombatMechAnimationView`, and it is load-bearing again

`public bool pauseUpdates` is declared at `CombatMechAnimationView.cs:16`, **not on `CombatView`** —
every earlier note in this file writing `view.pauseUpdates` meant `MechAnimationSystem`'s local mech
view. Getting that wrong does not fail to compile if you reach for the wrong `view`.

More important: the 2026-08-14 measurement downgraded it to insurance on the strength of
`Time.timeScale` being 0, and that is **too narrow**. `LoadingEnd2` schedules forced
`animatorUpdateManual` ticks at +0.5 s and +2.5 s after a save load (`DataHelperLoading.cs:427-435`)
which **bypass the `timeScale` gate** (`MechAnimationSystem.cs:125-129`); `UpdateAnimationsForUnitForced`
has further callers in `ScenarioUtility` and `AddCombatViewSystem`. All of them funnel through the
`pauseUpdates` checks at `MechAnimationSystem.cs:164` and `:1254`, and **no game code anywhere ever
sets `pauseUpdates = true`**, so nothing will flip ours back. Set it, and set it *before* the first
frame of playback rather than alongside — the window between resolving a unit and silencing it has
to be zero.

### PuppetMaster maps bones in LateUpdate, with no `timeScale` gate at all

The one bone-writer neither design accounted for. `PuppetMaster.LateUpdate → OnLateUpdate`
(`RootMotion.Dynamics/PuppetMaster.cs:678-754`) has no fixed-frame or timeScale guard in
`UpdateMode.Normal` (`:718-723`), and its mapping block (`:726-739`) calls `Muscle.Map`, which writes
**target bone transforms** (`Muscle.cs:525+`).

A functional mech is safe by accident: `OnUnitGetUp` puts it in `Mode.Kinematic`
(`UnitUtilities.cs:2651-2652`) with `mappingBlend = 0` (`PuppetMaster.cs:404`), so `MoveToTarget`
touches rigidbodies only. **A crashed or wrecked unit is `Mode.Active, State.Dead`**
(`UnitUtilities.cs:2628-2629`) with mapping weight 1 — every bone we write, overwritten, every frame,
regardless of `timeScale`. Vanilla closes this with exactly the half a hand-rolled driver is tempted
to skip: `PrepareUnitForReplay` hides the puppet view (`:755-760`) and `SleepPuppet` deactivates the
`puppetMaster` and `puppetBehaviour` GameObjects unconditionally (`:850-857`).

**Deactivate those two holders; skip only `Disable/EnableRagdollPhysics`.** The GameObject toggles
store nothing, so nothing can make them unrecoverable — whereas the physics-map halves are where the
crash of §"the puppet-wake crash" actually lives.

### The puppet-wake crash is latent, not unconditional

`EnableRagdollPhysics` carries the **same** functional/crashing/GetUp early-outs (`:925-934`) as
`DisableRagdollPhysics` (`:894-907`), so the unguarded `puppetPhysicsMap[...]` index at `:939` is only
reached when that state **changes between sleep and wake**. Still real on a client — the host's
snapshot lands mid-window — and still a reason to keep our own bookkeeping rather than the game's.

### `CIViewCombatExecution.ins` is assigned in `Awake`

`:93-97`. So "the view is not entered on a freshly-loaded client" never implied a null singleton, and
§7's per-frame NRE fear is smaller than it reads. What `ApplyTime` really costs at `:971-974` is
poking the timeline, execution, strike and scene helpers every frame — side effects, not exceptions.

### `turnStartTime` is rounded, and the turn slice was comparing against it

`turnStartTime = Mathf.RoundToInt(GetSimulationTime())` (`:240`), while the recorder stamps **raw**
simulation time. So a turn that overruns its rounded boundary leaves the **previous** turn's closing
keys satisfying `time >= turnStartTime` too, and the slice drags them in — arriving non-monotonic in
the middle, which is a unit jumping backwards mid-window.

Within a turn the stamps never decrease, so **the last place they do decrease is exactly the seam.**
Capture now takes the backward scan and then pushes forward to that last descent, for the transform
track as well as the pose track. This was a latent defect in **shipped M6 code**, not a new one.
Residual, recorded and not guessed at: a boundary that rounds *up* would put the turn's own opening
stamp above its first samples. Never observed — every measured turn has landed on an exact second.

### Confirmed, and it is the claim the whole milestone rests on

**A client's `GetRecordedBones()` is populated.** `UnitVisualManager.Awake → CheckInitialization`
(`UnitVisualManager.cs:341-344`) builds the list from pure prefab wiring (`:558-594`) with no
dependency on recording, simulating or ECS state, and `GetRecordedBones()` just returns the field
(`:2249-2251`). Awake fires when `AddCombatViewSystem` instantiates the view, which demonstrably
happens on a client — M6 playback already moves those views. Had this been false, both designs were
dead.

Also confirmed: the four weapon/palm Transforms are `public` fields on `CombatMechAnimationView`
(`:331`, `:333`, `:349`, `:351`) in Assembly-CSharp, so the palm sync needs no reflection; and the
capture-order claim holds — `CombatUISystems` is registered at `CombatSystems.cs:72` and
`CombatExecutionEndLateSystem` at `:93`, so `OnExecutionEnd` has already appended the window's final
pose key by the time our hook runs, and capture must not append another.

---

## Measured on a running game, 2026-08-14 — M8 works, and a standing mystery falls

One instance, `pbj_combat_test`, hosting with no peers, one turn executed and replayed through
`pbj.replay-last`.

```
[pb-and-j] turn 0 poses | 8 unit tracks | broadcast to 0 peers
[pb-and-j] turn 0 keyframes | 8 tracks, 408 keys | 0.00s-5.00s | broadcast to 0 peers
[pb-and-j] turn 1 poses complete | 8 unit tracks | playing the battle
replay=turn1/8posed → 300 frames → replay=idle
```

**8 of 8 units captured and dressed**, no `poses partly uncaptured`, no `poses dropped`, no
`replay driver failed`, no exception, clean unwind. Six screenshots across one playback window show a
mech's legs changing configuration continuously — mid-stride with one leg forward and one back, then
together, then striding again — while turning. **The units walk.** The argument is closed by the
driver itself: `animator.enabled` is false on every dressed mech for the length of the window, so
nothing but our bone writes can be moving them; had the writes been inert the mech would have been a
frozen statue.

Corroborating detail worth keeping: a mech that barely moved that turn barely changed pose, while one
that crossed the field animated heavily. An idle loop would have bobbed both identically.

### ✅ AND ON TWO REAL GAMES, 2026-08-14 — the wire half

`up → session → lobby → fight → turn → again`, fully scripted, no human keypress:

```
host    turn 0 poses | 14 unit tracks | broadcast to 1 peer
client  turn 0 DIVERGED | host 301af20c | local 1a220e29
        turn 0 corrected | 17 units | digest 301af20c OK
        turn 0 keyframes received | 14 tracks, 700 keys | 5.00s of motion
        turn 0 poses complete | 14 unit tracks | playing the battle
        turn 1 corrected | 17 units | digest 5d8a5ad1 OK
        turn 1 poses complete | 14 unit tracks | playing the battle
```

Two consecutive posed turns, complete sets both times, both windows unwound, no driver exception, no
malformed frame, no dropped peer. **The second turn is the one that matters** — a first can look
flawless and still leave every unit asleep, because the symptom only appears when something else
tries to animate them.

### ❌ NO BULLETS ON A CLIENT, AND THAT IS BY DESIGN — not a defect

M6 carries transforms, M8 adds bone poses. **Projectiles, beams, muzzle flashes and impact VFX are
none of it.** Asked during the first two-instance playtest and worth writing down, because "the
mechs walk but nothing shoots" reads like a half-broken feature rather than a scope line.

Every recording path for them is gated on `recordingAllowed` — `OnProjectileEnd` (`:1610`),
`OnBeamTransform` (`:1656`), and the rest — which is only true during the **host's** simulation. So
`assetsProjectiles`, `assetsBeams` and `assetsStandalone` are as empty on a client as `units` is.

**This cost the driver choice nothing.** Driving the game's `ApplyTime` would have iterated those
same empty collections (`:975-1001`) exactly as it would have iterated an empty `units` — no bullets
either way without new wire work.

What that work is, if it is ever wanted, is already enumerated in §8 above: the tracks are almost all
plain data, and the live references that cannot cross a process boundary are
`ReplayKeyframeUnitLight.firingTransform`, `ReplayEntityAssetStandalone.parent` (send
`parentPresent = false` and it falls back to world space) and the `ReplayAdvancedParticleBlock`
holders (re-resolvable by integer index; **`presimulated == true` with a null holder NREs**). Volume
has never been measured. Treat it as its own milestone, not a Stage 5.

### 🐛 A REAL DEFECT THE PLAYTEST FOUND, AND IT IS NOT M8's — visibility never crosses the wire

Reported by eye during the two-instance run: *an enemy mech is on the host and not on the client.*
The logs place it precisely, and it is an M5/M7-era gap that M8 merely made visible.

| turn | host snapshot | host pose tracks | client corrected |
|---|---|---|---|
| 0 | 17 units | **14** | 17 units, digest OK |
| 1 | 17 units | **14** | 17 units, digest OK |
| 2 | 17 units | **17** | 17 units, digest OK |
| 3 | 17 units | **17** | 17 units, digest OK |

The recorder skips hidden units — `!combatEntity.isDestroyed && !combatEntity.isHidden` (`:283`) —
and nothing was destroyed, since the snapshot held at 17 throughout. So **three units were hidden on
the host at turns 0–1 and became visible by turn 2.**

`UnitSnapshot` carries name, position, rotation, integrity, `IsDead` and `DeathTime`. **It has no
visibility flag**, so `isHidden` is never replicated. A client's copy is whatever the scenario save
froze at turn 0 and nothing ever updates it: the flag is written by the host's vision systems, and a
client does not simulate. An enemy the host has spotted therefore stays invisible on the client for
the rest of the fight.

**The digest still matches**, which is what makes this so quiet — `StateDigest` hashes name, position
and integrity, none of which visibility touches, so correction reports `OK` every turn while the two
machines show different battlefields. Note also that **M8 is doing its part correctly**: the pose
tracks for those units do arrive (14 → 17), the client simply will not draw the units.

Fix, when it is wanted: a visibility bit on `UnitSnapshot` and an apply on the client. That moves a
wire layout, so it owes a `ModVersion` bump. Its own piece of work, not a Stage 5.

### ⚡ THE COMBAT HUD IS NOT ENTERED UNTIL A UNIT IS SELECTED

`docs/design/networking.md`, the M8 plan and the drive-rig notes have all carried
"`CIViewCombatExecution` is NOT entered on a client that has just loaded into a fight, and why is
unexplained". **It is unit selection.** Loading a combat save selects nothing, so the HUD never
enters and `pbj.execute` refuses with "the execution view is not open". Pressing **F1–F4** snaps the
camera to a player unit and the whole combat UI comes up; `pbj.execute` then answers
"execute pressed — readied through the barrier".

Not client-specific and nothing to do with the network: it is a property of arriving in combat by
**loading a save** rather than by deploying through a briefing. A client following a host into a
fight arrives exactly that way, which is why it looked like a client bug.

**Fixed with `pbj.select-unit <index>`**, which calls `CIViewCombatMode.OnUnitSelectionByIndex` — the
very method the F1–F6 bindings call (`InputCombatShared.cs:153-157`), the player's own path rather
than the `ReplaceUnitSelected` beneath it. It reports the resulting selection rather than that it
pressed, because both that method and the `OnUnitClick` under it return void and refuse silently.
Verified on a running game: `selected unit 0: pb_mech_01`, and `pbj.execute` immediately went from
"the execution view is not open" to "execute pressed — readied through the barrier".
`pbj.ready` remains the lever that needs no view at all.

### ⚠️ Do not drive anything the moment `game-wait.sh` returns

`game-wait.sh` waits for the **drive channel**, which opens at `OnLoadEnd`. The launch **splash** —
studio logos, then a seizure warning that needs an Enter press or waits out its own 30-second timer
(`CIViewSplashScreen.seizureWarningTotalTime`) — is still up long after that, **and the game already
reports `state=mainmenu` underneath it**. A `pbj.combat-load` sent into that gap loads combat *and
then* the still-pending intro runs `CIViewPauseRoot.TryEntryAsMain`, dropping the title menu on top
of the loaded battle:

```
Combat game state enabled
...
TryEntryAsMain | Intro skip: False
View View_Pause (CIViewPauseRoot) | Entering (visible) | Enabling 6 colliders
IntroStart
```

The drive channel reports `state=combat` throughout, so this is invisible to every scripted
assertion and looks from the outside like a game that never left the menu. Re-issuing the load
afterwards clears it (`CIViewPauseRoot | Exiting...`).

`pbj.drive-state` now carries **`splash=`** and **`menu=`** for this. ⚠️ **Wait on `menu=True`, never
on `splash=False`** — measured, and the reason is the shape of the bug this file keeps finding:
`splash` reads false **before** the view enters as well as after it leaves, so a poll that starts at
launch passes on its very first sample and drives a game whose intro has not run. `menu` is
monotonic in the direction that matters. Observed sequence, five seconds apart:

```
splash=False menu=False      <- the trap: an await on splash=False stops HERE
splash=True  menu=False      (x7, ~35s)
splash=False menu=True       <- actually ready
```

⚠️ And a screenshot cannot be trusted to notice: KWin serves the **last rendered frame** for a window
that is minimised or occluded, so a capture showed the title menu long after the game had loaded,
executed a turn and returned to planning. Confirm from the log or the drive channel, never from a
picture alone.

## Still open

1. ~~A client's `Time.timeScale` during actual playback~~ — **answered above: 0, across 577 frames.**
2. ~~Mech bone name uniqueness~~ — answered, unique.
3. ~~`DestructionProgress` a digest input?~~ — **No.** `StateDigest.Compute` hashes name, position and
   `unitFrameIntegrity` only (`StateDigest.cs:104`). §6's ECS write is digest-safe.
4. ~~`ApplyTime` singletons~~ — answered, all non-null.
5. ~~Volume~~ — answered, 253 KB for 12 units.
6. **Whether the mech prefab's FinalIK components are serialized enabled.** If they are,
   `SolverManager.LateUpdate` (`RootMotion/SolverManager.cs:140-160`) would self-solve and fight the
   bone writes, and `IKExecutionOrder` (`RootMotion.FinalIK/IKExecutionOrder.cs:17-23`) would solve
   even disabled ones. **Unverifiable from source** — it is prefab state, not code. Recorded rather
   than assumed away because it is a real hole; the mitigating fact is that **vanilla replay makes
   the identical bet and ships**, deactivating only the FBBIK holder. If a client's mechs animate
   *nearly* right with the elbows fighting, look here first.
7. **A unit crashing mid-playback.** `CombatUnitCrashSyncSystem` (`CombatSystems.cs:108`) can run
   `OnUnitCrash` inside a window and flip a puppet to `Mode.Active`. Its mode-switch blend advances
   by `Time.deltaTime` (`PuppetMaster.cs:1253-1255`), which is 0 at `timeScale` 0, so the mapping
   weight probably never leaves zero — **inferred, not measured**. The driver deactivates the puppet
   holders at install time regardless, which covers a unit already crashed but not one that crashes
   after.
