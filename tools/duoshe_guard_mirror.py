#!/usr/bin/env python3
"""夺舍 · 「为什么这个对象被拒」只读镜像（不写一个字节）。

把 `SelectedObjectGuard.Validate` 对 SELECT UNIT CHANGE ID 那几条判据**原样**在进程外重算一遍，
并把每个判据的**真实读数**打出来 —— 用来回答「建筑/某个单位为什么不能夺舍」这类问题，
不必再靠猜、也不必让用户重试。

判据（与 SelectedObjectGuard.cs:100-160 一致）：
  · 链：S=[game+8DB73C] → N=[S+5C] → L=[S+50] → W=[L+8] → U=[W+0x138] → T=[U+4] → type=[T+8]
  · 类型：type 必须能在 built-in 单位目录里查到（ReinforcementUnitCatalog.LoadBuiltIn）
  · 转让黑名单：6 个类型码（SelectedObjectGuard.cs:140）
  · 生命：H=[U+0x33C]，H+4 / H+0xC / H+0x10 三个 float 都要落在 [1, 100000]
  · 坐标：U+0x38 / U+0x3C / U+0x40 三个 float 都要落在 [-1, 10000]
  · 归属：[U+0x418] 必须是可读指针

用法：
    python tools/duoshe_guard_mirror.py                 # 自动找模块基址
    python tools/duoshe_guard_mirror.py <ID块地址十六进制>   # 额外对照本机玩家 [ID]
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import os

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TARGET = "ra3_1.12.game"
SELECTION_ROOT_RVA = 0x8DB73C
TRANSFER_BLACKLIST = {0x95D6E965, 0xFD87E82A, 0x856C9DD6, 0xD20552E1, 0x1AFC9A6E, 0xD6D22475}
MIN_HEALTH, MAX_HEALTH = 1.0, 100000.0
MIN_COORD, MAX_COORD = -1.0, 10000.0
CATALOG = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                       "..", "src", "Ra3Trainer.Core", "Assets", "unit-catalog-full.txt")

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


class ME32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD), ("modBaseAddr", ctypes.POINTER(ctypes.c_byte)),
                ("modBaseSize", wt.DWORD), ("hModule", wt.HMODULE), ("szModule", ctypes.c_char * 256),
                ("szExePath", ctypes.c_char * 260)]


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


def find_base(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)   # TH32CS_SNAPMODULE | SNAPMODULE32
    m = ME32(); m.dwSize = ctypes.sizeof(m)
    ok = k32.Module32First(snap, ctypes.byref(m)); base = 0
    while ok:
        if m.szModule.decode("latin1", "replace").rstrip("\x00").lower() == TARGET.lower():
            base = ctypes.cast(m.modBaseAddr, ctypes.c_void_p).value; break
        ok = k32.Module32Next(snap, ctypes.byref(m))
    k32.CloseHandle(snap)
    return base


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf), n, ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw

    def u32(self, addr):
        b = self.raw(addr, 4)
        return None if b is None else struct.unpack("<I", b)[0]

    def f32(self, addr):
        b = self.raw(addr, 4)
        return None if b is None else struct.unpack("<f", b)[0]


def load_catalog_codes():
    codes = set()
    path = os.path.normpath(CATALOG)
    if not os.path.exists(path):
        return codes
    with open(path, encoding="utf-8", errors="replace") as h:
        for line in h:
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 2:
                try:
                    codes.add(int(parts[0], 16))
                except ValueError:
                    pass
    return codes


def verdict(ok):
    return "PASS" if ok else "FAIL  ← 就是这条拒了他"


def main():
    id_addr = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0
    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s 进程（游戏没在跑）" % TARGET)
    base = find_base(pid)
    m = Mem(pid)
    print("PID=%d  module base=0x%X  catalog=%d 条" % (pid, base, len(load_catalog_codes())))
    codes = load_catalog_codes()

    S = m.u32(base + SELECTION_ROOT_RVA)
    if not S:
        raise SystemExit("S=[game+8DB73C] 不可读")
    N = m.u32(S + 0x5C)
    print("S=0x%X  N(count)=%s" % (S, N))
    if not N:
        raise SystemExit("==> 当前没有选中对象（count=0）：请先在游戏里点选那个建筑/单位")
    L = m.u32(S + 0x50); W = m.u32(L + 8) if L else None
    U = m.u32(W + 0x138) if W else None
    if not U:
        raise SystemExit("单位对象链断裂（L=0x%X W=0x%X）" % (L or 0, W or 0))
    T = m.u32(U + 4); tc = m.u32(T + 8) if T else None
    print("L=0x%X  W=0x%X  U=0x%X  T=0x%X" % (L, W, U, T or 0))

    known = tc in codes if tc is not None else False
    print("\n[1] 类型：type=0x%08X  %s（%s）" %
          (tc or 0, "在单位目录里" if known else "不在单位目录里", "PASS" if known else "FAIL"))
    black = tc in TRANSFER_BLACKLIST
    print("[2] 转让黑名单：%s" % ("在名单里 → FAIL" if black else "不在名单里 PASS"))

    H = m.u32(U + 0x33C)
    print("\n[3] 生命链：H=[U+0x33C]=0x%X %s" % (H or 0, verdict(bool(H) and (H & 3) == 0)))
    if H:
        for off in (4, 0xC, 0x10):
            v = m.f32(H + off)
            ok = v is not None and v == 999999.0 or (v is not None and MIN_HEALTH <= v <= MAX_HEALTH)
            print("      H+0x%02X = %-16s bits=0x%08X  %s" %
                  (off, ("%.4f" % v) if v is not None else "不可读",
                   struct.unpack("<I", struct.pack("<f", v))[0] if v is not None else 0, verdict(bool(ok))))
    print("\n[4] 坐标：")
    for off in (0x38, 0x3C, 0x40):
        v = m.f32(U + off)
        ok = v is not None and MIN_COORD <= v <= MAX_COORD
        print("      U+0x%02X = %-16s %s" % (off, ("%.4f" % v) if v is not None else "不可读", verdict(bool(ok))))

    owner = m.u32(U + 0x418)
    print("\n[5] 归属：[U+0x418] = %s（%s）" %
          (("0x%08X" % owner) if owner is not None else "不可读", verdict(owner is not None)))
    if id_addr:
        lp = m.u32(id_addr)
        print("      [ID] 本机玩家 = %s  →  %s" %
              (("0x%08X" % lp) if lp else lp, "已属你（会判'已属本机'）" if lp and owner == lp else "非你所有（可转让）"))

    print("\n--- 附近只读窗口（找这个对象的血量到底在哪）---")
    for start, end in ((0x300, 0x380), (0x3E0, 0x440)):
        for off in range(start, end + 1, 4):
            b = m.raw(U + off, 4)
            if b is None:
                print("  U+0x%03X 不可读" % off); continue
            iv = struct.unpack("<I", b)[0]
            fv = struct.unpack("<f", b)[0]
            interesting = (MIN_HEALTH <= fv <= MAX_HEALTH) and abs(fv) > 1.0
            print("  U+0x%03X = 0x%08X  as float=%-18s %s" %
                  (off, iv, ("%.4f" % fv) if fv == fv and abs(fv) != float("inf") else "nan/inf",
                   "<== 像血量/有限值" if interesting else "")) 


main()
