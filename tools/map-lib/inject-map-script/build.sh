#!/usr/bin/env bash
# 编译 InjectMapScript.exe。用 .NET Framework 的 csc（MapCoreLib.dll 是 x86 / .NETFramework v4.5 程序集）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd -W)"   # 原生 Windows 路径：csc 是原生程序，不认 MSYS 的 /d/... 路径
WB="${WB:-D:/Games/CoronaLauncher/CoronaResources/NewWorldBuilder}"
CSC="${CSC:-C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe}"
BIN="$HERE/bin"
mkdir -p "$BIN"

echo "[build] MapCoreLib = $WB/bin/MapCoreLib.dll"
"$CSC" /nologo /platform:x86 /target:exe \
  /out:"$BIN/InjectMapScript.exe" \
  /r:"$WB/bin/MapCoreLib.dll" \
  /r:"$WB/bin/Newtonsoft.Json.dll" \
  /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll \
  "$HERE/InjectMapScript.cs"

cp -f "$WB/bin/MapCoreLib.dll" "$BIN/" 2>/dev/null || true
cp -f "$WB/bin/Newtonsoft.Json.dll" "$BIN/" 2>/dev/null || true
echo "[build] OK -> $BIN/InjectMapScript.exe"
ls -la "$BIN"
