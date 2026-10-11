#!/usr/bin/env python3
"""建造上限对照实验工具（**受控写入**，带快照/回读/还原）。

为什么需要它：光靠"无上限单位应该是 0/-1"的差分定不了案（两只单位两个候选字段都不同）。
唯一可靠的办法是**改一个候选字段 → 看游戏能不能多造一个**。

安全纪律（写死在代码里）：
  · 只认进程名 ra3_1.12.game（TARGET 常量），名字不对直接退出；
  · 只写**当前选中对象**的模板里**一个 dword**；
  · 写前落盘快照（旧值 + 我们写入的新值 + 模板地址 + 单位内部名）；
  · 写后立刻回读，回读 != 写入值 ⇒ 报错并提示快照路径；
  · `--restore` 只在"当前值 == 快照里我们写的值"时才写回旧值（否则拒绝：绝不覆盖别人的改动）。

用法：
    python tools/limit_try.py --show                     # 只看当前选中单位模板的 A/B 候选
    python tools/limit_try.py --set 11C 100              # 把 +0x11C 写成 100（落快照 + 回读）
    python tools/limit_try.py --restore artifacts/limit-snapshots/<file>.json
"""
import ctypes
import ctypes.wintypes as wt
import datetime
import io
import json
import os
import struct
import sys

TARGET = "ra3_1.12.game"
BASE = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
CANDIDATES = {"A": 0x11C, "B": 0x3CC}
SNAPDIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "artifacts", "limit-snapshots")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.WriteProcessMemory.restype = wt.BOOL
k32.WriteProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.POINTER(ctypes.c_size_t)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


def find_pid(expect=TARGET):
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e)); pid = None; names = []
    while ok:
        nm = e.szExeFile.decode("latin1", "replace").rstrip("\x00")
        names.append(nm.lower())
        if nm.lower() == expect.lower():
            pid = e.th32ProcessID; break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    if pid is None:
        print("找不到进程 %s。当前进程名里有：%s" % (expect, ", ".join(sorted(set(names)))[:200]))
        raise SystemExit(2)
    return pid


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(0x0400 | 0x0010 | 0x0020, False, pid)   # QUERY|VM_READ|VM_WRITE
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, a, n):
        b = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(a & 0xFFFFFFFF), ctypes.byref(b), n,
                                     ctypes.byref(got)) or got.value != n:
            return None
        return b.raw

    def u32(self, a):
        b = self.raw(a, 4)
        return None if b is None else struct.unpack("<I", b)[0]

    def w32(self, a, v):
        buf = struct.pack("<I", v & 0xFFFFFFFF); wrote = ctypes.c_size_t()
        if not k32.WriteProcessMemory(self.h, ctypes.c_void_p(a & 0xFFFFFFFF), buf, 4, ctypes.byref(wrote)) \
                or wrote.value != 4:
            return False
        return self.u32(a) == (v & 0xFFFFFFFF)

    def cstr(self, a, n=48):
        b = self.raw(a, n)
        if not b:
            return None
        s = b.split(b"\x00")[0]
        return s.decode("latin1") if 3 <= len(s) <= 40 and all(32 <= c < 127 for c in s) else None


def selected(mem):
    root = mem.u32(BASE + SELECTION_ROOT_RVA)
    node = mem.u32(root + 0x50) if root else None
    wrap = mem.u32(node + 8) if node else None
    obj = mem.u32(wrap + 0x138) if wrap else None
    if not obj:
        raise SystemExit("现在没有选中对象（先在游戏里点一下那个单位）")
    tmpl = mem.u32(obj + 4)
    if not tmpl:
        raise SystemExit("对象 0x%08X 的模板指针读不到" % obj)
    name = None
    for off in range(0, 0x400, 4):
        p = mem.u32(tmpl + off)
        if p and 0x100000 < p < 0x7FFF0000:
            s = mem.cstr(p)
            if s and "\\" not in s and "/" not in s:
                name = s; break
    return obj, tmpl, mem.u32(tmpl + 8), name


def main():
    args = sys.argv[1:]
    pid = find_pid()
    mem = Mem(pid)
    obj, tmpl, tid, name = selected(mem)
    print("PID=%d 对象=0x%08X 模板=0x%08X 类型=0x%08X 单位=%s" % (pid, obj, tmpl, tid or 0, name))

    if "--show" in args or not args:
        for label, off in CANDIDATES.items():
            v = mem.u32(tmpl + off)
            print("   %s +0x%X = %s" % (label, off, v))
        return 0

    if "--set" in args:
        i = args.index("--set")
        field = args[i + 1].upper().replace("0X", "")
        value = int(args[i + 2], 0)
        off = CANDIDATES.get(field) or int(field, 16)
        old = mem.u32(tmpl + off)
        if old is None:
            raise SystemExit("旧值读不到，拒绝写")
        os.makedirs(SNAPDIR, exist_ok=True)
        path = os.path.join(SNAPDIR, "limit-%s.json" % datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
        io.open(path, "w", encoding="utf-8").write(json.dumps({
            "ts": datetime.datetime.now().isoformat(timespec="seconds"),
            "pid": pid, "object": "0x%08X" % obj, "template": "0x%08X" % tmpl,
            "type": "0x%08X" % (tid or 0), "unit": name,
            "offset": "0x%X" % off, "oldValue": old, "writtenValue": value,
        }, ensure_ascii=False, indent=1))
        print("快照: %s（旧值 %d → 写入 %d）" % (path, old, value))
        ok = mem.w32(tmpl + off, value)
        readback = mem.u32(tmpl + off)
        print("写入%s；回读 = %s" % ("成功" if ok else "失败", readback))
        if not ok:
            raise SystemExit("回读与写入不一致，请用 --restore %s 还原" % path)
        print("现在去游戏里试试能不能多造一个。测完：python tools/limit_try.py --restore %s" % path)
        return 0

    if "--setaddr" in args:
        # 直接对绝对地址写一个 dword（用于"计数表"这类不在模板里的字段）。同样落快照 + 回读。
        i = args.index("--setaddr")
        addr = int(args[i + 1], 16)
        value = int(args[i + 2], 0)
        old = mem.u32(addr)
        if old is None:
            raise SystemExit("旧值读不到，拒绝写")
        os.makedirs(SNAPDIR, exist_ok=True)
        path = os.path.join(SNAPDIR, "addr-%s.json" % datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
        io.open(path, "w", encoding="utf-8").write(json.dumps({
            "ts": datetime.datetime.now().isoformat(timespec="seconds"), "pid": pid,
            "address": "0x%08X" % addr, "oldValue": old, "writtenValue": value,
            "note": "按绝对地址写入（如每玩家每类型建造计数）",
        }, ensure_ascii=False, indent=1))
        print("快照: %s（0x%08X: %d → %d）" % (path, addr, old, value))
        ok = mem.w32(addr, value)
        print("写入%s；回读 = %s" % ("成功" if ok else "失败", mem.u32(addr)))
        if not ok:
            raise SystemExit("回读不一致，请 --restore %s" % path)
        print("测完：python tools/limit_try.py --restore %s" % path)
        return 0

    if "--restore" in args:
        path = args[args.index("--restore") + 1]
        snap = json.load(io.open(path, encoding="utf-8"))
        if "address" in snap:
            # 绝对地址快照（如每玩家每类型建造计数）
            addr = int(snap["address"], 16)
            cur = mem.u32(addr)
            if cur != snap["writtenValue"]:
                print("当前值 %s != 我们写入的 %s ⇒ 引擎自己改过（不是我们那次改动），不还原" % (cur, snap["writtenValue"]))
                return 0
            ok = mem.w32(addr, snap["oldValue"])
            print("还原%s：0x%08X = %s（旧值 %s）" % ("成功" if ok else "失败", addr, mem.u32(addr), snap["oldValue"]))
            return 0
        off = int(snap["offset"], 16)
        # 还原**不依赖当前选中**：用快照里记的模板地址（选中状态可能早就没了）。
        tmpl = int(snap["template"], 16)
        cur = mem.u32(tmpl + off)
        if cur != snap["writtenValue"]:
            raise SystemExit("当前值 %s != 我们写入的 %s ⇒ 不是我们那次改动，拒绝还原" % (cur, snap["writtenValue"]))
        ok = mem.w32(tmpl + off, snap["oldValue"])
        print("还原%s：+0x%X = %s（旧值 %s）" % ("成功" if ok else "失败", off, mem.u32(tmpl + off), snap["oldValue"]))
        return 0

    print(__doc__)
    return 2


sys.exit(main())
