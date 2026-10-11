#!/usr/bin/env python3
"""建造上限探针（**只读**）：把当前选中单位的"模板对象"倒出来，找"只能造 N 个"的那个 N。

思路（不猜偏移）：
  1. 复用 owner_probe 的选中解析：root=[0x400000+0x8DB73C] → [root+0x50] → [+8] → [+0x138] = 对象；
  2. 对象 +4 = 模板对象（ThingTemplate），[tmpl+8] = 类型 id（名字哈希）；
  3. 把模板对象前 0x400 字节按 dword 倒出来，**标出所有 0..16 的小整数**（上限就是个小整数）；
  4. 顺手找模板附近的字符串（单位名/类型名），供人工确认是哪个单位。

用法：
    python tools/limit_probe.py            # 用当前选中的单位
    python tools/limit_probe.py --obj 5EF3E90 --tmpl 6A1B2C3

只申请 PROCESS_QUERY_INFORMATION|PROCESS_VM_READ，只读，一个字节都不写；不碰键鼠、不激活窗口。
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

TARGET = "ra3_1.12.game"
BASE = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
SMALL_MAX = 17          # 0..16 都算"小整数"候选
SCAN = 0x400            # 模板对象扫描长度

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


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
        self.h = k32.OpenProcess(0x0400 | 0x0010, False, pid)
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

    def strings_near(self, base, span=0x300, limit=8):
        """在 [base, base+span) 里找指向可打印字符串的指针，返回 (偏移, 字符串)。"""
        out = []
        for off in range(0, span, 4):
            p = self.u32(base + off)
            if p and 0x00100000 < p < 0x7FFF0000:
                b = self.raw(p, 48)
                if not b:
                    continue
                s = b.split(b"\x00")[0]
                if 3 <= len(s) <= 39 and all(32 <= c < 127 for c in s) and b"\\" not in s and b"/" not in s:
                    out.append((off, s.decode("latin1")))
                    if len(out) >= limit:
                        break
        return out


def main():
    args = sys.argv[1:]
    obj = tmpl = None
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--obj" and nxt:
            obj = int(nxt, 16)
        if a == "--tmpl" and nxt:
            tmpl = int(nxt, 16)

    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）" % TARGET)
    mem = Mem(pid)
    print("PID=%d" % pid)

    if obj is None:
        root = mem.u32(BASE + SELECTION_ROOT_RVA)
        node = mem.u32(root + 0x50) if root else None
        wrap = mem.u32(node + 8) if node else None
        obj = mem.u32(wrap + 0x138) if wrap else None
        if not obj:
            raise SystemExit("现在没有选中对象（先在游戏里点一下那个单位）")
    if tmpl is None:
        tmpl = mem.u32(obj + 4)
    if not tmpl:
        raise SystemExit("对象 0x%08X 的模板指针读不到" % obj)

    type_id = mem.u32(tmpl + 8)
    print("对象 0x%08X  模板 0x%08X  类型 id=0x%08X" % (obj, tmpl, type_id or 0))

    print("\n模板附近的字符串（认单位用）：")
    for off, s in mem.strings_near(tmpl, 0x400):
        print("   +0x%03X → %s" % (off, s))

    print("\n模板对象前 0x%X 字节（只标小整数 0..%d）：" % (SCAN, SMALL_MAX - 1))
    data = mem.raw(tmpl, SCAN)
    if not data:
        raise SystemExit("模板内存读不到")
    smalls = []
    for off in range(0, SCAN, 4):
        v = struct.unpack_from("<I", data, off)[0]
        if v < SMALL_MAX:
            smalls.append((off, v))
    if not smalls:
        print("   （这一段里没有小整数）")
    for off, v in smalls:
        mark = "   ← 候选（=3 最可疑）" if v == 3 else ""
        print("   +0x%03X = %d%s" % (off, v, mark))

    print("\n提示：再选一个**没有上限**的单位跑一次，两者差集里那个 =N 的字段就是「建造上限」。")
    print("      找到之后优先级：改那个字段（数据）> 改读它的比较指令（代码）。")


main()
