#!/usr/bin/env bash
#
# Launch a SECOND instance of Phantom Brigade on this machine, so two real games
# can join one co-op session without a second player or a second PC.
#
# Steam refuses to launch one appid twice, so the second instance is started
# outside Steam. That runs into two things the game insists on, both read from
# the decompile rather than guessed:
#
#   * Heartbeat.Awake (Heartbeat.cs:13-15) calls SteamHelper.InitSteam, which
#     calls Application.Quit() if SteamAPI.Init() returns false
#     (SteamHelper.cs:68-73). There is NO degraded mode — an instance that
#     cannot reach Steam closes rather than running without achievements.
#     SteamAppId/SteamGameId below are what let it reach Steam; steam_appid.txt
#     is the belt to that braces, and is read from the process WORKING
#     DIRECTORY, which is why this script cd's into the game folder. `proton run`
#     does not chdir there on your behalf.
#
#   * RestartAppIfNecessary (SteamHelper.cs:55-59) quits and asks Steam to
#     relaunch if it thinks it was started outside Steam. The same two settings
#     satisfy it.
#
# A SEPARATE PROTON PREFIX is not optional. Saves, settings, Mods/ and
# Player.log all live under the prefix's AppData, and two instances sharing one
# would fight over the save folder — both write pbj_-prefixed campaign saves
# into the same directory during a session — and over Player.log, which is the
# mod's only diagnostic channel. The game DIRECTORY is shared: nothing writes
# into it at runtime (SaveLocation.Internal is read-only, DataManagerSave.cs
# :84,163,178), so there is no need for a second 6.1 GB copy.
#
# The second prefix's Mods directory is symlinked at the first's, so one
# `make deploy` serves both instances and a ModVersion mismatch between the two
# — which the handshake would refuse — is structurally impossible.
#
# Usage:
#   tools/game-instance.sh 2            # set up if needed, then launch instance 2
#   tools/game-instance.sh 3 --setup    # set up instance 3 and stop
#
# Instance N gets prefix 553540-pbjN and drive port 27700+N, and is offset on
# screen so two windows do not land on top of each other.
#
# TWO INSTANCES AT ONCE IS THE CEILING, enforced below. The machine will not
# comfortably carry more, and the Steam-launched instance is not driveable by
# the mod's control channel — so the pair is two script-launched instances with
# Steam's own game closed. The Steam CLIENT must still be running: SteamAPI.Init
# failing is a hard quit, not a degraded mode.
#
# Deploy the mod with ALL games closed: `make deploy` rm -rf's a mod folder
# whose DLL a running instance holds open.

set -euo pipefail

INSTANCE="${1:-2}"
if ! [[ "$INSTANCE" =~ ^[0-9]+$ ]]; then
  echo "game-instance: first argument must be an instance number, e.g. 'tools/game-instance.sh 2'" >&2
  exit 1
fi
shift || true

STEAM="${STEAM_ROOT:-$HOME/.local/share/Steam}"
GAME_DIR="$STEAM/steamapps/common/Phantom Brigade"
PROTON="$STEAM/steamapps/common/Proton - Experimental/proton"
SOURCE_PREFIX="$STEAM/steamapps/compatdata/553540"
PREFIX="${PBJ_SECOND_PREFIX:-$STEAM/steamapps/compatdata/553540-pbj$INSTANCE}"
DRIVE_PORT="${PBJ_DRIVE_PORT:-$((27700 + INSTANCE))}"

MODS_REL="pfx/drive_c/users/steamuser/AppData/Local/PhantomBrigade/Mods"
FIRST_MODS="$SOURCE_PREFIX/$MODS_REL"
SECOND_MODS="$PREFIX/$MODS_REL"

die() { echo "second-instance: $*" >&2; exit 1; }

# Is the requested prefix the game's own? Resolved before comparing, so two
# paths spelling the same directory differently cannot slip through.
#
# This gates SETUP only. Setup does `rm -rf` on the target's Mods directory
# before linking it at the first prefix's — aimed at the real prefix that would
# delete the live mod folder and link it to itself. LAUNCHING against the real
# prefix is legitimate and stays allowed: once a manually-started instance
# registers with Steam, Steam's Play button becomes Stop, and starting the
# second game outside Steam is then the only way to get two running at once.
SAME_AS_SOURCE=false
if [ "$(readlink -f "$PREFIX" 2>/dev/null || echo "$PREFIX")" \
   = "$(readlink -f "$SOURCE_PREFIX" 2>/dev/null || echo "$SOURCE_PREFIX")" ]; then
  SAME_AS_SOURCE=true
fi

[ -d "$GAME_DIR" ]      || die "game not found at $GAME_DIR"
[ -x "$PROTON" ]        || die "Proton - Experimental not found at $PROTON (it is the tool 553540 is mapped to; match it)"
[ -d "$SOURCE_PREFIX" ] || die "the game's own prefix is missing at $SOURCE_PREFIX — launch it through Steam once first"

# --- setup, idempotent ---

if [ "$SAME_AS_SOURCE" = true ]; then
  [ "${1:-}" = "--setup" ] && die "refusing to set up on top of the game's own prefix ($SOURCE_PREFIX) — that would delete the live Mods folder"
  echo "second-instance: target IS the game's own prefix — setup skipped, launching only"
elif [ ! -d "$PREFIX" ]; then
  # Bracketed so the pattern cannot match this script's own command line —
  # `pgrep -f PhantomBrigade.exe` run from a shell whose invocation contains
  # that string reports the shell itself, which reads as "the game is running"
  # when nothing is.
  if pgrep -f "[P]hantomBrigade.exe" >/dev/null 2>&1; then
    die "the game is running — close it before copying its prefix, or the copy catches a mid-write state"
  fi
  echo "second-instance: copying prefix (384 MB) -> $PREFIX"
  cp -a "$SOURCE_PREFIX" "$PREFIX"
fi

# Copying the prefix brought a real Mods directory with it. Replace that copy
# with a link at the first prefix's, so both instances always run the same build.
#
# SAME_AS_SOURCE must gate this too, not just the copy above: when the target IS
# the source, SECOND_MODS and FIRST_MODS are the same real directory, `! -L`
# passes, and this would rm -rf the LIVE mod folder and then link it to itself.
if [ "$SAME_AS_SOURCE" = false ] && [ ! -L "$SECOND_MODS" ]; then
  [ -d "$FIRST_MODS" ] || die "no Mods directory in the game's own prefix at $FIRST_MODS — run 'make deploy' once first"
  echo "second-instance: linking Mods -> $FIRST_MODS"
  rm -rf "$SECOND_MODS"
  mkdir -p "$(dirname "$SECOND_MODS")"
  ln -s "$FIRST_MODS" "$SECOND_MODS"
fi

# Read from the working directory, which the cd below establishes.
if [ ! -f "$GAME_DIR/steam_appid.txt" ]; then
  echo "second-instance: writing steam_appid.txt into the game folder"
  echo 553540 > "$GAME_DIR/steam_appid.txt"
fi

if [ "${1:-}" = "--setup" ]; then
  echo "game-instance: setup complete — prefix $PREFIX"
  exit 0
fi

# --- launch ---

# The user's ceiling, enforced by the tool rather than by memory.
#
# Counting instances is fiddlier than it looks, and two obvious ways are both
# wrong:
#
#   * `pgrep -x PhantomBrigade.exe` NEVER MATCHES. Process names come from
#     /proc/PID/comm, which the kernel truncates to 15 characters;
#     "PhantomBrigade.exe" is 18. pgrep says so on stderr and returns nothing,
#     so a guard written that way silently never fires. (Found the hard way,
#     2026-08-09.)
#   * `pgrep -f -c` OVERCOUNTS. Proton's wrapper processes carry the exe path
#     in their own command lines, so one instance reports as several and the
#     ceiling would refuse at one.
#
# So count what actually distinguishes instances: the Proton prefix each one is
# running against. One prefix, one game. The bracketing keeps this script's own
# command line from matching itself.
RUNNING=$(
  for p in $(pgrep -f "[P]hantomBrigade.exe" 2>/dev/null); do
    tr '\0' '\n' < "/proc/$p/environ" 2>/dev/null \
      | sed -n 's/^STEAM_COMPAT_DATA_PATH=//p'
  done | sort -u | grep -c . || true
)
if [ "${RUNNING:-0}" -ge 2 ]; then
  echo "game-instance: $RUNNING instances already running — two is the ceiling, close one first" >&2
  exit 1
fi

cd "$GAME_DIR"

export STEAM_COMPAT_DATA_PATH="$PREFIX"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
export SteamAppId=553540
export SteamGameId=553540

# Belt and braces. Nothing in the mod had ever read an environment variable from
# Mono under Proton before this, so the port is also passed on the command line
# and the mod logs which of the two answered. Unity ignores arguments it does
# not recognise.
export PBJ_DRIVE_PORT="$DRIVE_PORT"

echo "game-instance: launching instance $INSTANCE | prefix $PREFIX | drive port $DRIVE_PORT"
echo "game-instance: if it dies within seconds, look for '[Steamworks.NET] SteamAPI_Init() failed' in"
echo "  $PREFIX/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log"
echo "game-instance: NOTE the mod still loads before Application.Quit takes effect at end of frame,"
echo "  so a log line saying pb-and-j loaded is NOT evidence the instance survived."

# Windowed and smaller, so two instances do not fight over the display. Unity's
# own arguments, passed through to the game; --pbj-drive-port is ours and Unity
# ignores it.
exec "$PROTON" run "$GAME_DIR/PhantomBrigade.exe" \
  -screen-fullscreen 0 -screen-width 1280 -screen-height 720 \
  "--pbj-drive-port=$DRIVE_PORT"
