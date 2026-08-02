# Combat state machine & planned-action model (game 2.2.2-b8339)

Mapped from decompiled `Assembly-CSharp.dll`. Names/signatures only, paraphrased — no decompiled code.

## Phase state (there is NO combat FSM class — it's Entitas data)

- **Authoritative flag**: `CombatContext.Simulating` (bool-like property over a singleton entity carrying
  `PhantomBrigade.Combat.Components.Simulating`). `true` = executing, `false` = planning.
- **UI mode**: enum `PhantomBrigade.Input.Components.CombatUIModes`
  (`Simulating=0, Unit_Selection=1, Path_Drawing=2, Wait_Drawing=3, Time_Placement=4, Targeting_Units=5,
  Targeting_Locations=6, AI_Planning=50, Intermission=60, Replay=90, End=100`), set via
  `input.ReplaceCombatUIMode(...)`; fan-out in `InputUILinkModeSync` (reactive on `InputMatcher.CombatUIMode`).
- Turn/time: unique CombatContext components `CurrentTurn`, `TurnLength` (default 5s), `SimulationTime`,
  `SimulationTargetTime`, `PredictionTime*`, `ScenarioAllowingExecution`. turnStart = `turn * turnLength`.

## Planning → execution chain (patch targets ranked)

1. `CIViewCombatExecution.CheckAndAttemptExecution(bool)` — execute button; validates mode/scenario.
2. **★ `CombatUtilities.ConfirmExecution(int turnsAdvanced, float timeScaleForced = -1f)`** — the commit
   point; last chance to inject/validate actions; ends by `combat.ReplaceCurrentTurn(turn+n)`.
   (Also called by `CombatForceExecution` function + debug console.)
3. `TurnSystem` (reactive on `CombatMatcher.CurrentTurn`) sets `SimulationTargetTime`.
4. `SimulationTimeSystem.Execute()` flips `combat.Simulating` true at window start, false when
   simulationTime reaches target — the actual 5s window.
5. On Simulating **added**: `CombatUILinkSimulationStart.Execute` — sets UI mode Simulating, calls
   `CombatReplayHelper.OnExecutionStart`, iterates every unit's actions via
   `action.GetEntitiesWithActionOwner(id)`, injects `move_run` extrapolation actions. Good injection point.
6. On Simulating **removed**: `CombatExecutionEndSystem` → … → `CombatExecutionEndLateSystem` (disposes
   extrapolated actions) → `CombatUILinkSimulationEnd` (UI mode Unit_Selection,
   `CombatUIUtility.SwitchToPlanningMode()`, `CombatReplayHelper.OnExecutionEnd()`).
- System order reference: `PhantomBrigade.Combat.Systems.CombatSystems` (Feature): SimulationTimeSystem(21),
  TurnSystem(24), ActionPlaybackSystem(30), ActionCreationSystem(73), ActionDisposalSystem(75),
  CombatExecutionEndSystem(84), CombatExecutionEndLateSystem(93).
- Replay (`CombatUIModes.Replay`) is keyframe playback in `CombatReplayHelper` — ECS sim does NOT re-run;
  `Simulating` stays false. See [save-and-replay.md](save-and-replay.md).

## A planned action = an `ActionEntity` (ActionContext), a bag of components

Key components (namespace `PhantomBrigade.Action.Components` unless noted):
- `ActionOwner { [EntityIndex] int combatID }` — owner unit; **setting this last is what triggers
  `ActionCreationSystem`** (reactive on `ActionMatcher.ActionOwner.Added()`).
- `StartTime { float f }`, `Duration { float f }`; `DataKeyAction { string s }` (blueprint key, indexed);
  `DataLinkAction*` resolved blocks from `DataContainerAction` (via `DataMultiLinker<DataContainerAction>`).
- Movement: `MovementPath { List<Vector3> points; List<AreaNavLink> links }`, `MovementPathProcessed`,
  flag `MovementPathChanged`.
- Targeting: `TargetedEntity { int combatID }`, `TargetedPoint/Secondary/Local/Final { Vector3 }`,
  `TargetedDirection`, `TargetedPart`, `ActiveEquipmentPart { int equipmentID }`.
- Lifecycle: `ExecutionStage { int }` (0=planned), flags `Started/Ended/Disposed/Destroyed/Locked`,
  `OnPrimaryTrack`/`OnSecondaryTrack`, `MovementExtrapolated`, `Reaction`.
- **Gotcha**: `CompletedAction` and `AIAction` flags have NO `is` prefix (`a.CompletedAction`, `a.AIAction`).
- `DataBlockActionCore`: `locking`, `TrackType {Primary,Secondary,Double}`,
  `PaintingType {Wait,Path,Melee,Dash,Targeting,TargetingDirectional,Timing,DualTiming}`, durations, heat,
  validation/execution function lists.

## Creation API (what the game itself calls)

- **★ `DataHelperAction.InstantiateAction(CombatEntity, string actionKey, float startTime, out bool valid,
  bool refreshScenarioState = true)`** — the public factory used by UI, AI, scenario scripting, AND save-load.
- `ActionUtility.CreatePathAction(CombatEntity, string pathActionKey, List<Vector3> points,
  List<AreaNavLink> links, bool aiAction = false, float startTimeOverride = -1f, bool validateTime = true)` —
  movement orders; computes duration from unit speed; rejects paths < 1.5 length, duration < 0.25,
  start beyond `turnStart + DataShortcuts.sim.maxActionTimePlacement`.
- Placement legality: `CombatUIUtility.IsIntervalOverlapped(ownerID, actionData, startTime, duration,
  out intersectedID, ...)`, `DataHelperAction.IsAvailableAtTime(...)`, `DataHelperAction.GetAvailableActions(unit)`,
  `ActionUtility.GetLastActionTime(unit, primaryOnly)`.
- UI flows: `InputCombatPathDrawingUtility.AttemptFinish()`, `InputCombatTargetingUtility.AttemptTargeting*()`,
  `InputCombatMeleeUtility/DashUtility.AttemptTargeting()`, `CombatUIUtility.AttemptToFinishTimePlacement()`.

## Validation / where an injected action would be silently dropped

- **★ `DataHelperAction.IsValid(ActionEntity, bool checkDualLinks = true)`** — requires DataKeyAction +
  DataLinkActionCore + ActionOwner, live functional un-wrecked owner, conscious pilot (unless uncrewed).
  **`isLocked = true` short-circuits validation to valid.**
- Applied EVERY sim tick by `ActionPlaybackSystem.CleanActionsList()` (private): invalid →
  `CompletedAction = true; isDisposed = true` — this is the silent-drop path M3b must guard against.
- `ActionDisposalSystem` (reactive on `Disposed.Added()`): **disposal of a primary-track action cascades
  to all later non-locked primary-track actions of the same owner.**
- Bulk: `ActionUtility.CompleteActionsFromTime(...)`, `DestroyActionsFromTime(...)`.

## Enumerating planned actions (for the M2 dump patch)

- Per unit: `Contexts.sharedInstance.action.GetEntitiesWithActionOwner(combatEntity.id.id)` (EntityIndex).
- All: `contexts.action.GetGroup(ActionMatcher.AllOf(ActionMatcher.ActionOwner, ActionMatcher.Duration,
  ActionMatcher.StartTime).NoneOf(ActionMatcher.Destroyed))` (idiom from ActionPlaybackSystem).
- Units: `combat.GetGroup(CombatMatcher.UnitTag)`; standard skip predicate:
  `!hasStartTime || !hasDuration || CompletedAction || isDestroyed || isDisposed || !hasDataKeyAction`.

## Faction / control model

- Combat flags: `isPlayerControllable`, `isAIControllable` (+`isAIControllableNextTurn` deferred handover in
  `CombatExecutionEndSystem`, driven by persistent memory `"unit_ai_control_countdown"`), `isOwnerAllied`.
- Persistent: `Faction { [EntityIndex] string s }` — `Factions.player = "Phantoms"`, `Factions.enemy = "Invaders"`;
  `persistent.GetEntitiesWithFaction(s)`.
- Helpers: `CombatUIUtility.IsUnitFriendly(unit)`, `IsUnitPlayerControllable(unit)` (notes
  `DataShortcuts.ai.allowAIControlByPlayer` — dev flag allowing player control of AI units!),
  `IsUnitValidTarget(a, b)` (different persistent faction strings).
- Selection: unique `UnitSelected { int id }` on CombatContext; `IDUtility.GetSelectedCombatEntity()`.
- Cross-context: `IDUtility.GetLinkedPersistentEntity(CombatEntity)` / `GetLinkedCombatEntity(PersistentEntity)` /
  `GetActionEntity(int)` / `GetCombatEntity(int)`.

## MP-relevant implications

- The commit point (`ConfirmExecution`) and the per-tick validator (`CleanActionsList` via `IsValid`) are both
  single, patchable chokepoints — client-order injection has exactly one front door and one bouncer.
- `isLocked` is a legitimate mechanism to protect injected actions from both validation drops and cascade disposal.
- `DataShortcuts.ai.allowAIControlByPlayer` suggests the dev hot-seat path is a supported flag, not a hack.
