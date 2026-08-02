# Faction control, event bus, console, mod-loader details (game 2.2.2-b8339)

Mapped from decompiled `Assembly-CSharp.dll` + reference mod repos. Paraphrased names/signatures only.

## Faction & control model

- Faction is a raw string on `PersistentEntity` (`Faction { string s }`, indexed): `Factions.player = "Phantoms"`,
  `Factions.enemy = "Invaders"`. Combat entities have no faction — only flags.
- Combat flags (orthogonal to faction!): `isPlayerControllable`, `isAIControllable`, `isAIControllableNextTurn`,
  `isOwnerAllied` (set at spawn when faction == "Phantoms" in `UnitUtilities.CreateCombatUnit`).
- UI selection gate: `InputCombatUnitSelectionUtility.AttemptUnitSelectionAtCursor()` — friendly but
  NOT `isPlayerControllable` → unselectable; planning requires `CombatUIUtility.IsUnitPlayerControllable(unit)`
  (which also returns true for AI units when `DataShortcuts.ai.allowAIControlByPlayer` is set).
- **The action pipeline itself has NO ownership check** — `DataHelperAction.InstantiateAction` /
  `ActionUtility.CreatePathAction` work for any unit from any caller (AI uses the same path). To make a unit
  UI-plannable: set `isPlayerControllable = true` + `CIHelperWorldMarkers.OnUnitControlChanged(id)`.
- Data-driven control change (usable from scenario YAML): `PhantomBrigade.Functions.CombatUnitControlChange
  { bool ai; bool player; }` (ICombatFunctionTargeted).
- Unit name: `persistentEntity.nameInternal.s` (component `NameInternal`); lookup
  `IDUtility.GetPersistentEntity(string nameInternal)`.

## Dev-mode hot-seat (all gated on `DataShortcuts.debug.developerMode`)

- Enable at runtime: pause menu → **F1** → type `dev` (toggles developerMode + console). Or `developerMode: true`
  in prefix `Settings/debug.yaml`.
- **Keypad9**: `CIViewInternalCombatTools` — `SetEnemiesActive(bool)` bulk-swaps every non-friendly unit:
  `isAIControllable = active; isPlayerControllable = !active` + disposes actions + AI replan flag. Also
  `buttonAIControllable` → `DataShortcuts.ai.allowAIControlByPlayer`.
- **Keypad8**: `CIViewInternalCombatUnit` — per-unit toggles for `isAIControllable` / `isPlayerControllable`.
- **Keypad7**: `CIViewInternalCombatSpawn` — dev spawns (player-controllable).
- Console: `controlai [bool]` → `allowAIControlByPlayer`.

## GameEventUtility (static bus)

- `SubscribeToEvent(GameEventType, Action)`, `SubscribeToEventOnObject(GameEventObject, Action<object>)`.
  No unsubscribe (only `ClearActions()`, never called by the game). Subscriber callbacks fire synchronously.
- `GameEventType` combat-relevant: `CombatContextEnabled` / `CombatContextDisabled` (emitted from
  `CombatBootstrap`); combat outcome arrives as `OverworldPointCombatVictory/Defeat/EndEarly/EndLate`.
  **No `CombatOutcome` event exists.** `GameEventObject`: `ViewEntry/ViewExit, UnitBuilt, PilotAdded, ...`.
- Subscribe from `ModLink.OnLoadEnd()`.

## Quantum Console (QFSW.QC)

- ⚠️ **Mod `[Command]` attributes are never discovered**: QC snapshots `AppDomain.GetAssemblies()` at static
  init (mods load later via `Assembly.LoadFrom`) AND its scan rule rejects assemblies not named
  `Assembly-CSharp*`/`QFSW.QC*`. Double barrier.
- Supported path (the game itself demos it in `ConsoleCommandsMods.TestCommandInjection`):
  `QuantumConsoleProcessor.TryAddCommand(new CommandData(methodInfo, "command-name"))` from `OnLoadEnd()`.
  Params must be QuantumParser-parseable; one overload per arity.
- Console availability = `developerMode` (`CIViewLoader.RefreshDeveloperMode` sets `QuantumConsole.Instance.enabled`).
  Useful members: `QuantumConsole.Instance.LogToConsole(string)`, `InvokeCommand(string)`, events `OnActivate/...`.
- Existing game commands worth knowing: `save`/`load`, `debug-config-save/load`, `mods.load-mods`, `controlai`.
- SRDebugger is vestigial (one call site); QC is the real console.

## Mod loader details (ModManager.TryLoadingLibraries)

- `Assembly.LoadFrom` on every DLL in `Libraries/`; exactly ONE `ModLink`-derived type allowed (extras skipped
  with a warning — our Core.dll must contain no ModLink subclass, which it doesn't).
- Creates `new Harmony(modID)`, calls `OnLoad(harmony)`; base impl = `SetModIndexAndID()` → `OnLoadStart()` →
  `PatchAll(assembly)` → `OnLoadEnd()` (→ `ModUtilities.Initialize()`).
- Per-mod settings idiom (echkode): `UtilitiesYAML.ReadFromFile<TSettings>(Path.Combine(modPath, "settings.yaml"), false)`.

## Reference repo idioms worth copying

- echkode patch organization: one `[HarmonyPatch] static partial class Patch {}` + file-per-target adding
  partial methods named `<TargetAbbrev>_<Method>Postfix/Transpiler`.
- Private-member access: `AccessTools.DeclaredField/DeclaredMethod`, `Traverse.Create(obj).Field<T>("name").Value`.
- Timeline facts (echkode CombatTimelineFixes README): min action duration 0.25s (CreatePathAction refuses
  shorter); max placement time = `turnStart + DataShortcuts.sim.maxActionTimePlacement`; wait actions spanning
  turn boundary get split; double-track placement uses a 4-iteration `IsIntervalOverlapped` scan hack.
- Sim timing (echkode SimulationTimeScaleSetting README): `DataShortcuts.sim.timeScaleMain` default 0.6,
  `timeScaleSlow` ≈ 0.0388; replay speeds hard-coded 0/0.12/0.6/1.2.
