#!/usr/bin/env python3
"""节点结构探针（**只读**）：回答"`U+0x414` 指向的那个东西到底是什么"。

背景：配对样本已定案 —— 归属变化时
  · 我们的路（引擎动作 0x09）只写 `U+0x418`；
  · 游戏自己的路（工程师占领）**还写 `U+0x414`**（每栋楼各有一个值，不是按玩家索引的常量）。
所以要知道"登记"到底做了什么，先得看清 `+0x414` 指向的节点：链表节点？数组项？里面有没有
"楼指针 / 玩家指针"？玩家对象里有没有指向它的头指针？

用法：
    python tools/node_probe.py --auto                  # 用修改器正在选中的对象
    python tools/node_probe.py --object A9ABC1F0       # 直接给对象地址
    python tools/node_probe.py --node 9B72FA0          # 直接给节点地址
    python tools/node_probe.py --object A9ABC1F0 --walk 24   # 顺着节点链走 24 步

只申请 PROCESS_QUERY_INFORMATION|PROCESS_VM_READ，一个字节都不写；不碰键鼠、不激活窗口。
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

TARGET = "ra3_1.12.game"
BASE_FALLBACK = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
OBJ_OWNER, OBJ_NODE = 0x418, 0x414

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


def find_base(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    m = ME32(); m.dwSize = ctypes.sizeof(m)
    ok = k32.Module32First(snap, ctypes.byref(m)); base = 0
    while ok:
        if m.szModule.decode("latin1", "replace").rstrip("\x00").lower() == TARGET.lower():
            base = ctypes.cast(m.modBaseAddr, ctypes.c_void_p).value; break
        ok = k32.Module32Next(snap, ctypes.byref(m))
    k32.CloseHandle(snap)
    return base or BASE_FALLBACK


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(0x0400 | 0x0010, False, pid)
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr & 0xFFFFFFFF), ctypes.byref(buf), n,
                                     ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw

    def u32(self, addr):
        b = self.raw(addr, 4)
        return None if b is None else struct.unpack("<I", b)[0]


def fmt(v):
    if v is None:
        return "不可读"
    f = struct.unpack("<f", struct.pack("<I", v))[0]
    return "0x%08X (float %.6g)" % (v, f)


def looks_like_ptr(v):
    return v is not None and 0x00010000 <= v <= 0x7FFE0000 and v % 4 == 0


def dump(mem, addr, n, label, annotate=None):
    print("\n--- %s @0x%08X ---" % (label, addr))
    for off in range(0, n, 4):
        v = mem.u32(addr + off)
        if v is None:
            print("   +0x%03X 不可读" % off)
            break
        note = ""
        if looks_like_ptr(v):
            note += "  ← 像指针"
            back = mem.u32(v)
            if back is not None:
                note += " | 该处首 dword=0x%08X" % back
        if annotate:
            note += annotate(mem, addr, off, v)
        print("   +0x%03X %s%s" % (off, fmt(v), note))


def main():
    args = sys.argv[1:]
    obj = node = player = None
    walk = 16
    for i, a in enumerate(args):
        if a == "--object" and i + 1 < len(args):
            obj = int(args[i + 1], 16)
        if a == "--node" and i + 1 < len(args):
            node = int(args[i + 1], 16)
        if a == "--player" and i + 1 < len(args):
            player = int(args[i + 1], 16)
        if a == "--walk" and i + 1 < len(args):
            walk = int(args[i + 1])
        if a == "--auto":
            obj = "auto"

    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）" % TARGET)
    base = find_base(pid)
    mem = Mem(pid)
    print("PID=%d base=0x%X" % (pid, base))

    if obj == "auto":
        root = mem.u32(base + SELECTION_ROOT_RVA)
        node_sel = mem.u32(root + 0x50) if root else None
        wrapper = mem.u32(node_sel + 8) if node_sel else None
        obj = mem.u32(wrapper + 0x138) if wrapper else None
        if not obj:
            raise SystemExit("现在没有选中对象（先在游戏里用鼠标点一下那栋楼）")
        print("选中对象（来自选择链）: 0x%08X" % obj)

    if obj:
        print("\n对象 0x%08X:" % obj)
        print("   +0x%X owner = %s" % (OBJ_OWNER, fmt(mem.u32(obj + OBJ_OWNER))))
        node = mem.u32(obj + OBJ_NODE)
        print("   +0x%X node  = %s   ← 研究对象" % (OBJ_NODE, fmt(node)))
        tmpl = mem.u32(obj + 4)
        if tmpl:
            print("   template+8 type = 0x%08X" % (mem.u32(tmpl + 8) or 0))
        if not node:
            raise SystemExit("这个对象的 +0x414 是 0（没有节点）—— 换一栋楼再试")

    if node:
        dump(mem, node, 0x40, "节点本体（前 0x40）")
        next_p, prev_p = mem.u32(node), mem.u32(node + 4)
        print("\n链表自检：")
        print("   node+0x00 = %s" % fmt(next_p))
        print("   node+0x04 = %s" % fmt(prev_p))
        for name, p, backoff in (("next", next_p, 4), ("prev", prev_p, 0)):
            if not looks_like_ptr(p):
                continue
            back = mem.u32(p + backoff)
            print("   %s(0x%08X)+0x%X = %s %s" % (name, p, backoff, fmt(back),
                  "✓ 指回本节点 ⇒ 双向链表" if back == node else "✗ 没指回本节点"))
        # 顺着链走：看能不能走到别的楼（同玩家的其它建筑）
        if looks_like_ptr(next_p):
            print("\n顺 next 走 %d 步：" % walk)
            seen, cur = set(), next_p
            for step in range(walk):
                if not looks_like_ptr(cur) or cur in seen:
                    print("   第 %d 步：%s 停" % (step, fmt(cur)))
                    break
                seen.add(cur)
                back = mem.u32(cur + 4)
                print("   第 %d 步 0x%08X  prev=%s" % (step, cur, fmt(back)))
                cur = mem.u32(cur)
        # 节点里有没有指回"楼"或"玩家"
        if obj:
            hits = [(off, mem.u32(node + off)) for off in range(0, 0x100, 4)]
            for off, v in hits:
                if v == obj:
                    print("\n   ★ node+0x%X == 楼地址 0x%08X" % (off, obj))
        # 玩家对象里有没有指向这个节点
        if player:
            hits = [off for off in range(0, 0x800, 4) if mem.u32(player + off) == node]
            print("\n玩家 0x%08X 里指向 node 的偏移: %s" % (player, [hex(h) for h in hits] or "无"))
        # 也扫一遍 owner 对象
        if obj:
            own = mem.u32(obj + OBJ_OWNER)
            if looks_like_ptr(own):
                hits = [off for off in range(0, 0x800, 4) if mem.u32(own + off) == node]
                print("owner 0x%08X 里指向 node 的偏移: %s" % (own, [hex(h) for h in hits] or "无"))


main()
