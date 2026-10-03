#!/usr/bin/env bash
# Launch RimWorld under Wine with the game-rl bridge correctly wired.
#
# Two things this exists to prevent:
#
#   1. A missing GAMERL_INSTANCE_ID. The mod now refuses to start without one,
#      which is correct, but it means the game has to be launched from here
#      rather than from a desktop entry.
#
#   2. A path mismatch between the two sides. Under Wine, .NET's
#      Path.GetTempPath() resolves to C:\users\<user>\AppData\Local\Temp, which
#      is inside the prefix. The Rust server looks in the host's /tmp. The two
#      only meet at Z:\tmp, so the socket path is pinned explicitly here instead
#      of relying on either side's default.
#
# Usage:
#   scripts/run-rimworld.sh                     # instance id from timestamp
#   scripts/run-rimworld.sh alpha                # explicit id
#
# Then, from another shell:
#   env-validate preflight
#   env-validate throughput --point 10k
#
# The instance id must be unique per running process. Two instances sharing one
# id is exactly the collision the isolation work removed.

set -euo pipefail

GAME_ROOT="${GAME_ROOT:-$HOME/Games/RimWorld}"
export WINEPREFIX="${WINEPREFIX:-$GAME_ROOT/prefix}"

# Explicit override wins. Otherwise fall back to the instance id. Never a bare
# global path.
INSTANCE_ID="${1:-$(date +%Y%m%d-%H%M%S)}"
export GAMERL_INSTANCE_ID="$INSTANCE_ID"

SOCKET_HOST="/tmp/gamerl-rimworld-${INSTANCE_ID}.sock"
SHM_HOST="/tmp/gamerl-rimworld-camera-${INSTANCE_ID}-{stream}.shm"
LOG_HOST="/tmp/gamerl-logs/${INSTANCE_ID}"

# Z: is the host root inside Wine, so Z:\tmp is the same file the Rust server
# will look at as /tmp. Passing the Windows-visible spelling is what makes the
# two sides agree.
export GAMERL_SOCKET="Z:\\tmp\\gamerl-rimworld-${INSTANCE_ID}.sock"
export GAMERL_LOG_DIR="Z:\\tmp\\gamerl-logs\\${INSTANCE_ID}"

EXE=""
for candidate in \
    "$GAME_ROOT/drive_c/Games/RimWorld/RimWorldLinux" \
    "$GAME_ROOT/RimWorldLinux" \
    "$GAME_ROOT/drive_c/Games/RimWorld/RimWorldWin64.exe"
do
    if [[ -f "$GAME_ROOT/$candidate" || -f "$candidate" ]]; then
        EXE="$candidate"
        break
    fi
done

if [[ -z "$EXE" ]]; then
    echo "error: no RimWorld executable under $GAME_ROOT" >&2
    echo "set GAME_ROOT to override" >&2
    exit 1
fi

mkdir -p "$LOG_HOST"

cat <<EOF
instance   $INSTANCE_ID
prefix     $WINEPREFIX
executable $EXE
socket     $SOCKET_HOST        (as Z:\\\\tmp inside Wine)
log dir    $LOG_HOST

RimWorld's own Player.log is Unity's, so it goes next to the others:
  it will still be written to the prefix's LocalLow directory.
EOF

echo
echo "launching. the bridge socket appears at $SOCKET_HOST once the mod initialises."
echo

exec wine "$EXE" "$@"
