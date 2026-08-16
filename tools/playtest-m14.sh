#!/usr/bin/env bash
#
# ============================================================================
# M14 (projectiles, beams and VFX on a client) playtest, driven end to end.
#
#   tools/playtest-m14.sh <stage> [save-key]
#
# Stages, each runnable on its own so a failure is picked up where it happened:
#
#   solo      ONE instance. Execute a turn, then replay it locally with
#             pbj.replay-last and WATCH FOR GUNFIRE. The cheapest possible
#             first look — no second game, no network, no barrier.
#   up        launch both instances and wait for their drive channels
#   session   host on 2, join from 3, prove the handshake
#   lobby     choose a save, both agree, watch the synchronised load land
#   fight     host enters a mission; the client follows it in
#   turn      both press Execute; assert the EFFECTS reach the client and that
#             playback runs and unwinds cleanly
#   again     a second turn, which is the one that proves the unwind — the
#             first turn can look perfect and still leak every instance
#   down      close both instances
#   all       up → session → lobby → fight → turn → again
#
# up / session / lobby / fight / down are delegated verbatim to
# playtest-m12b.sh, exactly as playtest-m8.sh delegates them. They are not
# M14's business and duplicating them would let the copies drift.
#
# ============================================================================
# READ THIS BEFORE RUNNING — it is written for a session that did not build it.
# ============================================================================
#
# WHAT THIS IS FOR. M14's failure mode is VISUAL, and worse than M8's: a
# projectile flying sideways, an explosion at the world origin, and an effect
# scaled to nothing all sail through every passing test, and so does a client
# where nothing fires at all. This script proves the DATA arrived and that the
# machinery did not throw. The question "does the battle look like a battle"
# needs a human looking at the screen. Both halves are required.
#
# ⚠️ "NOTHING SHOOTS ON THE CLIENT" AND "EVERYTHING SHOOTS IN THE WRONG PLACE"
# LOOK IDENTICAL IN A LOG. That sentence is the whole reason the eyes prompts
# below are as specific as they are. Read them; do not skim them.
#
# PRECONDITIONS: identical to playtest-m8.sh — read its header, all of it
# applies (the launch splash, the screenshot trap, wake_hud, the one rule about
# never committing a turn while a window is playing), plus:
#   * `make deploy` must have run since the M14 changes landed. A 0.15.0 build
#     has no effect wire at all, which looks exactly like this milestone
#     failing.
#   * Both instances must report mod 0.16.0. A peer built without the
#     ReplayAssets type faults on the first one it receives.
#
# WHAT GOOD LOOKS LIKE, on the host's log:
#
#   [pb-and-j] turn 0 effects | 43 tracks in 1 part | broadcast to 1 peer
#
# and on the client's:
#
#   [pb-and-j] turn 0 effects complete | 43 tracks | the battle will be shot
#                                                    as well as walked
#
# WHAT FAILURE LOOKS LIKE, and each of these is a different bug:
#
#   "effects: none sent"              a quiet turn, OR capture found nothing.
#                                     Check the host said "effects |" at all.
#   "effects incomplete — 3 of 8"     a part was lost or never sent
#   "effects: N tracks dropped"       capture built tracks the host refused
#   "cannot show effect '...'"        the pools disagree between the machines
#   "recorded N effect tracks but no unit motion"
#                                     the climax-turn discard — everyone died
#   no effect line at all             nothing fed the wire; check the build
#   "replay driver failed, stopping"  the driver threw; the unwind DID run
#
# THE FOUR MEASUREMENTS this run exists to take, from plan revision 5. None is
# answered by a passing stage; each needs the eyes prompts or a follow-up:
#
#   1. Does a sub-frame effect activated a frame late render anything? This
#      decides whether CrossedDuring earns its instantiate cost or should fall
#      back to the game's own point test.
#   2. The _TimeSimulation A/B against a BEAM. Revision 4 measured exactly one
#      effect. Restore the probe with:
#        git show 24867e6^:src/PBAndJ.Mod/Net/TimeSimProbeGlue.cs
#   3. Pool key sets diffed on both machines — workshop content can diverge at
#      identical mod versions, and the handshake would not catch it.
#   4. Standalone churn on an alpha-strike frame (~20 activations at once).
#      Pooled-B is the documented fallback if it hitches badly.
#
# ============================================================================

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_N="${PBJ_HOST_INSTANCE:-2}"
SOLO_N="${PBJ_SOLO_INSTANCE:-2}"
PEER_N="${PBJ_PEER_INSTANCE:-3}"
STAGE="${1:-}"
SAVE_KEY="${2:-}"

LOG_DIR="${PBJ_PLAYTEST_LOG_DIR:-$HERE/../.playtest}"
mkdir -p "$LOG_DIR"
RUN_LOG="$LOG_DIR/run-m14.log"

prefix_log() {
  echo "$HOME/.local/share/Steam/steamapps/compatdata/553540-pbj$1/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log"
}

say()  { printf '\n\033[1m== %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; }
note() { printf '   %s\n' "$*" | tee -a "$RUN_LOG"; }
fail() { printf '\n\033[31mFAIL: %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; exit 1; }
pass() { printf '\033[32m   ok: %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; }
eyes() { printf '\n\033[33m>> LOOK AT THE SCREEN: %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; }

drive() {
  local n="$1" cmd="$2" out
  if ! out="$("$HERE/drive.sh" "$n" "$cmd" 2>&1)"; then
    note "[$n] $cmd -> UNREACHABLE"
    echo "$out" | tee -a "$RUN_LOG" >&2
    fail "instance $n did not answer '$cmd'"
  fi
  printf '[%s] %s -> %s\n' "$n" "$cmd" "$out" >> "$RUN_LOG"
  echo "$out"
}

await() {
  local n="$1" pattern="$2" what="$3" timeout="${4:-90}"
  local deadline=$(( SECONDS + timeout )) last=""
  while [ "$SECONDS" -lt "$deadline" ]; do
    last="$(drive "$n" "pbj.drive-state")"
    if printf '%s' "$last" | grep -Eq "$pattern"; then
      pass "$what"
      note "     $last"
      return 0
    fi
    sleep 2
  done
  note "     last state: $last"
  fail "timed out after ${timeout}s waiting for $what on instance $n"
}

await_log() {
  local file="$1" pattern="$2" what="$3" timeout="${4:-60}"
  local deadline=$(( SECONDS + timeout ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    grep -q "$pattern" "$file" && { pass "$what"; grep "$pattern" "$file" | tail -1 | sed 's/^/     /'; return 0; }
    sleep 2
  done
  return 1
}

# See playtest-m8.sh's header: the drive channel and even state=mainmenu both
# arrive before the launch splash is over. menu=True is the monotonic one.
await_ready() {
  local n="$1"
  drive "$n" "pbj.drive-state" | grep -q "state=mainmenu" || { pass "instance $n is past the menu"; return 0; }
  await "$n" "menu=True" "instance $n has finished its launch splash" 180
}

# The combat HUD is not entered until a unit is selected, and arriving in combat
# by LOADING A SAVE selects nothing — so pbj.execute refuses until this runs.
wake_hud() {
  local n="$1" out
  out="$(drive "$n" "pbj.select-unit 0")"
  printf '%s' "$out" | grep -q "selected unit" \
    || fail "instance $n would not select a unit, so its combat HUD will never open: $out"
  pass "instance $n has a unit selected — $out"
}

# THE ONE RULE, inherited from M8 and no weaker here: never commit a turn while
# a window is playing. M14 adds its own reason — the instances a window holds
# are handed back by Stop(), so a commit landing mid-window is the re-entrancy
# the sweep is least likely to survive.
await_idle() {
  local n="$1" timeout="${2:-60}"
  await "$n" "replay=idle" "instance $n is not playing a window back" "$timeout"
}

# The three numbers from pbj.drive-state's effects= field: live, cumulative,
# unplayable. The cumulative one is the only one worth asserting on — most
# effects last under a second, so a poll between two of them reads zero live.
effects_of() {
  drive "$1" "pbj.drive-state" | grep -o 'effects=[0-9]*/[0-9]*/[0-9]*' | cut -d= -f2
}

report_effects() {
  local n="$1" trio shown revealed unplayable
  trio="$(effects_of "$n")"
  shown="${trio%%/*}"
  revealed="$(printf '%s' "$trio" | cut -d/ -f2)"
  unplayable="${trio##*/}"
  note "instance $n effects: $revealed shown this window, $shown on screen now, $unplayable unplayable"
  if [ "${unplayable:-0}" -gt 0 ]; then
    note "WARNING: $unplayable effect(s) could not be shown at all — grep the log for 'cannot show effect'"
    note "         that is measurement 3: the two machines disagree about their asset pools"
  fi
  printf '%s' "${revealed:-0}"
}

# --- M14's own stages -------------------------------------------------------

stage_solo() {
  say "solo — one instance replays its own turn, and it should SHOOT"

  # Worth doing first and every time the driver changes. It exercises capture,
  # the split, the codec round trip, the client's own accumulator, the pool
  # checkout and the unwind — everything except the wire — on ONE game in about
  # a minute. pbj.replay-last bypasses ClientSession deliberately, so this
  # proves the driver and says nothing about the transport.
  await_ready "$SOLO_N"

  if ! drive "$SOLO_N" "pbj.drive-state" | grep -q "combat=True"; then
    drive "$SOLO_N" "pbj.combat-load" | tee -a "$RUN_LOG"
  fi
  await "$SOLO_N" "combat=True" "instance $SOLO_N is in a combat" 120

  wake_hud "$SOLO_N"

  # A peerless session is still a session, and it is what arms capture at all:
  # the postfix that captures a turn is guarded on HasSession.
  if ! drive "$SOLO_N" "pbj.drive-state" | grep -q "session=host"; then
    drive "$SOLO_N" "pbj.host" | tee -a "$RUN_LOG"
    await "$SOLO_N" "session=host" "instance $SOLO_N is hosting (peerless, just to arm capture)" 30
  fi

  eyes "this turn executes LIVE. Watch what actually fires — muzzle flashes, tracers, impacts, explosions. That is the reference the replay has to match."
  drive "$SOLO_N" "pbj.execute" | tee -a "$RUN_LOG"
  await "$SOLO_N" "simulating=True"  "the turn is executing" 60
  await "$SOLO_N" "simulating=False" "the turn finished" 180

  local log; log="$(prefix_log "$SOLO_N")"
  await_log "$log" "keyframes |\|no keyframes recorded" "the turn was captured" 60 \
    || fail "nothing was captured — read $log"

  eyes "the next line replays the turn you just watched. Does it SHOOT? Compare against what you just saw live."
  drive "$SOLO_N" "pbj.replay-last" | tee -a "$RUN_LOG"

  await_log "$log" "effects complete" "effects survived capture, the split, the codec and reassembly" 30 \
    || {
         grep -n "effects: none sent\|failed the codec round-trip\|effects: .* dropped" "$log" | tail -5 | sed 's/^/     /'
         fail "the replay reported no effects — see above and read $log"
       }

  local revealed; revealed="$(report_effects "$SOLO_N")"
  [ "${revealed:-0}" -gt 0 ] || fail "effects arrived but NONE was ever put on screen — the tracks reassembled and the activation never fired"
  pass "$revealed effect(s) reached the screen"

  eyes "MEASUREMENT 1 — did you see muzzle flashes, or only the tracers and impacts? A flash lives under a tenth of a second, which is the case CrossedDuring exists for. No flashes at all means it is paying for nothing and should fall back to the point test."
  eyes "are the projectiles flying along the paths the shots took, or sideways / from the map origin? Sideways is a transposed position and rotation."
  eyes "are any effects invisible where you expected one — a shot with an impact you can hear but not see? That is the zero-scale trap."

  await_idle "$SOLO_N" 60
  grep -q "replay driver failed" "$log" && fail "the driver threw mid-window — read $log"
  pass "playback ran its full window and unwound"

  eyes "is the battlefield CLEAN now — no frozen bullet hanging in mid-air, no explosion stuck burning? Anything left on screen is an instance the sweep did not hand back."
}

stage_turn() {
  say "turn — both press Execute, and the EFFECTS reach the client"

  await_ready "$HOST_N"
  await_ready "$PEER_N"
  await_idle "$HOST_N" 60
  await_idle "$PEER_N" 60

  local host_log peer_log
  host_log="$(prefix_log "$HOST_N")"
  peer_log="$(prefix_log "$PEER_N")"

  # The client reached this fight by LOADING the host's save, so nothing is
  # selected and its combat HUD has never opened.
  wake_hud "$PEER_N"

  # Both sent unconditionally rather than branching on the reply: a ready
  # already counted is idempotent, whereas parsing a refusal out of a reply
  # string is the kind of guess that reads as a broken playtest a year on.
  note "$(drive "$PEER_N" "pbj.execute")"
  note "$(drive "$PEER_N" "pbj.ready")"
  drive "$HOST_N" "pbj.execute" | tee -a "$RUN_LOG"

  await "$HOST_N" "simulating=True"  "the turn is executing" 60
  await "$HOST_N" "simulating=False" "the turn finished" 180

  await_log "$host_log" "effects | " "the host broadcast effect tracks" 60 \
    || {
         grep -n "effect tracks but no unit motion\|effects: .* dropped\|past the per-turn cap" "$host_log" | tail -5 | sed 's/^/     /'
         fail "the host never sent effects — see above and read $host_log"
       }

  await_log "$peer_log" "effects complete" "the client received a COMPLETE effect set" 60 \
    || {
         grep -n "effects incomplete\|effects: none sent\|malformed" "$peer_log" | tail -5 | sed 's/^/     /'
         fail "the client did not get a complete effect set — see above and read $peer_log"
       }

  local revealed; revealed="$(report_effects "$PEER_N")"
  [ "${revealed:-0}" -gt 0 ] \
    || fail "the client received effects and put NONE on screen — reassembly worked and activation did not"
  pass "the client put $revealed effect(s) on screen"

  eyes "on the CLIENT: does the battle look like a battle? Weapons firing, rounds travelling, impacts landing where the damage went."
  eyes "MEASUREMENT 1 — muzzle flashes present on the client, or missing while tracers and impacts arrive?"
  eyes "compare the two screens if you can. COLOUR is the one to check: an effect that is the right shape in the wrong colour means the hue and colour blocks are not being applied."

  await_idle "$PEER_N" 60
  grep -q "replay driver failed" "$peer_log" && fail "the client's driver threw mid-window — read $peer_log"
  pass "the client played a full window of effects and unwound"

  eyes "is the CLIENT's battlefield clean now? A frozen bullet in mid-air is an instance the sweep missed, and it stays until the mission ends."

  grep -q "cannot show effect" "$peer_log" \
    && note "WARNING: the client could not resolve an asset key — that is measurement 3, and it means the pools differ"
}

stage_again() {
  say "again — a second turn, which is what proves the sweep"

  # A first turn can look flawless and still have leaked every straddling
  # projectile: those tracks are STILL ACTIVE at the window's end by
  # construction, so nothing in the per-frame retirement ever releases them
  # and only Stop() does. The symptom is cumulative, so the second turn is
  # where it starts to show.
  eyes "before this runs: is the client's battlefield clear of leftover effects from the last turn?"
  stage_turn
  pass "two consecutive turns of effects — activation and the sweep both survive repetition"
  eyes "MEASUREMENT 4 — did either turn HITCH at the moment a lot fired at once? The standalone route instantiates per effect, and an alpha strike is where that would show."
}

# --- delegated stages -------------------------------------------------------

delegate() {
  say "delegating '$1' to playtest-m12b.sh"
  "$HERE/playtest-m12b.sh" "$@" || fail "playtest-m12b.sh $1 failed — fix it there, not here"
}

case "$STAGE" in
  solo)     stage_solo ;;
  up)       delegate up ;;
  session)  delegate session ;;
  lobby)    delegate lobby "$SAVE_KEY" ;;
  fight)    delegate fight ;;
  turn)     stage_turn ;;
  again)    stage_again ;;
  down)     delegate down ;;
  all)      delegate up; delegate session; delegate lobby "$SAVE_KEY"; delegate fight
            stage_turn; stage_again
            say "M14 happy path complete — the verdict is what you SAW, not what passed" ;;
  *)        sed -n '2,90p' "$0" | sed 's/^# \{0,1\}//'; exit 64 ;;
esac
