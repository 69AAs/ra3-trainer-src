#!/usr/bin/env python3
"""对象结构采样器（**只读**）：用来找"夺舍之后为什么生产建筑不能生产"缺的那个字段。

背景（已验证的事实）：
  · 夺舍走引擎动作 0x09，它**只写一个字段** `[U+0x418] = [ID]`（Assets/00_bootstrap.aa:993-994）；
  · 真机 diff 过：`U+0x3E0..0x440` 共 25 个 dword **只有 +0x418 变** ⇒ 颜色/阵营显示、索敌、以及
    "这栋楼算不算我的生产设施"都挂在**别的结构**上（在哪：未证实，不猜偏移）。
  · 原版动作派发表里也没有"占领/改队伍"这类动作可复用（只有 13 个码，0x09 就是最接近的那个）。

这个工具做的事：每 N 秒把"当前选中对象"（或之后一直跟着的同一个对象）结构 dump 一次，
写成逐帧 jsonl，供"做一次游戏自己的归属变更（工程师占领 / 或用同型建筑做对照）"前后做 diff。

用法：
    python tools/object_structure_sampler.py [总秒数] [间隔秒] [输出文件]
    python tools/object_structure_sampler.py 1800 2 D:/work/ra3-trainer-refine/artifacts/structure-samples.jsonl

每帧每个被跟对象都带 `type` / `owner` / `reused`：**`reused=true` 表示这个地址已经换成别的对象了**
（他换选中对象时旧对象会死、地址被复用），那一帧的整块 4KB **不能**拿去当 diff 证据。

他在游戏里要做的（**不需要动这个工具**）：
    1. 选中那栋要研究的生产建筑（敌方/队友的都行）——工具立刻开始跟这栋；
       （若中途取消选中，工具会继续跟着同一个地址，并标记 source=last）
    2. 用游戏自己的办法把它变成你的：派工程师占领它，或者用你自己的同型生产建筑当对照（造一栋）；
    3. 完成后说一声，我 diff 时间线。

只申请 PROCESS_QUERY_INFORMATION|PROCESS_VM_READ，一个字节都不写；不碰键鼠、不激活窗口。
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TARGET = "ra3_1.12.game"
SELECTION_ROOT_RVA = 0x8DB73C
DUMP_FROM, DUMP_TO = 0x000, 0x800        # 覆盖整个已知对象体（0x418 owner / 0x33C health / 0x38 坐标都在里面）

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
    return base


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), ctypes.byref(buf), n,
                                     ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw

    def u32(self, addr):
        b = self.raw(addr, 4)
        return None if b is None else struct.unpack("<I", b)[0]


def main():
    total = float(sys.argv[1]) if len(sys.argv) > 1 else 1800.0
    interval = float(sys.argv[2]) if len(sys.argv) > 2 else 2.0
    out = sys.argv[3] if len(sys.argv) > 3 else "structure-samples.jsonl"

    # 游戏没在跑就**等它**（而不是退出）：仪器先挂上，他进游戏自然就开始采样 ——
    # 这样不用催他"现在就跑工具"，也不会因为他先退游戏就拿不到数据。
    wait_until = time.time() + total
    pid = find_pid()
    while pid is None and time.time() < wait_until:
        print("等 %s 起来…（%s）" % (TARGET, time.strftime("%H:%M:%S")), flush=True)
        time.sleep(5)
        pid = find_pid()
    if pid is None:
        raise SystemExit("等了 %0.0f 秒仍没等到 %s" % (total, TARGET))
    base = find_base(pid)
    mem = Mem(pid)
    print("PID=%d base=0x%X 采样 %0.0f 秒 / 每 %0.1f 秒 → %s" % (pid, base, total, interval, out),
          flush=True)

    deadline = time.time() + total
    # ★ 同时跟**多个**对象 ★：做"工程师占领"时他要切去选工程师，如果只跟"当前选中"，
    #   那栋被研究的建筑立刻就被丢掉了（我第一版就是这么写的）。这里按最近出现的顺序留 4 个地址，
    #   每帧把每一个都 dump 一遍 —— 于是"建筑"和"工程师"两条线都在时间线里。
    tracked = []
    last_type = {}
    frames = 0
    blank = 0
    with open(out, "a", encoding="utf-8") as handle:
        while time.time() < deadline:
            line = {"ts": time.strftime("%Y-%m-%d %H:%M:%S"),
                    "t": round(time.time(), 2)}
            root = mem.u32(base + SELECTION_ROOT_RVA)
            unit = None
            if root:
                count = mem.u32(root + 0x5C)
                line["count"] = count
                if count:
                    node = mem.u32(root + 0x50)
                    wrapper = mem.u32(node + 8) if node else None
                    unit = mem.u32(wrapper + 0x138) if wrapper else None
                    line["node"] = "%X" % node if node else None
            if unit:
                if unit in tracked:
                    tracked.remove(unit)
                tracked.insert(0, unit)
                del tracked[4:]
                line["selected"] = "%X" % unit
            line["tracked"] = ["%X" % a for a in tracked]
            objects = {}
            for address in tracked:
                template = mem.u32(address + 4)
                kind = ("%08X" % mem.u32(template + 8)) if (template and mem.u32(template + 8)) else None
                previous = last_type.get(address)
                # ★ 地址复用标记 ★：他测试时会不停换选中的建筑/单位，旧对象一死，同一个地址很快被新对象占用 ——
                #   那时逐帧 diff 出的"几百个字段全变了"是**两个不同对象**的差，不是真变化（第一轮分析就被这个骗过）。
                #   type 变了就是这个地址换了对象：取到的那一帧整块作废，别拿去当 diff 证据。
                objects["%X" % address] = {
                    "type": kind,
                    "reused": bool(previous is not None and kind is not None and previous != kind),
                    "owner": ("%08X" % mem.u32(address + 0x418)) if mem.u32(address + 0x418) is not None else None,
                    "words": blob.hex() if blob else None,      # 512 个 dword（4 KB）原始字节
                } if (blob := mem.raw(address + DUMP_FROM, DUMP_TO - DUMP_FROM)) is not None else {
                    "type": kind, "reused": False, "owner": None, "words": None}
                last_type[address] = kind
            line["objects"] = objects
            handle.write(json.dumps(line, ensure_ascii=False) + "\n")
            handle.flush()
            frames += 1
            # 游戏退了（或那局结束、内存全读不到）就**自己停**：第一版会一直刷空帧
            # （2026-10-08 那次 3600 秒里后 1595 帧全是空帧，白占文件、还容易把分析带偏）。
            if objects and all(o["words"] is None for o in objects.values()):
                blank += 1
                if blank >= 10:
                    print("连续 %d 帧读不到内存（游戏退了/那局结束）⇒ 停止采样，共 %d 帧" % (blank, frames), flush=True)
                    return
            else:
                blank = 0
            time.sleep(interval)
    print("采样结束：%d 帧" % frames, flush=True)


main()
