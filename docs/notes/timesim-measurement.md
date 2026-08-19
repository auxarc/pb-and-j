# `_TimeSimulation` on a client, and the pool key-set comparison

M14 measurements 2 and 3, from `m14-replayed-vfx.md` revision 5. Measurement 2 is a **named merge
gate** for anything that ships beam rendering, not a footnote.

This file is the record. The plan file is where the argument was had; this is where the readings live.

---

## Measurement 2 — does a replayed beam need the shader clock?

### Why beams and not everything

Revision 4 closed the frozen-clock worry for standalone effects with a specific argument:
`AssetLinker.SampleForReplay` calls `ParticleSystem.Simulate(t)`, which samples at an absolute time
and is therefore immune to `Time.timeScale == 0`.

**That argument does not reach beams.** `ReplayEntityAssetBeam.ApplyTime`
(`decompiled/ReplayEntityAssetBeam.cs:48-93`) never calls `SampleForReplay`; it writes the transform
and then `fxHelperBeam.SetAll(x, y)` / `SetScale(1, 1, z)`, and nothing else. The only two
`SampleForReplay` call sites in the whole decompile are `ReplayEntityAssetProjectile.cs:179` and
`ReplayEntityAssetStandalone.cs:48`, so no other path reaches a beam either.

The beam body is a scaled mesh (`FXHelperBeam.cs:81-85`) whose renderers receive `_FullyExtended`,
`_Thickness` and colours through a material property block (`:123-127`). Whether that shader also
samples a time global is **unknowable from the decompile** — shaders are compiled assets. Hence a
measurement rather than a reading.

### Every writer of `_TimeSimulation`, with its gate

Earlier notes named three and said a client reaches none. There are seven:

| Writer | Value | Gate | Reaches a client? |
|---|---|---|---|
| `CombatReplayHelper.cs:970` | `timeRequested`, absolute | inside vanilla `ApplyTime` | No — host replay only |
| `ActionRecordingSystem.cs:42` | `combat.simulationTime.f` | `:36` `!combat.Simulating` returns; reactive on SimulationTime | No |
| `ShaderHelper.cs:73` | `Time.time` | `!Application.isPlaying` | No — editor only |
| `OverworldSimulationTimeSystem.cs:151` | overworld clock | overworld state | Yes, in the overworld |
| `BaseCrawlerTimeSystem.cs:39` | overworld clock | overworld state | Yes, in the overworld |
| `CombatIntroStartupSystem.cs:367` | `Time.unscaledTime` | intro sweep, `shotCurrent != null` | **Yes** |
| `SimulationTimeSystem.cs:117` | `combat.simulationTime.f` | `:59` `!Approximately(f, f2) && f <= f2` | **Conditionally** |

Two things worth not re-deriving:

- **The last two rows are one hypothesis, not two.** `SimulationTimeSystem.cs:63` sets
  `combat.Simulating = true` inside the same block that reaches the write at `:117`. If that writer
  ever fires on a client, the `ActionRecordingSystem` row's "No" falls with it.
- **A client never advances `simulationTime`** — `src/PBAndJ.Mod/Net/ActuatorGlue.cs:358` and
  `CombatGameBridge.Snapshot.cs:84`. So the conditional row is expected not to fire, consistent with the
  stale `49.12` measured once on a real client. Expected, not proven: it is observed per run rather
  than asserted.

### The clock to mirror is absolute, not turn-local

Vanilla writes `timeRequested` (`CombatReplayHelper.cs:961-970`) — the same absolute clock it hands
to `CheckAssetTrackActivation` and `ApplyTimeToActiveAssetTracks`. Our cursor is on that clock too:
capture sets `windowStart = CombatReplayHelper.turnStartTime` and
`windowEnd = combat.simulationTime.f` (`CombatGameBridge.Keyframes.cs:100-103`), and `KeyframePlayer` seeds
`cursor` from `WindowStart` and passes it straight to `track.ApplyTime(cursor)`.

⚠️ The deleted `TimeSimProbeGlue` mirrored a 0-based `elapsed`. It was internally consistent — its
synthetic track used the same local clock, so its own negative result stands — but restoring it
verbatim against real tracks would have fed shaders a clock the game never writes.

### 🔴 The confounder, and why start/end sampling is not enough

If any other writer fires during the playback window, the mirror is **competing**, and the last write
before the frame renders wins. Our pump is a `Heartbeat.Update` postfix (`NetGlue.cs:913`); whether
that lands before or after the Entitas systems is a script-execution-order question no decompile
answers. **A confounded run looks exactly like the result we hope for.**

Sampling the global at the window's two ends does not catch it: a writer that writes the *same value
every frame* is invisible to start/end sampling and to per-frame min/max alike, while still winning
at render time on every frame.

The detector is therefore an **echo check** — read the global back at the top of each frame and
compare it against the cursor written last frame. `overwrites > 0` **voids the run**; it is not a
defect in the mirror, it is the finding.

### 🔴 Why this cannot be measured on a solo host

On a solo host, execution has just finished, so `_TimeSimulation` holds the last value
`ActionRecordingSystem:42` wrote — `combat.simulationTime.f` at end of turn, which is **exactly
`windowEnd`**, because capture reads the same field. The mirror-OFF arm's baseline is therefore
already nearly correct, and the mirror-ON arm sweeps `windowStart → windowEnd` and lands on that same
number.

A **client's** baseline is arbitrarily wrong: `49.12` from the overworld, or `Time.unscaledTime` left
by the intro sweep, which is hundreds of seconds off.

So a host-side A/B compares the two arms with the *least* contrast available, while its `tsim`
detector reads perfectly clean — because on a solo host nothing else genuinely is writing. **A beam
shader that really does sample `_TimeSimulation` can pass on a host and render wrong on every
client.** The gate is a two-instance run. `pbj.fx-tsim-set` exists so a host can stage the client's
baseline for a rehearsal, which is a rehearsal and not the gate.

### What this measurement does NOT settle

- **`_GlobalSimulationTime`** — a Vector4 written beside `_TimeSimulation` at
  `SimulationTimeSystem.cs:116`, `OverworldSimulationTimeSystem.cs:150`, `BaseCrawlerTimeSystem.cs:38`
  and `SimulatedTimeEmulator.cs:20`. Vanilla replay writes **only** `_TimeSimulation`, so mirroring
  only that is exact vanilla-replay parity — but a beam sampling `_GlobalSimulationTime` is frozen in
  both arms *and* in vanilla host replay.
- **`_GlobalUnscaledTime`** (`ShaderHelper.cs:75`) — written every frame from unscaled time, so
  anything sampling it animates everywhere and is never at risk.
- **`FXHelperBeam.systemFlare` / `systemEmbers`** (`FXHelperBeam.cs:27-29`) — ParticleSystems that
  nothing in `SetAll`/`SetScale`/`Refresh` ever samples. They run on the ordinary Unity clock, parked
  at `timeScale == 0` during planning, so they are frozen in both arms — and frozen in vanilla host
  replay too, which makes it **parity-safe rather than a defect**. Not detectable by this A/B and not
  to be read into an "identical beams" result.

The finding is therefore always phrased **"no shader in that beam samples `_TimeSimulation`"**, never
"samples the global".

### Getting a beam into the turn at all

No mech in this campaign carries a beam weapon, and the measurement is about beams specifically — so
one has to be put there. `pbj.fx-beam-inject <key> <seconds>` spawns one; `pbj.fx-beam-keys` finds a
key by **inspecting each pool's prefab for an `fxHelperBeam`** rather than by guessing at names, which
matters because `BeamVizSystem.cs:68` dereferences that field with no null check — a non-beam pool
would NRE inside the game's own system every frame.

**It spawns a beam entity rather than re-equipping a mech**, and the reason is a single guard:
`BeamVizSystem.cs:31` gates its subsystem-to-asset lookup on `!item.hasAssetLink`. Attach the asset
first and that entire branch is skipped — no equipment entity, no part graph, no action, nothing
written to the save — while `:74` still calls `CombatReplayHelper.OnBeamTransform` exactly as it does
for a fired beam. **The recorded track, the wire bytes and the client's replay are therefore
indistinguishable from a real beam.** Only the host-side question of how the beam came to exist
differs, and that is not what is being measured.

Three details that are load-bearing:

- ⚠️ **`EnergyBeamEmission` is deliberately NOT added.** That is the component
  `BeamProjectionSystem` matches on (`:78`) — the half that raycasts, damages, spawns impacts and
  builds reflection children. An injected beam must not be able to kill anything, or the measurement
  changes the fight it is measuring.
- **`BeamEmitter` IS required.** `BeamVizSystem.cs:29` reads `item.beamEmitter.combatID`
  unconditionally, above every other guard in that loop.
- **Teardown is the game's.** Setting `isDestroyed` is the entire trigger for `BeamDestroySystem`
  (`:13`), which calls `fxHelperBeam.OnBeamEnd()` and `CombatReplayHelper.OnBeamEnd` — and the latter
  is what stamps `timeEnd` and records the closing keyframe. A beam torn down any other way leaves a
  track that never ends.

The beam sweeps across its life rather than holding still: two identical keyframes would record a
beam that never moves, and whether motion survives a frozen shader clock is half of what the A/B is
looking at.

### The protocol

Two instances, the second joined as a genuine client. Measurement 3 comes out of the same session.

Scripted as `tools/playtest-m14.sh`: `up` → `session` → `lobby` → `fight` → **`beam`** → `turn` →
**`measure`**.

1. Host and client in a co-op battle. Inject a beam on the **host** (`beam`), then execute the turn.
2. Host: `pbj.vfx-probe` → confirm `beams=N`, `N > 0`. **If it is 0, stop.**
3. Client: `pbj.fx-tsim` → record the real baseline. This is the number the measurement is about.
4. Client: `pbj.fx-mirror 0`, replay, watch the beam. Record `beams=`, `tsim=`, `overwrites=` from
   `pbj.drive-state`.
5. Client: `pbj.fx-mirror 1`, replay the same turn, watch. Record all three again.
6. `pbj.fx-pools` on **both** machines.

Reading it:

| Observation | Conclusion |
|---|---|
| Identical beams, `overwrites=0`, baseline genuinely stale | No shader in that beam samples `_TimeSimulation`. The mirror stays out. |
| Different beams | The mirror is load-bearing and ships, with the restore-on-unwind revision 4 flagged. |
| `overwrites > 0` | **Void the run.** Something else is writing — that is the finding. |
| Baseline within a second or two of `windowEnd` | Not the stale case. Re-run after a fresh combat entry, or stage it with `pbj.fx-tsim-set`. |
| `beams=0/0` | The turn carried no beams. Nothing was measured. |

### Readings — 2026-08-16, mod 0.16.0, two instances, client is instance 3

✅ **ANSWERED: a shader in the beam DOES sample `_TimeSimulation`. The mirror is load-bearing and
ships.**

**The first attempt failed as an instrument, not as a run.** A turn with 378 effects, one of them the
beam, compared across two five-second replays: the verdict was "could not tell". The counters were
perfect and the answer was still unobtainable, which is worth recording — an A/B whose readout is
human recall does not survive a busy scene.

**What worked** was making the comparison a still image with exactly one variable:

1. An injected beam as the turn's **only** effect — host recorded `projectiles=0 standalone=0
   beams=1 keys=46`, client received `effects=0/1/0`, `beams=1/1`.
2. `pbj.fx-hold 2.5` froze playback mid-sweep (`replay=held@2.50`, `effects=1/1/0`).
3. `pbj.fx-tsim-set` moved the global by hand with nothing else on screen changing, and the frames
   were **photographed and diffed** rather than judged.

Interleaved, three shots per setting:

| Comparison | RMSE |
|---|---|
| Within baseline `230.87` (3 pairs) | 117.6, 118.1, 117.8 |
| Within `5.00` (3 pairs) | 117.8, 117.7, 117.8 |
| **Between settings (5 pairs)** | **319.9, 319.6, 319.8, 319.8, 319.8** |

Between-setting difference is **2.7× the noise floor** with no overlap between the groups. At an 8%
fuzz threshold the within-setting control shows **0 differing pixels** while the between-setting pair
differs across the frame — so the ~117 within-setting figure is uniform sub-threshold dithering, not
scene motion.

🔑 **And the difference is localised to the beam.** The difference image shows changed pixels
**only along the beam's length, in a dashed pattern** — the signature of a scrolling texture sampled
at two different times. Nothing else in the scene changed. That is what makes this a finding about
the beam rather than about something else on screen reading the global.

**Baselines observed, and they confirm the writer analysis.** Two separate client sessions read
`379.69` and `230.87` — in both cases very close to how long that instance had been running, which
identifies **`CombatIntroStartupSystem.cs:367`** (`Time.unscaledTime`, on the combat-intro camera
sweep) as the writer that leaves the value, not the overworld `49.12` the earlier note predicted.
Both are stale; this one names the source.

`overwrites=0` on every arm, so no run was confounded.

**Consequence:** without the mirror, a replayed beam's shader clock is pinned to an arbitrary
constant for the whole window — the texture does not scroll at all, where on the host it does. The
mirror is therefore not a refinement; it is the difference between a beam that animates and one that
is frozen mid-pattern.

### Still not measured

- **`_GlobalSimulationTime`** and **`_GlobalUnscaledTime`** — see above. This run says nothing about
  either, and a beam sampling the first would be frozen in vanilla host replay too.
- Whether any **non-beam** effect samples `_TimeSimulation`. Revision 4's A/B against
  `fx_mech_destruction_full` found no difference, so the two results are consistent — but the
  standalone case was measured once, on one effect.

---

## Measurement 3 — do two installs agree on their asset pool table?

"Asset keys resolve on the client" currently rests entirely on the handshake refusing a mismatched
game build and mod version. DLC and workshop content can diverge at identical versions, so the claim
has a hole only a comparison closes. `0 unplayable` on real turns proves these two installs agree,
not that any two do.

`pbj.fx-pools` prints `count`, a stable `digest`, and `prefabNull`, and writes the sorted key list to
`pb-and-j.asset-pools.txt` in the settings folder so a mismatch is diffable rather than merely known.

**The digest is FNV-1a 64 over ordinal-sorted UTF-8 keys** (`PBAndJ.Core.Net.AssetPoolDigest`, under
the coverage gate with a pinned regression vector). Explicitly **not** `string.GetHashCode`, which is
not stable across processes — the same reason `assetKeyHash` is computed client-side rather than sent.
The ordinal sort also neutralises the game's table being a `SortedDictionary` under a
culture-sensitive default comparer.

### 🔴 A digest match does not mean the keys are playable

`DataContainerAssetPool.OnAfterDeserialization` (`:60-66`) **keeps its entry when `Resources.Load`
fails** — it logs a warning and moves on with `prefab == null`. Two machines can therefore agree on
every key while one of them cannot instantiate a pool at all (`GetInstanceStandalone` bails at
`:160-162`).

So `prefabNull` is not a nicety. Without it, a digest match would be read as "asset keys resolve on
the client" and would be confidently wrong in exactly the case the comparison exists to catch.

| Observation | Conclusion |
|---|---|
| `count`, `digest` and `prefabNull` all equal | The tables agree and both can instantiate. |
| Digests equal, `prefabNull` differs | The key sets agree and the installs still do not. Diff the warnings in the log, not the key files — the keys are identical. |
| Digests differ | Diff the two `pb-and-j.asset-pools.txt` files. |

### Readings

_Unrun._
