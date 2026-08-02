# pb-and-j

An experiment in adding multiplayer/co-op to **Phantom Brigade** via the game's
official library-mod (Harmony) system. Currently in proof-of-concept phase —
see `docs/` for research notes and `GAME_BUILD.md` for the pinned game build.

Per [Brace Yourself Games' mod policy](https://braceyourselfgames.com/mod-policy/),
this code mod is open source (MIT) and any networking will be strictly opt-in.

## Layout

- `src/` — mod C# source (net472 class library, built inside the `pb-dev` distrobox)
- `vendor/Managed/` — vendored copy of the game's managed assemblies (gitignored; re-vendor on game update)
- `docs/notes/` — reverse-engineering notes (class/method names + paraphrase only, no decompiled code)

## Status

- [x] M0 — toolchain (pb-dev distrobox + .NET SDK), build pinning, repo setup
- [x] M1 — hello-world library mod loads in game (verified 2026-08-02: ModLink located, Core banner logged, Heartbeat.Start postfix fired)
- [x] M2 — combat internals mapped as verified Harmony hooks (see `docs/notes/`; action-dump patch verified in-game 2026-08-02: 30 actions captured at ConfirmExecution)
- [x] M3 — "two planners, one sim" proof, verified in-game 2026-08-02 (no networking yet):
  - 3a: mid-combat save round-trip — 37/37 planned actions restored, diff MATCH
  - 3b: `pbj.inject-move` — move order injected via `ActionUtility.CreatePathAction` for a friendly
    unit, survived validation to the ConfirmExecution commit point, unit visibly executed it

**Feasibility verdict: proven.** Next phase: networking design (transport, lobby, order sync).
