# Can the two-instance rig run headless? — feasibility, with evidence

Written 2026-08-21 against `main` = `1708a0b`, working tree otherwise untouched. Investigation only —
no production code, no game launched, the user's desktop never touched. Every claim below is marked
as MEASURED (I ran it, on this machine, today), READ (I read the file named), or **UNVERIFIED**.

---

## Verdict

**Possible-with-caveats, via `gamescope --backend headless` — one gamescope per instance, rendering
kept real on the RTX 4070 Ti.** Every drive-channel, counter, and log reading in
`docs/notes/rig-run-1-0.md` survives unchanged, because the control channel is loopback TCP and
never touches a display. The two 🧑 human-eyes readings partially convert to screenshots. The
rendering-dependent readings (VFX pools, `presimulated`, keyframe playback, nightfall) survive
**because this route does not stop rendering** — that is why `-batchmode` is rejected below.

The one hop I could not take: actually launching Phantom Brigade inside the headless compositor —
launching the game was out of scope for this investigation. Everything around that hop is measured;
the hop itself is a 15-minute experiment specified in §8. Until it is run, the verdict is
"everything measurable says yes", not "yes".

What was MEASURED today, on this machine, without touching the desktop:

- `gamescope --backend headless` starts, hosts its own XWayland (`:1`), and needs no visible window.
- A real Vulkan **xcb** client (`vkcube --wsi xcb` — the same client shape as a Proton/DXVK game)
  selected **GPU 0: NVIDIA GeForce RTX 4070 Ti** inside it and got a flip swapchain at 16.67 ms.
  This goes through the system-wide `VK_LAYER_FROG_gamescope_wsi` layer, which is exactly how a
  Proton game presents inside gamescope.
- `gamescopectl screenshot <abs path>` produced a PNG of the rendered scene (verified by looking at
  it: a fully rendered cube, not black).
- **Two headless gamescopes run concurrently** — sockets `gamescope-0`/`gamescope-1`, XWayland
  `:1`/`:2` — and each is screenshottable **from outside** via
  `GAMESCOPE_WAYLAND_DISPLAY=gamescope-N gamescopectl screenshot <path>`. Both tore down cleanly
  (`pgrep -c gamescope` = 0 afterwards).

Nothing needs installing: gamescope 3.16.19 and gamescopectl are already at `/usr/bin`.

---

## 1. The CoqAuVin precedent — what transfers and what does not

The sister project at `/var/home/auxarc/dev/CoQ` runs Caves of Qud fully headless
(`tools/headless.sh`, READ in full). Its mechanism, honestly compared:

| axis | CoqAuVin (Qud) | pb-and-j (Phantom Brigade) | transfers? |
|---|---|---|---|
| binary | **native Linux** `CoQ.x86_64`, launched directly from the game dir | Windows exe through **Proton** (`proton run`, `tools/game-instance.sh:189-200`) | partially — both are direct launches that inherit environment, so a display can be substituted for both; the Proton layer is the untested extra hop |
| display | bare **Xvfb :99**, software rendering (llvmpipe; its own comments say a cold start takes ~60 s) | needs real GPU: a 3D title whose planned readings are about rendering | **no** — Xvfb/llvmpipe is viable for a tile game and would be somewhere between unusable and reading-distorting for PB; hence gamescope, which keeps the GPU (§4) |
| Steam | `SteamAppId=333640` env; **`SteamAPI_Init(): Loaded steamclient.so OK` MEASURED from inside Xvfb** while the Steam client ran on the desktop (`docs/design/steam-transport.md`, 2026-08-14, this machine). Qud **survives** init failure | same env-var mechanism already in `game-instance.sh`; but `SteamAPI.Init` failure is a **hard quit** (`Heartbeat.cs:13-15` → `SteamHelper.cs:68-73`) | **the load-bearing half transfers**: Steam-client IPC is per-user, not per-display — measured across displays on this exact machine. What does NOT transfer: Qud's tolerance of failure. PB gets no second chance, and PB's path runs through Proton's steamclient shim, which CoQ's measurement says nothing about. UNVERIFIED for Proton; §8 tests it |
| control | xdotool **XTEST keys** + wish console + a mod socket + log greps + `import` screenshots | **loopback TCP drive channel** (`tools/drive.sh`) — no keys needed at all | pb-and-j is *better* placed: its channel needs no display in the first place |
| rendering | none needed; screenshots are of a text/tile UI | several readings are **about** rendering | **no** — and this is why the CoQ route (Xvfb) is not the recommendation here |

One CoQ finding worth importing as a *named risk*, not a fact: Qud's `GameManager.Update` sleeps
forever when the window is unfocused (`bThreadFocus`), invisible for nineteen sessions. Phantom
Brigade has **measured counter-evidence**: on 2026-08-16 the client instance ran a full campaign
load, combat entry and 594 effect tracks while entirely hidden behind (and unfocused under) the
host's window (`tools/window-arrange.sh` header, READ). And inside gamescope the game is the
focused app of its own compositor by construction. So the trap class is real, PB appears not to
have it, and the §8 experiment would surface it immediately if it did.

**Bottom line on the precedent:** it proves the two things it can prove — a Unity game driven and
read entirely off-desktop is routine on this machine, and `SteamAPI.Init` is satisfiable from a
virtual display with the desktop Steam client running. It does **not** prove the Proton hop or the
rendering hop; those are pb-and-j's own, and §4/§8 address them with pb-and-j's own evidence.

## 2. Q1 — does the game start without a visible display? And what about `-batchmode`?

The launch path is `exec "$PROTON" run PhantomBrigade.exe -screen-fullscreen 0 …`
(`tools/game-instance.sh`, READ) — a direct child process that inherits its environment. So the
display it renders to is whatever `DISPLAY` names; nothing in the path insists on the desktop.
Under gamescope the child gets `DISPLAY=:1` pointing at the compositor's own XWayland (MEASURED).

`-batchmode -nographics` is *reachable* — Unity args pass straight through (the rig already passes
`-screen-*`) — and is **rejected as the route**:

- It skips rendering, and the runbook's readings are partly about rendering: R1·8's
  `particleBlocks`/`presimulated`, the VFX-pool and keyframe behaviour M14/M15 verified with eyes,
  R1·7's corpse pose, R1·11's button swap. Under `-nographics` those range from unmeasurable to
  meaningless. The project's own law applies: "Frame counts are not evidence of rendering; only
  eyes are" (`m14-pool-and-vfx-probed.md`) — batchmode has no eyes at all.
- Whether this title even survives `-batchmode` under Proton is UNVERIFIED, and with a better route
  available there is no reason to spend a launch finding out.

A virtual display makes the question moot: the game renders normally, just nowhere visible.

## 3. Q2 — is `SteamAPI.Init` satisfiable off the desktop?

- The Steam **client** is running right now (13 processes, MEASURED) and must keep running; killing
  it hard-quits every instance (`two-game-instances-max.md`). The client stays where it is — on the
  user's desktop session, minimized or not. That does not conflict with the prize: the *game
  windows* are what dominate the screen today, not the client.
- Steamworks reaches the client over per-user IPC (pipe/shared memory), not over the display.
  Direct measurement on this machine: CoQ's game on Xvfb `:99` — a different display from the
  client — logged `SteamAPI_Init(): Loaded steamclient.so OK`, got user stats, enumerated 25
  relays, and completed a P2P self-connect (`CoQ/docs/design/steam-transport.md`, MEASURED
  2026-08-14 by that project).
- **UNVERIFIED**: the same across Proton's `lsteamclient` shim from inside a gamescope. No reason
  is known why the shim would be display-bound — it talks to the same client — but per this
  project's rules that is a mechanism stated, not shown. §8 step 3 is the test.
- Transient failure mode to expect (from `m15-part-destruction-built.md`): `SteamAPI_Init() failed`
  once killed both instances until the user **restarted the Steam client**; everything else checked
  out. On that symptom, restart the client before concluding the headless route failed.
- Running the Steam client itself with no desktop at all (own Xvfb, `-silent`): not needed for the
  prize, not investigated beyond noting `steamcmd` is **absent** and is not a substitute (it does
  not provide the runtime `SteamAPI.Init` IPC a game needs — it is a download/CLI tool).

## 4. Q3 — the virtual display, and why gamescope specifically

Installed on this machine (MEASURED with `command -v`; each PRESENT verdict from the same check
that returned absent for others, so the zero is not a pattern failure): `gamescope` 3.16.19,
`gamescopectl`, `Xvfb`, `xvfb-run`, `cage`, `kwin_wayland`, `wmctrl`, `xdotool`, `vkcube`,
`vulkaninfo`, ImageMagick, `ffmpeg`. Absent: `Xephyr`, `sway`, `weston`, `grim`, `steamcmd`,
`xwayland-run`.

Why not the alternatives:

- **Xvfb** (the CoQ route): no GPU. The NVIDIA driver's ability to present Vulkan into a bare Xvfb
  is doubtful and UNVERIFIED; the plausible fallback is lavapipe (CPU), which for a 3D mech title
  would crawl and would distort every timing reading (R0·1's `ms`, R1·4's stall). Kept only as the
  route of last resort.
- **cage / kwin_wayland --virtual**: plausible wlroots/KWin headless hosts, but screenshot tooling
  for them is absent (`grim` not installed; Bazzite is image-based, layering needs a reboot) and
  they add nothing gamescope lacks.
- **gamescope headless**: purpose-built for exactly this, already installed, and MEASURED working
  today (see the Verdict list): real-GPU rendering through the gamescope WSI layer, per-instance
  isolation, external screenshots, concurrent pairs, clean teardown. The compositor advertises a
  PipeWire capture node at startup ("stream available on node ID: N"), which is a video-capture
  avenue if screenshots prove too coarse (tooling UNVERIFIED).

Wrinkles observed while measuring, recorded so the §8 run does not read them as new:

- First (empty, client-less) gamescope run logged `NVVM compilation failed: 3` /
  `vkCreateComputePipelines failed (VkResult: -13)` at teardown. Subsequent runs with a real client
  rendered and screenshotted correctly. Cause unknown; watch for it.
- `vkGetPhysicalDeviceFormatProperties2 returned zero modifiers` errors for two DRM formats at
  startup — cosmetic in every observed run.
- `vulkaninfo` *inside* gamescope fails creating a raw-Wayland surface — irrelevant; games present
  via xcb/XWayland, which is the path that works (vkcube via `--wsi wayland` also failed, via
  `--wsi xcb` rendered — use the X11 path, which is what Proton uses anyway).
- `gamescopectl screenshot` with no composited frame produces **no file and no error** — a vacuous
  zero. Always check the PNG exists and open it; the pass condition is content, not exit code.
- `import -display :1 -window root` does **not** work as a screenshot fallback (MEASURED failing).
  `gamescopectl screenshot` with an **absolute path** is the mechanism.

## 5. Q4 — the control channel survives untouched

READ: `tools/drive.sh` is bash `/dev/tcp` to `127.0.0.1:2770N`; the game side is a loopback
`TcpListener` pumped from the `Heartbeat.Update` postfix (`drive-rig.md`). `tools/game-wait.sh`
polls the same socket. `grep -n "wmctrl\|xdotool\|DISPLAY"` over all three playtest scripts returns
zero hits — and the same grep bites on `tools/window-arrange.sh`, so the zero is about the scripts,
not the pattern. Nothing in the drive path knows a display exists.

`tools/window-arrange.sh` is the **only** display-bound tool, and headless makes its job vanish
rather than fail: its purpose is disambiguating two overlapped windows on one screen, and under
one-compositor-per-instance the ambiguity cannot arise — instance N *is* `gamescope-(N-2)`'s only
occupant, so a screenshot is labelled by construction (a stronger identification than the
pid→port mapping the desktop needs). Do not run it during a headless run; it would correctly exit 1
having found no desktop windows.

`make deploy`/`dist`/`test` run in the pb-dev distrobox and never touch a display (READ: Makefile).

## 6. Q5 — the 🧑 readings, honestly

The runbook has exactly two 🧑 readings (grep over `rig-run-1-0.md`), plus the free-form `eyes`
prompts in `playtest-m14.sh`:

- **R1·7 (corpse stays collapsed through planning)** — *becomes automatable with one caveat.* The
  reading is "the seconds after the kill", which a timed screenshot series takes better than a
  glance: `gamescopectl screenshot` at T+0/2/5/10/30 s on instance 3, then read the frames (an
  agent can read images; this project has already used held-frame diffing as an instrument,
  `m14-timesim-measured.md`). The caveat: **camera framing is now part of the instrument** — the
  screenshot shows what the client's camera shows, and whether `pbj.select-unit` (or anything else
  drivable) reliably frames the wreck is UNVERIFIED. Automatable in principle; promote it to
  "automated" only after one run where the wreck is demonstrably in frame.
- **R1·11 (M10 Leave-button swap)** — *stays human for now.* It needs a click on Multiplayer.
  Injecting input into gamescope's XWayland via `xdotool key` exited 0 (MEASURED) but gamescope
  logged `Unhandled libei event` and no client observably received anything — delivery is
  UNVERIFIED, and an exit code is not delivery. The runbook already marks this reading a
  non-gating ride-along, so leaving it human costs nothing. If it ever matters: verify XTEST→libei
  delivery with a throwaway client first, or take it on a desktop run.
- **`playtest-m14.sh` motion readings** (beam sweep, "does it SHOOT", battlefield-clean) —
  *degraded, not lost.* Screenshot series at ~2/s catch tracers-in-flight, leftover frozen effects,
  and beam presence; held-frame diffs catch "frozen vs animating". What polling screenshots cannot
  promise is a **sub-tenth-second muzzle flash** (the M14 measurement-1 case, which exists
  precisely because flashes live under 100 ms) — that needs the PipeWire video node (UNVERIFIED
  tooling) or human eyes. Say so in any run sheet rather than banking a screenshot miss as
  "no flash".

Everything else in the runbook — R0·1 through R1·6, R1·8 through R1·10b — is `tools/drive.sh`
replies and `Player.log` greps, and survives byte-for-byte.

## 7. Q6 — the ceiling and the Steam-launched instance

Neither constraint moves. The two-instance ceiling is about machine load ("taxing on my machine"),
not about screens; two headless instances cost the same GPU/CPU plus a little compositing. The
Steam-launched instance's undrivability is about how Steam launches it (no `PBJ_DRIVE_PORT`
environment), which headless does not touch; and both *script* instances are already drivable
today, so there was no drivability gap for headless to close. What headless does improve
operationally: "close instances as soon as the measurement is taken" becomes enforceable by
automation instead of courtesy, and a run no longer costs the user their screen — which is the
actual prize. The ask-before-launching courtesy stands.

## 8. The smallest confirming experiment — one sitting, ~15 minutes

Preconditions: Steam client running; Steam-launched game closed; both instances closed;
`make deploy` exits 0 (read the tail and the exit code, not the success words).

```bash
# 1. host instance inside a headless compositor (first gamescope launched => gamescope-0, X on :1)
gamescope -W 1280 -H 720 --backend headless -- tools/game-instance.sh 2 > /tmp/gs-host.log 2>&1 &

# 2. wait on the drive socket (the display-independent liveness signal)
tools/game-wait.sh 2        # PASS: "accepting on 127.0.0.1:27702"

# 3. the Steam hop: if this answers, SteamAPI.Init succeeded inside the compositor
tools/drive.sh 2 "pbj.drive-state"      # expect state=mainmenu | patched=<current count>

# 4. the rendering hop: screenshot must EXIST and SHOW the main menu (no file + exit 0 is the
#    vacuous case — open the image, do not trust the exit code)
GAMESCOPE_WAYLAND_DISPLAY=gamescope-0 gamescopectl screenshot /tmp/pb-headless-menu.png
sleep 2 && ls -la /tmp/pb-headless-menu.png   # then LOOK at it

# 5. (optional, same sitting) the pair: second instance, session smoke
gamescope -W 1280 -H 720 --backend headless -- tools/game-instance.sh 3 > /tmp/gs-client.log 2>&1 &
tools/game-wait.sh 3
tools/drive.sh 2 "pbj.host" && tools/drive.sh 3 "pbj.join"
tools/drive.sh 2 "pbj.net-status"       # expect session HOST, a peer connected
GAMESCOPE_WAYLAND_DISPLAY=gamescope-1 gamescopectl screenshot /tmp/pb-headless-client.png

# 6. down, as always
tools/playtest-m12b.sh down
```

Reading the failures apart (each step's zero means something different):

- `game-wait` reports **"vanished — it quit after starting"** → check the `-pbj2` prefix's
  `Player.log` for `SteamAPI_Init() failed`. If present: restart the Steam client and retry once
  (the m15 precedent) before concluding the Steam hop is closed to headless.
- Step 3 times out with the process alive → the game is up but stalled; screenshot it anyway
  (step 4) — a splash or a crash dialog in the PNG is the diagnosis. A stall at a splash that
  never advances would be PB's version of CoQ's focus trap, which the evidence says PB does not
  have; a screenshot settles it in one glance.
- Step 4 yields no file or a black frame while step 3 answered → the game runs headless but the
  render/present hop failed (the one place the NVVM error from §4 could be real). The rig is then
  still usable headless for every non-visual reading, and visuals stay on the desktop.
- All of 2–4 pass → the verdict is confirmed; the pair (step 5) is confidence, not a new mechanism.

If confirmed, the durable form is a ~10-line launch wrapper (env flag or
`tools/game-instance-headless.sh`) plus a screenshot helper — deliberately not written now.

## 9. What breaks or degrades under the headless route

- **R1·11 stays human** until XTEST→libei delivery is proven (§6).
- **Sub-100 ms visual events** (muzzle-flash presence) are not reliably capturable by screenshot
  polling; video via the PipeWire node is unproven tooling.
- **R1·7 depends on camera framing** that nothing currently guarantees.
- **"Compare the two screens"** prompts become comparing two PNG series — workable, but simultaneity
  is now two timestamps, not one glance.
- `tools/window-arrange.sh` must not be run (it would report failure by design); the runbook's
  pre-flight step naming it does not apply to a headless run.
- Machine load is unchanged: the ceiling of two stands, and closing instances promptly still
  matters.

## 10. What I could not determine (all UNVERIFIED, none contradicted)

1. **The launch itself**: Phantom Brigade under Proton inside headless gamescope — never run; the
   entire verdict is conditional on §8.
2. **SteamAPI.Init through Proton's shim from inside the compositor** — inferred from CoQ's
   native-game measurement on this machine, not measured for Proton.
3. **Input-event delivery** into a gamescope-hosted game via xdotool/XTEST (exit 0 observed,
   delivery not).
4. **PipeWire video capture** from the compositor's advertised node (no capture tool exercised).
5. **The NVVM/vkCreateComputePipelines error** seen once in an empty compositor — benign in every
   run that had a client, cause unknown.
6. Whether headless gamescope runs with **no desktop session at all** (fully logged out); all
   probes ran while a session existed, and the Steam client needs somewhere to live regardless.
7. Whether `pbj.select-unit` frames the camera well enough for screenshot-based R1·7.
