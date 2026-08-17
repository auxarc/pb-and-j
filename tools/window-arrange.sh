#!/usr/bin/env bash
#
# Put the two game windows side by side, and say which is which.
#
#   tools/window-arrange.sh [gap]
#
# WHY THIS EXISTS. game-instance.sh's header claims each instance "is offset on
# screen so two windows do not land on top of each other". It is not: the launch
# passes -screen-fullscreen 0 -screen-width 1280 -screen-height 720 and nothing
# positional, and nothing in the mod moves a window either. So both instances
# open at the same default position, one exactly behind the other.
#
# That cost a whole playtest on 2026-08-16. Every drive command answered, the
# client loaded a campaign, entered combat and played 594 effect tracks — while
# the operator could see only one window and reasonably concluded the client had
# never started. An eye test is worthless if you cannot tell which game you are
# looking at, and worse than worthless if you think you can.
#
# THE INSTANCE IS IDENTIFIED FROM THE PROCESS, NOT THE TITLE. Both windows are
# titled "Phantom Brigade", so the mapping goes window -> pid -> that process's
# PBJ_DRIVE_PORT environment variable, which game-instance.sh sets to 27700+N.
# Titles would be a guess; the port is what the instance actually answers on.
#
# Proton runs the game as an XWayland client, so wmctrl reaches it on KDE
# Wayland exactly as it would on X11.

set -uo pipefail

GAP="${1:-20}"
WIDTH=1280
HEIGHT=720

command -v wmctrl >/dev/null || { echo "window-arrange: needs wmctrl" >&2; exit 1; }

# -l lists windows, -p adds the owning pid. Both are needed: the pid is the only
# honest route back to which instance a window belongs to.
mapfile -t rows < <(wmctrl -lp | grep -i "Phantom Brigade")

if [ "${#rows[@]}" -eq 0 ]; then
  echo "window-arrange: no Phantom Brigade windows found — is the game up?" >&2
  exit 1
fi

instance_of() {
  local pid="$1" port=""
  # The launcher exports PBJ_DRIVE_PORT before exec'ing proton, so it survives
  # into the game process. tr because /proc/*/environ is NUL-separated.
  port="$(tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null \
            | sed -n 's/^PBJ_DRIVE_PORT=//p' | head -1)"
  if [ -n "$port" ]; then
    echo "$(( port - 27700 ))"
    return
  fi
  echo "?"
}

placed=0
for row in "${rows[@]}"; do
  id="$(awk '{print $1}' <<<"$row")"
  pid="$(awk '{print $3}' <<<"$row")"
  n="$(instance_of "$pid")"

  x=$(( placed * (WIDTH + GAP) ))
  y=0

  # 0 = gravity default; -1 leaves a field alone. Unmaximise first or a
  # maximised window ignores the move entirely.
  wmctrl -i -r "$id" -b remove,maximized_vert,maximized_horz 2>/dev/null
  wmctrl -i -r "$id" -e "0,$x,$y,$WIDTH,$HEIGHT"

  role="instance $n"
  case "$n" in
    2) role="instance 2 — HOST (left)" ;;
    3) role="instance 3 — CLIENT (right)" ;;
  esac
  printf '  %s  pid %s  ->  %s at x=%s\n' "$id" "$pid" "$role" "$x"

  placed=$(( placed + 1 ))
done

if [ "$placed" -lt 2 ]; then
  echo "window-arrange: WARNING — only $placed window(s) placed. If you expected two," >&2
  echo "  the second instance is not up, or its window has not mapped yet. Wait and re-run;" >&2
  echo "  do NOT start an eye test until both are placed and labelled." >&2
  exit 2
fi

echo "window-arrange: host on the left, client on the right."
