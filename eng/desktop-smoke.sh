#!/usr/bin/env bash
# Runs the real X11 backend, not Avalonia.Headless. Synthetic X11 input is not a physical-device audit.
set -euo pipefail
mkdir -p artifacts/desktop
binary=$(find src/Tessera.App/bin -path '*/Release/net10.0/Tessera.dll' -print -quit)
dotnet "$binary" --design-mode >artifacts/desktop/native.log 2>&1 &
pid=$!
trap 'kill "$pid" 2>/dev/null || true' EXIT
window=''
for attempt in $(seq 1 60); do
  window=$(xdotool search --onlyvisible --pid "$pid" --name Tessera 2>/dev/null | head -1 || true)
  test -n "$window" && break
  kill -0 "$pid"
  sleep .25
done
test -n "$window"
xdotool windowfocus --sync "$window"
sleep 1
import -window "$window" artifacts/desktop/obsidian-x11.png
xdotool key --clearmodifiers ctrl+shift+p
sleep .3
xdotool type --clearmodifiers 'SFTP file workspace'
sleep .3
xdotool key Return
sleep .5
kill -0 "$pid"
import -window root artifacts/desktop/sftp-x11.png
xdotool key Escape
# Process must remain responsive while native dialogs and palette are used.
kill -0 "$pid"
printf '{"backend":"X11","input":"synthetic X11 keyboard","physicalDevicesAudited":false,"passed":true}\n' > artifacts/desktop/smoke.json
