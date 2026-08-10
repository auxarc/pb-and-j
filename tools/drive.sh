#!/usr/bin/env bash
#
# Send a console command to a running game instance and print what it returned.
#
#   tools/drive.sh 2 "pbj.net-status"
#   tools/drive.sh 3 "ow.load-scenario generic_convoy"
#
# Instance N listens on 127.0.0.1:(27700+N), and only if it was launched by
# tools/game-instance.sh — the channel stays shut otherwise.
#
# This exists because console return values never reach Player.log: Quantum
# Console renders them in its own view. The reply here IS that value.
#
# Uses bash's own /dev/tcp rather than netcat, which is not installed here and
# is one more thing to be missing on a machine that needs to run this.
#
# ⚠️ Never send ow.load-scenario to a CLIENT. The mod's EnterCombat prefix stops
# only the last hop; by then ForceScenarioAndArea has already stripped
# feature_check and re-rolled the combat description including loot. The
# suppression patch is a boundary, not permission to poke at it.

set -euo pipefail

INSTANCE="${1:-}"
COMMAND="${2:-}"

if [ -z "$INSTANCE" ] || [ -z "$COMMAND" ]; then
  echo "usage: tools/drive.sh <instance-number> <command>" >&2
  exit 64
fi

PORT="${PBJ_DRIVE_PORT:-$((27700 + INSTANCE))}"
# Longer than the mod's own 30s per-command ceiling, so a command that the game
# times out on reports as TIMEOUT rather than as an unreachable instance.
READ_TIMEOUT="${PBJ_DRIVE_TIMEOUT:-35}"

if ! exec 3<>"/dev/tcp/127.0.0.1/$PORT"; then
  echo "drive: could not reach instance $INSTANCE on 127.0.0.1:$PORT" >&2
  echo "drive: is it running, and was it launched by tools/game-instance.sh?" >&2
  exit 69
fi

printf '%s\n' "$COMMAND" >&3

status_line=""
body=()
first=true
while IFS= read -r -t "$READ_TIMEOUT" line <&3; do
  line="${line%$'\r'}"
  if [ "$first" = true ]; then
    status_line="$line"
    first=false
    continue
  fi
  # A lone dot ends the reply; '..' is an escaped literal dot.
  [ "$line" = "." ] && break
  [ "$line" = ".." ] && line="."
  body+=("$line")
done
read_status=$?

exec 3<&- || true
exec 3>&- || true

if [ "$first" = true ]; then
  if [ "$read_status" -gt 128 ]; then
    echo "drive: no reply from instance $INSTANCE within ${READ_TIMEOUT}s" >&2
    echo "drive: the frame pump may be stalled, or the game is mid-load" >&2
  else
    echo "drive: connection closed with no reply" >&2
  fi
  exit 1
fi

if [ "${#body[@]}" -gt 0 ]; then
  printf '%s\n' "${body[@]}"
fi

case "$status_line" in
  OK) exit 0 ;;
  *)  echo "drive: $status_line" >&2; exit 1 ;;
esac
