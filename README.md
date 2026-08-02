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

Next: M5 — letting the client actually *see* execution (end-of-turn snapshot correction), plus
an outbound writer thread, which is a hard prerequisite before state snapshots go over the wire.

## Multiplayer is opt-in

No listener, thread or socket exists unless you explicitly start a session with
`pbj.host` or `pbj.join` in the dev console. With no session, the mod behaves
exactly as it did before networking existed. Binds `127.0.0.1` by default.
