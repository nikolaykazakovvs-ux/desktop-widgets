#!/usr/bin/env bash
# Самопроверка прототипа на macOS/Linux в реальном окне: CLI, окно, трей, мониторы, телеметрия, память,
# «сон» процесса (SIGSTOP на SUSPEND_S секунд: таймеры стоят, настенные часы идут — так процесс видит сон системы).
# Использование: xplat/scripts/selftest-unix.sh <out-dir> [аргументы LimitsXplat...]
# Пример с тестовыми данными: selftest-unix.sh /tmp/st --fixture-usage usage.json --telemetry-dir /tmp/tele
set -euo pipefail
OUT=${1:?out-dir}; shift
SUSPEND_S=${SUSPEND_S:-40}
HERE=$(cd "$(dirname "$0")/.." && pwd)
DLL="$HERE/Limits.App/bin/Release/net10.0/LimitsXplat.dll"
DOTNET=${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}
rm -rf "$OUT"; mkdir -p "$OUT"
"$DOTNET" "$DLL" --selftest "$OUT" --selftest-seconds $((SUSPEND_S + 30)) "$@" >"$OUT/stdout.txt" 2>&1 &
APP=$!
for _ in $(seq 1 150); do [ -f "$OUT/ready" ] && break; kill -0 $APP 2>/dev/null || break; sleep 1; done
[ -f "$OUT/ready" ] || { echo "selftest не дошёл до готовности"; wait $APP || true; exit 1; }
PID=$(cat "$OUT/pid")
sample() {
  echo "== $1" >>"$OUT/memory-os.txt"
  ps -o rss=,vsz= -p "$PID" | awk '{printf "rss_mb=%.1f\n", $1/1024}' >>"$OUT/memory-os.txt"
  if command -v footprint >/dev/null; then footprint "$PID" 2>/dev/null | grep -E "Footprint|phys_footprint" | head -2 >>"$OUT/memory-os.txt" || true; fi
}
sample ready
echo "SIGSTOP на ${SUSPEND_S} с"; kill -STOP "$PID"; sleep "$SUSPEND_S"; kill -CONT "$PID"
sleep 15; sample after_wake
wait $APP; CODE=$?
echo "exit=$CODE"
exit $CODE
