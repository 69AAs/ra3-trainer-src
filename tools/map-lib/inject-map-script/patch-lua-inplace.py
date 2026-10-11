#!/usr/bin/env python3
"""patch-lua-inplace.py — 把 Lua 写进 .map 的**纯字节原地补丁**路线。

原理（为什么这条路线最可靠）：
  官方图里脚本 = PlayerScriptsList 资产里一条 ScriptAction 的字符串参数，
  形如  "#!ra3luabridge\\r\\n" + <lua源码> + "\\r\\n" + 100 * "-- end of script...".
  这段字符串在**未压缩**的 .map 里是明文。只要**新内容与旧内容字节数完全相同**，
  就可以原地覆盖，于是：
    - 所有长度字段 / 名字池下标 / 嵌套资产字段**一个字节都不变**；
    - 文件总字节数不变；
    - 与官方图逐字节的差异**只落在 lua 正文这一段 span 内**。
  这就绕开了 MapCoreLib 重写脚本区带来的所有结构漂移（编码、字段重排、+3 字节…）。

流程：MapTool resave 0 解压 → 原地补丁 → MapTool resave 1 重新压缩。
"""
import sys, os

PREFIX = b"#!ra3luabridge\r\n"
PAD = b"-- end of script, please ignore this line"
REDUNDANCY = 100


def find_content_span(raw):
    i = raw.find(PREFIX)
    if i < 0:
        raise SystemExit("找不到 #!ra3luabridge 前缀")
    j = raw.find(PAD, i)
    if j < 0:
        raise SystemExit("找不到占位注释行")
    start = i
    end = j + REDUNDANCY * len(PAD)
    return start, end


def build_content(lua_bytes, target_len):
    """构造与目标等长的脚本内容：PREFIX + lua + CRLF + N*PAD + 空格补齐。"""
    lua = lua_bytes.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")
    if not lua.endswith(b"\r\n"):
        lua += b"\r\n"
    fixed = PREFIX + lua
    tail = target_len - len(fixed)
    if tail < 0:
        raise SystemExit(
            "新脚本比原脚本区还长：%d > %d，请精简 lua（原图脚本区含 %d 行占位，可减行）"
            % (len(fixed), target_len, REDUNDANCY)
        )
    npads = tail // len(PAD)
    rem = tail - npads * len(PAD)
    return fixed + PAD * npads + b" " * rem


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        print("用法: patch-lua-inplace.py <raw输入.map> <输出raw.map> <lua文件>")
        return 2
    raw_in, raw_out, lua_path = sys.argv[1], sys.argv[2], sys.argv[3]
    raw = bytearray(open(raw_in, "rb").read())
    start, end = find_content_span(bytes(raw))
    orig_len = end - start
    lua = open(lua_path, "rb").read()
    # 去掉可能的 utf-8 BOM
    if lua[:3] == b"\xef\xbb\xbf":
        lua = lua[3:]
    new = build_content(lua, orig_len)
    assert len(new) == orig_len, (len(new), orig_len)
    print("脚本区 span = [%d, %d)  长度 %d" % (start, end, orig_len))
    print("新内容长度 = %d（等长，原地覆盖）" % len(new))
    raw[start:end] = new
    assert len(raw) == len(open(raw_in, "rb").read())
    open(raw_out, "wb").write(bytes(raw))
    print("已写出 %s（%d 字节，与输入等长）" % (raw_out, len(raw)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
