# Harmony patch durability — what a co-op desync next door means for this mod

Written 2026-08-09, handed over from `../StS2-Mods/drifter`, which lost a multiplayer run to a
Harmony patch that silently stopped executing mid-process. Source material: that repo's
`docs/HANDOFF.md` ("Session five").

There is a companion note in `../spirit-island/docs/harmony-patch-loss-2026-08-09.md`. This one is
not a copy of it — the risk profile here is different and considerably worse, and the recommendations
diverge.

**Incorporated into the design 2026-08-09**: [The patch surface](../design/networking.md#the-patch-surface)
in `networking.md` carries the verdict, both recommendations and the diagnostic discipline;
`m12-concurrent-management.md` carries what it means for `PassengerGlue`; `GAME_BUILD.md` points at
the lock. Two of this note's counts were wrong against the tree and are corrected below.

## Verdict in three lines

1. **The specific mechanism cannot happen here.** It is a .NET Core tiered-compilation effect; this
   mod is Unity/Mono on `net472`. Do not chase it, and do not add `DOTNET_*` variables to anything.
2. **The architectural exposure is the highest of the three projects**, because here the patches
   *are* the netcode rather than a seam into it.
3. **There is one concrete, verifiable defect to fix**, described under "The `PatchAll` problem".
   It is worth acting on independently of anything else in this note.

## What happened next door, briefly

A two-player co-op run died to a lockstep state divergence. Both machines' state dumps differed by
exactly one line — one debuff stack, 3 against 4 — with byte-identical builds, identical library
versions and an identical action stream.

The cause was a boolean the engine does not checksum, which a Harmony postfix was responsible for
clearing. On one peer the postfix ran; on the other it had **stopped executing partway through the
run**. Not thrown: zero exceptions, zero swallowed-failure messages, a clean `Applied 272 patches
successfully, 0 failed` at startup identical to the other machine. It simply stopped being in the
code that ran, and the disagreement stayed invisible across nine matching checkpoints before
surfacing a turn later as a wrong number.

On CoreCLR, Harmony installs a native jump over a method's *compiled* code while its IL stays
unpatched, so every recompilation the runtime performs — tier-0 to tier-1 promotion at
`TC_CallCountThreshold`, then again under `TieredPGO` — republishes an entry point that the detour
has to be re-applied to. MonoMod hooks `ICorJitCompiler::compileMethod` specifically to handle this
and does not reliably win the race. Across four observed runs the detour survived twice and was lost
twice, at both threshold settings. **Whether a patch survives a promotion is a coin flip**, decided
per-process.

## Why it cannot happen here

Unity's Mono JIT compiles a method once and does not re-JIT it. There is no tiering, no PGO
recompilation, and no entry-point republication for a detour to lose. A patch that is applied and
takes effect here stays in effect for the life of the process.

`Directory.Build.props` targets `net472` for the mod assembly; the host is Phantom Brigade 2.2.2
(Unity, Steam app 553540) per `GAME_BUILD.md`. Nothing in that stack has tiered compilation.

## Why this repo is nonetheless the most exposed

Spirit Island's patches are thin seams that delegate to provider interfaces that project owns; a
patch dying there degrades to *cosmetically wrong*. This mod is the inverse. It retrofits multiplayer
onto a singleplayer game **through the patches themselves** — 35 `[HarmonyPatch]` classes, of which
eight are suppression-style prefixes that return `false` to replace game behaviour outright.

**Corrected 2026-08-09 against the tree.** The first draft of this paragraph listed
`CombatGameBridge` (4), `ConnectSettingsStore` (4), `LobbyPicker` (3) and one each in six more files.
`CombatGameBridge`, `ConnectSettingsStore`, `ConnectScreenGlue`, `LobbyScreenGlue` and `UpdateGlue`
contain no Harmony patch at all, and `NetGlue`'s two are postfixes. The actual eight are
`PassengerGlue` (4), `LobbyPicker` (2), `ExecutionPatches` (1) and `SaveVisibilityPatches` (1). The
35-class total is right; the shape of the exposure is unchanged, and `ExecutionPatches` is still the
sharp one.

`Patch_CIViewCombatExecution_CheckAndAttemptExecution` in `Net/ExecutionPatches.cs` is the sharp
example. It swallows the local Execute and posts a Ready instead, so the host decides when anyone
executes. If that prefix stops running, nothing looks broken: **the game runs the turn locally inside
a networked session.** That is not a degraded experience, it is immediate, silent, one-sided
divergence — the same failure class as the desync next door, reached by a different route.

**And there is no relocation fix available.** The Drifter's repair was to move the rule out of a patch
and into an engine-dispatched hook, because a hook reaches the JIT as the mod's own IL and cannot be
lost the way a detour can. That option does not exist for a suppression gate: a prefix returning
`false` *is* the extension point, and there is no non-patch equivalent of "do not run the original".
The defence here has to be **detection**, not relocation. Everything below follows from that.

## The `PatchAll` problem

This mod never instantiates Harmony. The game does it, in `ModLink.OnLoad`
(`decompiled/PhantomBrigade.Mods/ModLink.cs:65-79`):

```csharp
try
{
    Assembly assembly = GetType().Assembly;
    Debug.LogFormat("Mod {0} ({1}) is executing OnLoad | Using HarmonyInstance.PatchAll ...");
    harmonyInstance.PatchAll(assembly);
}
finally
{
    if (Harmony.DEBUG) { ... }
}
OnLoadEnd();
```

`try`/`finally` with **no `catch`**. Two consequences, and the second is the one that matters.

**First, `PatchAll` is all-or-nothing.** One patch throwing — an `AmbiguousMatchException` on an
overload, a prefix parameter bound by a name the game does not use, or a target method that moved in
a game update — aborts the pass. Every patch after it in Harmony's enumeration order silently never
applies. The Drifter repo lost a whole session to exactly this: the mod loaded, registered, appeared
in the mod list, and did nothing, with a single line at startup thousands of lines above the crash it
eventually caused. `GAME_BUILD.md` already says to "re-verify every Harmony patch target before
trusting anything" after a game update — a moved target is precisely the trigger.

**Second, and specific to this mod: `OnLoadEnd()` is downstream of the throw** (line 79). If
`PatchAll` fails, the exception leaves `OnLoad` and `OnLoadEnd` never runs — so
`ModEntry.cs:21-35` never executes, meaning no console commands registered, no
`SaveLoadGlue.EnableCombatSaves()`, no `NetGlue.RegisterConsoleCommands()`.

That is genuinely good news, and worth stating plainly: **this failure is loud.** Unlike the Drifter's
outage, you would notice immediately, because none of the mod's console commands would exist. The bad
part is the state it leaves behind — an **arbitrary partial patch set**, with some suppression gates
live and others not, decided by enumeration order. A half-gated multiplayer layer is worse than an
absent one, and nothing currently stops a session being started in that state.

### Recommendation: assert the patch surface at session entry

Not at load — `OnLoadEnd` cannot be trusted to run, which rules out the obvious place. Put it where
being half-patched is actually fatal: **hosting or joining**.

```csharp
// count of methods this assembly is expected to have patched
var patched = Harmony.GetAllPatchedMethods()
    .Count(m => Harmony.GetPatchInfo(m).Owners.Contains(modID));
if (patched != ExpectedPatchCount) { refuse to host/join, say why }
```

**`ExpectedPatchCount` is 31, not 35** — four targets are patched twice
(`CombatUtilities.ConfirmExecution`, `DataHelperLoading.TryLoading`,
`OverworldUtility.OrderMovementToPosition`, `ScenarioSetupUtility.EnterCombat`), and
`GetAllPatchedMethods` counts methods. Writing 35 there fails on a healthy build, on the first run.

Refusing the session is the right call rather than logging and continuing. A session that cannot gate
execution produces divergence that looks like a gameplay bug and costs somebody a run to diagnose —
which is the entire content of the Drifter story.

### Recommendation: a patch-surface lock, mirroring `wire-surface.lock`

The discipline for this already exists in the repo. `wire-surface.lock` pins a hash of the wire
surface with a `check-wire-surface` Makefile target and a comment saying to re-record only when the
change is intentional. The identical treatment applied to the **patch surface** — the set of
(declaring type, method, patch kind) that every `[HarmonyPatch]` in the assembly resolves to, checked
at build time against `vendor/Managed/` — catches a moved target *before* a build ships, rather than
at `PatchAll` time on a player's machine.

That closes the loop `GAME_BUILD.md` currently leaves to human diligence: re-vendoring after a game
update would fail the check instead of relying on someone remembering to re-verify by hand. It also
pairs naturally with the existing `Assembly-CSharp.dll` SHA256 assertion, which already refuses to
deploy on mismatch — the SHA says *the game changed*, and the patch-surface lock says *and here is
what it broke*.

## Mono's own version of the hazard

Real, but differently shaped, and probably not biting you today.

Mono does not re-JIT, so the risk moves to **patch-application time**. If a target method is small
and gets inlined into a caller that was already JIT-compiled before the patch landed, the patch never
takes effect for that call site and never will — permanently, because there is no second compilation
to fix it.

`ModLink.OnLoad` runs during mod loading, well before combat code executes, which is what protects
you. That protection ends the moment anything is patched lazily — on a timer, on scene load, on
session start, or in response to content being registered. If deferred patching is ever added,
this becomes live, and the mitigation is the usual one: do not patch tiny members, prefer a larger
method or an interface boundary.

## The diagnostic technique, which transfers whole

The investigation next door only closed because **the absence of a log line was itself the evidence**,
and it took three prior investigations before anyone added the instrument that made that possible.
The pattern:

- A trace patch on the *same target* as each critical patch, writing one line per invocation.
- A second trace written from **non-patch** code, as a control that proves the process is alive and
  logging when the patched one goes quiet. That control is what turned "we cannot explain this" into
  a one-line answer: hook-dispatched breadcrumbs kept flowing for 18 more turn boundaries after the
  patch-driven ones stopped, in the same process and the same file.
- Flush per line, to a file separate from the engine log. Engine logs and stdout both buffer and both
  drop their tails when a process dies.
- **Keep one previous generation of that file.** The decisive artifact was the *rotated* copy: the
  machine had relaunched, and a truncate-on-launch policy had already destroyed the evidence for an
  earlier occurrence of the same bug.

For this mod the highest-value instance is a counter on whether the Execute gate fired, logged
per turn against whether a Ready was actually posted. That is one line and it makes the worst failure
mode self-reporting.

## What is already right here

Recorded so nobody reworks it:

- **`wire-surface.lock` plus its Makefile check.** The right instinct, and the model for the
  patch-surface lock above.
- **The `Assembly-CSharp.dll` SHA256 assertion that refuses to deploy on mismatch**, plus the manual
  Steam "only update on launch" step in `GAME_BUILD.md`. Both exist because a game update silently
  invalidating patch targets is a known hazard here — which is more than most mods do.
- **The backstop comment in `Net/ExecutionPatches.cs`**: *"The real backstop is
  `CombatGameBridge.CommitTurn` reporting whether the turn actually advanced."* That is precisely the
  correct principle — verify the effect rather than trust the gate — already written down in the
  codebase. The recommendations above are mostly a request to generalise it from one patch to the
  patch set.

## If the mod ever runs under CoreCLR

It will not while it is a Phantom Brigade Unity mod. Flagged only so the assumption is written down
rather than inherited: everything in "Why it cannot happen here" depends on the Mono runtime, and if
that ever changes, the coin-flip detour loss becomes live and every suppression prefix in `Net/`
becomes a desync waiting on a JIT decision.
