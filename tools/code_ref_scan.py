#!/usr/bin/env python3
"""代码引用扫描（**只读**）：在游戏的可执行内存里找"写 `[reg+414h]` / `[reg+418h]` 的指令"。

为什么用它（替代挂调试器）：配对样本已定案缺口字段是 `U+0x414`，而游戏自己在归属变化时会写它。
那条指令必然是"带 disp32 的内存写"，字节模式是固定的：

    89 /r   mov r/m32, r32         → 89 [80-BF] <disp32>            （mod=10 才有 disp32）
    C7 /0   mov r/m32, imm32       → C7 [80-87] <disp32> <imm32>
    88 /r   mov r/m8, r8           （顺带看，不是我们的字段）

于是只要在 **RX 区域**里搜 `<disp32>` 这 4 个字节，再回头校验前 1~2 字节是不是
"写指令 + mod=10"（rm=100 时中间还夹一个 SIB 字节），就能把候选点全找出来。

★ 自证方式 ★：我们的载荷里有 `mov [ebx+418],eax`（`89 83 18 04 00 00`）。
  这个扫描器**必须**把它找出来 —— 找得到才证明扫描器是准的；找不到就说明模式写错了。

用法：
    python tools/code_ref_scan.py --selftest                 # 合成数据自检（不需要游戏）
    python tools/code_ref_scan.py                            # 扫正在跑的游戏（0x414 + 0x418）
    python tools/code_ref_scan.py --offsets 414,418,4E4 --group 0x100 --out artifacts/code-scan.jsonl

只申请 PROCESS_QUERY_INFORMATION|PROCESS_VM_READ，一个字节都不写；不碰键鼠、不激活窗口。
局限：用 `lea` 先算出地址再写的写法扫不到（会漏），这一点写在结论里。
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys

TARGET = "ra3_1.12.game"
PROCESS_QUERY_INFORMATION, PROCESS_VM_READ = 0x0400, 0x0010
MEM_COMMIT = 0x1000
EXECUTE_PROTECTIONS = {0x10, 0x20, 0x40, 0x80}   # EXECUTE / EXECUTE_READ / EXECUTE_READWRITE / EXECUTE_WRITECOPY
PAGE_GUARD, PAGE_NOACCESS = 0x100, 0x01
WRITE_OPS = {0x89: "mov r/m32,r32", 0x88: "mov r/m8,r8"}
IMM_OPS = {0xC7: "mov r/m32,imm32", 0xC6: "mov r/m8,imm8"}
READ_OPS = {0x8B: "mov r32,r/m32", 0x0F: "sse?"}
CHUNK = 1 << 20


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__align1", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD), ("__align2", wt.DWORD)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.restype = ctypes.c_size_t
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]


def proc_name(pid):
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e)); name = None
    while ok:
        if e.th32ProcessID == pid:
            name = e.szExeFile.decode("latin1", "replace").rstrip("\x00"); break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return name


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e)); pid = None
    while ok:
        if e.szExeFile.decode("latin1", "replace").rstrip("\x00").lower() == TARGET.lower():
            pid = e.th32ProcessID; break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return pid


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr & 0xFFFFFFFF), ctypes.byref(buf), n,
                                     ctypes.byref(got)):
            return None
        return buf.raw[:got.value]


REG32 = ("eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi")


def base_reg(blob, modrm_pos, modrm, has_sib):
    """算出这条内存操作数的**基址寄存器**。★ 必须排除 esp/ebp ★：
    那些形如 `mov [esp+414h], eax` / `mov [ebp+414h], eax` 的"命中"是**栈上的大局部变量**
    （很多函数栈帧就是 0x43C/0x418 字节），与"对象字段 +0x414"毫无关系 —— 第一版没排除，
    1312 条候选里绝大多数是这种噪声（`RA3_1.12.game+241CC0` 那个 10 条的"强候选"整窝都是）。"""
    if not has_sib:
        return REG32[modrm & 0x07]
    sib = blob[modrm_pos + 1]
    base = sib & 0x07
    return REG32[base] if base != 5 else None     # base=101 ⇒ 无基址（只有 index*scale+disp32）


def match_at(blob, pos, disp_pos, include_stack=False):
    """disp_pos 处的 4 字节等于目标偏移时，往回校验是不是一条"写"指令。返回 (偏移, 说明) 或 None。"""
    for back, sib in ((2, False), (3, True)):
        if disp_pos - back < 0:
            continue
        op = blob[disp_pos - back]
        modrm_pos = disp_pos - back + 1
        modrm = blob[modrm_pos]
        if (modrm & 0xC0) != 0x80:            # mod=10 才有 disp32
            continue
        has_sib = (modrm & 0x07) == 0x04
        if has_sib != sib:
            continue
        br = base_reg(blob, modrm_pos, modrm, has_sib)
        if not include_stack and br in ("esp", "ebp", None):
            continue
        if op in WRITE_OPS:
            return back, "写 %s  [%s+...]" % (WRITE_OPS[op], br)
        if op in IMM_OPS and not sib and (modrm & 0x38) == 0:
            return back, "写 %s  [%s+...]" % (IMM_OPS[op], br)
        if op in READ_OPS:
            return back, "读 %s  [%s+...]" % (READ_OPS[op], br)
    return None


def scan_region(mem, base, size, targets, include_stack=False):
    """返回 [(insn_addr, disp, kind, region_base)]。disp 是"该指令里引用 0x??? 偏移"。"""
    out = []
    pos = 0
    while pos < size:
        n = min(CHUNK, size - pos)
        blob = mem.raw(base + pos, n)
        if not blob:
            return out
        for disp in targets:
            needle = struct.pack("<I", disp)
            start = 0
            while True:
                i = blob.find(needle, start)
                if i < 0:
                    break
                m = match_at(blob, pos + i, i, include_stack)
                if m:
                    back, kind = m
                    out.append({"addr": base + pos + i - back, "disp": disp, "kind": kind})
                start = i + 1
        pos += n
        if len(blob) < n:
            return out
    return out


def selftest():
    """合成数据自检：已知写法必须挑出来，**栈形式必须被排除**。"""
    blob = bytearray(b"\x90" * 0x300)
    known, must_skip = {}, {}
    blob[0x40:0x46] = bytes.fromhex("898318040000")     # mov [ebx+418h], eax ← 我们载荷里那条
    known[0x40] = 0x418
    blob[0x80:0x86] = bytes.fromhex("899114040000")     # mov [ecx+414h], edx
    known[0x80] = 0x414
    blob[0xC0:0xC6] = bytes.fromhex("898E14040000")     # mov [esi+414h], ecx
    known[0xC0] = 0x414
    blob[0x100:0x107] = bytes.fromhex("c7801404000078563412")   # mov dword [eax+414h], imm32
    known[0x100] = 0x414
    blob[0x140:0x147] = bytes.fromhex("89940814040000")  # mov [eax+ecx*1+414h], edx（SIB：base=eax）
    known[0x140] = 0x414
    blob[0x180:0x186] = bytes.fromhex("8b8318040000")    # mov eax,[ebx+418h]（读）
    known[0x180] = 0x418
    blob[0x200:0x206] = bytes.fromhex("89842414040000")  # mov [esp+414h], eax  ← 栈帧，必须排除
    must_skip[0x200] = "esp 基址"
    blob[0x240:0x246] = bytes.fromhex("898514040000")    # mov [ebp+414h], eax  ← 栈帧，必须排除
    must_skip[0x240] = "ebp 基址"
    hits = {}
    for disp in (0x414, 0x418):
        needle = struct.pack("<I", disp)
        start = 0
        while True:
            i = blob.find(needle, start)
            if i < 0:
                break
            m = match_at(blob, 0, i)
            if m:
                hits[i - m[0]] = (disp, m[1])
            start = i + 1
    for a in sorted(hits):
        print("  0x%03X → disp=0x%X %s" % (a, hits[a][0], hits[a][1]))
    missing = [hex(a) for a in known if a not in hits]
    wrong = [hex(a) for a in known if a in hits and hits[a][0] != known[a]]
    leaked = [hex(a) for a in must_skip if a in hits]
    ok = not missing and not wrong and not leaked
    print("自检：%s（应挑出 %d 条、命中 %d 条%s%s%s）"
          % ("通过 ✓" if ok else "失败 ✗", len(known), len(hits),
             ("，漏: " + ",".join(missing)) if missing else "",
             ("，值错: " + ",".join(wrong)) if wrong else "",
             ("，栈形式没排除: " + ",".join(leaked)) if leaked else ""))


def main():
    args = sys.argv[1:]
    offsets, group, out = [0x414, 0x418], 0x100, None
    include_stack = False
    if "--selftest" in args:
        selftest(); return
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--offsets" and nxt:
            offsets = [int(x, 16) for x in nxt.split(",")]
        elif a == "--group" and nxt:
            group = int(nxt, 16)
        elif a == "--out" and nxt:
            out = nxt
        elif a == "--include-stack":
            include_stack = True

    forced_pid, expect = None, TARGET
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--pid" and nxt:
            forced_pid = int(nxt)
        elif a == "--expect" and nxt:
            expect = nxt
    pid = forced_pid if forced_pid else find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）—— 扫描器要读它的可执行内存" % expect)
    real = proc_name(pid)
    if real is None or real.lower() != expect.lower():
        raise SystemExit("拒绝：PID %d 的进程名是 %r，与 --expect %r 不符" % (pid, real, expect))
    mem = Mem(pid)
    print("PID=%d，扫 0x%s 的写指令" % (pid, "/0x".join("%X" % o for o in offsets)), flush=True)

    mbi = MBI(); addr = 0; hits = []; regions = 0
    while k32.VirtualQueryEx(mem.h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        base = mbi.BaseAddress or 0
        size = mbi.RegionSize
        prot = mbi.Protect
        if (mbi.State == MEM_COMMIT and size and prot not in (0, PAGE_NOACCESS)
                and not (prot & PAGE_GUARD) and (prot & 0xFF) in EXECUTE_PROTECTIONS):
            regions += 1
            hits += scan_region(mem, base, size, offsets, include_stack)
        addr = base + size
        if addr > 0x7FFFFFFF:
            break
    print("可执行区域 %d 个，候选指令 %d 条" % (regions, len(hits)), flush=True)

    # 按"同一段代码"分组：地址接近的算一处（一条例程里常同时写 414 和 418）
    hits.sort(key=lambda h: h["addr"])
    groups = []
    for h in hits:
        if groups and h["addr"] - groups[-1][-1]["addr"] <= group:
            groups[-1].append(h)
        else:
            groups.append([h])
    print("\n聚成 %d 处（间隔 <= 0x%X 归一组）：" % (len(groups), group))
    for g in groups:
        disps = sorted({h["disp"] for h in g})
        writes = [h for h in g if h["kind"].startswith("写")]
        print("\n  ▸ 0x%08X 附近（%d 条；涉及 0x%s）%s"
              % (g[0]["addr"], len(g), "/0x".join("%X" % d for d in disps),
                 "  ★ 同时写 414 和 418 ⇒ 这就是登记例程的强候选" if {0x414, 0x418} <= set(disps) else ""))
        for h in g[:12]:
            print("      0x%08X  %-18s disp=0x%X" % (h["addr"], h["kind"], h["disp"]))

    if out:
        with open(out, "w", encoding="utf-8") as fh:
            for h in hits:
                fh.write(json.dumps(h, ensure_ascii=False) + "\n")
        print("\n已写入 %s" % os.path.abspath(out))


if __name__ == "__main__":
    main()
