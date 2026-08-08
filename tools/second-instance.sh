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
#   tools/second-instance.sh            # set up if needed, then launch
#   tools/second-instance.sh --setup    # set up and stop
#
# Deploy the mod with BOTH games closed: `make deploy` rm -rf's a mod folder
# whose DLL a running instance holds open.

set -euo pipefail

STEAM="${STEAM_ROOT:-$HOME/.local/share/Steam}"
GAME_DIR="$STEAM/steamapps/common/Phantom Brigade"
PROTON="$STEAM/steamapps/common/Proton - Experimental/proton"
SOURCE_PREFIX="$STEAM/steamapps/compatdata/553540"
PREFIX="${PBJ_SECOND_PREFIX:-$STEAM/steamapps/compatdata/553540-pbj2}"

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
  echo "second-instance: setup complete — prefix $PREFIX"
  exit 0
fi

# --- launch ---

cd "$GAME_DIR"

export STEAM_COMPAT_DATA_PATH="$PREFIX"
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
export SteamAppId=553540
export SteamGameId=553540

echo "second-instance: launching | prefix $PREFIX"
echo "second-instance: if it dies within seconds, look for '[Steamworks.NET] SteamAPI_Init() failed' in"
echo "  $PREFIX/pfx/drive_c/users/steamuser/AppData/LocalLow/Brace Yourself Games/Phantom Brigade/Player.log"
echo "second-instance: NOTE the mod still loads before Application.Quit takes effect at end of frame,"
echo "  so a log line saying pb-and-j loaded is NOT evidence the instance survived."

# Windowed and smaller, so the second instance does not fight the first for the
# display. Unity's own arguments, passed through to the game.
exec "$PROTON" run "$GAME_DIR/PhantomBrigade.exe" \
  -screen-fullscreen 0 -screen-width 1600 -screen-height 900
