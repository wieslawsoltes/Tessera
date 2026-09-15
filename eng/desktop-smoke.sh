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
xdotool windowmove "$window" 10 20
xdotool windowsize "$window" 960 850
sleep 1
import -window "$window" artifacts/desktop/obsidian-x11.png
palette() {
  xdotool key --clearmodifiers ctrl+shift+p
  sleep .25
  xdotool type --clearmodifiers --delay 8 "$1"
  sleep .25
  xdotool key Return
  sleep .7
}
palette 'Float terminal in a window'
floating=''
for attempt in $(seq 1 40); do
  floating=$(xdotool search --onlyvisible --pid "$pid" --name 'Development.*Tessera' 2>/dev/null | head -1 || true)
  test -n "$floating" && break
  sleep .25
done
test -n "$floating"
xdotool windowmove "$floating" 980 100
xdotool windowsize "$floating" 600 600
xdotool windowraise "$floating"
xdotool windowfocus --sync "$floating"
sleep .7
import -window root artifacts/desktop/floating-x11.png
# Drive Xdnd through genuine X11 pointer delivery, not the docking model/broker seam.
xdotool mousemove --sync 1040 165 mousedown 1
sleep .15
for point in '1025 178' '975 205' '850 250' '700 300' '500 350' '300 410'; do
  read -r x y <<< "$point"
  xdotool mousemove --sync "$x" "$y"
  sleep .2
done
import -window root artifacts/desktop/cross-window-drag-x11.png
xdotool mouseup 1
for attempt in $(seq 1 40); do
  test -z "$(xdotool search --onlyvisible --pid "$pid" --name 'Development.*Tessera' 2>/dev/null || true)" && break
  sleep .25
done
test -z "$(xdotool search --onlyvisible --pid "$pid" --name 'Development.*Tessera' 2>/dev/null || true)"
kill -0 "$pid"
import -window root artifacts/desktop/cross-window-docked-x11.png
xdotool windowfocus --sync "$window"
palette 'SFTP file workspace'
sftp=$(xdotool search --onlyvisible --pid "$pid" --name 'SFTP' 2>/dev/null | head -1)
test -n "$sftp"
kill -0 "$pid"
import -window root artifacts/desktop/sftp-x11.png
xdotool key Escape
printf '{"backend":"X11","input":"synthetic X11 keyboard and pointer","nativeCrossWindowDragPassed":true,"physicalDevicesAudited":false,"passed":true}\n' > artifacts/desktop/smoke.json
