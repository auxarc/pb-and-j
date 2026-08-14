# pb-and-j

An experiment in adding multiplayer/co-op to **Phantom Brigade** via the game's
official library-mod (Harmony) system. Currently in proof-of-concept phase —
see `docs/` for research notes and `GAME_BUILD.md` for the pinned game build.

Per [Brace Yourself Games' mod policy](https://braceyourselfgames.com/mod-policy/),
this code mod is open source (MIT) and any networking will be strictly opt-in.

## Layout

- `src/PBAndJ.Core/` — pure protocol + logic (netstandard2.0, zero dependencies, 100% covered)
- `src/PBAndJ.Net/` — TCP transports (sockets and threads; outside the coverage gate by design)
- `src/PBAndJ.Mod/` — game glue: ModLink, Harmony patches, ECS bridge (net472)
- `tools/pbj-peer/` — standalone peer that speaks the protocol, for testing a running game
- `vendor/Managed/` — vendored copy of the game's managed assemblies (gitignored; re-vendor on game update)
- `docs/notes/` — reverse-engineering notes (class/method names + paraphrase only, no decompiled code)
- `docs/design/` — our own architecture decisions (start with `networking.md`)

## Status

- [x] M0 — toolchain (pb-dev distrobox + .NET SDK), build pinning, repo setup
- [x] M1 — hello-world library mod loads in game (verified 2026-08-02: ModLink located, Core banner logged, Heartbeat.Start postfix fired)
- [x] M2 — combat internals mapped as verified Harmony hooks (see `docs/notes/`; action-dump patch verified in-game 2026-08-02: 30 actions captured at ConfirmExecution)
- [x] M3 — "two planners, one sim" proof, verified in-game 2026-08-02 (no networking yet):
  - 3a: mid-combat save round-trip — 37/37 planned actions restored, diff MATCH
  - 3b: `pbj.inject-move` — move order injected via `ActionUtility.CreatePathAction` for a friendly
    unit, survived validation to the ConfirmExecution commit point, unit visibly executed it

- [x] M4 — networking foundations, verified in-game 2026-08-02
  - [x] 0a: sockets verified under Proton, including native-Linux → Wine in both directions
  - [x] 0b: disposal cascade and silent commit-refusal confirmed in-game
  - [x] 4a: architecture recorded in `docs/design/networking.md`
  - [x] 4b/4c: protocol and session state machines, 518 tests at 100% line/branch/method
  - [x] 4d: TCP transports, `pbj-peer` harness — `make peer-selftest` walks a full turn
        cycle over real loopback sockets with no game running; verified in-game 2026-08-02
        (harness handshook with the running game, real units assigned, bad protocol rejected,
        port released cleanly on stop)
  - [x] 4e: order relay end to end — a `move_run` authored in the standalone `pbj-peer`
        process crossed TCP into the running game, was ownership-checked, applied as a real
        `ActionEntity`, appeared in the commit-point action dump, and the mech walked it
        normally with full animation

**Feasibility verdict: proven. Two planners, one sim, over a network.**

- [x] M5 — the client can see execution, verified in-game 2026-08-02
  - [x] 5a: `Unready`, `OrderResult` (by batch index), `CombatStart`/`CombatEnd`
  - [x] 5b: per-peer outbound queue + writer thread — sends no longer block the frame
  - [x] 5c: keepalive (`Ping`/`Pong`), peer and host timeouts, asymmetric on purpose
  - [x] 5d: **snapshot correction** — the host broadcasts authoritative unit state at
        end-of-turn and the client hard-sets to it, then verifies its own digest.
        **Verified in-game 2026-08-02:** a `move_run` of 18 units was relayed from `pbj-peer`
        to the real game for `pb_mech_02`, the mech walked it, and the client's independently
        recomputed digest matched the host's after correction.
  - [x] 5e: reconnect-after-drop — units are held through a grace window and rebound to the
        returning player's new peer id (wire v2).
        **Verified in-game 2026-08-02:** a dropped peer reconnected under the same player name
        during the grace window, was issued a fresh peer id, and got `pb_mech_02` back.
  - [x] in-game checklist cleared: `pbj.unready`, `OrderResult` rejecting an unowned order,
        peer timeout, reconnect
  - [ ] 5f (stretch, not attempted): two real game instances. The blocker is not Proton —
        there is no scenario transfer, so both processes would have to independently enter
        an identical combat, which the game gives no mechanism to guarantee.

- [ ] M6 — keyframe streaming, so the client watches execution rather than being corrected
      after it. Code complete; **in-game verification outstanding**.
  - [x] 6a: `KeyframesMessage` (wire v2, type 19) carrying a transform track per unit —
        transforms only, poses and state keys deliberately deferred
  - [x] 6b: host capture from `CombatReplayHelper`, re-keyed from the process-local ECS id to
        `nameInternal`, sliced to the current turn by index, with a final key appended from the
        same read the snapshot uses — so a track always ends where the correction put the unit
  - [x] 6c: broadcast after the snapshot, never before; a turn with nothing recorded (prediction
        disabled) sends nothing and degrades to exactly M5 behaviour
  - [x] 6d: `KeyframePlayback.TrySample` — pure interpolation under the 100% gate — plus the
        play/stop effects and their cancellation edges
  - [x] 6e: `KeyframePlayer` writes the **view** transform (`combatView.view.transform`), never
        the ECS, so playback cannot touch order authoring or the state digest;
        `pbj.replay-last` round-trips a real capture through the codec and plays it on the host
  - [x] 6f: `make peer-selftest` scenario 5 — tracks survive the wire key for key, sampling at
        the window's end reproduces the snapshot exactly, and combat ending stops playback
  - [x] `pbj.replay-last` run in-game: units retrace their real paths, **sliding in an idle pose**.
        Expected — M6 moves the root transform and nothing drives the animator. This is also how the
        `hasTransformLink` trap below was caught
  - [ ] in-game checklist, remaining: `keyframes sent` counts look sane (~50 keys per moving unit
        for a 5 s turn), the M5d digest still matches, and a prediction-disabled scenario degrades
        quietly

- [ ] M7 — remote play: packaging and the guards a session between two machines needs.
      Code complete; **no cross-machine session run yet**.
  - [x] 7a: wire **v3** — `Hello`/`Rejoin` carry the game build and a session passphrase, and
        the host refuses a peer whose mod or game build differs, naming both sides. Plus a
        10s deadline for sockets that connect and never speak.
  - [x] 7b: `pbj.host <bind> <port> <passphrase>` and `pbj.join <addr> <port> <passphrase>`.
        A non-loopback bind **requires** a passphrase and logs the exposure loudly.
  - [x] 7c: `pbj-peer` gains `--passphrase`, `--game-build`, `--mod-version`, and a sixth
        selftest scenario driving every rejection plus the handshake deadline over real sockets
  - [x] 7d: `make package` — mod zip, self-contained `pbj-peer.exe` for win-x64, friend README
  - [x] stage 1: **verified cross-country 2026-08-02.** A second player, on Windows, ran
        `pbj-peer` against this machine's real game over a port-forwarded TCP connection,
        handshook under wire v3 with a passphrase, was dealt units, queued a `move_run` and
        readied — and when both sides readied, the turn committed and his order executed here.
        First time the protocol has crossed a real network between two people.
  - [ ] stage 2: two real game instances, combat transferred by save file

- [x] M9 — **scenario transfer**: the combat save crosses the wire instead of being carried by hand.
      Code complete; **no two-instance run yet**.
  - [x] 9a: `ScenarioOffer` / `ScenarioRequest` / `Scenario` (types 20–22) — the host offers on
        handshake, a peer that does not already hold the save asks, the bytes cross. Happens in
        `Lobby`, so two games at the main menu can transfer before either enters combat
  - [x] 9b: a peer decides by reading **its own** save through the same bridge call and comparing
        digests locally, so a rejoining peer — which holds it by definition — transfers nothing
  - [x] 9c: the receiver's guards, three deep — the save directory name is ours and never the
        wire's, a character allowlist rejects anything that could escape a directory, and the file
        names are narrowed to the two a save has. The digest is recomputed from the bytes that
        arrived before anything is written, and the write stages and moves so an interrupted
        transfer cannot leave half a save
  - [x] 9d: `pbj.scenario-pull` for the cases the automatic path deliberately excludes; `pbj-peer`
        gains `scenario` and `pull`, and a seventh selftest scenario drives the transfer and every
        refusal over real sockets
  - [ ] in-game: `pbj.combat-save` on the host, then a peer receives a byte-identical copy

- [x] M10 — streamlining the lift on a second player.
  - [x] 10a: **update check** — on session start (and via `pbj.check-update`) the mod asks the
        GitHub releases API for the newest published version and compares. Version *ordering* and
        the wording live in `PBAndJ.Core` under the coverage gate, because `0.9.0` sorts after
        `0.10.0` as a string and would silently tell everyone they were current; the fetch and the
        JSON live in the glue, using `UnityWebRequest` (the path the game's own crash reporter
        proves works under Proton) and a real JSON parser rather than a key scan, since a release
        body is user-authored text that could contain a convincing fake `tag_name`
  - [x] 10b: an **offer**, not an install — a confirmation dialog naming both versions, opening
        the releases page. Download-and-install was deliberately cut after review: an unproven zip
        stack, a file-lock probe whose answer differs between Proton and Windows, and staging that
        trips `ModManager`'s warning dialog — all for something Steam Workshop replaces
  - [x] 10c: title-menu **Multiplayer** button and a connect screen with address, port and
        passphrase. The risky part was as expected: the UI is NGUI, and free-text entry needed a
        `UIInput` cloned from an existing view. `docs/notes/ngui-surface.md` is the record

- [ ] M11 — the **lobby**: the host picks a multiplayer save and everyone loads into it together.
      Scoped in `docs/design/campaign-coop.md`; the lobby's job starts at the main menu and ends
      the moment everyone is loaded.
  - [x] 11a: lobby state on the wire — a `LobbyBarrier` alongside the turn barrier, `LobbyState`
        broadcast as full state, and `LobbyReady`/`LobbyUnready` upward. Pure Core under the
        coverage gate, and deliberately inert: nothing acts on a filled barrier yet, which is
        11d's job. The eighth `peer-selftest` scenario is what stops that inertness becoming M6's
        ship-it-inert failure
  - [ ] 11b: the save catalogue — enumerate `pbj_`-prefixed saves, create one, convert a
        singleplayer save by copy, and filter `pbj_` out of the singleplayer load screen
  - [ ] 11c: the lobby screen, reusing all of 10c's NGUI machinery
  - [ ] 11d: the synchronised load — **and the proper fix** for the defect below, rather than a
        patch of its own
  - [ ] 11e: save transfer generalised from M9's scenario transfer. Measure a campaign save first

**A peer accepted while the host is already in combat is never sent `CombatStart`**, and
`ClientSession.HandleWelcome` then decides its state from the *client's own* combat state — so a
client joining from the menu lands in `Lobby` with no route to `Planning`, and its Execute is
swallowed. Found 2026-08-03, deliberately unfixed: M11d's host-driven load instruction is the
missing message, and fixing it separately would build a second mechanism for the same job. M11a
already sidesteps the second half of it, gating lobby-ready on data rather than session state.

Also open: **M8 — replay handoff**, parked for M11 rather than abandoned. Rather than streaming
animation poses, hand the client the host's recorded replay and let the game's own playback system
draw it. Four of its five open questions are now answered from a running game — see
`docs/notes/replay-handoff-recon.md`, **not** `networking.md`'s M8 section, which is wrong in
several places.

**5f was never blocked.** M5 recorded two real instances as impossible because "there is no
scenario transfer" — but M3a had already built one, and M9 has now put it on the wire.

**Writing `velocity` will not make mechs walk**, though `docs/design/networking.md` claimed it
would until M9 corrected the record. `isMoving` comes from `CurrentMovementAction`
(`MechAnimationSystem.cs:1278`), which only the simulation writes and a client therefore never has;
and the system's non-reactive path is gated on `Time.timeScale > 0`, which is zero during planning
whenever prediction is on. Sliding is the accepted state until M8.

A trap paid for during M6, recorded so it is not re-paid: `TransformLinkSystem` looks like the
ECS→view path for units, but `CombatEntity.ReplaceTransformLink` is never called anywhere in the
game — no unit has that component. Units are driven by `PositionLinkSystem`/`RotationLinkSystem`,
which are reactive on `Position`/`Rotation` and are not gated on the simulation running. The
rendered transform of a unit is `combatView.view.transform`.

## Multiplayer is opt-in

No listener, thread or socket exists unless you explicitly start a session with
`pbj.host` or `pbj.join` in the dev console. With no session, the mod behaves
exactly as it did before networking existed. Binds `127.0.0.1` by default.

**One outbound request, and only on session start.** Starting a session also asks
`api.github.com` whether a newer mod build has been released, so a version
mismatch reads as "you are on 0.4.0, 0.5.0 exists" rather than as a handshake
refusal that looks like a netcode bug. It sends nothing but a User-Agent naming
the mod version, it is not made at launch or at any other time, and
`pbj.check-update` is the same check on demand. The log says so when it happens.
