#!/usr/bin/env bash
#
# ============================================================================
# M12b two-instance playtest, driven end to end.
#
#   tools/playtest-m12b.sh <stage> [save-key]
#
# Stages, each runnable on its own so a failure is picked up where it happened
# rather than from the top:
#
#   up        launch both instances and wait for their drive channels
#   session   host on 2, join from 3, prove the handshake
#   lobby     choose a save, both agree, watch the synchronised load land
#   fight     host enters a mission; the fight is written, offered, and the
#             client follows it in
#   turn      both press Execute; the shared turn runs
#   wedge-d   pbj.ship-fight on the CLIENT — must log and not kill its session
#   drop      kill the client mid-entry; the host must drop it and fight on
#   down      close both instances
#   all       up → session → lobby → fight → turn (the happy path)
#
# ============================================================================
# READ THIS BEFORE RUNNING — it is written for a session that did not build it.
# ============================================================================
#
# WHAT THIS IS FOR. M12b is feature-complete and its Core half was reviewed into
# shape, but only Stage A (one instance, no peers) has ever been seen running.
# Everything this script drives has never executed against a second real game.
# Expect it to fail somewhere the first time; that IS the deliverable. Read
# ../docs/design/m12-concurrent-management.md §M12b·2 for what should happen.
#
# PRECONDITIONS, none of which this script can create for you:
#   * The Steam CLIENT must be running. The Steam-launched GAME must be closed —
#     SteamAPI.Init failing is a hard quit, and killing the client kills both
#     instances. Two instances is the ceiling and game-instance.sh enforces it.
#   * `make deploy` must have run with both games closed (it rm -rf's a mod
#     folder whose DLL a running instance holds open), and it must be a DEV
#     build — deploy sets PBJ_DRIVE=true, which is what opens the drive channel.
#     A shipping build has no channel and every drive.sh call will be refused.
#   * A pbj_-prefixed campaign save must already exist, and BOTH prefixes must be
#     able to see it. Pass its key as the second argument, or the script picks
#     the first one `pbj.saves` reports on the host.
#
# THE ONE RULE. Never send `ow.load-scenario` to the CLIENT. The mod's
# EnterCombat prefix stops only the last hop; by then ForceScenarioAndArea has
# stripped feature_check and re-rolled the combat description including loot.
# The suppression patch is a boundary, not permission to poke at it.
#
# WHY EVERYTHING POLLS. Writing the fight waits on DataManagerSave.CanSave, which
# was MEASURED at 6.5 seconds on a real entry (blocked by `the turn is being
# simulated`, not by the scenario intro the design doc predicted). Anything timed
# rather than observed races it. There is no bare sleep in this file for that
# reason; if you add one, you have introduced a flake.
#
# ============================================================================

set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST_N="${PBJ_HOST_INSTANCE:-2}"
PEER_N="${PBJ_PEER_INSTANCE:-3}"
STAGE="${1:-}"
SAVE_KEY="${2:-}"

LOG_DIR="${PBJ_PLAYTEST_LOG_DIR:-$HERE/../.playtest}"
mkdir -p "$LOG_DIR"
RUN_LOG="$LOG_DIR/run.log"

# Where each instance writes its own Player.log. Not the same directory for the
# two of them, which is the whole reason each gets its own Proton prefix.
prefix_log() {
  echo "$HOME/.local/share/Steam/steamapps/compatdata/553540-pbj$1/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log"
}

say()  { printf '\n\033[1m== %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; }
note() { printf '   %s\n' "$*" | tee -a "$RUN_LOG"; }
fail() { printf '\n\033[31mFAIL: %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; exit 1; }
pass() { printf '\033[32m   ok: %s\033[0m\n' "$*" | tee -a "$RUN_LOG"; }

# Run a command on an instance and echo the reply. Failure to reach the channel
# is fatal: every later assertion would be meaningless.
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

# Poll an instance's state until a regex matches. This is the shape every wait
# in this file takes; see "WHY EVERYTHING POLLS" above.
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

# --- stages -----------------------------------------------------------------

stage_up() {
  say "up — launching instances $HOST_N (host) and $PEER_N (client)"

  pgrep -f "[P]hantomBrigade.exe" >/dev/null 2>&1 \
    && note "something is already running; game-instance.sh will refuse above two"

  nohup "$HERE/game-instance.sh" "$HOST_N" > "$LOG_DIR/instance-$HOST_N.log" 2>&1 &
  "$HERE/game-wait.sh" "$HOST_N" 240 || fail "host instance never opened its channel"

  # Staggered deliberately. Two SteamAPI.Init calls landing together is the one
  # part of the two-instance setup with no margin — it works, but it was proven
  # with a gap between them, so keep the gap.
  sleep 15
  nohup "$HERE/game-instance.sh" "$PEER_N" > "$LOG_DIR/instance-$PEER_N.log" 2>&1 &
  "$HERE/game-wait.sh" "$PEER_N" 240 || fail "client instance never opened its channel"

  # patched=33 is the healthy number (37 patch classes over 33 distinct target
  # methods). A different one means PatchAll aborted partway and an arbitrary
  # subset of the suppression gates is live — which looks like nothing at all
  # until a client drives the host's world. Worth failing on before a playtest,
  # not during one.
  #
  # 32 -> 33 with M14 stage B: WeaponLightPatches postfixes
  # CombatReplayHelper.OnUnitLightWeapon, which is the only place a weapon
  # light's world position can be resolved while the barrel is still pointing
  # where it fired.
  for n in "$HOST_N" "$PEER_N"; do
    local st; st="$(drive "$n" "pbj.drive-state")"
    printf '%s' "$st" | grep -q "patched=33" \
      || fail "instance $n reports $(printf '%s' "$st" | grep -o 'patched=[0-9?]*') — expected patched=33; the patch set is incomplete"
  done
  pass "both instances up, both fully patched"
}

stage_session() {
  say "session — host on $HOST_N, join from $PEER_N"

  drive "$HOST_N" "pbj.host" | tee -a "$RUN_LOG"
  # Loopback, so no passphrase: ConnectRules only demands one for a non-loopback
  # bind. Both instances are on this machine.
  drive "$PEER_N" "pbj.join 127.0.0.1" | tee -a "$RUN_LOG"

  await "$HOST_N" "session=host" "host has a session" 30
  await "$PEER_N" "session=client" "client joined" 30

  local st; st="$(drive "$HOST_N" "pbj.net-status")"
  printf '%s' "$st" | grep -q "participants 2" \
    || fail "host does not see two participants: $st"
  pass "handshake complete, 2 participants"
}

stage_lobby() {
  say "lobby — choose a save, both agree, load in unison"

  if [ -z "$SAVE_KEY" ]; then
    local saves; saves="$(drive "$HOST_N" "pbj.saves")"
    note "saves on the host:"; printf '%s\n' "$saves" | sed 's/^/     /'
    # Skip the scenario slot (pbj_combat_test is M12b's own write target, not a
    # campaign) and skip autosaves, which churn — pbj.saves lists newest first,
    # so an autosave would usually win and the playtest would run against
    # whatever the last session happened to leave behind.
    SAVE_KEY="$(printf '%s\n' "$saves" \
      | grep -o 'pbj_[A-Za-z0-9_.-]*' \
      | grep -v 'pbj_combat_test' \
      | grep -v '^pbj_autosave' \
      | head -1)"
    [ -n "$SAVE_KEY" ] || fail "no deliberate pbj_ campaign save found — make one with pbj.save-as or pbj.save-convert"
    note "picked '$SAVE_KEY' (pass one as argument 2 to override)"
  fi

  # Host-only by design: LobbySelect refuses on a client.
  drive "$HOST_N" "pbj.lobby-select $SAVE_KEY" | tee -a "$RUN_LOG"

  # Selecting offers the save to peers that do not hold it (M11e transfers on
  # SELECTION, not on a failed load, so the client can only ready once it holds
  # those exact bytes). A campaign is 24-71 KB, so this is quick — but poll it.
  # ⚠️ AND POLLING MEANS RE-POSTING, not just waiting. A ready posted while the
  # save is still crossing is REFUSED, not queued — the host logs "ignoring
  # lobby ready from #N for selection M — still waiting for the save to arrive"
  # and the client never readies again on its own. This stage used to post each
  # ready exactly once and then wait 240s for a load that could not come; the
  # comment above already said "but poll it" while the code did not. (2026-08-14)
  #
  # Re-posting is safe: a ready that is already counted is idempotent, and the
  # selection version has not moved, so nothing clears it.
  drive "$HOST_N" "pbj.lobby-ready" | tee -a "$RUN_LOG"

  # ⚠️ Do NOT assert on "ready 2/2". It is TRANSIENT: satisfying the barrier
  # fires the load and clears every ready in the same breath, so a poll for the
  # count races the event it is waiting for and usually loses. Observed on the
  # first successful two-party run — the tally never read 2/2 once, and both
  # machines loaded anyway. Stop when the count is full OR the host has left the
  # menu, and let the load assertions below be the real verdict.
  local ready_deadline=$(( SECONDS + 120 )) lobby_state=""
  while [ "$SECONDS" -lt "$ready_deadline" ]; do
    drive "$PEER_N" "pbj.lobby-ready" >> "$RUN_LOG"
    lobby_state="$(drive "$HOST_N" "pbj.drive-state")"
    if printf '%s' "$lobby_state" | grep -Eq "ready 2/2"; then
      pass "both readied (client readied once it held the save)"
      break
    fi
    if ! printf '%s' "$lobby_state" | grep -Eq "state=mainmenu"; then
      pass "the load fired (the ready tally cleared before it could be sampled)"
      break
    fi
    sleep 2
  done

  # Both machines land in the campaign. This is M11d, in-game-verified single
  # party but the two-party path is what is being exercised here.
  await "$HOST_N"  "state=overworld|state=basecrawler" "host loaded the campaign" 240
  await "$PEER_N"  "state=overworld|state=basecrawler" "client loaded the campaign" 240

  # Poll, do not grep once. The drive channel answers from memory while the
  # engine's log writer buffers, so drive-state can report a finished load
  # seconds before the line describing it reaches the file. Grepping on the
  # instant the state assertion passes loses that race and warns on a perfectly
  # healthy run. (2026-08-14)
  local log_deadline=$(( SECONDS + 30 ))
  while [ "$SECONDS" -lt "$log_deadline" ]; do
    grep -q "load complete | 2 of 2" "$(prefix_log "$HOST_N")" && break
    sleep 2
  done
  grep -q "load complete | 2 of 2" "$(prefix_log "$HOST_N")" \
    && pass "host logged 'load complete | 2 of 2'" \
    || note "WARNING: host did not log 'load complete | 2 of 2' — check the log"
}

stage_fight() {
  say "fight — host starts a mission, ships it, client follows"

  # ⚠️ HOST ONLY. See THE ONE RULE at the top.
  #
  # ow.load-scenario skips site selection (ForceScenarioAndArea falls back to
  # hidden_root_enemy) and, for scenarios with loadImmediately, the briefing too.
  # Only 6 of 56 shipped scenarios set that flag and all six are debug content,
  # so a campaign scenario WILL raise the briefing and needs the two clicks
  # below. Both paths are handled.
  local scenario="${PBJ_SCENARIO:-}"
  if [ -n "$scenario" ]; then
    # ⚠️ LEAVE THE BASE FIRST. A synchronised load lands both machines in
    # `basecrawler`, and every ow.* command guards on game state `overworld`,
    # refusing via QuantumConsole.LogToConsole — which never reaches Player.log
    # and arrives over the drive channel as an EMPTY REPLY. So a scripted fight
    # from the base looks exactly like a scenario key that does not exist.
    # (Cost one confused round on 2026-08-14.)
    if drive "$HOST_N" "pbj.drive-state" | grep -q "state=basecrawler"; then
      drive "$HOST_N" "pbj.nav-world" | tee -a "$RUN_LOG"

      # Leaving the base can raise a disengage dialog, and when it does the
      # nav press alone never reaches the overworld — the stage just times out
      # 120s later pointing at state=basecrawler, which reads like the nav
      # actuator being broken. It is conditional, so the stage passes without
      # this often enough to look fine; it has now cost two runs.
      #
      # Unconditional on purpose. pbj.dialog-confirm guards on IsEntered(), so
      # calling it with no dialog open is an honest no-op rather than the
      # closed-dialog callback re-fire it used to be.
      drive "$HOST_N" "pbj.dialog-confirm" | tee -a "$RUN_LOG"

      await "$HOST_N" "state=overworld" "host reached the overworld map" 120
    fi

    drive "$HOST_N" "ow.load-scenario $scenario" | tee -a "$RUN_LOG"
  else
    note "no PBJ_SCENARIO set — expecting the briefing to be open already"
  fi

  # If a briefing appeared, deploy through it exactly as a player would. The
  # refusals are honest, so calling these when no briefing is up costs nothing.
  local out
  out="$(drive "$HOST_N" "pbj.briefing-deploy")"; note "$out"
  if printf '%s' "$out" | grep -q "deploy pressed"; then
    await_dialog_then_confirm
  fi

  await "$HOST_N" "combat=True" "host is in combat" 240

  # The measured part. CanSave refused for 6.5s on the observed entry, so this
  # is the wait that most needs to be a poll.
  local log; log="$(prefix_log "$HOST_N")"
  local deadline=$(( SECONDS + 120 ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    grep -q "fight written to" "$log" && break
    grep -q "gave up writing the fight" "$log" && fail "host gave up writing the fight — read $log"
    sleep 2
  done
  grep -q "fight written to" "$log" || fail "the fight was never written — read $log"
  pass "fight written"
  grep "fight written to\|offering the fight" "$log" | tail -2 | sed 's/^/     /'

  await "$PEER_N" "combat=True" "client followed into the fight" 300
  pass "both machines are in the same fight"
}

await_dialog_then_confirm() {
  local deadline=$(( SECONDS + 30 )) out
  while [ "$SECONDS" -lt "$deadline" ]; do
    out="$(drive "$HOST_N" "pbj.dialog-confirm")"
    printf '%s' "$out" | grep -q "dialog confirmed" && { pass "deploy confirmed"; return 0; }
    sleep 2
  done
  fail "the deploy confirmation dialog never opened"
}

stage_turn() {
  say "turn — both press Execute, the barrier fills, the turn runs"

  # Through CIViewCombatExecution.CheckAndAttemptExecution, which our prefix
  # intercepts and turns into a Ready. NOT CombatUtilities.ConfirmExecution:
  # that bypasses the barrier and runs the turn locally inside a networked
  # session, which is silent one-sided divergence. The deleted pbj.commit did
  # exactly that and is deliberately not what came back.
  drive "$PEER_N" "pbj.execute" | tee -a "$RUN_LOG"
  drive "$HOST_N" "pbj.execute" | tee -a "$RUN_LOG"

  await "$HOST_N" "simulating=True" "the turn is executing" 60
  await "$HOST_N" "simulating=False" "the turn finished" 180

  local t; t="$(drive "$HOST_N" "pbj.drive-state" | grep -o 'turn=[0-9-]*')"
  note "host is on $t"
  grep -q "DIVERGED" "$(prefix_log "$PEER_N")" \
    && note "WARNING: the client logged DIVERGED — expected before correction, a problem after it"
  pass "a shared turn ran on two real games"
}

stage_wedge_d() {
  say "wedge D — pbj.ship-fight on the CLIENT must not kill its session"

  # A LocalCombatReadyEvent reaching a ClientSession used to hit Handle's
  # default arm, which throws, and NetGlue.Pump turns a throw into "networking
  # stopped" for the whole process. There is an ignore arm now and the glue
  # posts only while hosting. This is the check that both hold.
  drive "$PEER_N" "pbj.ship-fight" | tee -a "$RUN_LOG"
  sleep 5
  local st; st="$(drive "$PEER_N" "pbj.net-status")"
  printf '%s' "$st" | grep -q "CLIENT" \
    || fail "the client lost its session to a hand-driven ship: $st"
  pass "client still has its session"
}

stage_drop() {
  say "drop — kill the client mid-entry; the host must drop it and fight on"

  note "this stage is destructive and expects you to be at a combat entry"
  pkill -f "[P]hantomBrigade.exe" --oldest 2>/dev/null || true
  note "killed the oldest instance — verify from the host log that it was the client"

  local log; log="$(prefix_log "$HOST_N")"
  local deadline=$(( SECONDS + 180 ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    grep -q "peer left" "$log" && break
    sleep 2
  done
  grep -q "peer left" "$log" || fail "the host never dropped the departed peer — this is the wedge"
  grep "peer left\|combat started" "$log" | tail -3 | sed 's/^/     /'
  pass "the host dropped the peer with a reason"
}

stage_down() {
  say "down — closing both instances"
  pkill -f "[P]hantomBrigade.exe" 2>/dev/null || true
  local deadline=$(( SECONDS + 30 ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    pgrep -f "[P]hantomBrigade.exe" >/dev/null 2>&1 || { pass "both closed"; return 0; }
    sleep 1
  done
  note "something is still running — close it by hand"
}

case "$STAGE" in
  up)       stage_up ;;
  session)  stage_session ;;
  lobby)    stage_lobby ;;
  fight)    stage_fight ;;
  turn)     stage_turn ;;
  wedge-d)  stage_wedge_d ;;
  drop)     stage_drop ;;
  down)     stage_down ;;
  all)      stage_up; stage_session; stage_lobby; stage_fight; stage_turn
            say "happy path complete — now run 'wedge-d' and 'drop' by hand" ;;
  *)        sed -n '2,55p' "$0" | sed 's/^# \{0,1\}//'; exit 64 ;;
esac
