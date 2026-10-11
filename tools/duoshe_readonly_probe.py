#!/usr/bin/env python3
"""夺舍 · 真机只读诊断（不写一个字节）。

只做一件事：把"当前选中单位"按引擎原文那条链读出来，并与本机玩家指针 [ID] 对照。
  S=[game+8DB73C] → N=[S+5C] → L=[S+50] → W=[L+8] → U=[W+138]
  T=[U+4] ; type=[T+8] ; owner=[U+418]
  [ID] = 修改器分配的 ID 块基址处的 dword（地址由修改器会话日志给出，每次附加都变）
出处：Assets/00_bootstrap.aa:966-997、ReadOnlyProbe.cs:26-55、SelectedObjectGuard.cs:100-108
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TARGET = "ra3_1.12.game"
SELECTION_ROOT_RVA = 0x8DB73C
SELECTED_CODE_RVA = 0x8E9838

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)

    class PE32(ctypes.Structure):
        _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                    ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                    ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                    ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]
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

    def u32(self, addr):
        buf = ctypes.create_string_buffer(4); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf), 4, ctypes.byref(got)) or got.value != 4:
            return None
        return struct.unpack("<I", buf.raw)[0]

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf), n, ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw


def main():
    base = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x400000
    id_addr = int(sys.argv[2], 16) if len(sys.argv) > 2 else 0
    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s 进程" % TARGET)
    m = Mem(pid)
    print("PID=%d  base=0x%X  [ID] block=0x%X" % (pid, base, id_addr))

    S = m.u32(base + SELECTION_ROOT_RVA)
    print("S = [game+8DB73C]      = %s" % ("0x%X" % S if S else S))
    if not S:
        raise SystemExit("选择根不可用")
    N = m.u32(S + 0x5C)
    print("N = [S+0x5C] 选中数量   = %d" % (N if N is not None else -1))
    if N is not None and N == 0:
        print("==> 当前没有选中单位（count=0）")
        return
    L = m.u32(S + 0x50); print("L = [S+0x50] 头节点     = %s" % ("0x%X" % L if L else L))
    W = m.u32(L + 8) if L else None; print("W = [L+8]   包装对象    = %s" % ("0x%X" % W if W else W))
    U = m.u32(W + 0x138) if W else None; print("U = [W+0x138] 单位本体  = %s" % ("0x%X" % U if U else U))
    if not U:
        raise SystemExit("单位对象链断裂")
    T = m.u32(U + 4); print("T = [U+4]   模板        = %s" % ("0x%X" % T if T else T))
    tc = m.u32(T + 8) if T else None
    owner = m.u32(U + 0x418)
    code = m.u32(base + SELECTED_CODE_RVA)
    print("type = [T+8]            = %s" % ("0x%08X" % tc if tc else tc))
    print("owner = [U+0x418]       = %s" % ("0x%08X" % owner if owner is not None else "不可读"))
    print("selected code [game+8E9838] = %s" % ("0x%08X" % code if code else code))
    if id_addr:
        lp = m.u32(id_addr)
        print("[ID] 本机玩家指针        = %s" % ("0x%08X" % lp if lp else lp))
        if owner is not None and lp:
            print("==> owner %s [ID]：%s" % ("==" if owner == lp else "!=",
                  "已归属本机玩家（夺舍会判定'已属本机，不动'）" if owner == lp else "非本机（可走引擎 0x09 转让）"))
    print("--- U+0x3E0..0x440 只读窗口 ---")
    for off in range(0x3E0, 0x441, 4):
        v = m.u32(U + off)
        mark = "  <== owner(0x418)" if off == 0x418 else ""
        print("  U+0x%03X = %s%s" % (off, ("0x%08X" % v) if v is not None else "不可读", mark))


main()
