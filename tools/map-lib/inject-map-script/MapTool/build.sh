#!/usr/bin/env bash
# 编译 MapTool（net8.0，引用地编伴侣自带的 Dreamness.RA3.Map.Parser.dll）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd -W)"
DOTNET="${DOTNET:-/d/HermesAI/dotnet/dotnet.exe}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
"$DOTNET" build "$HERE/MapTool.csproj" -c Release -v q --nologo
echo "[build] OK -> $HERE/bin/Release/net8.0/MapTool.dll"
