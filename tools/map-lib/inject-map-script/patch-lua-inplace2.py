#!/usr/bin/env python3
"""patch-lua-inplace2.py — 把 Lua 等长原地写进 RA3 日冕 .map 的**正确边界版**。

正确的脚本正文边界（已用官方例子图 / 意大利战役 挑战版 6 段脚本校准）：
    正文起点 X = "#!ra3luabridge" 的字节偏移
    正文长度 L = 该偏移**前 2 字节**的 uint16(LE)      ← 记录长度字段，不是"到下一段 #! 的距离"
    正文 span  = [X, X+L)

旧（错误）算法："本段 #! 到下一段 #!"，对意大利战役第 1 段会多算 125 字节
(4239 vs 4114)，把后面 Script 记录的头字段一起覆盖掉 ⇒ 进游戏地图空、直接判负。

本脚本只改 [X, X+L) 这 L 个字节（长度字段本身不动），因此：
    - 该 Script 记录长度不变 → 后续结构一字不动；
    - 文件总字节不变；
    - 解压后与原图的差异只落在这段 span 内。

用法：patch-lua-inplace2.py <raw输入> <raw输出> <lua文件> [第几段=0]
"""
import sys, struct

PREFIX_OUT = b"#!ra3luabridge\r\n"          # 写入用前缀（与官方例子图一致）
PAD = b"-- end of script, please ignore this line"


def list_scripts(raw):
    """列出所有真实脚本条目 (X, L)。判据：'#!ra3luabridge' 且其前 8~2 字节是结构性 6 个 0。"""
    out = []
    i = 0
    while True:
        j = raw.find(b"#!ra3luabridge", i)
        if j < 0:
            break
        if raw[j - 8:j - 2] == b"\x00" * 6:
            L = struct.unpack("<H", raw[j - 2:j])[0]
            out.append((j, L))
        i = j + 1
    return out


def build_content(lua_body, L):
    body = lua_body.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    if not body.endswith(b"\r\n"):
        body += b"\r\n"
    fixed = PREFIX_OUT + body
    if len(fixed) > L:
        raise SystemExit("新正文 %d > 记录长度 %d，放不下，请精简 lua" % (len(fixed), L))
    rem = L - len(fixed)
    k = rem // len(PAD)
    r = rem - k * len(PAD)
    return fixed + PAD * k + b" " * r


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    raw_in, raw_out, lua_path = sys.argv[1], sys.argv[2], sys.argv[3]
    which = int(sys.argv[4]) if len(sys.argv) > 4 else 0
    raw = bytearray(open(raw_in, "rb").read())
    scripts = list_scripts(bytes(raw))
    X, L = scripts[which]
    old = bytes(raw[X:X + L])
    lua = open(lua_path, "rb").read()
    if lua[:3] == b"\xef\xbb\xbf":
        lua = lua[3:]
    # 去掉自带的首行 #!ra3luabridge
    first_nl = lua.find(b"\n")
    if lua[:first_nl].strip().startswith(b"#!ra3luabridge"):
        lua = lua[first_nl + 1:]
    new = build_content(lua, L)
    assert len(new) == L
    print("源图脚本条目数 = %d" % len(scripts))
    print("目标第 %d 段: X=%d 记录长度 L=%d (0x%x)" % (which, X, L, L))
    print("  原正文尾部 : %r" % old[-48:])
    print("  新正文尾部 : %r" % new[-48:])
    print("  新正文头部 : %r" % new[:48])
    raw[X:X + L] = new
    assert len(raw) == len(open(raw_in, "rb").read())
    open(raw_out, "wb").write(bytes(raw))
    print("已写出 %s (%d 字节, 与输入等长)" % (raw_out, len(raw)))
    # 差异必须只在 [X, X+L)
    base = open(raw_in, "rb").read()
    diffs = [i for i in range(len(base)) if base[i] != raw[i]]
    if diffs:
        print("  与输入的差异区间 = [%d, %d]  全部落在目标 span 内? %s"
              % (diffs[0], diffs[-1], diffs[0] >= X and diffs[-1] < X + L))
    else:
        print("  (与输入无差异)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
