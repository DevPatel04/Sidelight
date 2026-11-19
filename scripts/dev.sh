#!/usr/bin/env bash
# Development loop for Linux and macOS: run the agent from source in the background and push panel changes to the phone.
#
#   ./scripts/dev.sh restart   # rebuild and restart the agent, rebuild the app and reinstall it on the phone
#   ./scripts/dev.sh start     # build and start the agent (no-op when it is already running)
#   ./scripts/dev.sh stop      # stop the agent
#   ./scripts/dev.sh agent     # rebuild and restart only the agent (settings.html, C# changes)
#   ./scripts/dev.sh phone     # rebuild and reinstall only the app, then reopen it (panel www/ changes)
#   ./scripts/dev.sh status    # is the agent running, is a phone connected
#   ./scripts/dev.sh logs      # follow the agent's output
#
# The phone loads the panel from the APK's own assets, not from the agent, so changes under www/ (themes, widgets,
# styles) reach the phone only after "phone" or "restart". The settings page and the browser preview at /panel/ are
# embedded in the agent, so they need "agent" (then reload the browser tab).
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
state="${XDG_STATE_HOME:-$HOME/.local/state}/phonebox-dev"
pidfile="$state/agent.pid"
log="$state/agent.log"
exe="$root/agent/PhoneBox.Agent/bin/Debug/net10.0/PhoneBoxAgent"
url="http://127.0.0.1:8765"
pkg="net.oxog.phonebox.deskpanel"
mkdir -p "$state"

# ~/.bashrc only sets these for interactive shells
export ANDROID_HOME="${ANDROID_HOME:-$HOME/Android/Sdk}"
if [ -z "${JAVA_HOME:-}" ]; then
  for j in /usr/lib/jvm/java-17-openjdk-* /usr/lib/jvm/temurin-17-*; do [ -d "$j" ] && export JAVA_HOME="$j" && break; done
fi
adb="$(command -v adb || true)"
[ -n "$adb" ] || adb="$ANDROID_HOME/platform-tools/adb"

agent_pid() {
  local pid
  pid="$(cat "$pidfile" 2>/dev/null || true)"
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then echo "$pid"; return; fi
  # an agent started some other way (dotnet run in a terminal) holds the port too
  pgrep -f "$exe" | head -n1 || true
}

stop_agent() {
  local pid
  pid="$(agent_pid)"
  if [ -z "$pid" ]; then echo "agent: not running"; rm -f "$pidfile"; return; fi
  echo "agent: stopping ($pid)"
  kill "$pid" 2>/dev/null || true
  # it closes the phone link and its pollers first; a hung shutdown should not block the next start
  for _ in $(seq 1 20); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
  kill -0 "$pid" 2>/dev/null && { echo "agent: did not exit, killing"; kill -9 "$pid" 2>/dev/null || true; }
  pkill -f "dotnet run --project agent/PhoneBox.Agent" 2>/dev/null || true
  rm -f "$pidfile"
}

start_agent() {
  local pid
  pid="$(agent_pid)"
  if [ -n "$pid" ]; then echo "agent: already running ($pid), use restart to pick up changes"; return; fi
  echo "agent: building"
  dotnet build "$root/agent/PhoneBox.Agent" -f net10.0 --nologo -v q -clp:ErrorsOnly
  echo "agent: starting, log in $log"
  # detached from this shell's stdio and job table, so the script (or a pipe reading it) ends while the agent runs on
  cd "$root"
  nohup "$exe" >"$log" 2>&1 </dev/null &
  echo $! >"$pidfile"
  disown
  for _ in $(seq 1 60); do
    if curl -fs -o /dev/null "$url/api/state"; then echo "agent: ready on $url"; return; fi
    kill -0 "$(cat "$pidfile")" 2>/dev/null || { echo "agent: exited during start-up:" >&2; tail -n 20 "$log" >&2; exit 1; }
    sleep 1
  done
  echo "agent: no answer on $url after 60 s; see $log" >&2
  exit 1
}

phone_connected() { [ -x "$adb" ] && "$adb" get-state >/dev/null 2>&1; }

update_phone() {
  echo "app: building"
  (cd "$root/android" && ./gradlew assembleDebug -q --console=plain)
  if ! phone_connected; then echo "app: no phone on adb, built only (android/app/build/outputs/apk/debug/app-debug.apk)"; return; fi
  echo "app: installing"
  local out
  if ! out="$("$adb" install -r "$root/android/app/build/outputs/apk/debug/app-debug.apk" 2>&1)"; then
    echo "$out" >&2
    case "$out" in
      *UPDATE_INCOMPATIBLE*) echo "app: the installed app is signed with another key. Uninstall it once (this clears the app's data):" >&2
                             echo "     adb shell pm uninstall $pkg" >&2 ;;
    esac
    exit 1
  fi
  "$adb" shell am force-stop "$pkg"
  "$adb" shell am start -n "$pkg/.MainActivity" >/dev/null
  echo "app: reinstalled and reopened"
}

status() {
  local pid
  pid="$(agent_pid)"
  if [ -n "$pid" ]; then echo "agent: running ($pid) on $url"; else echo "agent: not running"; fi
  if phone_connected; then echo "phone: $("$adb" get-serialno)"; else echo "phone: none on adb"; fi
}

case "${1:-}" in
  start) start_agent ;;
  stop) stop_agent ;;
  agent) stop_agent; start_agent ;;
  phone) update_phone ;;
  restart) stop_agent; start_agent; update_phone ;;
  status) status ;;
  logs) exec tail -n 50 -f "$log" ;;
  *) sed -n '2,14p' "$0"; exit 2 ;;
esac
