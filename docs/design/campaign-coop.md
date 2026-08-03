# Campaign co-op — the target flow

Status: **direction agreed 2026-08-03, nothing built.** This records where the mod is going so that
work done before it starts does not have to be undone. `networking.md` remains the record of what
exists; this is the record of what it is for.

## The flow

1. The host **creates a lobby** and can set and view its settings — chiefly which save the session
   will play, chosen from existing multiplayer saves or created fresh.
2. Multiplayer saves are **kept apart from singleplayer ones** and only those are offered in the
   lobby.
3. The host may optionally **convert a singleplayer save into a multiplayer one** — a copy, never a
   move. The original campaign is never touched.
4. Players **load into the save in unison** once everyone in the lobby is ready.

## Authority split

The line runs through the out-of-combat game, not around it:

| | Who acts |
|---|---|
| Overworld base, tactical map screen — world movement, mission selection, the campaign clock | **Host only** |
| Everything else out of combat — mech customisation, loadouts, pilot assignment, salvage, repairs | **All players, concurrently** |
| Combat | All players, under the existing turn barrier |

This is deliberate and it is the cheap half of the problem. The overworld runs a *continuous* clock
rather than the combat turn barrier, and "who moved the base" and "who spent the last of the alloy"
are conflict problems with no good silent resolution. Keeping the map host-driven removes both. What
survives is the part that makes co-op feel like co-op: between fights everyone kits out their own
machines at the same time.

**Consequence to design for early:** concurrent management still needs an ownership rule. Two players
editing the same mech, or spending from one shared resource pool, is the same class of conflict the
map avoids. The likely answer is the one combat already uses — per-unit ownership, host validates —
but it is unproven and it is the first real design question this direction raises.

## Save storage

**Decision: a `pbj_` name prefix inside the normal save folder.** Not a new save location and not a
subdirectory.

`SaveLocation` is a fixed five-value enum (`Normal`, `Internal`, `InternalEditable`, `Reporter`,
`Temporary`) switch-mapped to hardcoded paths in `DataManagerSave.GetSaveFolderPath`. There is **no
extension point** — a custom location cannot be added, and `GetSavePath` / `SetSaveName` compose a
flat path inside whichever of the five is named. So:

- A **prefix** leaves the game's own load and save path completely unmodified. M9 already proves it
  in this codebase: `pbj.combat-save` writes `SavedGames/pbj_combat_test/`, a plain directory in the
  normal folder, and both processes derive identical join keys from it.
- A **subdirectory** would mean intercepting every load and save path, and would need checking that a
  stray directory does not upset the game's own save enumeration.

The cost of the prefix is that multiplayer saves also appear in the singleplayer load screen. Hide
them with a Harmony filter on `CIViewPauseLoad` — the same shape of patch as the Multiplayer menu
entry, and reversible if it turns out people want to see them.

Conversion is then a directory copy plus a rename, with no format translation: a multiplayer save
*is* a campaign save, distinguished only by where it sits and what created it.

## What this changes about work already done

- **The connect screen (M10c) becomes the door, not the room.** Address, port and passphrase are how
  you reach a lobby; they are not lobby settings. The screen needs a second stage — the lobby proper,
  with the save list, the ready state and the roster. The existing screen's structure survives; it
  gains a successor rather than being replaced.
- **M9 generalises from scenario transfer to save transfer.** The mechanism is already right — offer
  by digest, request only if absent, bytes over the wire, write to `SavedGames/`. What changes is the
  payload: a campaign save is not a 65 KB combat scenario, and the size has never been measured.
- **"Load in unison" is exactly the gap found on 2026-08-03.** A peer accepted while the host is
  already in combat is never sent `CombatStart`, and `ClientSession.HandleWelcome` decides its state
  from the *client's own* combat state. A synchronised load is the correct fix for that gap rather
  than a workaround — the host should be telling everyone when to load, which is what this flow says.
- **M8 (replay handoff) is orthogonal** and keeps its value regardless.

## M11 — the lobby, scoped

**The lobby's job begins at the main menu and ends the moment everyone is loaded into the same
save.** It is not the campaign co-op system; it is the thing that gets everyone into one. Concurrent
management, ownership of mechs and resources, and anything that happens *after* the load are
explicitly out of scope and are a later milestone.

The game hands us more than expected here, and each piece was read rather than assumed:

- **`DataManagerSave.GetSaveHeaders(refresh, internalEditable)`** returns
  `Dictionary<string, DataContainerSavedMetadata>` keyed by save name — the catalogue, already built.
- **`GetSaveHeaderLatest(refresh, keyFilter)` already filters on `key.StartsWith(keyFilter)`.** The
  game itself thinks in save-name prefixes, which independently validates the `pbj_` decision.
- **`DataHelperLoading.TryLoading(key, saveLocation, callbackOnEnd, keepScreenAfterLoading, isScheduleOverworldAudio)`**
  loads a save by name **with a completion callback**. That callback is what makes "in unison"
  verifiable rather than hopeful — every peer reports when it is actually in, instead of the host
  guessing from a timer.
- `CIViewPauseLoad` populates its list from `GetSaveHeaders` (`:492`), so that is the patch point for
  keeping `pbj_` saves out of the singleplayer browser.

### Stages

**M11a — lobby state on the wire.** Pure Core, under the 100% gate, no UI. The host holds lobby
settings (selected save key and its digest) and a per-peer lobby-ready flag; clients receive the
state and send their ready. A `LobbyBarrier` alongside the existing `TurnBarrier` — deliberately a
second barrier rather than a reuse, because the turn barrier's participants are decided by combat
assignment and the lobby's by the roster.

New message types from `PbjMessageType` 23+ (unallocated), new effects from `PbjEffectKind` 13+, new
local events from 105+. Per the project's own rule, **new message types do not bump the wire
version** — no layout moves. `ModVersion` remains the real compatibility gate.

**M11b — the save catalogue.** Enumerate `pbj_`-prefixed saves and their metadata; create a new
multiplayer save; convert a singleplayer one by directory copy and rename, never a move. Filter
`pbj_` out of `CIViewPauseLoad`. Mostly glue, with the naming and validity rules in Core.

**M11c — the lobby screen.** The connect screen gains a successor: roster with ready states, the save
picker (editable for the host, read-only for clients), a ready toggle, and Start for the host. The
M10c machinery carries over wholesale — the NGUI cloning, the geometry constants, the
`ConnectText`/`ConnectForm` humble-object split, and the hard-won rules in `ngui-surface.md`.

**M11d — the synchronised load.** Host broadcasts "load this save"; each peer calls `TryLoading` and
reports completion through the callback; the host waits on all of them before anyone proceeds.

**M11d also fixes the defect found on 2026-08-03**, and this is why it belongs here rather than in a
patch of its own. A peer accepted while the host is already in combat is never told to load anything,
and `ClientSession.HandleWelcome` falls back to reading the client's *own* combat state — so a client
that joins from the menu lands in `Lobby` with no route out, and its Execute is swallowed by
`HandleLocalReady`'s `State != Planning` guard. A host-driven load instruction is the missing message.
Fixing it separately would build a second mechanism for the same job.

**M11e — save transfer, generalised.** M9's scenario transfer already has the right shape: offer by
digest, request only when absent, bytes over the wire, write into `SavedGames/`. What changes is the
payload — a campaign save is not a 65 KB combat scenario, and **its size has never been measured**.
Measure before designing chunking.

### Sequencing

`M11a` first and alone; everything else depends on it. `M11b` is independent of `a` and can run in
parallel. `M11c` needs both. `M11d` needs `a`. `M11e` needs a measurement before it needs a design.

### The risk worth naming

**Whether `TryLoading` can be driven from outside the load screen's own flow is unverified.** Every
call site read so far is inside `CIViewPauseLoad`. If it carries hidden assumptions about screen
state, the synchronised load needs a different entry point and M11d changes shape. This is a
decompile-shaped question, and this project has now paid five times for the difference between a
careful reading and a running game — **answer it with a probe before designing around it.**

## Open questions

1. **Ownership under concurrent management.** Per-unit like combat, or something coarser? What
   happens to a shared resource pool?
2. **Campaign save size**, measured, and whether the existing transfer path carries it.
3. **What the client does while the host is on the tactical map.** A blocked screen is honest but
   dull; the management UI staying live is more interesting and is the point of the split.
4. **When the campaign save is written back**, and by whom. Only the host holds the authoritative
   copy, so clients need re-sync at some cadence — plausibly the same handoff as entering combat.
5. **Whether the game's management UI can be driven at all outside its normal flow**, which is a
   decompile question and therefore one to answer by probe, not by reading.
