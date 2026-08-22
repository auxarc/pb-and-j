# M12c stage D — the fork, and the reading that decides it

Written 2026-08-21 by lane L1 (M12c build) at `main` = `1708a0b`, with stages A, B and C built and
the gate green (mod 0.23.0, wire v9 unmoved, 2093 tests at 100%). **Stage D is NOT built.** This
file exists because stage D's design forks on one piece of evidence, and the lane that built A–C was
scoped to stop before guessing at it.

Every citation below was read in this session. Line numbers rot; each also names the **member**.

---

## 1. What stage D is

On a reconnect — or on any resume — the checkpoint the host has been writing every turn has to
become the fight everyone is in again. The plan
(`m12c-plan-full.md` §3.4) proposed doing it with no new message at all: the host offers
`CombatOfferMessage(CheckpointSlot, digest, turn)`, and clients take the `HandleCombatOffer` path
they already take at combat entry (`src/PBAndJ.Core/Net/ClientSession.CombatEntry.cs`,
`HandleCombatOffer`) into `LoadGlue.BeginCombat` (`src/PBAndJ.Mod/Net/LoadGlue.cs`, `BeginCombat`),
which accepts an arbitrary save key and skips the campaign catalogue check.

The plan then named its own largest hole (§9.3): **nobody has verified that a mid-combat reload
drives `PbjRuntime.ObserveCombatEdge` false→true.** If it does, the resume is free — the existing
combat-entry machinery re-runs by itself. If it does not, stage D has to force an explicit combat
exit before the reload.

---

## 2. 🔴 THE FORK'S PREMISE IS ALREADY DECIDED, AND NOT BY THE RIG

**The plan filed this as a rig question. It is not one. The decompile answers it, and the answer is
forced rather than probable.**

The chain, each link read this session:

1. `PbjRuntime.ObserveCombatEdge` (`src/PBAndJ.Core/Net/PbjRuntime.cs`, `ObserveCombatEdge`) is a
   level-to-edge detector on `bridge.InCombat`, run once per `Pump`.
2. `CombatGameBridge.InCombat` (`src/PBAndJ.Mod/Net/CombatGameBridge.cs`, `InCombat`) is
   `IDUtility.IsGameState("combat") && Contexts.sharedInstance.combat.hasCurrentTurn`.
3. `IDUtility.gameState` (`decompiled/PhantomBrigade/IDUtility.cs`, `gameState`) returns
   `game.gameControllerStateCurrent.s` and nothing else.
4. `DataHelperLoading.TryLoading` (`decompiled/DataHelperLoading.cs`, `TryLoading`) — **every** load
   funnels through it, ours included (`LoadGlue.Start` calls it directly) — does this when the game
   is not already at the menu:

   ```
   game.isTeardownOfCampaignRequested = true;
   game.ReplaceGameControllerStatePopRequest("mainmenu");
   Co.DelayFrames(2, () => LoadingStart(key, saveLocation));
   ```

5. `DataHelperLoading.LoadingStart` then **refuses outright** unless the state has actually become
   the menu: `if (s != "mainmenu") { LogWarning("wrong game state"); OnLoadFailed(); return; }`.
6. The pump is a postfix on `Heartbeat.Update` (`src/PBAndJ.Mod/Net/NetGlue.PumpPatch.cs`,
   `Patch_Heartbeat_Update`), i.e. it runs every frame for the life of the process, including
   across a load.

⇒ **The two outcomes are exhaustive and both are known.** Either the state pops to `mainmenu` — in
which case `InCombat` is false for at least the two delayed frames plus the whole of the load, and a
per-frame pump cannot miss it — or the pop does not happen, in which case `LoadingStart` refuses and
**there is no reload at all**. There is no third branch in which the save loads while the session
never sees the edge.

🔴 **So the road map's serial-spine item 3 ("M12c stage D after R0", *reason: evidence dependency*)
is wrong as stated.** Stage D is not blocked on R0 for this question. It is blocked on a different
one, stated in §4.

---

## 3. What the edge actually costs — the part the plan did not price

The plan called the edge "the resume, for free". It is not free, and the price is not the save.
`HostSession.HandleCombatExited` (`src/PBAndJ.Core/Net/HostSession.CombatEntry.cs`,
`HandleCombatExited`) is not a bookkeeping no-op. On the way out it:

- sets `State = HostSessionState.Lobby`;
- **sets `assignments = UnitAssignments.Empty`**;
- `barrier.AdvanceTo(-1)`;
- **broadcasts `CombatEndMessage` to every peer**;
- advances the lobby selection, which invalidates every peer's lobby readiness.

and on the way back in, `HandleCombatEntered` emits a `ShipCombatEffect`, so
`CombatShipGlue.Write` writes **`pbj_combat_test`** — the scenario slot — from the freshly reloaded
state and offers *that*, then `StartCombatForEveryone` calls `Reassign`.

Three consequences, all new:

1. **The resume needs no `CombatOfferMessage(CheckpointSlot, …)` and no checkpoint transfer at
   all.** The reloaded state is laundered into the scenario slot by machinery that already ships.
   The checkpoint is the host's *local* rollback medium; the wire never carries it. That is a
   simpler stage D than the plan's, and it is the plan's own §3.4 arriving at the right answer for
   the wrong reason.
2. **Every client is told the fight ENDED and then offered a new one.** Whatever a client does with
   `CombatEndMessage` — leaving the fight, stopping playback, returning to a lobby view — it does,
   visibly, in the middle of what the player experiences as a rollback.
3. 🔴 **Unit ownership does not survive.** `assignments` is emptied on exit and recomputed by
   `Reassign` on entry. A peer that owned `unit_b` before the reload owns whatever the fresh plan
   gives it afterwards. **Nothing in the design says a rollback may re-deal the units**, and this is
   the first place it has been written down.

There is also a second full `SaveData` — the re-ship — on top of the reload, and `CombatShipGlue`'s
90-second poll window in front of it. On the stall numbers M12c is trying to measure, that is not
nothing.

---

## 4. The two branches, restated on today's evidence

### Branch A — free resume via the edge (no new message, no new effect)

The host reloads the checkpoint locally; the edge tears the session's fight down and rebuilds it
through the shipped M12b·2 path.

- **Cost in code: almost none.** Stage D becomes a console command that reloads
  `LobbySaveNames.CheckpointSlot` on the host and lets the pump do the rest, plus whatever is needed
  to make §3's three consequences acceptable.
- **Cost in behaviour: §3.2 and §3.3.** A visible end-of-combat on every client, and re-dealt unit
  assignments.
- **Wire cost: zero.** No message type, no layout, no `Seams.cs` move — so stage D would not need a
  merge window of its own.

### Branch B — an explicit, session-aware resume

The host announces a rollback *before* reloading, so the session distinguishes "this fight ended"
from "this fight is being rewound": suppress the `CombatEndMessage`/`Reassign` pair across the
window, preserve `assignments`, and re-offer with the turn the checkpoint holds.

- **Cost in code: real.** It needs a way for `HandleCombatExited` to know a rollback is in flight —
  which is session state, so Core, so a new effect or event, and probably a new message so clients
  do not act on an exit they are about to un-see. **That is a protocol bump and a merge window.**
- **Cost in behaviour: none of §3's.** Ownership is preserved and the clients see a rewind rather
  than an ending.

Branch B is strictly more work and strictly better behaviour. Branch A is shippable in an afternoon
and may be good enough.

---

## 5. ⭐ THE SINGLE READING THAT DECIDES IT

Not the one the plan asked for. That one is answered (§2). The reading is:

> **On a two-instance session, with the host mid-fight and a client holding assigned units: reload
> `pbj_combat_turn` on the host, and record what the CLIENT does between the reload and the
> re-offer — and whether it ends up owning the same units.**

Concretely, on the rig, with `pbj.checkpoint-stat` confirming the host has actually been writing:

1. Both machines into one fight through the shipped path; drive two or three turns so assignments
   are settled and non-trivial (each peer owning a different unit).
2. Note the client's owned units (`pbj.net-status`).
3. On the host: **`pbj.checkpoint-load`**. (Written here as `pbj.combat-load` pointed at
   `pbj_combat_turn` until 2026-08-21; that was a **wrong instruction** — `pbj.combat-load`
   takes no arguments and can only ever load `pbj_combat_test`, so following it literally
   would have reloaded the scenario slot and read a rollback that never happened. The
   review's §C3 blocker 1; the command now exists, registered in
   `src/PBAndJ.Mod/Net/NetGlue.Commands.cs` beside `pbj.checkpoint-stat`.)
4. Watch the client's log for `CombatEndMessage`, then the new `CombatOfferMessage`.
5. **Read the client's owned units again.**

On the host's side, the reload also prints the action diff for R0 reading 2. That diff is armed by
the automatic checkpoint write itself (`CheckpointGlue.Write`, review §C3 blocker 2) — before the
fix only the manual `pbj.combat-save` ever armed it, so a checkpoint load printed
`no pre-save capture this session — diff skipped` and the reading could not be taken at all. The
capture is keyed by slot, so a `MATCH` is only ever printed against the capture taken for
`pbj_combat_turn`, and the "diff skipped" line now names which slots *do* hold a capture.

**If the units are the same and the client's screen survived the round trip, take branch A** and
stage D is a console command plus a paragraph. **If the units moved** — which §3.3 says they will,
by construction, unless `Reassign` happens to be stable for two peers and three units — **branch B
is mandatory**, and stage D needs its own merge window after L3's.

⚠️ The `Reassign`-is-stable case is a trap: with a small enough roster the re-deal can land on the
same answer by luck and read as "ownership survives". **Use a roster where the assignment is not
forced**, or assert against `UnitAssignments` directly rather than against what the screen shows.

---

## 6. Two smaller things stage D must check, recorded so they are not rediscovered

- **The campaign bit after a checkpoint load.** `SaveNamespacePatches.CampaignBitFromLoad` is a
  postfix on the game's `LoadingEnd2` and reads `DataManagerSave.saveName`; after a checkpoint
  reload that is `pbj_combat_turn`, which `IsMultiplayerKey` accepts, so it calls
  `MultiplayerCampaign.Enter("pbj_combat_turn")`. `BeginCombatLoadEffect`'s own doc says a fight
  slot "must not mark the campaign as entered" — but that patch fires on the game's path regardless
  of which of ours asked, so the scenario slot has the same shape today. **Pre-existing, not
  introduced by M12c**, and stage D is where it becomes load-bearing.
- **`NetLog.ScenarioWritten` wording.** It says "run pbj.combat-load to enter it" only for an exact
  match on `ScenarioSlot`; a checkpoint arriving through `WriteScenarioEffect` would get the
  campaign wording ("the lobby will load it when everyone is ready"), which is wrong for a
  checkpoint. Left alone deliberately in stages A–C: under branch A nothing ever writes a checkpoint
  through that path, and under branch B the wording is part of the new design rather than a patch
  to the old one.

---

## 7. What stages A–C already give you, with or without stage D

A checkpoint that exists and is never resumed from is still the floor M12d's rollback principle
needs, and it is provable without a second machine — which is the whole of what was built here. The
`pbj.checkpoint-stat` instrument in `src/PBAndJ.Mod/Net/CheckpointGlue.cs` is the other half: it
carries the stall number (design open question 6, never measured by anyone) and it prints its own
alternative hypotheses beside a zero, so "no checkpoints" cannot be read as "checkpoints are cheap".
