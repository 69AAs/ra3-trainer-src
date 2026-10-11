#!/usr/bin/env python3
"""「账本条目」补写探针：把夺舍后的对象再补上 `U+0x414`（游戏自己的那条登记）。

背景（**已核实**，不是猜）：
  · 游戏自己的归属转移 = `+3AE230`（对象.setOwner(条目)）→ `+39DFD0` → `+38DBB0`；
    核心那步只写两个字段：`[U+0x418] = owner`、`[U+0x414] = 条目`。
  · 我们的引擎动作 0x09 **只写 `+0x418`** ⇒ 对象卡在"半是你的"：不能生产、不能再被工程师占领。
  · 条目的来源（游戏自己的写法）：`条目 = [玩家对象 + 0xAC]`
    —— 实测：玩家自己那栋楼 `+0x414 = 0x099AEAA0` 恰好等于 `[它的 owner + 0xAC]`，且 owner 内部
    只有 `+0xAC` 这一处存着它。

⚠ 写入量：**一个 dword**。写前落盘快照、写后回读、可一键还原。
  这是"按已核实的规则补一个字段"的**探针**，不是最终交付；验证能不能生产之后，再把同样的逻辑做进修改器。

用法：
    python tools/entry_fix_probe.py --learn     # 先在游戏里选中「你自己」的一栋建筑（读出你的玩家/条目）
    python tools/entry_fix_probe.py --show      # 只读：看当前选中对象的 owner / 条目 / 目标条目
    python tools/entry_fix_probe.py --apply     # 写：把当前选中对象的 +0x414 补成你的条目（快照+回读）
    python tools/entry_fix_probe.py --restore   # 还原：把最近一次写的对象写回旧条目（按快照）
    python tools/entry_fix_probe.py --list      # 列出快照
"""
import ctypes
import ctypes.wintypes as wt
import glob
import json
import os
import struct
import sys
import time

TARGET = "ra3_1.12.game"
BASE = 0x400000
SELECTION_ROOT_RVA = 0x8DB73C
OBJ_ENTRY, OBJ_OWNER = 0x414, 0x418
PLAYER_ENTRY = 0xAC
ART = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "artifacts")
STATE = os.path.join(ART, "entry-fix-state.json")
SNAP_DIR = os.path.join(ART, "entry-fix-snapshots")

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
        self.h = k32.OpenProcess(0x0400 | 0x0010 | 0x0020 | 0x0008, False, pid)   # QUERY|VM_READ|VM_WRITE|VM_OPERATION
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def u32(self, a):
        b = ctypes.create_string_buffer(4); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(a & 0xFFFFFFFF), ctypes.byref(b), 4,
                                     ctypes.byref(got)) or got.value != 4:
            return None
        return struct.unpack("<I", b.raw)[0]

    def w32(self, a, v):
        b = struct.pack("<I", v & 0xFFFFFFFF); got = ctypes.c_size_t()
        return bool(k32.WriteProcessMemory(self.h, ctypes.c_void_p(a & 0xFFFFFFFF), b, 4, ctypes.byref(got))
                    and got.value == 4)


def selected_object(mem):
    root = mem.u32(BASE + SELECTION_ROOT_RVA)
    node = mem.u32(root + 0x50) if root else None
    wrap = mem.u32(node + 8) if node else None
    return mem.u32(wrap + 0x138) if wrap else None


def load_state():
    if os.path.exists(STATE):
        return json.load(open(STATE, encoding="utf-8"))
    return None


def cmd_learn(mem):
    obj = selected_object(mem)
    if not obj:
        raise SystemExit("先在游戏里选中「你自己」的一栋建筑（兵营/工厂），再跑 --learn")
    owner = mem.u32(obj + OBJ_OWNER)
    entry = mem.u32(obj + OBJ_ENTRY)
    player_entry = mem.u32(owner + PLAYER_ENTRY) if owner else None
    tmpl = mem.u32(obj + 4)
    kind = mem.u32(tmpl + 8) if tmpl else None
    print("选中对象 0x%08X type=0x%08X owner=0x%08X 条目(+0x414)=0x%08X" % (obj, kind or 0, owner or 0, entry or 0))
    print("它 owner 的 [+0xAC] = 0x%08X" % (player_entry or 0))
    if not owner or not player_entry:
        raise SystemExit("取不到 owner / 条目，换个对象再试")
    if player_entry != entry:
        print("⚠ 不相等：这个对象的 +0x414 与 [owner+0xAC] 不一致 ⇒ 它可能不是正规玩家槽的对象（例如中立/野怪），"
              "学到的条目可能不对。请确认这是**你自己**的建筑。")
        if "--force" not in sys.argv:
            raise SystemExit("已停手（要强行记住就加 --force）")
    os.makedirs(ART, exist_ok=True)
    json.dump({"player": "%08X" % owner, "entry": "%08X" % player_entry, "learned_from": "%08X" % obj,
               "ts": time.strftime("%Y-%m-%d %H:%M:%S")}, open(STATE, "w", encoding="utf-8"), ensure_ascii=False)
    print("已记住：玩家 0x%08X，条目 0x%08X（存 %s）" % (owner, player_entry, os.path.abspath(STATE)))


def cmd_show(mem):
    st = load_state()
    obj = selected_object(mem)
    if not obj:
        raise SystemExit("当前没有选中对象")
    owner = mem.u32(obj + OBJ_OWNER); entry = mem.u32(obj + OBJ_ENTRY)
    print("选中对象 0x%08X  owner=0x%08X  条目=0x%08X" % (obj, owner or 0, entry or 0))
    if st:
        tgt = int(st["entry"], 16)
        print("已记住你的条目 = 0x%08X（玩家 %s）" % (tgt, st["player"]))
        print("⇒ %s" % ("已经就是它了（无需写）" if entry == tgt else "需要补写"))
    else:
        print("还没有 --learn 过（先在你自己的一栋建筑上跑 --learn）")


def cmd_apply(mem):
    st = load_state()
    if not st:
        raise SystemExit("先 --learn（选中你自己的一栋建筑）")
    tgt = int(st["entry"], 16); myplayer = int(st["player"], 16)
    # ★ 现场重新推导条目：每个新局地址都会变，学到的旧值绝不复用（拿旧值写＝往游戏里塞野指针）。
    live = mem.u32(myplayer + PLAYER_ENTRY)
    if live is None:
        raise SystemExit("读不到 [玩家+0xAC]，停手（可能已换局/游戏已退）")
    if live == 0:
        raise SystemExit("本局 [玩家+0xAC] 是 0，停手（先在本局里 --learn 一次）")
    if live != tgt:
        print("⚠ 学到的条目 0x%08X 与本局现场的 0x%08X 不同 ⇒ 已换局，改用现场值（旧值作废）" % (tgt, live))
        tgt = live
        st["entry"] = "%08X" % live; st["ts"] = time.strftime("%Y-%m-%d %H:%M:%S")
        json.dump(st, open(STATE, "w", encoding="utf-8"), ensure_ascii=False)
    obj = selected_object(mem)
    if not obj:
        raise SystemExit("先在游戏里选中要补写的那个对象（夺舍后的那栋楼）")
    owner = mem.u32(obj + OBJ_OWNER); old = mem.u32(obj + OBJ_ENTRY)
    if owner is None or old is None:
        raise SystemExit("对象字段读不到，停手")
    if owner != myplayer:
        raise SystemExit("停手：这个对象的 owner=0x%08X 不是你的玩家 0x%08X —— 请先用修改器夺舍它，再来补写"
                         % (owner, myplayer))
    if old == tgt:
        print("已经一致（+0x414 = 0x%08X），不写。" % old); return
    os.makedirs(SNAP_DIR, exist_ok=True)
    path = os.path.join(SNAP_DIR, "entry-%s.json" % time.strftime("%Y%m%d-%H%M%S"))
    json.dump({"ts": time.strftime("%Y-%m-%d %H:%M:%S"), "obj": "%08X" % obj, "entry_addr": "%08X" % (obj + OBJ_ENTRY),
               "old_entry": "%08X" % old, "new_entry": "%08X" % tgt, "owner": "%08X" % owner},
              open(path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("快照已落盘：%s" % path)
    print("写入：0x%08X (+0x414) 0x%08X → 0x%08X" % (obj + OBJ_ENTRY, old, tgt))
    if not mem.w32(obj + OBJ_ENTRY, tgt):
        raise SystemExit("写入失败（err=%d）" % ctypes.get_last_error())
    back = mem.u32(obj + OBJ_ENTRY)
    print("写后回读 = 0x%08X ⇒ %s" % (back or 0, "✓ 已生效" if back == tgt else "✗ 回读不一致！用 --restore 还原"))
    print("\n现在去游戏里试：这栋楼能不能生产单位？（生产/不生产都告诉我）")


def cmd_restore(mem):
    snaps = sorted(glob.glob(os.path.join(SNAP_DIR, "entry-*.json")))
    if not snaps:
        raise SystemExit("没有快照可还原")
    s = json.load(open(snaps[-1], encoding="utf-8"))
    addr = int(s["entry_addr"], 16); old = int(s["old_entry"], 16); new = int(s["new_entry"], 16)
    cur = mem.u32(addr)
    print("最近一次快照：%s\n  地址 0x%08X  旧 0x%08X  新 0x%08X  当前 0x%08X" % (snaps[-1], addr, old, new, cur or 0))
    if cur != new:
        raise SystemExit("当前值不等于我们写进去的新值 ⇒ 对象已换代/别人改过，拒绝还原（不动别人的对象）")
    if not mem.w32(addr, old):
        raise SystemExit("还原写入失败")
    print("还原后回读 = 0x%08X ⇒ %s" % (mem.u32(addr) or 0, "✓ 已还原" if mem.u32(addr) == old else "✗ 不一致"))


def main():
    if "--list" in sys.argv:
        for f in sorted(glob.glob(os.path.join(SNAP_DIR, "entry-*.json"))):
            print("  ", f)
        return
    pid = find_pid()
    if pid is None:
        raise SystemExit("找不到 %s（游戏没在跑）" % TARGET)
    mem = Mem(pid)
    print("PID=%d" % pid)
    if "--learn" in sys.argv: cmd_learn(mem)
    elif "--show" in sys.argv: cmd_show(mem)
    elif "--apply" in sys.argv: cmd_apply(mem)
    elif "--restore" in sys.argv: cmd_restore(mem)
    else:
        print(__doc__)


main()
