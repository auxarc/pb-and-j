# Combat saves & replay recorder (game 2.2.2-b8339)

Mapped from decompiled `Assembly-CSharp.dll`. Names/signatures only, paraphrased — no decompiled code.
Relevance: combat saves are the leading candidate for host→client state transfer; the replay
recorder for host→client visual playback.

## Combat save — trigger from code

- `PhantomBrigade.Data.DataManagerSave` (static API on a MonoBehaviour):
  - `DoSave(string saveName, SaveLocation, string autosaveTextKey = null, int autosaveIndex = -1, bool previewScreenshot = true, Func<bool> delayUntil = null)` — performs no gating itself.
  - `CanSave(bool playerFacingSave = true)` — the gate: requires `DataShortcuts.debug.allowCombatSaves` for combat saves, and blocks while `combat.Simulating` (i.e. only during planning), during loading, after combat resolution.
  - `QuickSave()` / `QuickLoad()`; `data` property proxies `SaveSerializationHelper.data` (`DataContainerSave`).
- `DataHelperSaveSerialization.SaveFromECS(...)` walks ECS → `DataContainerSave`; `NewFormatSave(path)` zips to `content.zip` (metadata.yaml stays loose). Save = `SavedGames/<name>/{metadata.yaml, content.zip}`; zip holds `core/world/crawler/combat/difficulty.yaml` + `Units/ Pilots/ CombatActions/ ...` one YAML per key.
- Load: `DataHelperLoading.TryLoading(key, location, ...)` (or `OnLoadingExternal`) → rebuilds overworld ECS → pushes `"combat"` state → `CombatBootstrap.Enable()` → `DataManagerSave.LoadToECSCombat()` → per-unit `UnitUtilities.CreateCombatUnit(...)` + `DataHelperAction.InstantiateAction(owner, blueprint, startTime, out bool valid)` for each saved action → `CombatUIUtility.SwitchToPlanningMode()`.

## Enabling the experimental features from a mod

- UI option keys (`SettingKeys`): `Experimental_CombatSaves = "exp_combat_saves"`, `Experimental_ReplayExtended = "exp_replay_extended"`, `Graphics_Replay_SamplingFrequency`.
- Two independent flags: `SettingUtility.combatSavesAllowed` (what the pause-menu UI checks) and `DataShortcuts.debug.allowCombatSaves` (what `CanSave` checks; lives in `Settings/debug.yaml` via `DataLinkerSettingsDebug`).
- From code: write selection into `SettingUtility.GetSelections()` then `ApplyOption(key, forceApply: true)` (ApplyOption no-ops if the selection key is absent), or set the runtime fields directly. `SettingUtility.SaveData()` persists.
- Caveat: using combat saves marks the campaign with persistent memory `"used_exp_combat_saves"` → save shows as "unsupported" in load UI.

## What a combat save contains / loses

Contains (`DataContainerSavedCombat` + per-unit `DataBlockSavedCombatStatus` + `DataContainerSavedAction`):
sim time, turn, scenario step/state machinery, per-unit position/rotation/faction/`playerControllable`/
`aiControllable`/AI keys/status buildups+states/frame+part integrity, layer-0 animation hash+time (mechs only),
level damage as `areaIntegrity` list, camera, selected unit, and **all planned actions** (blueprint, owner,
startTime, duration, targeting point/direction/entity, `MovementPath`, melee/dash blocks).

Loses: in-flight projectiles/beams (simply never serialized), non-mech + non-layer-0 animation, ragdolls,
VFX/audio in flight, prop destruction/interior reveal, AI destinations (loader forces full AI replan).
Dead fields: `commsInfo`, `retreatState` (declared, never written).

**Implication for MP:** a combat save taken during planning (the only time `CanSave` allows) is nearly
lossless — the lossy parts are all mid-execution transients that don't exist during planning. Save-at-
planning → transfer → load is a viable full-state sync primitive.

## `DataContainerSavedAction` = the wire format for orders

`blueprint, ownerName, startTime, duration, targetedPointUsed/targetedPoint, targetedDirectionUsed/
targetedDirection, targetedEntityName, targetedSocketName, targetedHardpointName, MovementPath,
directionInterpolant, offsetInterpolant, melee, dash, dashBulldoze, dashVertical` — YAML-serializable,
and the game restores it via `DataHelperAction.InstantiateAction(...)`. This is effectively a ready-made,
dev-maintained serialization format for a planned action → prime candidate for the M3b injection payload
and later the network order format.

## Replay recorder (`CombatReplayHelper`, global namespace, static)

- Recording: `OnExecutionStart(CombatEntity[])` (from `CombatUILinkSimulationStart`), per-frame
  `OnUnitSnapshot(unit)` (from `ActionRecordingSystem`, throttled 0.016–0.1s), many event hooks
  (`OnProjectileTransform`, `OnBeamTransform`, `OnLevelDamageChange`, …), `OnExecutionEnd()`
  (from `CombatUILinkSimulationEnd`). `experimentalMode` (= `exp_replay_extended`) keeps history across turns.
- Playback: `SetReplayActive(bool)`, `SetPlaybackTimeFromUI(float)`, private `ApplyTime(...)` scrubber;
  `IsReplayAllowed()` requires feature unlock `"feature_combat_replay"`.
- **Not serializable as-is**: keyframe classes are plain (no attributes) and asset/particle tracks hold live
  Unity refs (Transforms, ParticleSystems). The pure-data subset (`ReplayUnit.keyframesTransform/
  keyframesStates/keyframesPoses`, joints, level damage, popups) could be serialized by a mod →
  a possible "stream the host's execution visuals" channel later, but requires custom serialization work.

## Harmony hook candidates (for M2 verification patches)

- `DataManagerSave.DoSave` postfix — observe/confirm combat save trigger.
- `DataManagerSave.LoadToECSCombat` postfix — confirm restore completed.
- `CombatReplayHelper.OnExecutionStart` / `OnExecutionEnd` postfix — turn execution window bracketing.
