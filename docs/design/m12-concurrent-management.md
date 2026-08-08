# M12 — concurrent management, planned from measurements

Status: **designed 2026-08-07, nothing built.** Every claim here rests on
`docs/notes/overworld-recon.md`, which records two real game instances running one campaign. Where
this document and `campaign-coop.md` disagree, this one is newer and measured.

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
- **Generated contracts are rolled per machine after the load** and diverge wholesale — different
  `areaKey`, `biomeKey`, spawn points. Two players disagree about what a mission is.
- Base control has a **single funnel**: `OverworldUtility.OrderMovementToPosition`.

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

- **Suppress base control on a client.** One Harmony prefix on
  `OverworldUtility.OrderMovementToPosition` — observed to catch every player movement order, always
  identifying the player base. Note it also restarts the clock from `simulationTimeScaleBackUp`, so
  suppressing it suppresses both. The time-scale buttons need nothing: they route through
  `RefreshTimeScale`, which derives from `isBaseMoving`, so they are already inert on a base that
  cannot move. `CIViewOverworldRoot.ForceTimeScale` is the only bypass and is console-only.
- **Mirror the host's base**, using the game's own teleport recipe
  (`ConsoleCommandsOverworld.cs:893-901`, and `pbj.ow-mirror` is a working implementation):
  `StopMovement`, `ReplacePosition`, **`ReplacePositionTarget`** (not optional — `OverworldMovementSystem`
  drags position back toward a stale target), `isPositionUnchecked = true`, and
  `ReplaceSimulationTime(same value)` to kick the reactive collectors while paused.
- **Send X and Z only.** The receiving machine's ground-snap finds its own Y — observed correcting
  33.3 to 13.3.
- **Accept catch-up, do not fight it.** In `basecrawler` the write lands but does not render. That is
  correct and needs no workaround: the position is already right when the player returns to the map.

**Verification:** two instances, client in the workshop while the host drives, client returns to the
map and the base is in the right place; client cannot issue a move order; client's clock stays at
scale 0 throughout.

---

## M12b — mission-generation authority

**Why it is not optional and not last:** contracts diverge wholesale, so a selected mission would
build a different combat scenario on each machine — breaking the combat path M4–M7 already made work.
The salvage budget also derives from the mission preset, so **every pool in M12d is wrong until this
is fixed.**

**What is known:** the transferred save contains **no** contract entities (44 files in, 47 after
load). They are generated once, post-load, per machine, and do not regenerate while idle.

**Approach, in preference order:** make generation host-only and replicate the result; or, failing
that, resync the three contract files through the existing transfer. Everything that diverges between
two machines is 5 files of 47, all inside the 62 KB the transfer already moves reliably — so a
post-load resync is a legitimate whole-problem answer, not a hack.

**Also unbuilt and belonging here:** what a client experiences when the host starts a mission.
`ScenarioSetupUtility.EnterCombat` is observable and fires in state `overworld`. M11's turn barrier
assumes combat has already begun; nothing covers the transition into it.

---

## M12c — session-owned combat autosaves

Automatic, written by the session, never surfaced to players except after a disconnect. This is the
floor that makes M12d's rollback principle affordable.

- **Rolling per-turn save.** Needs no new permission: the mod already sets `allowCombatSaves = true`
  at load and `CombatSave()` calls `CanSave(false)`. M3a already round-tripped a mid-combat save.
  **Save at the planning phase, never while `combat.Simulating`.** The host should order the write at
  a shared turn boundary so every machine's save is from the same turn.
- **Resolved-window save.** `ScenarioUtility.cs:3586` sets `CombatResolved(outcome, early)`; the
  window closes at `CIViewCombatEnd.cs:353` / `OverworldCombatCompletionSystem.cs:25`. `CanSave()`
  refuses on `hasCombatResolved`, so this needs a direct `DoSave` — the pattern the game itself uses
  in `OnAfterCombatSaveUnchecked`. **⏳ Probe first:** vanilla never restores to that state, so
  whether it restores into the debriefing is genuinely unknown. The per-turn save makes this optional
  rather than load-bearing.
- **⚠️ Reserved names, excluded from the lobby catalogue.** The M11b trap exactly: `pbj_combat_test`
  sat inside the namespace the catalogue claimed and would have been offered as a campaign while
  being rewritten underneath. A save rewritten every turn is the same shape and worse. `LobbySaveNames`
  owns the names; **reserve the unprefixed form** or the guarding arm is unreachable and breaks the
  100% gate while letting the colliding input through.
- `previewScreenshot: false`, off the critical path — this runs every turn.
- **Accepted tradeoff:** surfacing a resume only on disconnect invites deliberate drops. Judged a fair
  price for robustness against accidents. Recorded so it is not re-litigated.

---

## M12d — assigned gear, and the salvage screen

**Ownership rides the save.** `customTags` is a free-form `HashSet<string>` per equipment item,
mutable at runtime and round-tripped through the save. So a `pbj_owner_<id>` tag set by the host
reaches every client through the transfer M11e already performs — **no new message type, no side
table.** ⚠️ Prefix with `pbj_`: the namespace is live, `CombatDamageSystem` reads `flag_no_damage`
and `flag_no_loss`. ⏳ The round-trip is read-verified, not run-verified.

**Identity:** `serial` is a sound key for items that came from the shared save and **unsound for
anything minted afterwards** — the allocator is a per-process counter seeded from the save, so two
machines holding the same save mint the same serial for different objects. Salvage mints items.
**The host assigns the identity clients quote back.**

**The salvage screen:** the budget splits into **equal pools, one per present player, remainder
discarded** (integer division keeps the sum under the total, so the vanilla `costTotal <= budget`
check at commit stays a real backstop). Everyone picks from the shared list, spending only their own
pool. **Every change is broadcast, and another player's picks show as `reserved`** — a refusal before
the fact. **Nobody leaves until all confirm.**

The game supplies each piece: `salvageBudgetLast` is one `int`; selection is a `SalvageSelection`
component per entity carrying `dismantle`; price is `GetSalvageCost`; and there is a single commit
funnel, `EquipmentUtility.ProcessSalvageSelections(host, inventory, budget, victory)`.

**This is the third barrier in this codebase, not a new pattern.** `TurnBarrier` and `LobbyBarrier`
already encode "everyone must agree", and a `SalvageBarrier` inherits their traps: a departing peer
must not silently satisfy it, and the trigger must be an **edge, not a level**.

**On a disconnect:** roll back per the principle. Never forfeit, never redistribute, never
auto-recover — `destroyWithoutSelection: true` means silence destroys loot, and that is exactly the
outcome the principle forbids.

---

## Sequencing

`M12a` is independent and is the visible win. **`M12b` gates `M12d`** — pools computed from a mission
the two machines disagree about are wrong pools. `M12c` is independent of both and should land before
`M12d`, because it is what makes the rollback principle affordable. `M12d` is the largest.

## The rig, for whoever picks this up

`tools/second-instance.sh` — setup is already done; run with no arguments to launch the client.
Steam's Play button becomes **Stop** once a manual instance registers, and it then controls that
instance, so start the Steam host first. Both instances share one `Mods` directory by symlink, so one
`make deploy` serves both; deploy with both closed. A second concurrent `SteamAPI.Init()` on the same
appid is verified to work.

The throwaway probe (`pbj.ow-probe`, `-sample`, `-mirror`, `-watch`) is still registered and should be
deleted when M12a lands — every finding is already in `overworld-recon.md`.

## Still unanswered

1. Whether a save taken in the `CombatResolved` window restores into the debriefing (M12c).
2. Whether `customTags` survives the save round-trip in practice (M12d) — read-verified only.
3. Whether the management UI can be driven **from outside its own flow**; every edit measured so far
   was made by a human clicking.
4. Claim granularity for the general inventory outside salvage — per item serial, or a coarser lease.
