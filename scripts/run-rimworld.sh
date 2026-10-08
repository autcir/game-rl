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

LOG_HOST="/tmp/gamerl-logs/${INSTANCE_ID}"

# TCP, not Unix sockets. AF_UNIX does not exist on Windows, so Wine answers
# WSAEAFNOSUPPORT and .NET Framework exposes no Socket support for it. Wine is
# the only way to run RimWorld on Linux, so Unix sockets are not an option here.
#
# Port 0 asks the OS for a free port, so two instances can never collide. The
# mod publishes the resolved endpoint to the endpoint file below, and the Rust
# side reads it from there rather than guessing.
#
# Z: is the host root inside Wine, so Z:/tmp is the same file the Rust server
# sees as /tmp.
export GAMERL_TCP="127.0.0.1:${GAMERL_PORT:-0}"
export GAMERL_ENDPOINT_FILE="Z:/tmp/gamerl-logs/${INSTANCE_ID}/endpoint"
export GAMERL_LOG_DIR="Z:/tmp/gamerl-logs/${INSTANCE_ID}"

EXE=""
# Probe both layouts. A Wine install normally keeps the game *inside* the prefix
# (WINEPREFIX/drive_c/Games/...), and WINEPREFIX above points into
# $GAME_ROOT/prefix -- so probing only $GAME_ROOT/drive_c/... can never find it.
# The prefix is the authoritative location, so it is probed too and its paths are
# tried last, letting an explicit GAME_ROOT override still win.
for candidate in \
    "$GAME_ROOT/drive_c/Games/RimWorld/RimWorldLinux" \
    "$GAME_ROOT/RimWorldLinux" \
    "$GAME_ROOT/drive_c/Games/RimWorld/RimWorldWin64.exe" \
    "${WINEPREFIX}/drive_c/Games/RimWorld/RimWorldLinux" \
    "${WINEPREFIX}/drive_c/Games/RimWorld/RimWorldWin64.exe"
do
    if [[ -f "$GAME_ROOT/$candidate" || -f "$candidate" ]]; then
        EXE="$candidate"
        break
    fi
done

if [[ -z "$EXE" ]]; then
    echo "error: no RimWorld executable found" >&2
    echo "  GAME_ROOT   = $GAME_ROOT" >&2
    echo "  WINEPREFIX  = $WINEPREFIX" >&2
    echo "  searched    = \$GAME_ROOT{,/prefix}/drive_c/Games/RimWorld/{RimWorldLinux,RimWorldWin64.exe}" >&2
    echo "set GAME_ROOT or WINEPREFIX to override" >&2
    exit 1
fi

mkdir -p "$LOG_HOST"

cat <<EOF
instance   $INSTANCE_ID
prefix     $WINEPREFIX
executable $EXE
tcp        127.0.0.1:${GAMERL_PORT:-0}   (0 = OS picks a free port)
endpoint   $LOG_HOST/endpoint            (written once bound)
log dir    $LOG_HOST
EOF

echo
echo "launching. once the mod binds, read the endpoint with:"
echo "  cat $LOG_HOST/endpoint"
echo

exec wine "$EXE" "$@"
