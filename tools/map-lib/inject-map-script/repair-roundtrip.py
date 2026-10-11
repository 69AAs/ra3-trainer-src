#!/usr/bin/env python3
# repair-roundtrip.py — 修掉 MapCoreLib 往 .map 里"写坏"的资产字节。
#
# 背景（实测）：MapCoreLib 对某些地图的某些资产（例如 超爽单人防守地图1 的 Teams / ObjectsList）
# 会在 round-trip 时写出与其自身解析器不一致的字节（表现为：未压缩流里有成片 ff -> 0f 的位翻转），
# 导致「地编伴侣」的解析器把这些资产标成 errored=True（数据长度不变，但内容被改坏）。
# 这是库的既有缺陷：**不做任何注入、纯 round-trip 同一张图**就能复现；而用 MapTool(Dreamness)
# 单独 resave 同一张图是干净的（errored=False）。
#
# 做法：把我们注入后的未压缩流里"被 MapCoreLib 写坏的那些字节"换回干净值。
#   clean.raw = MapTool resave 0 <源图>          ← Dreamness 写的、干净的
#   mc.raw    = InjectMapScript roundtrip raw <源图>   ← MapCoreLib 纯 round-trip（无注入）
#   inj.raw   = <我们注入后的> .map.raw          ← 基准
# clean.raw 与 mc.raw 长度相同、只有被写坏的字节不同；把这些偏移映射到 inj.raw 上覆盖即可。
#
# 偏移映射：注入只在开头 stringPool 追加了名字（pool_delta 字节）、并把 PlayerScriptsList 撑大
# （psl_delta 字节），所以 PSL 之前 +pool_delta、PSL 之后 +pool_delta+psl_delta；PSL 区间本身
# 用的是我们注入的版本，**必须跳过**（否则会把我们的脚本覆盖掉）。
#
# 用法：
#   python repair-roundtrip.py <clean.raw> <mc.raw> <inj.raw> <out.raw> <psl_dataStartPos> <psl_dataSize>
import struct, sys

def pool_bytes(d):
    o = 4
    cnt = struct.unpack_from('<i', d, o)[0]; o += 4
    for _ in range(cnt):
        ln = d[o]; o += 1 + ln + 4
    return o

def main():
    clean, mcr, inj, out, psl_start, psl_len = sys.argv[1:7]
    psl_start = int(psl_start); psl_len = int(psl_len)
    d = open(clean, 'rb').read()
    m = open(mcr, 'rb').read()
    j = bytearray(open(inj, 'rb').read())
    if len(d) != len(m):
        print("!! clean.raw 与 mc.raw 长度不同（%d vs %d），无法逐字节对照" % (len(d), len(m))); sys.exit(3)
    pool_delta = pool_bytes(j) - pool_bytes(m)
    psl_delta = (len(j) - len(m)) - pool_delta
    psl_end = psl_start + psl_len
    patched = skipped = 0
    for o in range(len(d)):
        if d[o] == m[o]:
            continue
        if psl_start <= o < psl_end:
            skipped += 1; continue
        k = o + (pool_delta if o < psl_start else pool_delta + psl_delta)
        if 0 <= k < len(j):
            j[k] = d[o]; patched += 1
    open(out, 'wb').write(bytes(j))
    print("repair: pool_delta=%d psl_delta=%d  复原字节=%d  跳过(PSL内)=%d -> %s"
          % (pool_delta, psl_delta, patched, skipped, out))

if __name__ == '__main__':
    main()
