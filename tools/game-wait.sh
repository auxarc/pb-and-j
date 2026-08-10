#!/usr/bin/env bash
#
# Block until instance N's drive channel is actually accepting connections.
#
#   tools/game-instance.sh 2 & tools/game-wait.sh 2 && tools/drive.sh 2 "pbj.drive-state"
#
# ⚠️ Waiting on a LOG LINE instead of this does not work, and it looks like it
# does. Player.log is rotated to Player-prev.log at launch, so for the first
# moments of a run the file still holds the PREVIOUS run's lines — including
# "drive channel listening" from last time. A grep-based wait returns
# immediately, the caller connects, and gets Connection refused from a game that
# is still loading. Cost a confusing round of "the channel is broken" once.
#
# A listening socket cannot be stale, so poll that instead.

set -euo pipefail

INSTANCE="${1:-}"
if [ -z "$INSTANCE" ]; then
  echo "usage: tools/game-wait.sh <instance-number> [timeout-seconds]" >&2
  exit 64
fi

TIMEOUT="${2:-180}"
PORT="${PBJ_DRIVE_PORT:-$((27700 + INSTANCE))}"

deadline=$(( SECONDS + TIMEOUT ))
while [ "$SECONDS" -lt "$deadline" ]; do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
    exec 3<&- 2>/dev/null || true
    echo "game-wait: instance $INSTANCE is accepting on 127.0.0.1:$PORT"
    exit 0
  fi

  # If no game is running at all, waiting the full timeout tells us nothing we
  # do not already know. SteamAPI.Init failure is a hard quit, and this is what
  # that looks like from outside.
  if ! pgrep -f "[P]hantomBrigade.exe" >/dev/null 2>&1; then
    echo "game-wait: no game process — it quit, or never started" >&2
    echo "game-wait: check Player.log for '[Steamworks.NET] SteamAPI_Init() failed'" >&2
    exit 1
  fi

  sleep 2
done

echo "game-wait: instance $INSTANCE did not open 127.0.0.1:$PORT within ${TIMEOUT}s" >&2
exit 1
