#!/usr/bin/env bash
# Rebuild and deploy the bridge mod, then restart Stardew Valley (SMAPI) and wait until the
# bridge is listening. Dev helper for the Linux VM (game on DISPLAY :1).
# SMAPI is started directly, so Steam does not need to be running (only achievements are lost).
set -euo pipefail
cd "$(dirname "$0")/.."
export DISPLAY="${DISPLAY:-:1}"
LOG="$HOME/.config/StardewValley/ErrorLogs/SMAPI-latest.txt"
GAME_DIR="${GAME_DIR:-$HOME/.steam/debian-installation/steamapps/common/Stardew Valley}"

# Stop the running game (process name is truncated to 15 characters by the kernel).
if pgrep -x StardewModdingA >/dev/null; then
  pkill -x StardewModdingA || true
  for _ in $(seq 30); do pgrep -x StardewModdingA >/dev/null || break; sleep 1; done
fi

dotnet build mod/StardewPilot -c Release -v quiet -nologo | grep -E "error|Build succeeded" || true

rm -f "$LOG"
(cd "$GAME_DIR" && SteamAppId=413150 nohup xterm -e env TERM=xterm ./StardewModdingAPI >/dev/null 2>&1 &)
for _ in $(seq 90); do
  if grep -qE "Bridge listening|Could not start the bridge" "$LOG" 2>/dev/null; then
    grep -E "Bridge listening|Could not start the bridge" "$LOG"
    exit 0
  fi
  sleep 2
done
echo "Game did not report the bridge within 3 minutes; see $LOG" >&2
exit 1
