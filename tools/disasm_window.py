#!/usr/bin/env python3
"""只读反汇编窗口：把游戏某个地址附近的**真实机器码**读出来反汇编，并把 call/jmp 目标归位到模块。

用途（B 路取证）：`code_ref_scan.py` 找出"写 `[reg+414h]`"的候选地址后，用它看清这段代码到底在干什么 ——
写进去的值是从哪来的？函数入口在哪？调了谁？**不写一个字节、不挂调试器。**

用法：
    python tools/disasm_window.py --addr 641D43 --func --before 0x60 --after 0x60
    python tools/disasm_window.py --modules                       # 列出模块 ↔ 地址范围（把偏移归位）
    python tools/disasm_window.py --addr 641D43 --func --follow   # 顺带把 call 目标也反汇编一小段
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

TARGET = "ra3_1.12.game"
PROCESS_QUERY_INFORMATION, PROCESS_VM_READ = 0x0400, 0x0010

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


class ME32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD),
                ("modBaseAddr", ctypes.POINTER(ctypes.c_byte)), ("modBaseSize", wt.DWORD),
                ("hModule", wt.HMODULE), ("szModule", ctypes.c_char * 256), ("szExePath", ctypes.c_char * 260)]


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


def modules(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    m = ME32(); m.dwSize = ctypes.sizeof(m)
    out = []
    if k32.Module32First(snap, ctypes.byref(m)):
        while True:
            out.append((ctypes.cast(m.modBaseAddr, ctypes.c_void_p).value or 0, m.modBaseSize,
                        m.szModule.decode("latin1", "replace").rstrip("\x00")))
            if not k32.Module32Next(snap, ctypes.byref(m)):
                break
    k32.CloseHandle(snap)
    return sorted(out)


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


def where(mods, addr):
    for base, size, name in mods:
        if base <= addr < base + size:
            return "%s+%X" % (name, addr - base)
    return "??+%08X" % addr


def find_func_start(mem, addr):
    """从 addr 往回找函数入口：先找最近的 int3 填充/ret 之后，再退到看起来像序言的地方。"""
    back = mem.raw(addr - 0x800, 0x800) or b""
    if not back:
        return addr
    base = addr - 0x800
    cands = []
    for i in range(len(back) - 1):
        if back[i] == 0xCC and back[i + 1] in (0x55, 0x53, 0x56, 0x57, 0x8B, 0x64, 0x6A, 0x83, 0x81, 0xE8):
            cands.append(base + i + 1)
        if back[i] in (0xC3, 0xC2) and back[i + 1] in (0xCC, 0x55, 0x53, 0x8B, 0xCC):
            cands.append(base + i + 1)
    return max(cands) if cands else addr


def disasm(mem, start, end, mods, probe):
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    md.detail = True
    blob = mem.raw(start, end - start)
    if not blob:
        print("   （读不到 0x%08X）" % start); return
    for ins in md.disasm(blob, start):
        note = ""
        for op in ins.operands:
            if op.type == 3 and op.mem.base:   # X86_OP_MEM
                if op.mem.disp in (0x414, 0x418):
                    note += "   ← 引用 +0x%X" % op.mem.disp
            if op.type == 2:                   # X86_OP_IMM
                t = op.imm & 0xFFFFFFFF
                if 0x00400000 <= t <= 0x7FFFFFFF:
                    note += "   → %s" % where(mods, t)
        print("   %08X  %-28s %s%-8s%s" % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str, note))


def main():
    args = sys.argv[1:]
    addr = None; before = 0x60; after = 0x60; do_func = False; follow = False
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--addr" and nxt:
            addr = int(nxt, 16)
        elif a == "--before" and nxt:
            before = int(nxt, 0)
        elif a == "--after" and nxt:
            after = int(nxt, 0)
        elif a == "--func":
            do_func = True
        elif a == "--follow":
            follow = True

    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）" % TARGET)
    mem = Mem(pid)
    mods = modules(pid)

    if addr is None:
        print("PID=%d，模块 %d 个：" % (pid, len(mods)))
        for base, size, name in mods:
            print("   %-28s base=0x%08X size=0x%X" % (name, base, size))
        return

    start = find_func_start(mem, addr) if do_func else max(0, addr - before)
    print("PID=%d 地址 0x%08X = %s" % (pid, addr, where(mods, addr)))
    if do_func:
        print("推出函数入口 0x%08X = %s（距目标 0x%X）" % (start, where(mods, start), addr - start))
    print("\n反汇编 0x%08X → 0x%08X：" % (start, addr + after))
    disasm(mem, start, addr + after, mods, follow)


main()
