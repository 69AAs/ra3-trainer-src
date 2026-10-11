#!/usr/bin/env bash
# inject-map-script.sh — 把一段 Lua 脚本"导入进" RA3 日冕 mod 的 .map（等价于编辑器插件
# RA3MapUtil_LuaImporter），并自证脚本明文确实进了输出文件。
#
# ★关键纪律：不要再"从零新建脚本组"来写脚本 —— 实测那样写出来的图在红警3里整组不生效。
#   本流水线走两条安全路线：
#     replace : **原地替换**已存在脚本组里某个 Script 的内容（组/脚本/条件对象及其所有字段一字不动）。
#     add     : **深拷贝**一个已存在脚本组（连字段一起拷），改名 + 换内容，插到同一 ScriptList。
#
# 用法：
#   ./inject-map-script.sh replace <源.map> <输出.map> <脚本.lua> [组名] [脚本名]
#   ./inject-map-script.sh add     <源.map> <输出.map> <脚本.lua> <被拷组名> [新组名] [新脚本名] [extra...]
#
#   例：
#   ./inject-map-script.sh replace "…/R3Training.map" out/R3Training.inplace.map ../probe.lua \
#       Example_origin_05_top_button init.lua
#   ./inject-map-script.sh add "…/1v5(v1.3).map" out/hs_1v5_test.HSTest.map ../probe.lua \
#       Example_origin_05_top_button HSTest init.lua --template-map "…/R3Training.map.orig"
#
# 环境开关：
#   REPAIR=1   注入后先用 repair-roundtrip.py 修掉 MapCoreLib 往返写坏的资产字节，再验证。
#              （某些图需要，例如 超爽单人防守地图1 的 Teams/ObjectsList。）
#
# 纪律：源 .map 只读；所有中间产物都落在 <输出.map> 所在目录的 .inject-work/ 下。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd -W)"
DOTNET="${DOTNET:-/d/HermesAI/dotnet/dotnet.exe}"
INJECT_EXE="$HERE/bin/InjectMapScript.exe"
MAPTOOL="$HERE/MapTool/bin/Release/net8.0/MapTool.dll"

MODE="${1:-}"; shift || true
if [ "$MODE" != "replace" ] && [ "$MODE" != "add" ]; then
  echo "用法: $0 replace <源.map> <输出.map> <脚本.lua> [组名] [脚本名]" >&2
  echo "      $0 add     <源.map> <输出.map> <脚本.lua> <被拷组名> [新组名] [新脚本名] [extra...]" >&2
  exit 2
fi
[ $# -ge 3 ] || { echo "参数不足" >&2; exit 2; }
SRC="$1"; OUT="$2"; LUA="$3"; shift 3
EXTRA=("$@")

[ -f "$SRC" ] || { echo "找不到源图: $SRC" >&2; exit 2; }
[ -f "$LUA" ] || { echo "找不到脚本: $LUA" >&2; exit 2; }
[ -f "$INJECT_EXE" ] || { echo "请先跑 $HERE/build.sh" >&2; exit 2; }
[ -f "$MAPTOOL" ] || { echo "请先跑 $HERE/MapTool/build.sh" >&2; exit 2; }

OUTDIR="$(cd "$(dirname "$OUT")" && pwd -W)"
WORK="$OUTDIR/.inject-work"
mkdir -p "$WORK"

echo "== 0. 源图只读，先做副本 =="
cp -f "$SRC" "$WORK/src-copy.map"
SRC_SIZE=$(stat -c %s "$WORK/src-copy.map")
echo "   src-copy.map = $SRC_SIZE bytes"

echo "== 1. 用 MapCoreLib 注入（模式 $MODE） =="
"$INJECT_EXE" "$MODE" "$WORK/src-copy.map" "$WORK/injected.map" "$LUA" "${EXTRA[@]}" \
    --raw --dump-fields "$WORK/fields-out.txt" | tee "$WORK/inject.log"
grep -oE "PSL-OFFSETS dataStartPos=[0-9]+ dataSize=[0-9]+" "$WORK/inject.log" > "$WORK/psl.txt" || true

if [ "${REPAIR:-0}" = "1" ]; then
  echo "== 1b. 修复 MapCoreLib 往返写坏的资产字节（REPAIR=1） =="
  "$INJECT_EXE" roundtrip "$WORK/src-copy.map" "$WORK/mc.raw" raw > /dev/null
  "$DOTNET" "$MAPTOOL" resave "$WORK/src-copy.map" "$WORK/clean.raw" 0 > /dev/null
  PS=$(sed -E 's/.*dataStartPos=([0-9]+) dataSize=([0-9]+).*/\1 \2/' "$WORK/psl.txt")
  echo "   PSL: $PS"
  python "$HERE/repair-roundtrip.py" "$WORK/clean.raw" "$WORK/mc.raw" "$WORK/injected.map.raw" "$WORK/repaired.raw" $PS
  "$DOTNET" "$MAPTOOL" resave "$WORK/repaired.raw" "$WORK/injected.map" 1 > /dev/null
fi

echo "== 2. 用「地编伴侣」同款解析器验证注入结果可读 =="
"$DOTNET" "$MAPTOOL" info "$WORK/injected.map"
"$DOTNET" "$MAPTOOL" info "$WORK/src-copy.map"  > "$WORK/info-src.txt" || true
"$DOTNET" "$MAPTOOL" info "$WORK/injected.map"  > "$WORK/info-inj.txt"
python - "$WORK/info-src.txt" "$WORK/info-inj.txt" <<'PY'
import re, sys
def parse(p):
    d = {}
    for line in open(p, encoding='utf-8', errors='replace'):
        m = re.match(r'\s*\[(\w+)\]\s+(\S+)\s+ver=(\d+)\s+dataSize=(\d+)\s+parsed=(\w+)\s+errored=(\w+)', line)
        if m: d[m.group(1)] = (m.group(2), m.group(3), int(m.group(4)), m.group(5), m.group(6))
    return d
a, b = parse(sys.argv[1]), parse(sys.argv[2])
if set(a) != set(b):
    print("   资产集合不同:", set(a) ^ set(b)); sys.exit(1)
same = diff = 0
for k in sorted(a):
    if a[k] == b[k]: same += 1
    else:
        diff += 1
        print("   ~ %-20s 源=%s  输出=%s" % (k, a[k], b[k]))
print("   资产总数=%d  一致=%d  变化=%d（只应变化 PlayerScriptsList）" % (len(a), same, diff))
PY

echo "== 3. 另存最终 .map（压缩）+ 一份未压缩副本（用于 grep 明文） =="
"$DOTNET" "$MAPTOOL" resave "$WORK/injected.map" "$OUT" 1
"$DOTNET" "$MAPTOOL" resave "$OUT" "$OUT.raw" 0
OUT_SIZE=$(stat -c %s "$OUT"); RAW_SIZE=$(stat -c %s "$OUT.raw")

echo "== 4. 在未压缩副本里核对脚本明文（独特字符串） =="
python - "$OUT.raw" <<'PY'
import sys
d = open(sys.argv[1], 'rb').read()
for name, pat in [('HS-TEST  (ASCII)', b'HS-TEST'), ('HS2      (ASCII)', b'HS2'),
                  ('ra3luabridge 前缀', b'ra3luabridge'), ('exCenterTopBtnShow', b'exCenterTopBtnShow')]:
    print("   %-22s 命中 %d 次" % (name, d.count(pat)))
PY

echo
echo "== 结果 =="
echo "   模式          : $MODE"
echo "   源图          : $SRC  ($SRC_SIZE bytes)"
echo "   最终 .map     : $OUT  ($OUT_SIZE bytes)"
echo "   未压缩副本    : $OUT.raw  ($RAW_SIZE bytes)"
echo "   字段表        : $WORK/fields-out.txt"
