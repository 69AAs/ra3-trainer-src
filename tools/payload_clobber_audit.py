#!/usr/bin/env python3
"""载荷「顶了等于白顶」普查（**只读 exe + aa，不碰游戏**）。

用途：这类老 CE 表（Ken22265）的载荷通常是"把某个字段强顶成一个大数"，但如果 **挂钩返回点后面紧跟着的
指令又把这个字段写回去**，那这个功能就是"开了没反应"。2026-10-11「消散战争迷雾」就是这么坏的
（`0x260` 被下面的 `fld [ebp+258] / fstp [ebp+260]` 覆盖）。

这套检查把 `00_bootstrap.aa` 里每个挂钩点解出来（挂钩 RVA + patch 长度 = jmp(5) + 紧随的 nop 数），
再看载荷里顶过的每个 `[reg+disp]`，在返回点之后 8 条指令内有没有被**再写一次** ⇒ 有就是可疑。

用法：D:/work/tools-venv/Scripts/python.exe tools/payload_clobber_audit.py
（需要 capstone；venv 在 D:/work/tools-venv）
"""

import re, struct
from capstone import Cs, CS_ARCH_X86, CS_MODE_32
AA = r"D:/work/ra3-trainer-refine/src/Ra3Trainer.Core/Assets/00_bootstrap.aa"
EXE = r"D:/Games/Red Alert 3/Data/RA3_1.12.game"
BASE = 0x400000
data = open(EXE, "rb").read()
lines = open(AA, encoding="utf-8", errors="replace").read().splitlines()

hook_re = re.compile(r"^ra3_1\.12\.game\+([0-9A-Fa-f]+):\s*$")
nop_re = re.compile(r"^\s*nop\s*$", re.I)
# 载荷里"顶字段"的写法：mov [reg+disp],imm   （含 [ebp+00000260] 这种）
write_re = re.compile(r"^\s*mov\s+\[([a-z]{3})\+([0-9A-Fa-f]+)\]\s*,\s*([0-9A-Fa-f]+)", re.I)

hooks = []
i = 0
while i < len(lines):
    m = hook_re.match(lines[i].strip())
    if m:
        site = int(m.group(1), 16)
        # patch 长度 = 5(jmp rel32) + 紧随的 nop 行数
        j = i + 1; nops = 0
        if j < len(lines) and lines[j].strip().lower().startswith("jmp"):
            j += 1
            while j < len(lines) and nop_re.match(lines[j]):
                nops += 1; j += 1
        patch_len = 5 + nops
        # 找这个挂钩对应的载荷体（最近的 MustCode…: 之后到下一个 label）
        body_writes = []
        k = i
        scanned = 0
        while k < len(lines) and scanned < 200:
            s = lines[k].strip()
            if k > i and hook_re.match(s):      # 下一个挂钩点 ⇒ 停
                break
            wm = write_re.match(s)
            if wm:
                body_writes.append((wm.group(1).lower(), int(wm.group(2), 16), wm.group(3)))
            k += 1; scanned += 1
        hooks.append((site, patch_len, body_writes))
    i += 1

md = Cs(CS_ARCH_X86, CS_MODE_32)
print(f"解析出 {len(hooks)} 个挂钩点\n")
risky = []
for site, patch_len, writes in hooks:
    if not writes:
        continue
    cont = site + patch_len
    off = cont
    code = data[off:off+0x40]
    following = []
    for ins in md.disasm(code, BASE + cont):
        following.append(ins)
        if ins.mnemonic in ("ret", "jmp") or len(following) >= 8:
            break
    for reg, disp, imm in writes:
        for ins in following:
            ops = ins.op_str.replace(" ", "").lower()
            if "[" in ops and reg in ops and hex(disp)[2:] in ops:
                # 这条后续指令是不是"写"这个字段（mov/movss/fstp 到内存）
                if ins.mnemonic in ("mov", "movss", "movsd", "fstp", "fst", "movaps") and ops.split(",")[0].startswith("dwordptr["):
                    risky.append((site, reg, disp, imm, ins.mnemonic, ins.op_str, patch_len))
                    break
print("=== 可疑：载荷顶了字段、但返回后立刻又被写（等于白顶）===")
if not risky:
    print("  没有")
for site, reg, disp, imm, mn, ops, pl in risky:
    print(f"  game+{site:07X}  载荷顶 [{reg}+{disp:X}]={imm}  ⇒ 返回后第一条写同字段: {mn} {ops}  (patch_len={pl})")
print(f"\n共检查带字段写的载荷 {sum(1 for _,_,w in hooks if w)} 个，可疑 {len(risky)} 处")
