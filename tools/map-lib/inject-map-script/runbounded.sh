#!/usr/bin/env bash
# 在后台跑一个命令，最多等 N 秒，然后强杀 Windows 进程。用来探测"会挂住"的解析。
# 用法: runbounded.sh <秒> <日志文件> <命令...>
SECS="$1"; LOG="$2"; shift 2
export MSYS2_ARG_CONV_EXCL="*"
rm -f "$LOG"
"$@" > "$LOG" 2>&1 &
PID=$!
for i in $(seq 1 "$SECS"); do
  sleep 1
  if ! kill -0 "$PID" 2>/dev/null; then break; fi
done
if kill -0 "$PID" 2>/dev/null; then
  echo "[runbounded] 超时 ${SECS}s，强杀 (pid=$PID)" >> "$LOG"
  taskkill /F /PID "$PID" >/dev/null 2>&1
  taskkill /F /IM InjectMapScript.exe >/dev/null 2>&1
  kill -9 "$PID" 2>/dev/null
fi
wait "$PID" 2>/dev/null
echo "[runbounded] exit=$?"
