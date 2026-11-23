#!/bin/bash
# Retakes the website's screenshots of the real game in one language: website/shots.sh <en|zh|fr|ru>
# Needs the Linux dev setup: the game on DISPLAY=:1 with SMAPI, the copilot connected (Launcher on), xdotool and
# ImageMagick, and the dev-only test kit mod (it reads /tmp/sp/tk.cmd) for warps. The dev save has a routine
# "long walk" set to start by itself at 6am. The script restarts the game in the language, so the day starts over.
set -e
L=${1:?language}
case $L in en) Q="what does Abigail like? short answer";; zh) Q="阿比盖尔喜欢什么？简短回答";; fr) Q="qu'est-ce qu'Abigail aime ? réponse courte";; ru) Q="что любит Эбигейл? коротко";; esac
case $L in en) G=en;; zh) G=zh;; fr) G=fr;; ru) G=ru;; esac
REPO=$(cd "$(dirname "$0")/.." && pwd); OUT=$REPO/website/assets/shots/$L; mkdir -p "$OUT"
export DISPLAY=:1
call() { node --input-type=module -e "
import { BridgeClient, openWebSocket, readConnectionInfo } from '$REPO/agent/src/bridge-client.ts';
const c = new BridgeClient(await openWebSocket(readConnectionInfo()));
const r = await c.callTool(process.argv[1], JSON.parse(process.argv[2] ?? '{}'), 600000); console.log(r.content[0].text); c.close();" "$@"; }
tk() { echo "$1" > /tmp/sp/tk.cmd; for _ in $(seq 20); do [ -f /tmp/sp/tk.cmd ] || break; sleep 0.5; done; sleep 0.5; }
win() { xdotool search --name "^Stardew Valley 1" | head -1; }
key() { xdotool windowactivate --sync "$(win)"; xdotool keydown "$1"; sleep 0.3; xdotool keyup "$1"; sleep 0.6; }
shot() { import -window "$(win)" "$OUT/$1.png"; }

xdotool mousemove 0 799                                 # park the cursor before the day starts: moving it later counts as input
sed -i "s#<languageCode>[^<]*</languageCode>#<languageCode>$G</languageCode>#" ~/.config/StardewValley/startup_preferences
"$REPO/scripts/restart-game.sh" > /tmp/sp/shots-restart.log 2>&1
for _ in $(seq 120); do call get_status 2>/dev/null | grep -q '"saveLoaded":true' && break; sleep 1; done
sleep 2; shot morning                                   # the 6am countdown of a routine that starts by itself
[ "$ONLY" = morning ] && exit 0                          # ONLY=morning: retake just this one
key a; sleep 2                                          # keep the farmer: the offer stays
tk "debug warp Town 43 62"; sleep 5
key F8; sleep 1.5
xdotool type --delay 140 "$Q"; sleep 0.5; key Return
sleep ${WAIT:-45}                                       # the model's answer
shot chat; key Escape; sleep 1
tk "debug warp Farm 64 15"; sleep 5
(call schedule_routine '{"name":"long walk","at":"4:30pm","days":["Mon","Thu"],"start":"offer"}' > /dev/null 2>&1 &)
sleep 5; shot ask; key Escape; sleep 2
convert "$OUT/chat.png" -crop 1020x650+127+78 +repage "$OUT/chat-window.png"   # the chat window alone, shown at full size
for f in morning chat chat-window ask; do convert "$OUT/$f.png" -strip -define png:compression-level=9 "$OUT/$f.png"; done
ls -la "$OUT"
