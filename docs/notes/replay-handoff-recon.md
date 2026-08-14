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

## Still open

1. ~~A client's `Time.timeScale` during actual playback~~ — **answered above: 0, across 577 frames.**
2. ~~Mech bone name uniqueness~~ — answered, unique.
3. ~~`DestructionProgress` a digest input?~~ — **No.** `StateDigest.Compute` hashes name, position and
   `unitFrameIntegrity` only (`StateDigest.cs:104`). §6's ECS write is digest-safe.
4. ~~`ApplyTime` singletons~~ — answered, all non-null.
5. ~~Volume~~ — answered, 253 KB for 12 units.
