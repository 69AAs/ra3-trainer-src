#!/usr/bin/env python3
"""硬件**写**断点看守（B1 取证用）：抓出"游戏自己在归属变化时写 `U+0x414` 的那条指令"。

为什么用它：配对样本已证明缺口字段是 `U+0x414`（游戏自己的路会写它、我们的引擎动作 0x09 不写）。
要知道"登记"是什么语义，就得抓住写它的那条指令 → 回溯到例程入口 → 才能在载荷里调用/复刻。

安全纪律（写死在代码里，别绕过）：
  · **不开任何 GUI 调试器**，只用 Windows 调试 API；不抢焦点、不留窗口；
  · **只挂一个进程，且先校验进程名**（`--pid` 必须配 `--expect`，名字不符直接拒挂）——
    绝不"按名字匹配"盲挂：机器上同时有别的同名进程时那是灾难；
  · **一个字节都不写**目标内存 —— 硬件断点只改线程 DR0/DR7 寄存器，结束前必定还原；
  · 到点（`--seconds`）、命中上限、`Ctrl+C`、或任何异常 ⇒ **先摘断点 + 还原上下文，再 Detach**（finally 保证）；
  · 只跑一次、命中记录落 jsonl，失败不重试。

用法：
    # 生产用法：挂正在跑的游戏
    python tools/hwbp_write_watch.py --addr 9B17D20 --seconds 180 --out artifacts/hwbp-20261008.jsonl
    # 自检用法：挂我自己的假进程（进程名必须对上，否则拒挂）
    python tools/hwbp_write_watch.py --addr 1A2B3C40 --seconds 20 --pid 12345 --expect python.exe
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time

DEFAULT_TARGET = "ra3_1.12.game"
CONTEXT_i386 = 0x00010000
CONTEXT_CONTROL = CONTEXT_i386 | 0x0001
CONTEXT_INTEGER = CONTEXT_i386 | 0x0002
CONTEXT_DEBUG_REGISTERS = CONTEXT_i386 | 0x0010
DR7_ENABLE0_WRITE_LEN4 = 0x000D0001   # L0=1 | RW0=01(写)<<16 | LEN0=11(4字节)<<18
THREAD_ALL = 0x1F0FFF                 # GET|SET_CONTEXT | SUSPEND|RESUME | QUERY
DBG_CONTINUE, DBG_EXCEPTION_NOT_HANDLED = 0x00010002, 0x80010001

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


class TE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ThreadID", wt.DWORD),
                ("th32OwnerProcessID", wt.DWORD), ("tpBasePri", ctypes.c_long),
                ("tpDeltaPri", ctypes.c_long), ("dwFlags", wt.DWORD)]


class CONTEXT(ctypes.Structure):
    """i386 CONTEXT：只把要用到的字段命名出来（布局必须与 Windows 一致）。"""
    _fields_ = [("ContextFlags", ctypes.c_ulong),
                ("Dr0", ctypes.c_ulong), ("Dr1", ctypes.c_ulong), ("Dr2", ctypes.c_ulong),
                ("Dr3", ctypes.c_ulong), ("Dr6", ctypes.c_ulong), ("Dr7", ctypes.c_ulong),
                ("FloatSave", ctypes.c_byte * 112),
                ("SegGs", ctypes.c_ulong), ("SegFs", ctypes.c_ulong),
                ("SegEs", ctypes.c_ulong), ("SegDs", ctypes.c_ulong),
                ("Edi", ctypes.c_ulong), ("Esi", ctypes.c_ulong), ("Ebx", ctypes.c_ulong),
                ("Edx", ctypes.c_ulong), ("Ecx", ctypes.c_ulong), ("Eax", ctypes.c_ulong),
                ("Ebp", ctypes.c_ulong), ("Eip", ctypes.c_ulong), ("SegCs", ctypes.c_ulong),
                ("EFlags", ctypes.c_ulong), ("Esp", ctypes.c_ulong), ("SegSs", ctypes.c_ulong),
                ("ExtendedRegisters", ctypes.c_byte * 512)]


class DEBUG_EVENT(ctypes.Structure):
    _fields_ = [("dwDebugEventCode", wt.DWORD), ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD),
                ("u", ctypes.c_byte * 160)]


EXCEPTION_DEBUG_EVENT, CREATE_THREAD_DEBUG_EVENT, EXIT_PROCESS_DEBUG_EVENT = 1, 2, 5
STATUS_BREAKPOINT, STATUS_SINGLE_STEP = 0x80000003, 0x80000004
PROCESS_QUERY_INFORMATION, PROCESS_VM_READ = 0x0400, 0x0010


def proc_name(pid):
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e)); name = None
    while ok:
        if e.th32ProcessID == pid:
            name = e.szExeFile.decode("latin1", "replace").rstrip("\x00"); break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return name


def find_pid(exe):
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    e = PE32(); e.dwSize = ctypes.sizeof(e)
    ok = k32.Process32First(snap, ctypes.byref(e)); pid = None
    while ok:
        if e.szExeFile.decode("latin1", "replace").rstrip("\x00").lower() == exe.lower():
            pid = e.th32ProcessID; break
        ok = k32.Process32Next(snap, ctypes.byref(e))
    k32.CloseHandle(snap)
    return pid


def module_base(pid):
    snap = k32.CreateToolhelp32Snapshot(0x8 | 0x10, pid)
    m = ME32(); m.dwSize = ctypes.sizeof(m)
    base = 0
    if k32.Module32First(snap, ctypes.byref(m)):
        base = ctypes.cast(m.modBaseAddr, ctypes.c_void_p).value or 0
    k32.CloseHandle(snap)
    return base


def require_i386(pid):
    """★ 必须校验目标是 32 位 ★（2026-10-08 踩过：拿 32 位 CONTEXT 去改 64 位线程的 DR 寄存器是**静默失效**，
    断点装了、一个命中都没有，白等一场）。游戏 ra3_1.12.game 是 i386，所以这里只接受 0x14C。"""
    base = module_base(pid)
    if not base:
        raise SystemExit("读不到主模块基址，拒挂")
    hdr = read(pid, base, 0x400)
    if not hdr or hdr[0:2] != b"MZ":
        raise SystemExit("主模块头不对，拒挂")
    el = struct.unpack("<I", hdr[0x3C:0x40])[0]
    if hdr[el:el + 4] != b"PE\x00\x00":
        raise SystemExit("PE 签名不对，拒挂")
    machine = struct.unpack("<H", hdr[el + 4:el + 6])[0]
    if machine != 0x14C:
        raise SystemExit("拒挂：目标 Machine=0x%04X（不是 32 位 i386 0x14C）。"
                         "本工具的 CONTEXT 布局只对 32 位目标有效，对 64 位是静默失效。" % machine)
    return base


def is_wow64(pid):
    """目标是不是 WOW64（32 位进程跑在 64 位 Windows 上）。★ 这一步是必须的 ★
    2026-10-08 踩过：64 位调试器对 WOW64 线程用 Get/SetThreadContext 是**静默失效**
    （SetThreadContext 返回成功，DR 寄存器其实没改，断点永远 0 命中）——必须用 Wow64* 版本。"""
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION, False, pid)
    if not h:
        return False
    try:
        flag = wt.BOOL()
        if not k32.IsWow64Process(h, ctypes.byref(flag)):
            return False
        return bool(flag.value)
    finally:
        k32.CloseHandle(h)


def thread_ids(pid):
    snap = k32.CreateToolhelp32Snapshot(0x4, pid)
    t = TE32(); t.dwSize = ctypes.sizeof(t)
    ok = k32.Thread32First(snap, ctypes.byref(t)); out = []
    while ok:
        if t.th32OwnerProcessID == pid:
            out.append(t.th32ThreadID)
        ok = k32.Thread32Next(snap, ctypes.byref(t))
    k32.CloseHandle(snap)
    return out


class Threads:
    """给某个线程装/摘 DR0 写断点（挂起→改上下文→恢复）。

    `wow64=True` 时走 `Wow64Get/SetThreadContext`（64 位调试器 + 32 位目标必须这样，
    否则静默失效）。两套 API 用的是同一个 WOW64_CONTEXT/i386 CONTEXT 内存布局。"""

    def __init__(self, address, wow64=False):
        self.address = address
        self.wow64 = wow64
        self.armed = set()

    def _get(self, h, c):
        return (k32.Wow64GetThreadContext(h, ctypes.byref(c)) if self.wow64
                else k32.GetThreadContext(h, ctypes.byref(c)))

    def _set(self, h, c):
        return (k32.Wow64SetThreadContext(h, ctypes.byref(c)) if self.wow64
                else k32.SetThreadContext(h, ctypes.byref(c)))

    def _ctx(self, h):
        c = CONTEXT(); c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_DEBUG_REGISTERS
        return c, bool(self._get(h, c))

    def arm(self, tid):
        h = k32.OpenThread(THREAD_ALL, False, tid)
        if not h:
            return False
        try:
            k32.SuspendThread(h)
            c, ok = self._ctx(h)
            if not ok:
                return False
            c.Dr0 = self.address
            c.Dr7 = (c.Dr7 & ~DR7_ENABLE0_WRITE_LEN4) | DR7_ENABLE0_WRITE_LEN4
            c.Dr6 = 0
            if self._set(h, c):
                self.armed.add(tid)
                return True
            return False
        finally:
            k32.ResumeThread(h)
            k32.CloseHandle(h)

    def disarm(self, tid):
        h = k32.OpenThread(THREAD_ALL, False, tid)
        if not h:
            return
        try:
            k32.SuspendThread(h)
            c, ok = self._ctx(h)
            if ok:
                c.Dr0 = 0
                c.Dr7 = c.Dr7 & ~DR7_ENABLE0_WRITE_LEN4
                c.Dr6 = 0
                self._set(h, c)
        finally:
            self.armed.discard(tid)
            k32.ResumeThread(h)
            k32.CloseHandle(h)

    def disarm_all(self):
        for tid in list(self.armed):
            self.disarm(tid)


def read(pid, addr, n):
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h:
        return None
    try:
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr & 0xFFFFFFFF), ctypes.byref(buf), n,
                                     ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw
    finally:
        k32.CloseHandle(h)


def main():
    addr = seconds = out = max_hits = pid = None
    expect = DEFAULT_TARGET
    args = sys.argv[1:]
    for i, a in enumerate(args):
        nxt = args[i + 1] if i + 1 < len(args) else None
        if a == "--addr" and nxt:
            addr = int(nxt, 16)
        elif a == "--seconds" and nxt:
            seconds = float(nxt)
        elif a == "--out" and nxt:
            out = nxt
        elif a == "--max" and nxt:
            max_hits = int(nxt)
        elif a == "--pid" and nxt:
            pid = int(nxt)
        elif a == "--expect" and nxt:
            expect = nxt
    if addr is None:
        raise SystemExit(__doc__.split("用法：")[1].split("自检用法")[0].strip())
    seconds = 180.0 if seconds is None else seconds
    out = out or "artifacts/hwbp-hits.jsonl"
    max_hits = 12 if max_hits is None else max_hits

    if pid is None:
        pid = find_pid(expect)
        if pid is None:
            raise SystemExit("找不到 %s（游戏没在跑）" % expect)
    real = proc_name(pid)
    if real is None:
        raise SystemExit("PID %d 不存在" % pid)
    if real.lower() != expect.lower():
        raise SystemExit("拒挂：PID %d 的进程名是 %r，与 --expect %r 不符（怕挂错进程）" % (pid, real, expect))
    base = require_i386(pid)
    print("目标 %s (PID=%d, base=0x%X, 32 位 ✓)，写断点在 0x%08X 上，最多 %0.0f 秒 / %d 次命中"
          % (real, pid, base, addr, seconds, max_hits), flush=True)

    bp = Threads(addr, wow64=is_wow64(pid))
    print("上下文 API：%s" % ("Wow64Get/SetThreadContext（64 位调试器 + 32 位目标）" if bp.wow64
                              else "Get/SetThreadContext（32 位）"), flush=True)
    ev = DEBUG_EVENT()
    hits = 0
    attached = False
    os.makedirs(os.path.dirname(os.path.abspath(out)) or ".", exist_ok=True)
    handle = open(out, "a", encoding="utf-8")
    deadline = time.time() + seconds

    try:
        if not k32.DebugActiveProcess(pid):
            raise SystemExit("DebugActiveProcess 失败（err=%d）—— 被拒或反调试；我不硬来"
                             % ctypes.get_last_error())
        attached = True
        armed = sum(1 for tid in thread_ids(pid) if bp.arm(tid))
        print("已挂上：%d 个线程装了断点（现在可以去做归属变化了）" % armed, flush=True)

        while time.time() < deadline and hits < max_hits:
            if not k32.WaitForDebugEvent(ctypes.byref(ev), 200):
                continue
            code, tid = ev.dwDebugEventCode, ev.dwThreadId
            cont = DBG_CONTINUE
            if code == CREATE_THREAD_DEBUG_EVENT:
                bp.arm(tid)
            elif code == EXIT_PROCESS_DEBUG_EVENT:
                print("目标进程退出 ⇒ 收工", flush=True)
                k32.ContinueDebugEvent(pid, tid, cont)
                break
            elif code == EXCEPTION_DEBUG_EVENT:
                exc_code = struct.unpack("<I", bytes(ev.u[0:4]))[0]
                if exc_code == STATUS_SINGLE_STEP:
                    h = k32.OpenThread(THREAD_ALL, False, tid)
                    if h:
                        c = CONTEXT()
                        c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_DEBUG_REGISTERS
                        if k32.GetThreadContext(h, ctypes.byref(c)):
                            raw = read(pid, c.Esp, 64) or b""
                            stack = ["%08X" % v for v in struct.unpack("<%dI" % (len(raw) // 4), raw)]
                            prev = read(pid, (c.Eip - 16) & 0xFFFFFFFF, 16) or b""
                            hits += 1
                            rec = {"ts": time.strftime("%Y-%m-%d %H:%M:%S"), "hit": hits, "tid": tid,
                                   "eip": "%08X" % c.Eip, "esp": "%08X" % c.Esp,
                                   "eax": "%08X" % c.Eax, "ecx": "%08X" % c.Ecx,
                                   "edx": "%08X" % c.Edx, "ebx": "%08X" % c.Ebx,
                                   "esi": "%08X" % c.Esi, "edi": "%08X" % c.Edi, "ebp": "%08X" % c.Ebp,
                                   "code_bytes": prev.hex(), "stack": stack}
                            handle.write(json.dumps(rec, ensure_ascii=False) + "\n")
                            handle.flush()
                            print("\n★ 命中 #%d  写指令地址(Eip)=0x%08X  线程 %d" % (hits, c.Eip, tid), flush=True)
                            print("   Eax=0x%08X Ecx=0x%08X Edx=0x%08X Ebx=0x%08X"
                                  % (c.Eax, c.Ecx, c.Edx, c.Ebx), flush=True)
                            print("   Esi=0x%08X Edi=0x%08X Ebp=0x%08X Esp=0x%08X"
                                  % (c.Esi, c.Edi, c.Ebp, c.Esp), flush=True)
                            print("   Eip 前 16 字节机器码: %s" % rec["code_bytes"], flush=True)
                            print("   栈上前 16 个 dword: %s" % " ".join(stack[:16]), flush=True)
                            c.Dr6 = 0
                            c.Dr7 = (c.Dr7 & ~DR7_ENABLE0_WRITE_LEN4) | DR7_ENABLE0_WRITE_LEN4
                            c.EFlags |= 0x10000   # resume flag：防同一条指令重复触发
                            bp._set(h, c)
                        k32.CloseHandle(h)
                elif exc_code != STATUS_BREAKPOINT:
                    cont = DBG_EXCEPTION_NOT_HANDLED
            k32.ContinueDebugEvent(pid, tid, cont)
    except KeyboardInterrupt:
        print("\n收到中断 ⇒ 摘断点并 Detach", flush=True)
    finally:
        bp.disarm_all()
        if attached:
            k32.DebugActiveProcessStop(pid)
            print("断点已摘、已 Detach（目标进程不受影响：一个字节都没改）", flush=True)
        handle.close()
        print("命中 %d 次，记录在 %s" % (hits, os.path.abspath(out)), flush=True)


main()
