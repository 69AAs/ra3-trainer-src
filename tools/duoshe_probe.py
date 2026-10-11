#!/usr/bin/env python3
"""夺舍取证：只读轮询“当前选中单位”的对象快照（不写一个字节、不注入、不抢焦点）。

背景：RunTimeout 时用户可能正在打游戏。我们只读 selection 链，命中选中对象就把
对象内存原始 dump 落到 artifacts/duoshe-live-dumps.jsonl（含时间戳、对象地址、owner 候选、
类型码、十六进制原文）。用于比较“自己的单位 vs 敌方单位”的归属字段。

链（出处：ReadOnlyProbe.cs:26-55、SelectedObjectGuard.cs:100-108、REPORT.md:738）：
  S = [game+8DB73C] ; N = [S+5C] ; L = [S+50] ; W = [L+8] ; U = [W+138]
  owner 候选 = [U+0x418] ; template = [U+4] ; type = [template+8]
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time
from datetime import datetime, timezone, timedelta

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010
TARGET = "ra3_1.12.game"
SELECTION_ROOT_RVA = 0x8DB73C
SELECTED_CODE_RVA = 0x8E9838
CST = timezone(timedelta(hours=8))

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class ME32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD), ("modBaseAddr", ctypes.c_void_p),
                ("modBaseSize", wt.DWORD), ("hModule", wt.HMODULE),
                ("szModule", ctypes.c_char * 256), ("szExePath", ctypes.c_char * 260)]


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
        if e.szExeFile.decode("latin1", "replace").rstrip("\x00") == TARGET:
            pid = e.th32ProcessID; break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return pid


def open_proc(pid):
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h: raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())
    return h


def base_of(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    e = ME32(); e.dwSize = ctypes.sizeof(e); out = None
    ok = k32.Module32First(snap, ctypes.byref(e))
    while ok:
        if e.szModule.decode("latin1", "replace").rstrip("\x00").lower() == TARGET:
            out = (e.modBaseAddr, e.modBaseSize); break
        ok = k32.Module32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return out


def rd(h, addr, n):
    buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)):
        return None
    return buf.raw[:got.value]


def u32(h, addr):
    d = rd(h, addr, 4)
    return struct.unpack("<I", d)[0] if d and len(d) == 4 else None


def hexdump(data):
    lines = []
    for o in range(0, len(data), 16):
        c = data[o:o + 16]
        lines.append("%08X  %-47s  %s" % (o, " ".join("%02x" % b for b in c),
                     "".join(chr(b) if 32 <= b < 127 else "." for b in c)))
    return lines


def main():
    seconds = int(sys.argv[1]) if len(sys.argv) > 1 else 300
    interval = float(sys.argv[2]) if len(sys.argv) > 2 else 1.5
    outdir = sys.argv[3] if len(sys.argv) > 3 else "artifacts"
    out = os.path.join(outdir, "duoshe-live-dumps.jsonl")
    pid = find_pid()
    if not pid: raise SystemExit("游戏没在跑")
    h = open_proc(pid)
    base, size = base_of(pid)
    root_va = base + SELECTION_ROOT_RVA
    code_va = base + SELECTED_CODE_RVA
    print("pid=%d base=0x%08X selection_root=0x%08X" % (pid, base, root_va), flush=True)
    seen = set()
    end = time.time() + seconds
    frames = 0
    while time.time() < end:
        frames += 1
        s = u32(h, root_va)
        if s:
            n = u32(h, s + 0x5C)
            code = u32(h, code_va)
            if n:
                for which, off in (("head", 0x50), ("tail", 0x54)):
                    node = u32(h, s + off)
                    if not node:
                        continue
                    wrapper = u32(h, node + 8)
                    unit = u32(h, wrapper + 0x138) if wrapper else None
                    if not unit or (which, unit) in seen:
                        continue
                    seen.add((which, unit))
                    tmpl = u32(h, unit + 4)
                    typ = u32(h, tmpl + 8) if tmpl else None
                    owner = u32(h, unit + 0x418)
                    hlt = u32(h, unit + 0x33C)
                    dump = rd(h, unit, 0x460)
                    rec = {
                        "ts": datetime.now(CST).isoformat(),
                        "pid": pid, "base": hex(base),
                        "selection_root": hex(s), "count": n, "selected_code": hex(code) if code is not None else None,
                        "which": which, "node": hex(node), "wrapper": hex(wrapper) if wrapper else None,
                        "unit": hex(unit),
                        "type_code": hex(typ) if typ is not None else None,
                        "owner_418": hex(owner) if owner is not None else None,
                        "health_ptr_33C": hex(hlt) if hlt is not None else None,
                        "dump_hex": hexdump(dump) if dump else None,
                    }
                    with open(out, "a", encoding="utf-8") as fh:
                        fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
                    print("CAPTURED %s unit=%s type=%s owner418=%s" % (which, hex(unit), rec["type_code"], rec["owner_418"]), flush=True)
        time.sleep(interval)
    print("done frames=%d distinct=%d -> %s" % (frames, len(seen), out), flush=True)


if __name__ == "__main__":
    main()
