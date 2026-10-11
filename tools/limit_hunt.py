#!/usr/bin/env python3
"""限量猎手（**只读**）：把"只能造 N 个"的那道闸门找出来。

背景与已实证的结论（2026-10-11，用户在游戏里配合取样）：
  * 模板里两个候选字段（塔 +0x11C / +0x3CC = 3）改成 100 ⇒ 按钮**还是灰的**；
  * 挖到的"每玩家每类型建造计数表"（[模板][键 94035248][3]）清零 ⇒ **还是灰的**；
  * 卖掉一个、重新造，总数仍是 3 ⇒ **闸门是图上活体数量**；
  ⇒ 所以"改数据"这条路走死了。要改的是**引擎每次数完活体之后的那次比较**。
     本工具只负责"把活体计数这个字段钉死"，改代码另做（见 runbook）。

用法（照顺序跑，全程只读、不写一个字节、不碰键鼠）：
    python tools/limit_hunt.py snap grey      # 他造满（按钮灰）时抓一份
    python tools/limit_hunt.py snap bright    # 他卖掉 1 个（按钮亮）时抓一份
    python tools/limit_hunt.py diff grey bright   # 两次之间"变了哪些字段"
    python tools/limit_hunt.py readers 0x11C      # 谁在读 [reg+0x11C]（候选点交给 disasm_window.py 看）
    python tools/limit_hunt.py selftest           # 不需要游戏的自检

安全：只 OpenProcess(PROCESS_QUERY_INFORMATION|PROCESS_VM_READ)，只读；不申请写权限。
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def crs():
    """延迟导入 code_ref_scan（它 import 时就会去连游戏 ⇒ 自检/离线命令不能被它挡住）。"""
    import code_ref_scan as module
    return module

TARGET = "ra3_1.12.game"
BASE = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
PLAYER_RECORD_HINT = 0x00C5F678      # 早先挖到的"玩家记录对象"指针所在处（best-effort）
SNAP_DIR = os.path.join("artifacts", "limit-hunt")

REG32 = ("eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi")


def base_reg(blob, modrm_pos, modrm, has_sib):
    """从 ModRM（+SIB）算出内存操作数的**基址寄存器**。自带一份，免得为了自检去连游戏。
    ★ 必须排除 esp/ebp ★（原话见 code_ref_scan.py）：`mov [esp+11Ch], eax` 是**栈上的大局部变量**，
    和"对象字段 +0x11C"毫无关系 —— 第一版没排除，1312 条候选里绝大多数是这种噪声。"""
    if not has_sib:
        return REG32[modrm & 7]
    sib = blob[modrm_pos + 1]
    if (sib & 7) == 5 and (modrm >> 6) == 0:
        return "disp32"          # [disp32 + index*scale]
    return REG32[sib & 7]

# 每个区域抓多少字节：模板给足（限额可能在更深处），其余够用就行
REGION_SPANS = {
    "root": 0x200,
    "node": 0x200,
    "wrapper": 0x400,
    "object": 0x400,
    "template": 0x1000,
    "player": 0x400,
}


def u32(raw, off=0):
    return None if raw is None or len(raw) < off + 4 else struct.unpack_from("<I", raw, off)[0]


def resolve_selection(mem):
    """复用 owner_probe/limit_probe 的选中解析：root → [root+0x50] → +8 → +0x138 = 对象；对象 +4 = 模板。"""
    root_ptr = u32(mem.raw(BASE + SELECTION_ROOT_RVA, 4))
    if not root_ptr:
        raise SystemExit("读不到选择根：游戏没进存档 / 没选中任何东西？")
    node = u32(mem.raw(root_ptr + 0x50, 4))
    wrapper = u32(mem.raw(node + 8, 4)) if node else None
    unit = u32(mem.raw(wrapper + 0x138, 4)) if wrapper else None
    template = u32(mem.raw(unit + 4, 4)) if unit else None
    return {"root": root_ptr, "node": node, "wrapper": wrapper, "object": unit, "template": template}


def snapshot(tag):
    pid = crs().find_pid()
    if not pid:
        raise SystemExit(f"没找到进程 {TARGET}：先把游戏跑起来并进到存档里。")
    mem = crs().Mem(pid)
    chain = resolve_selection(mem)
    if not chain["template"]:
        raise SystemExit("选中链没解析出来：先在游戏里**选中那个限量单位**再抓。")

    regions = {}
    for name, span in REGION_SPANS.items():
        base = chain.get("player") if name == "player" else chain.get(name)
        if name == "player":
            base = u32(mem.raw(PLAYER_RECORD_HINT, 4))       # 那里存的是"玩家记录对象"的地址
        if not base:
            continue
        raw = mem.raw(base, span)
        if raw:
            regions[name] = {"base": base, "hex": raw.hex()}

    code = u32(mem.raw(chain["template"] + 8, 4)) if chain["template"] else None
    payload = {"tag": tag, "code": code, "chain": chain, "regions": regions}
    os.makedirs(SNAP_DIR, exist_ok=True)
    path = os.path.join(SNAP_DIR, f"{tag}.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=1)

    print(f"✓ 快照 {tag}: 单位 type=0x{code:08X}" if code else f"✓ 快照 {tag}")
    print(f"  选中链: " + " · ".join(f"{k}=0x{v:08X}" for k, v in chain.items() if v))
    for name, data in regions.items():
        print(f"  {name:9s} @0x{data['base']:08X}  {len(data['hex']) // 2} 字节")
    print(f"  → {path}")
    return path


def _diffs(region, old_hex, new_hex):
    old = bytes.fromhex(old_hex)
    new = bytes.fromhex(new_hex)
    if len(old) != len(new):
        return [(region, -1, None, None, "区域长度不同（对象被换过？）")]
    out = []
    for off in range(0, len(old) - 3, 4):
        a = struct.unpack_from("<I", old, off)[0]
        b = struct.unpack_from("<I", new, off)[0]
        if a != b:
            out.append((region, off, a, b, "dword"))
    return out


def diff(tag_a, tag_b):
    with open(os.path.join(SNAP_DIR, f"{tag_a}.json"), encoding="utf-8") as h1:
        a = json.load(h1)
    with open(os.path.join(SNAP_DIR, f"{tag_b}.json"), encoding="utf-8") as h2:
        b = json.load(h2)

    print(f"单位代号: {tag_a}=0x{(a.get('code') or 0):08X}  {tag_b}=0x{(b.get('code') or 0):08X}"
          f"  {'（同一个单位 ✓）' if a.get('code') == b.get('code') else '（⚠ 不是同一个单位，对比无效）'}")
    rows = []
    for name, data in a["regions"].items():
        other = b["regions"].get(name)
        if not other:
            continue
        rows.extend(_diffs(name, data["hex"], other["hex"]))

    if not rows:
        print("两次快照没有任何 dword 变化 ⇒ 这两个状态下这个单位相关的内存没变（可能没选对单位）")
        return
    print(f"\n变了 {len(rows)} 处（按区域、偏移）:")
    print(f"  {'区域':9s} {'偏移':>7s}  {'旧值':>12s} {'新值':>12s}  小整数?  浮点参考")
    for name, off, old, new, kind in sorted(rows):
        flag = " ★小整数" if isinstance(old, int) and old <= 64 and isinstance(new, int) and new <= 64 else ""
        f_old = struct.unpack("<f", struct.pack("<I", old))[0] if isinstance(old, int) else 0
        f_new = struct.unpack("<f", struct.pack("<I", new))[0] if isinstance(new, int) else 0
        print(f"  {name:9s} 0x{off:05X}  0x{old:08X}  0x{new:08X}{flag}    {f_old:.3f}→{f_new:.3f}")
    print("\n★ 在变化的小整数里，围着 3/2 变的那几个就是「活体计数」的强候选；"
          "拿到偏移后用 `readers <disp>` 找谁在读它。")


# ---- 找"读/比较 [reg+disp]"的代码（复用 code_ref_scan 的 RX 枚举与基址解码）----

READ_FORMS = [
    (0x8B, "mov r32, [reg+disp]"),      # mov
    (0x3B, "cmp r32, [reg+disp]"),      # cmp（register, memory）
    (0x39, "cmp [reg+disp], r32"),      # cmp（memory, register）
]


def match_read_at(blob, pos, disp, want_disp_bytes):
    """在 blob[pos] 处判断是不是"读 [reg+disp]"，是就返回 (指令长度, 说明)。"""
    if pos + 2 > len(blob):
        return None
    opcode = blob[pos]
    entry = next((row for row in READ_FORMS if row[0] == opcode), None)
    if entry is None:
        return None
    modrm = blob[pos + 1]
    mod = modrm >> 6
    rm = modrm & 7
    if mod == 0:
        return None                                           # 无位移，不是我们要的
    has_sib = rm == 4
    disp_len = 1 if mod == 1 else 4
    if want_disp_bytes is not None and (disp_len == 4) != want_disp_bytes:
        return None
    start = pos + 2 + (1 if has_sib else 0)
    if start + disp_len > len(blob):
        return None
    value = struct.unpack_from("<i" if disp_len == 4 else "<b", blob, start)[0]
    if value != disp:
        return None
    reg = base_reg(blob, pos + 1, modrm, has_sib)
    if reg in ("esp", "ebp"):
        return None                                           # 栈帧噪声（老坑）
    return start + disp_len - pos, f"{entry[1]} 基址={reg}"


def readers(disp):
    pid = crs().find_pid()
    if not pid:
        raise SystemExit(f"没找到进程 {TARGET}（这一步需要游戏在跑，代码在内存里）。")
    mem = crs().Mem(pid)
    found = []
    for base, size, protect in (crs().regions(mem) if hasattr(crs(), "regions") else _fallback_regions(mem)):
        if not (protect & 0x10 or protect & 0x20 or protect & 0x40 or protect & 0x80):   # 只扫可执行页
            continue
        blob = mem.raw(base, size)
        if not blob:
            continue
        for pos in range(0, len(blob) - 8):
            hit = match_read_at(blob, pos, disp, want_disp_bytes=None)   # disp32 与 disp8 都认
            if hit:
                found.append((base + pos, hit[1]))
    print(f"读到 [reg+{disp:#x}] 的候选：{len(found)} 处")
    for address, what in found[:200]:
        module = "RA3_1.12.game+" + format(address - BASE, "X") if BASE <= address < BASE + 0x800000 else f"0x{address:08X}"
        print(f"  {module:24s} {what}")
    if len(found) > 200:
        print(f"  …还有 {len(found) - 200} 处（先看前 200）")
    print("\n下一步：挑候选，用 `python tools/disasm_window.py --addr <地址> --func --before 0x60 --after 0x60` 看上下文。")


def _fallback_regions(mem):
    """code_ref_scan 没暴露 regions() 时的兜底：用 VirtualQueryEx 自己走一遍。"""
    class MBI(ctypes.Structure):
        _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                    ("AllocationProtect", wt.DWORD), ("__pad", wt.DWORD), ("RegionSize", ctypes.c_size_t),
                    ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__pad2", wt.DWORD)]

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.VirtualQueryEx.restype = ctypes.c_size_t
    address = 0
    results = []
    while address < 0x80000000:
        mbi = MBI()
        if k32.VirtualQueryEx(mem.h, ctypes.c_void_p(address), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
            address += 0x1000
            continue
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        if mbi.State == 0x1000 and size and size < 64 * 1024 * 1024:
            results.append((base, size, mbi.Protect))
        address = (base + size) & 0xFFFFFFFF
        if size == 0:
            address += 0x1000
    return results


def selftest():
    """不需要游戏：拿手工构造的机器码验证"读 [reg+disp]"的识别（含必须排除的栈帧噪声）。"""
    cases = [
        # 真命中：mov ecx, [ebx+0x11C]  (8B 8B 1C 01 00 00)
        (bytes.fromhex("8b8b1c010000"), 0, 0x11C, True),
        # 真命中：cmp eax, [esi+0x3CC]  (3B 86 CC 03 00 00)
        (bytes.fromhex("3b86cc030000"), 0, 0x3CC, True),
        # 真命中：cmp [edi+0x50], ecx   (39 4F 50, disp8)
        (bytes.fromhex("394f50"), 0, 0x50, True),
        # 噪声：mov eax, [esp+0x11C] —— 栈帧，必须剔除
        (bytes.fromhex("8b84241c010000"), 0, 0x11C, False),
        # 噪声：位移不匹配
        (bytes.fromhex("8b8b20010000"), 0, 0x11C, False),
        # 噪声：写指令（mov [reg+disp], r32）
        (bytes.fromhex("898b1c010000"), 0, 0x11C, False),
    ]
    for blob, pos, disp, expected in cases:
        got = match_read_at(blob, pos, disp, want_disp_bytes=None) is not None
        tag = "✓" if got == expected else "✗"
        print(f"  {tag} {blob.hex():18s} disp={disp:#x} 期望命中={expected} 实得={got}")
        if got != expected:
            raise SystemExit("自检失败")
    print("✓ 自检通过：识别「读 [reg+disp]」、剔除栈帧噪声、不误判写指令")


def main():
    parser = argparse.ArgumentParser(description="只读找出「只能造 N 个」的闸门")
    sub = parser.add_subparsers(dest="command", required=True)
    p_snap = sub.add_parser("snap", help="抓一份快照（只读）")
    p_snap.add_argument("tag")
    p_diff = sub.add_parser("diff", help="对比两份快照里变化的字段")
    p_diff.add_argument("a")
    p_diff.add_argument("b")
    p_read = sub.add_parser("readers", help="找「读 [reg+disp]」的代码")
    p_read.add_argument("disp", help="例如 0x11C")
    sub.add_parser("selftest", help="不需要游戏的自检")
    args = parser.parse_args()

    if args.command == "snap":
        snapshot(args.tag)
    elif args.command == "diff":
        diff(args.a, args.b)
    elif args.command == "readers":
        readers(int(args.disp, 0))
    else:
        selftest()


if __name__ == "__main__":
    main()
