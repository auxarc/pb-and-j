#!/usr/bin/env bash
#
# ============================================================================
# M8 (the replay handoff) playtest, driven end to end.
#
#   tools/playtest-m8.sh <stage> [save-key]
#
# Stages, each runnable on its own so a failure is picked up where it happened:
#
#   solo      ONE instance. Execute a turn, then replay it locally with
#             pbj.replay-last and watch. The cheapest possible first look —
#             no second game, no network, no barrier.
#   up        launch both instances and wait for their drive channels
#   session   host on 2, join from 3, prove the handshake
#   lobby     choose a save, both agree, watch the synchronised load land
#   fight     host enters a mission; the client follows it in
#   turn      both press Execute; assert the POSES reach the client and that
#             playback runs and unwinds cleanly
#   again     a second turn, which is the one that proves the unwind — the
#             first turn can look perfect and still leave every unit asleep
#   down      close both instances
#   all       up → session → lobby → fight → turn → again
#
# up / session / lobby / fight / down are delegated verbatim to
# playtest-m12b.sh. They are not M8's business and duplicating them would let
# the two copies drift.
#
# ============================================================================
# READ THIS BEFORE RUNNING — it is written for a session that did not build it.
# ============================================================================
#
# WHAT THIS IS FOR. M8's failure mode is VISUAL. Mechs posed with an elbow on a
# knee sail through every one of the 1680 passing tests, and so does a mech that
# never moves a joint at all. This script proves the DATA arrived and that the
# machinery did not throw; the question "do they walk" needs a human looking at
# the screen. Both halves are required. Neither substitutes for the other.
#
# PRECONDITIONS, none of which this script can create for you — see
# playtest-m12b.sh, they are identical, plus:
#   * `make deploy` must have run since the M8 changes landed. A 0.13.0 build
#     from before them has the pose wire and nothing feeding it, which looks
#     exactly like a failure of this milestone.
#   * The build must carry `pbj.select-unit`. The combat HUD is not entered
#     until a unit is SELECTED, and arriving in combat by loading a save selects
#     nothing — so `pbj.execute` refuses with "the execution view is not open"
#     until something presses F1. That is the whole of the mystery the older
#     notes recorded as unexplained; it is not client-specific and has nothing
#     to do with the network. Every stage below calls `wake_hud` for it.
#     `pbj.ready` still works without the view, and is the fallback.
#
# ⚠️ NEVER DRIVE A LOAD THE MOMENT game-wait.sh RETURNS. That script waits for
# the drive channel, which opens at OnLoadEnd — long before the game is ready.
# The launch SPLASH (logos, then a seizure warning that needs Enter or waits 30
# seconds out) sits over the main menu while the game already reports
# `state=mainmenu`. A load driven into that gap succeeds, and then the pending
# intro drops CIViewPauseRoot on top of the loaded battle — `pbj.drive-state`
# says `state=combat` throughout, so nothing else here can see it. Wait for
# `splash=False`, which is the field that exists for exactly this.
#
# ⚠️ AND DO NOT TRUST A SCREENSHOT ALONE. KWin serves the last rendered frame
# for a minimised or occluded window, so a capture showed the title menu long
# after the game had loaded, executed a turn and gone back to planning.
#
# THE ONE M8 RULE. NEVER COMMIT A TURN WHILE A REPLAY WINDOW IS PLAYING. The
# units are asleep — animators off, FullBodyIK and puppet holders deactivated —
# until the unwind runs, and a commit landing mid-window is the re-entrancy the
# unwind is least likely to survive. `pbj.drive-state` reports `replay=` for
# exactly this reason; every stage below waits for `replay=idle`. The deleted
# `pbj.commit` would walk straight past both the barrier and this rule, which is
# why it is not coming back.
#
# WHAT GOOD LOOKS LIKE, on the client's log:
#
#   [pb-and-j] turn 0 poses complete | 8 unit tracks | playing the battle
#
# and on the host's:
#
#   [pb-and-j] turn 0 poses | 8 unit tracks | broadcast to 1 peer
#
# WHAT FAILURE LOOKS LIKE, and each of these is a different bug:
#
#   "poses incomplete — 3 of 8 arrived"  a part was lost or never sent
#   "poses dropped: Ragged on ..."       capture built a track it cannot repair
#   "poses partly uncaptured"            the host had bones it could not read
#   no pose line at all                  nothing fed the wire; check the build
#   "replay driver failed, stopping"     the driver threw; the unwind DID run
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
RUN_LOG="$LOG_DIR/run-m8.log"

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

# Poll a log file rather than grepping it once. The drive channel answers from
# memory while Unity buffers its log writer, so a state assertion can pass
# seconds before the line describing it reaches the file. Paid for on 2026-08-14.
await_log() {
  local file="$1" pattern="$2" what="$3" timeout="${4:-60}"
  local deadline=$(( SECONDS + timeout ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    grep -q "$pattern" "$file" && { pass "$what"; grep "$pattern" "$file" | tail -1 | sed 's/^/     /'; return 0; }
    sleep 2
  done
  return 1
}

# The launch splash is over, so the game is genuinely drivable. See the header:
# the drive channel and even `state=mainmenu` both arrive first. The seizure
# warning clears on Enter or after 30 seconds on its own, so this may simply
# wait it out — that is fine, and it is why the timeout is generous.
await_ready() {
  local n="$1"
  # Already in a game? Then the launch is long behind us and the title menu is
  # deliberately gone.
  drive "$n" "pbj.drive-state" | grep -q "state=mainmenu" || { pass "instance $n is past the menu"; return 0; }
  # ⚠️ menu=True, NOT splash=False. `splash` reads false BEFORE the view enters
  # as well as after it leaves, so waiting on it passes on the first poll and
  # drives a game whose intro has not run. Measured, 2026-08-14.
  await "$n" "menu=True" "instance $n has finished its launch splash" 180
}

# The combat HUD is not entered until a unit is selected, and arriving in combat
# by LOADING A SAVE selects nothing — so pbj.execute refuses with "the execution
# view is not open" until this runs. That is the whole of the refusal the older
# notes recorded as unexplained. pbj.select-unit is the same method the F1-F6
# bindings call, and it reports what happened rather than that it pressed.
wake_hud() {
  local n="$1" out
  out="$(drive "$n" "pbj.select-unit 0")"
  printf '%s' "$out" | grep -q "selected unit" \
    || fail "instance $n would not select a unit, so its combat HUD will never open: $out"
  pass "instance $n has a unit selected — $out"
}

# THE ONE M8 RULE, as a function. Nothing that commits a turn may run until this
# has returned.
await_idle() {
  local n="$1" timeout="${2:-60}"
  await "$n" "replay=idle" "instance $n is not playing a window back" "$timeout"
}

# --- M8's own stages --------------------------------------------------------

stage_solo() {
  say "solo — one instance replays its own turn"

  # Worth doing first and worth doing every time the driver changes. It exercises
  # capture, the codec round trip, the remap, the sleep, the bone writes, the
  # palm sync and the unwind — everything except the wire — on ONE game, in about
  # a minute. pbj.replay-last deliberately bypasses ClientSession, so this proves
  # the driver and says nothing at all about the transport.
  await_ready "$SOLO_N"

  if ! drive "$SOLO_N" "pbj.drive-state" | grep -q "combat=True"; then
    drive "$SOLO_N" "pbj.combat-load" | tee -a "$RUN_LOG"
  fi
  await "$SOLO_N" "combat=True" "instance $SOLO_N is in a combat" 120

  wake_hud "$SOLO_N"

  # A session with no peers is still a session, and it is what makes capture
  # run at all: the postfix that captures a turn is guarded on HasSession, so a
  # game with no session records nothing and pbj.replay-last has nothing to
  # replay. Hosting on loopback needs no passphrase and costs nothing.
  if ! drive "$SOLO_N" "pbj.drive-state" | grep -q "session=host"; then
    drive "$SOLO_N" "pbj.host" | tee -a "$RUN_LOG"
    await "$SOLO_N" "session=host" "instance $SOLO_N is hosting (peerless, just to arm capture)" 30
  fi

  local before; before="$(drive "$SOLO_N" "pbj.drive-state" | grep -o 'turn=[0-9-]*')"
  note "starting from $before"

  drive "$SOLO_N" "pbj.execute" | tee -a "$RUN_LOG"
  await "$SOLO_N" "simulating=True"  "the turn is executing" 60
  await "$SOLO_N" "simulating=False" "the turn finished" 180

  local log; log="$(prefix_log "$SOLO_N")"
  await_log "$log" "keyframes |\|no keyframes recorded" "the turn was captured" 60 \
    || fail "nothing was captured — read $log"

  eyes "the next line replays the turn you just watched. Do the mechs WALK, or do they slide?"
  drive "$SOLO_N" "pbj.replay-last" | tee -a "$RUN_LOG"

  await_log "$log" "poses complete" "poses were applied to real skeletons" 30 \
    || fail "pbj.replay-last did not report poses — read $log for 'poses partly uncaptured' or 'failed the codec round-trip'"

  # The window is about five seconds. Waiting it out is the point: it is where
  # the unwind runs, and the unwind is what a first turn cannot prove.
  await_idle "$SOLO_N" 60
  grep -q "replay driver failed" "$log" && fail "the driver threw mid-window — read $log"
  pass "playback ran its full window and unwound"

  eyes "are the mechs animating normally again now, or frozen? Frozen means the unwind did not run."
}

stage_turn() {
  say "turn — both press Execute, and the POSES reach the client"

  await_ready "$HOST_N"
  await_ready "$PEER_N"
  await_idle "$HOST_N" 60
  await_idle "$PEER_N" 60

  local host_log peer_log
  host_log="$(prefix_log "$HOST_N")"
  peer_log="$(prefix_log "$PEER_N")"

  # Through CIViewCombatExecution.CheckAndAttemptExecution, which our prefix
  # turns into a Ready. On a freshly-loaded client that view is NOT entered and
  # pbj.execute refuses there — pbj.ready posts the local ready without the view
  # and is the working client-side lever. Why the view is closed is still
  # unexplained, and M8's driver touches that same HUD, so if this ever starts
  # working the difference is worth understanding rather than celebrating.
  # The client reached this fight by LOADING the host's save, so nothing is
  # selected and its combat HUD has never opened. Without this, pbj.execute
  # below refuses every time.
  wake_hud "$PEER_N"

  # Both are sent, unconditionally, rather than branching on the reply. A ready
  # already counted is idempotent and the selection has not moved, so the second
  # call cannot do harm — whereas parsing a refusal out of a reply string is the
  # kind of guess that reads as a broken playtest a year from now.
  note "$(drive "$PEER_N" "pbj.execute")"
  note "$(drive "$PEER_N" "pbj.ready")"
  drive "$HOST_N" "pbj.execute" | tee -a "$RUN_LOG"

  await "$HOST_N" "simulating=True"  "the turn is executing" 60
  await "$HOST_N" "simulating=False" "the turn finished" 180

  await_log "$host_log" "poses | " "the host broadcast pose tracks" 60 \
    || fail "the host never sent poses — grep $host_log for 'poses dropped' or 'poses partly uncaptured'"

  await_log "$peer_log" "poses complete" "the client received a COMPLETE pose set" 60 \
    || {
         grep -n "poses incomplete\|poses dropped\|malformed" "$peer_log" | tail -5 | sed 's/^/     /'
         fail "the client did not get a complete pose set — see above and read $peer_log"
       }

  eyes "on the CLIENT: do its mechs walk the host's paths, or slide in an idle pose?"
  eyes "watch the weapons especially — a rifle floating off the hand is the palm sync failing."

  await_idle "$PEER_N" 60
  grep -q "replay driver failed" "$peer_log" && fail "the client's driver threw mid-window — read $peer_log"
  pass "the client played a full posed window and unwound"

  grep -q "DIVERGED" "$peer_log" \
    && note "WARNING: the client logged DIVERGED — expected before correction, a problem after it"
}

stage_again() {
  say "again — a second turn, which is what proves the unwind"

  # A first turn can look flawless and still leave every unit asleep: the
  # symptom of a failed unwind is not visible until something else tries to
  # animate them. The second turn is that something else.
  eyes "before this runs: are the client's mechs idling normally, or standing frozen?"
  stage_turn
  pass "two consecutive posed turns — sleep and wake both survive repetition"
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
            say "M8 happy path complete — the verdict is what you SAW, not what passed" ;;
  *)        sed -n '2,63p' "$0" | sed 's/^# \{0,1\}//'; exit 64 ;;
esac
