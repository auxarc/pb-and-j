#!/usr/bin/env bash
#
# The headless-rig confirming experiment, from docs/notes/headless-rig-feasibility.md §8.
#
#   tools/headless-experiment.sh            # host only  (steps 1-4, ~8 min)
#   tools/headless-experiment.sh --pair     # + the second instance and a session (~15 min)
#   tools/headless-experiment.sh --keep-up  # do not tear down at the end
#
# WHAT THIS ANSWERS. Whether Phantom Brigade will run inside a headless gamescope
# compositor with rendering still on the GPU, so the two-instance rig stops
# taking the user's desktop. Every component was measured separately on this
# machine; what has never been shown is the COMPOSED system -- specifically
# SteamAPI.Init through PROTON's shim inside the compositor. That is the whole
# point of running this.
#
# ⚠️ THE ONE THING THIS SCRIPT CANNOT DECIDE FOR YOU. Step 4 is a rendering
# reading and it ends in a PNG. A file existing is not the reading; a file whose
# contents are a black frame is a FAILURE that exits 0 everywhere else in this
# script. You have to open the image. The script refuses to print PASS for it
# and says so at the time, because "grep the output for success words" has
# already banked a failing run as a pass in this repo once.
#
# Preconditions, enforced below rather than assumed:
#   * the Steam CLIENT is running (SteamAPI.Init failing is a HARD QUIT, and the
#     CoQ measurement that makes this experiment worth running had the client up)
#   * the Steam-LAUNCHED game is closed, and so are both rig instances
#   * `make deploy` has been run and exited 0
#
# Two instances is the machine's ceiling and game-instance.sh enforces it. This
# script never launches a third.

set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO="$PWD"
RESULTS="/tmp/pb-headless-experiment-results.txt"
PAIR=0
KEEP_UP=0
FAILED=0

for arg in "$@"; do
  case "$arg" in
    --pair)    PAIR=1 ;;
    --keep-up) KEEP_UP=1 ;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 64 ;;
  esac
done

exec > >(tee "$RESULTS") 2>&1

say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
pass() { printf '   \033[32mPASS\033[0m  %s\n' "$*"; }
fail() { printf '   \033[31mFAIL\033[0m  %s\n' "$*"; FAILED=1; }
note() { printf '         %s\n' "$*"; }
look() { printf '   \033[33mLOOK\033[0m  %s\n' "$*"; }

# ---------------------------------------------------------------- preconditions

say "0. Preconditions"

if pgrep -x steam >/dev/null 2>&1 || pgrep -f 'steamwebhelper' >/dev/null 2>&1; then
  pass "Steam client is running"
else
  fail "Steam client is NOT running."
  note "SteamAPI.Init failing is a hard quit -- the game would vanish in step 2 and"
  note "you would read it as 'headless does not work' when the cause is unrelated."
  note "Start Steam, then re-run."
  exit 1
fi

# ⚠️ NOT `pgrep -f PhantomBrigade.exe`. That matches COMMAND LINES, so the very
# shell running the check matches itself and the count is never below 1 -- a
# guard that reports "an instance is already running" on a machine with none,
# for ever. Cost one false reading before it was caught.
#
# `pgrep -x` cannot rescue it either: the name is longer than 15 characters, so
# pgrep warns and returns 0 unconditionally. That is WORSE -- a zero that is
# structurally unable to be anything else, which reads as a clean machine.
#
# `ps -eo comm=` compares the kernel's own (15-char-truncated) process name, so
# nothing in this script's command line can match it. The canary below proves the
# method can still find something, so a 0 here means zero instances rather than a
# broken check.
RUNNING="$(ps -eo comm= | grep -c '^PhantomBrigade')"
CANARY="$(ps -eo comm= | grep -c '^steam$')"
if [ "$CANARY" -eq 0 ]; then
  fail "the instance check's canary found no 'steam' process, so its zero proves nothing."
  note "Either Steam really is down (the precondition above should have caught that)"
  note "or 'ps -eo comm=' is not behaving as expected here. Do not trust RUNNING=$RUNNING."
  exit 1
fi
if [ "$RUNNING" -eq 0 ]; then
  pass "no PhantomBrigade instance is running (canary: the same check sees steam)"
else
  fail "$RUNNING PhantomBrigade process(es) already running."
  note "Close the Steam-launched game and any rig instance first:  tools/playtest-m12b.sh down"
  note "The Steam-launched instance cannot be driven, and two is the ceiling."
  exit 1
fi

for b in gamescope gamescopectl; do
  if command -v "$b" >/dev/null; then pass "$b present: $(command -v "$b")"
  else fail "$b not found -- install gamescope"; exit 1; fi
done

MOD_DLL="$HOME/.local/share/Steam/steamapps/compatdata/553540/pfx/drive_c/users/steamuser/AppData/Local/PhantomBrigade/Mods/pb-and-j/Libraries/PBAndJ.Mod.dll"
if [ -f "$MOD_DLL" ]; then
  pass "mod is deployed ($(date -r "$MOD_DLL" '+%Y-%m-%d %H:%M'))"
  note "if that timestamp is older than your last build, run 'make deploy' first"
else
  fail "mod not deployed -- run 'make deploy' (with both instances closed) and check it exits 0"
  exit 1
fi

# ---------------------------------------------------------------- teardown

teardown() {
  if [ "$KEEP_UP" -eq 1 ]; then
    say "Leaving instances up (--keep-up)"
    note "tear down with: tools/playtest-m12b.sh down"
    return
  fi
  if [ "$FAILED" -eq 1 ]; then
    say "Leaving instances UP because something failed"
    note "The live process is the diagnosis. Useful next looks:"
    note "  tail -50 /tmp/gs-host.log"
    note "  grep -i 'steamapi\\|exception\\|vulkan' ~/.local/share/Steam/steamapps/compatdata/553540-pbj2/pfx/drive_c/users/steamuser/AppData/LocalLow/'Brace Yourself Games'/'Phantom Brigade'/Player.log | tail -30"
    printf '\n   \033[33m%s\033[0m\n' "TEAR DOWN AS SOON AS THE DIAGNOSIS IS SETTLED:"
    printf '   \033[33m%s\033[0m\n' "  tools/playtest-m12b.sh down"
    note "These are left up ONLY so you can read them. A Phantom Brigade instance"
    note "holds a GPU context, a Proton prefix and several hundred MB of RAM each,"
    note "and there are up to two of them plus their compositors. Nothing reaps them"
    note "for you -- the script's own teardown is skipped on this path by design."
    note "Leaving them running also poisons the NEXT run: the preconditions above"
    note "refuse to start while any instance is alive, so a forgotten pair reads as"
    note "'the experiment is broken' the following day."
    return
  fi
  say "6. Teardown"
  tools/playtest-m12b.sh down >/dev/null 2>&1 && pass "game instances down" || note "playtest down reported an issue"
  reap_compositors
}

# ⚠️ `playtest-m12b.sh down` knows nothing about compositors, and gamescope does
# not stay named `gamescope`: it execs into `gamescope-wl` (plus a
# `gamescopereaper` child). A `pkill -f 'gamescope -W'` therefore matches
# NOTHING and exits quietly, which is exactly what happened on 2026-08-22 — the
# surviving compositor held a Vulkan device, every later launch failed with
# vkCreateDevice -3, and three rounds were spent blaming setsid and a supposed
# two-compositor limit before the orphan was found. The teardown was the vacuous
# step, not the test.
#
# Kill by EXACT process name, verify, escalate to SIGKILL, and verify again —
# a teardown that cannot confirm it worked is the thing that poisons tomorrow.
reap_compositors() {
  local left
  pkill -x gamescope-wl 2>/dev/null
  pkill -x gamescopereaper 2>/dev/null
  sleep 3
  left="$(ps -eo comm= | grep -cxE 'gamescope-wl|gamescopereaper')"
  if [ "$left" -ne 0 ]; then
    note "compositor ignored SIGTERM; escalating to SIGKILL"
    pkill -9 -x gamescope-wl 2>/dev/null
    pkill -9 -x gamescopereaper 2>/dev/null
    sleep 3
    left="$(ps -eo comm= | grep -cxE 'gamescope-wl|gamescopereaper')"
  fi
  # Proton leaves winedevice.exe behind after the game dies; it is not a
  # compositor but it is the other thing nothing else reaps.
  pkill -9 -x winedevice.exe 2>/dev/null
  if [ "$left" -eq 0 ]; then
    pass "compositors reaped (verified by name, not by pkill's exit code)"
  else
    fail "$left compositor process(es) still alive after SIGKILL."
    note "They hold a Vulkan device. Until they are gone, EVERY new GPU"
    note "application on this machine — gamescope, games, vulkaninfo — will fail"
    note "with vkCreateDevice ERROR_INITIALIZATION_FAILED, and it will look like"
    note "a driver fault rather than a leftover process."
    note "  ps -eo pid,comm= | grep gamescope   then  kill -9 <pid>"
  fi
  # The canary that would have saved three rounds: prove a Vulkan device can
  # still be created at all. If this fails with everything reaped, the driver
  # itself is wedged and only a reboot (or a logout, which restarts the
  # compositor holding the leaked contexts) will clear it.
  if command -v vulkaninfo >/dev/null; then
    if timeout 25 vulkaninfo --summary >/dev/null 2>&1; then
      pass "Vulkan device creation still works — the machine is left as it was found"
      else
      fail "vulkaninfo can no longer create a device."
      note "NOTHING NEW WILL START ON THE GPU until this clears — your desktop keeps"
      note "working only because it already holds its device. A reboot clears it; a"
      note "logout usually does too. Say so rather than leaving it to be discovered."
    fi
  fi
}
trap teardown EXIT

# ---------------------------------------------------------------- step 1-2

say "1. Host instance inside a headless compositor"
note "gamescope -W 1280 -H 720 --backend headless -- tools/game-instance.sh 2"
gamescope -W 1280 -H 720 --backend headless -- "$REPO/tools/game-instance.sh" 2 > /tmp/gs-host.log 2>&1 &
GS_HOST=$!
note "compositor pid $GS_HOST, log /tmp/gs-host.log"

say "2. Wait on the drive socket (display-independent liveness)"
if tools/game-wait.sh 2 300; then
  pass "instance 2 is accepting on 127.0.0.1:27702"
else
  fail "instance 2 never came up."
  note "Read these apart -- they mean different things:"
  note "  'vanished -- it quit after starting'  => check Player.log for SteamAPI_Init() failed."
  note "     If present: restart the Steam client and retry ONCE (the m15 precedent) before"
  note "     concluding the Steam hop is closed to headless. This is THE question at issue."
  note "  no process ever appeared            => gamescope did not start the launcher;"
  note "     read /tmp/gs-host.log, not Player.log."
  note "  timeout with the process alive      => it is up but stalled; step 3 and 4 still"
  note "     worth taking by hand -- a splash in the PNG is the diagnosis."
  exit 1
fi

# ---------------------------------------------------------------- step 3

say "3. The Steam hop -- if this answers, SteamAPI.Init succeeded inside the compositor"
REPLY_STATE="$(tools/drive.sh 2 'pbj.drive-state' 2>&1)"
echo "$REPLY_STATE" | sed 's/^/         | /'
if printf '%s' "$REPLY_STATE" | grep -q 'state='; then
  pass "the game answered on the control channel from inside a headless compositor"
  note "This is the headline: the process is alive, Steam did not hard-quit it, and"
  note "drive.sh (pure loopback TCP) does not care that there is no desktop."
else
  fail "no state= in the reply."
  note "An empty or refused reply is NOT the same as a game that is down -- step 2 already"
  note "proved the socket accepts. Check /tmp/gs-host.log and Player.log before concluding."
fi

# ---------------------------------------------------------------- step 4

say "4. The rendering hop -- and the one step this script will not grade"
SHOT=/tmp/pb-headless-menu.png
rm -f "$SHOT"
GAMESCOPE_WAYLAND_DISPLAY=gamescope-0 gamescopectl screenshot "$SHOT" >/dev/null 2>&1
sleep 3
if [ -s "$SHOT" ]; then
  note "$(ls -la "$SHOT")"
  look "OPEN IT:  xdg-open $SHOT"
  look "The reading is whether it SHOWS THE MAIN MENU."
  note "  main menu visible  => rendering works headless. Verdict confirmed."
  note "  black/empty frame  => the game runs headless but the render/present hop failed."
  note "     (this is the one place the unexplained NVVM error could be real)"
  note "     The rig is then still usable headless for every NON-VISUAL reading;"
  note "     visual readings stay on the desktop. That is a partial win, not a loss."
  note "  splash or a dialog => it is stalled, not rendering-broken. Different fix."
  printf '   \033[33m%s\033[0m\n' "NOT GRADED BY THIS SCRIPT -- a file existing is not the reading."
else
  fail "no screenshot file was produced (or it is empty)."
  note "If step 3 passed, the game is running and only the capture failed -- that is still"
  note "the partial win above. Do not record this as 'headless does not work'."
fi

# ---------------------------------------------------------------- step 5

if [ "$PAIR" -eq 1 ]; then
  say "5. The pair -- confidence, not a new mechanism"
  gamescope -W 1280 -H 720 --backend headless -- "$REPO/tools/game-instance.sh" 3 > /tmp/gs-client.log 2>&1 &
  note "second compositor pid $!, log /tmp/gs-client.log"
  if tools/game-wait.sh 3 300; then
    pass "instance 3 is accepting on 127.0.0.1:27703"
    tools/drive.sh 2 'pbj.host' 2>&1 | sed 's/^/         | /'
    sleep 3
    tools/drive.sh 3 'pbj.join' 2>&1 | sed 's/^/         | /'
    sleep 5
    REPLY_NET="$(tools/drive.sh 2 'pbj.net-status' 2>&1)"
    echo "$REPLY_NET" | sed 's/^/         | /'
    if printf '%s' "$REPLY_NET" | grep -qi 'host'; then
      pass "a session stood up between two headless instances"
      note "⚠️ role=HOST alone is not a connected peer -- read the peer count on that line."
    else
      fail "no host role in pbj.net-status"
    fi
    SHOT2=/tmp/pb-headless-client.png
    rm -f "$SHOT2"
    GAMESCOPE_WAYLAND_DISPLAY=gamescope-1 gamescopectl screenshot "$SHOT2" >/dev/null 2>&1
    sleep 3
    [ -s "$SHOT2" ] && look "second compositor: xdg-open $SHOT2" || note "no second screenshot"
  else
    fail "instance 3 never came up -- the single-instance result above still stands"
  fi
fi

# ---------------------------------------------------------------- summary

say "Summary"
note "Steps 2 and 3 are the mechanism. Step 4 is yours to grade by eye."
note "Full transcript saved to: $RESULTS"
note ""
note "If steps 2-4 all pass, the durable form is a ~10-line launch wrapper"
note "(tools/game-instance-headless.sh) plus a screenshot helper -- deliberately"
note "not written yet, per docs/notes/headless-rig-feasibility.md §8."
if [ "$FAILED" -eq 0 ]; then
  note "No step reported FAIL."
else
  note "Something reported FAIL -- see above. Instances were left up for diagnosis;"
  note "run 'tools/playtest-m12b.sh down' once you have what you need."
fi
exit 0
