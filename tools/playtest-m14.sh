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
#   beam      inject a beam into the host's NEXT turn, because no mech in this
#             campaign carries a beam weapon and measurement 2 is about beams
#             specifically. Run between `fight` and `turn`.
#   measure   measurements 2 and 3 — the _TimeSimulation beam A/B and the pool
#             key-set diff. Run it after `turn`, on the same fight. It refuses
#             unless the host recorded a beam, so `beam` comes first.
#             Client-side by construction: see the stage's own header for why
#             a solo host measures this at its blind spot.
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

# ⚠️ Every note here goes to STDERR, and that is not style. This function is
# called as `revealed="$(report_effects 3)"`, and `note` tees to STDOUT — so
# without the redirects the caller captures the note text along with the number,
# and `[ "$revealed" -gt 0 ]` compares a paragraph against zero. It fails, so a
# turn that put 378 effects on screen reports "the client put NONE on screen".
# Cost a real run on 2026-08-16. The notes still reach the terminal and the log,
# because `tee -a` is inside `note`.
report_effects() {
  local n="$1" trio shown revealed unplayable
  trio="$(effects_of "$n")"
  shown="${trio%%/*}"
  revealed="$(printf '%s' "$trio" | cut -d/ -f2)"
  unplayable="${trio##*/}"
  note "instance $n effects: $revealed shown this window, $shown on screen now, $unplayable unplayable" >&2
  if [ "${unplayable:-0}" -gt 0 ]; then
    note "WARNING: $unplayable effect(s) could not be shown at all — grep the log for 'cannot show effect'" >&2
    note "         that is measurement 3: the two machines disagree about their asset pools" >&2
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

# ---------------------------------------------------------------------------
# Injects a beam into the turn about to be fought, because measurement 2 needs
# one and no mech in this campaign carries a beam weapon.
#
# Run it AFTER `fight` and BEFORE `turn`, on the HOST — the host is the only
# machine that records (every recording path is gated on recordingAllowed,
# which is only ever true during the host's own simulation).
#
# Why a spawned entity rather than a re-equipped mech: BeamVizSystem.cs:31
# guards its subsystem-to-asset lookup on !item.hasAssetLink, so attaching the
# asset up front skips the equipment graph entirely while :74 still records
# through CombatReplayHelper.OnBeamTransform. The recorded track is the real
# thing. See BeamInjectGlue for the full argument.
# ---------------------------------------------------------------------------
stage_beam() {
  say "beam — inject a beam into the host's next turn"

  await_ready "$HOST_N"
  await_idle "$HOST_N" 60

  local keys key
  keys="$(drive "$HOST_N" "pbj.fx-beam-keys")"
  note "$keys"

  # Prefer a friendly-looking pool over an enemy one purely so the colour is
  # the one a player would normally see; either records identically.
  # Checked before parsing, not after: with no keys the line has no ' | ' tail
  # for the sed below to strip, so the whole log line would survive as the
  # "key" and fail much later as a missing asset pool.
  printf '%s' "$keys" | grep -q '| 0 beam pool(s)' \
    && fail "no beam asset pools on this install — pbj.fx-beam-keys found none"

  key="${PBJ_BEAM_KEY:-}"
  if [ -z "$key" ]; then
    key="$(printf '%s' "$keys" | sed 's/.*beam pool(s) | //' | tr ',' '\n' \
           | sed 's/^ *//;s/ *$//' | grep -v 'enemy' | head -1)"
  fi
  [ -n "$key" ] || fail "could not pick a beam key out of: $keys"
  pass "using beam pool '$key'"

  # A unit must be selected for the beam to have somewhere to fire from, and
  # arriving in combat by loading a save selects nothing.
  wake_hud "$HOST_N"

  local out
  out="$(drive "$HOST_N" "pbj.fx-beam-inject $key 4")"
  note "$out"
  printf '%s' "$out" | grep -q "fx-beam-inject |" \
    || fail "the beam was not injected: $out"
  pass "a beam is live on the host and will be recorded when the turn executes"

  eyes "on the HOST: there should be a beam coming out of one of your mechs right now, standing still. It is inert — it deals no damage (EnergyBeamEmission is deliberately not added) and it sweeps only once the turn runs."
  note "now run:  tools/playtest-m14.sh turn    then:  tools/playtest-m14.sh measure"
}

# ---------------------------------------------------------------------------
# Measurements 2 and 3 from plan revision 5. Run AFTER `turn`, on the same
# fight, without executing anything else — it replays the turn the client has
# already been sent, twice, which is the only way the two arms of an A/B are
# the same turn rather than two different ones.
#
# ⚠️ THIS STAGE MUST RUN ON THE CLIENT AND NOWHERE ELSE. A solo host measures
# the question at its blind spot: a host that has just executed leaves
# _TimeSimulation at its own end-of-turn simulationTime, which IS windowEnd
# (capture reads the same field) — so the mirror-off arm's baseline is already
# nearly right, both arms look alike whatever the shaders do, and the
# confounder detector reads clean because on a solo host nothing else is
# writing. A beam shader that genuinely samples the global would pass there and
# render wrong on every client.
# ---------------------------------------------------------------------------
stage_measure() {
  say "measure — the _TimeSimulation beam A/B, and the pool key-set diff"

  await_ready "$HOST_N"
  await_ready "$PEER_N"
  await_idle "$HOST_N" 60
  await_idle "$PEER_N" 60

  local peer_log; peer_log="$(prefix_log "$PEER_N")"

  # --- the precondition, and it is a hard gate -----------------------------
  # A run whose turn carried no beams answers nothing about beams, and looks
  # exactly like a clean result. Read on the HOST: every recording path is
  # gated on recordingAllowed, so a client reports zeroes for all of it.
  say "precondition — did this turn actually fire a beam?"
  local probe beams
  probe="$(drive "$HOST_N" "pbj.vfx-probe")"
  note "$probe"
  beams="$(printf '%s' "$probe" | grep -o 'beams=[0-9]*' | head -1 | cut -d= -f2)"
  if [ "${beams:-0}" -eq 0 ]; then
    note "the host recorded NO beam tracks for this turn."
    note "measurement 2 is about beams specifically: ReplayEntityAssetBeam.ApplyTime never"
    note "calls SampleForReplay, so the particle-immunity argument that closed this worry"
    note "for standalone effects does not reach beams at all."
    note "run the 'beam' stage before 'turn' — it injects one:"
    note "    tools/playtest-m14.sh beam && tools/playtest-m14.sh turn"
    fail "no beams in this turn — inject one and refight, then re-run measure"
  fi
  pass "the host recorded $beams beam track(s)"

  # --- measurement 3, taken first because it needs no replay ---------------
  say "measurement 3 — do the two installs agree on their asset pool table?"
  local host_pools peer_pools
  host_pools="$(drive "$HOST_N" "pbj.fx-pools")"
  peer_pools="$(drive "$PEER_N" "pbj.fx-pools")"
  note "host:   $host_pools"
  note "client: $peer_pools"

  local host_digest peer_digest host_null peer_null
  host_digest="$(printf '%s' "$host_pools" | grep -o 'digest=[0-9a-f]*' | cut -d= -f2)"
  peer_digest="$(printf '%s' "$peer_pools" | grep -o 'digest=[0-9a-f]*' | cut -d= -f2)"
  host_null="$(printf '%s' "$host_pools" | grep -o 'prefabNull=[0-9]*' | cut -d= -f2)"
  peer_null="$(printf '%s' "$peer_pools" | grep -o 'prefabNull=[0-9]*' | cut -d= -f2)"

  if [ "$host_digest" = "$peer_digest" ]; then
    pass "the key sets agree — digest $host_digest"
  else
    note "DIGESTS DIFFER: host $host_digest, client $peer_digest"
    note "diff the two pb-and-j.asset-pools.txt files named in the lines above"
  fi

  # A digest match is NOT a clean bill of health, and this is the field that
  # says so. DataContainerAssetPool.OnAfterDeserialization keeps its entry when
  # Resources.Load fails — it warns and moves on with a null prefab — so two
  # machines can agree on every key while one cannot instantiate a pool at all.
  if [ "${host_null:-0}" != "${peer_null:-0}" ]; then
    note "prefabNull DIFFERS: host ${host_null}, client ${peer_null}"
    note "the key sets can still match here. This is the case a digest alone would MISS:"
    note "one install cannot instantiate pools the other can. Grep both logs for"
    note "'Failed to load pooled asset prefab'."
  else
    pass "both installs failed to load the same ${host_null:-0} prefab(s)"
  fi

  # --- measurement 2 -------------------------------------------------------
  say "measurement 2 — the client's real _TimeSimulation baseline"
  local baseline
  baseline="$(drive "$PEER_N" "pbj.fx-tsim")"
  note "$baseline"
  note "THIS NUMBER IS WHAT THE MEASUREMENT IS ABOUT. A client reaches none of the"
  note "writers that keep the global current, so it holds whatever was last left there"
  note "— measured once as a stale OVERWORLD value of 49.12. If it instead sits within"
  note "a second or two of the turn's end time, this is not the stale case and the run"
  note "proves less than it appears to; stage it with pbj.fx-tsim-set and say so."

  local arm
  for arm in 0 1; do
    say "arm mirror=$arm"
    note "$(drive "$PEER_N" "pbj.fx-mirror $arm")"

    # Same stored capture both times. NetGlue.RememberPlayed exists precisely
    # so this is one turn replayed twice rather than two different turns.
    note "$(drive "$PEER_N" "pbj.replay-last")"
    sleep 3
    await_idle "$PEER_N" 90

    local state
    state="$(drive "$PEER_N" "pbj.drive-state")"
    note "     $(printf '%s' "$state" | grep -o 'beams=[0-9]*/[0-9]* tsim=[^ ]* overwrites=[0-9]* mirror=[a-z]*')"

    local shown_beams overwrites
    shown_beams="$(printf '%s' "$state" | grep -o 'beams=[0-9]*/[0-9]*' | cut -d= -f2)"
    overwrites="$(printf '%s' "$state" | grep -o 'overwrites=[0-9]*' | cut -d= -f2)"

    [ "${shown_beams%%/*}" -gt 0 ] \
      || note "WARNING: the client put NO beams on screen this arm — check for 'no beam helper' in $peer_log"

    # The detector the whole A/B is trusted on. Sampling the global at the
    # window's two ends cannot catch a writer that writes the same value every
    # frame, and such a writer still wins at render time on every frame.
    if [ "${overwrites:-0}" -gt 0 ]; then
      note "OVERWRITES=$overwrites — something else wrote _TimeSimulation during the window."
      note "THE RUN IS VOID. That is not a defect in the mirror; it IS the finding."
      note "Suspects, in order: SimulationTimeSystem.cs:117 (fires only while sim time is"
      note "advancing, and sets combat.Simulating at :63 — so if it fired, the"
      note "ActionRecordingSystem row falls with it), and CombatIntroStartupSystem.cs:367."
    else
      pass "arm mirror=$arm ran unconfounded (overwrites=0)"
    fi

    eyes "arm mirror=$arm — WATCH THE BEAM. Its motion, thickness and any scrolling along its length. Do not judge the flare or ember particles: those are frozen at timeScale 0 in BOTH arms and in vanilla host replay too, so they are parity-safe and outside this measurement."
  done

  # Left OFF, which is the shipped behaviour. A run that walked away with the
  # mirror on would silently change what every later measurement means.
  note "$(drive "$PEER_N" "pbj.fx-mirror 0")"

  say "the verdict is what you SAW across the two arms"
  note "identical beams + overwrites=0 + a genuinely stale baseline"
  note "    -> no shader in that beam samples _TimeSimulation. The mirror stays out."
  note "different beams"
  note "    -> the mirror is load-bearing and ships, with the restore-on-unwind."
  note "either way: the finding is about _TimeSimulation SPECIFICALLY. _GlobalSimulationTime"
  note "(4 writers) and _GlobalUnscaledTime are unmeasured neighbours — see"
  note "docs/notes/timesim-measurement.md, which is where the readings go."
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

# ⚠️ EVERY DELEGATED STAGE IS GATED ON BOTH INSTANCES HAVING FINISHED THEIR
# LAUNCH SPLASH, and this gate is the whole reason this wrapper is not a one
# liner. game-wait.sh returns when the DRIVE CHANNEL opens, which is long before
# a game is ready to be driven — and a stage sent into that gap SUCCEEDS. The
# load lands, and the pending intro then drops the title view on top of the
# loaded battle. pbj.drive-state reports state=combat throughout, so no
# assertion in any stage can see it.
#
# Cost a full two-instance run on 2026-08-15: session, lobby and fight were all
# driven while the client still read splash=True, every check passed, and the
# client sat on its title menu while its own state said it was in the fight.
# The M8 header documents the trap; nothing was enforcing it.
delegate() {
  say "delegating '$1' to playtest-m12b.sh"

  if [ "$1" != "up" ] && [ "$1" != "down" ]; then
    await_ready "$HOST_N"
    await_ready "$PEER_N"
  fi

  "$HERE/playtest-m12b.sh" "$@" || fail "playtest-m12b.sh $1 failed — fix it there, not here"

  # And again after launching, so the NEXT stage cannot be the one that races.
  if [ "$1" = "up" ]; then
    await_ready "$HOST_N"
    await_ready "$PEER_N"
  fi
}

case "$STAGE" in
  solo)     stage_solo ;;
  up)       delegate up ;;
  session)  delegate session ;;
  lobby)    delegate lobby "$SAVE_KEY" ;;
  fight)    delegate fight ;;
  turn)     stage_turn ;;
  again)    stage_again ;;
  beam)     stage_beam ;;
  measure)  stage_measure ;;
  down)     delegate down ;;
  all)      delegate up; delegate session; delegate lobby "$SAVE_KEY"; delegate fight
            stage_turn; stage_again
            say "M14 happy path complete — the verdict is what you SAW, not what passed" ;;
  *)        sed -n '2,90p' "$0" | sed 's/^# \{0,1\}//'; exit 64 ;;
esac
