#!/usr/bin/env python3
"""归属/条目探针（**只读**）：一次说清"当前选中的对象属于谁、它的 `+0x414` 到底是什么"。

回答的问题：
  1. 选中对象的 owner（`+0x418`）在玩家表里是哪个槽、叫什么（`PlyrCreeps` / `Player_2` / …）；
  2. 它的 `+0x414`（"条目"）是否等于**某个玩家**的 `[玩家+0xAC]` 或 `[[玩家+0x8C]+0x10]`
     —— 这是判定"条目是按玩家还是按每栋楼分配"的关键（决定能不能安全调用游戏自己的换归属例程 `+3AE230`）；
  3. owner 对象自己哪几个偏移里存着这个条目（能看出它是不是挂在 owner 的某个列表上）。

用法：
    python tools/owner_probe.py --auto          # 用修改器当前选中的对象
    python tools/owner_probe.py --object 5EF3E90

只申请 PROCESS_QUERY_INFORMATION|PROCESS_VM_READ，一个字节都不写；不碰键鼠、不激活窗口。
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

TARGET = "ra3_1.12.game"
BASE_FALLBACK = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
PLAYER_LIST_GLOBAL = 0x00CE8C9C      # [全局]+0x30 起是玩家表（0x83B330 里这么用的）
PLAYER_TABLE_OFF = 0x30
OBJ_NODE, OBJ_OWNER = 0x414, 0x418
OWNER_ENTRY_BRANCHES = (0x8C, 0xAC)  # [[玩家+0x8C]+0x10] / [玩家+0xAC]

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

    def cstr(self, a, n=48):
        b = self.raw(a, n)
        if not b:
            return None
        s = b.split(b"\x00")[0]
        if 2 <= len(s) <= 40 and all(32 <= c < 127 for c in s):
            return s.decode("latin1")
        return None


def main():
    obj = None
    args = sys.argv[1:]
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--object" and nxt:
            obj = int(nxt, 16)
        if a == "--auto":
            obj = "auto"

    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）" % TARGET)
    mem = Mem(pid)
    print("PID=%d" % pid)

    if obj == "auto":
        root = mem.u32(BASE_FALLBACK + SELECTION_ROOT_RVA)
        node = mem.u32(root + 0x50) if root else None
        wrap = mem.u32(node + 8) if node else None
        obj = mem.u32(wrap + 0x138) if wrap else None
        if not obj:
            raise SystemExit("现在没有选中对象（先在游戏里用鼠标点一下那栋楼）")
    if not obj:
        raise SystemExit("用法：--auto 或 --object <hex>")

    tmpl = mem.u32(obj + 4)
    owner = mem.u32(obj + OBJ_OWNER)
    entry = mem.u32(obj + OBJ_NODE)
    print("\n选中对象 0x%08X  type=0x%08X  owner=0x%08X  条目(+0x414)=0x%08X"
          % (obj, (mem.u32(tmpl + 8) or 0) if tmpl else 0, owner or 0, entry or 0))

    glob = mem.u32(PLAYER_LIST_GLOBAL)
    local = mem.u32(glob + PLAYER_TABLE_OFF) if glob else None
    print("\n玩家表 [0x%08X]+0x%X（本机玩家=0x%08X）：" % (PLAYER_LIST_GLOBAL, PLAYER_TABLE_OFF, local or 0))
    rows = []
    for i in range(10):
        p = mem.u32(glob + PLAYER_TABLE_OFF + i * 4) if glob else None
        if not p:
            print("   槽%d (空)" % i); continue
        name = None
        for off in range(0, 0x240, 4):
            v = mem.u32(p + off)
            if v and 0x00100000 < v < 0x7FFF0000:
                s = mem.cstr(v)
                if s and "\\" not in s and "/" not in s:
                    name = "%s (在 +0x%X)" % (s, off); break
        team = mem.u32(p + 0x8C)
        rows.append((i, p, mem.u32(p + 0x94) if mem.u32(p + 0x94) is not None else -1, team,
                     mem.u32(p + 0xAC), (mem.u32(team + 0x10) if team else None), name))
    for i, p, slot, team, e_ac, e_team, name in rows:
        marks = []
        if p == owner: marks.append("← 本对象 owner")
        if p == local: marks.append("← 本机玩家")
        if slot == 0xFFFFFFFF: marks.append("(无玩家槽)")
        print("   槽%-2d obj=0x%08X slot=%s team=0x%08X [team+0x10]=0x%08X [+0xAC]=0x%08X %-22s %s"
              % (i, p, slot, team or 0, e_team or 0, e_ac or 0, (name or ""), " ".join(marks)))

    print("\n★ 判定：对象的条目 0x%08X 落在哪一支？" % (entry or 0))
    hit = False
    for i, p, slot, team, e_ac, e_team, name in rows:
        tag = []
        if e_ac == entry: tag.append("== [玩家+0xAC]")
        if e_team and e_team == entry: tag.append("== [[玩家+0x8C]+0x10]")
        if tag:
            hit = True
            print("   槽%d（0x%08X%s）：%s" % (i, p, ("，本对象 owner" if p == owner else ""), " ".join(tag)))
    if not hit:
        print("   哪个玩家都不匹配 ⇒ 条目**按每栋楼分配**（或 owner 不是正规玩家槽），不能拿猜的值去调 +3AE230")

    print("\nowner 对象里哪些偏移存着这个条目：")
    if owner:
        offs = [off for off in range(0, 0x300, 4) if mem.u32(owner + off) == entry]
        print("   " + (" ".join("+0x%X" % o for o in offs) if offs else "（前 0x300 内没有指向它的字段）"))


main()
