#!/usr/bin/env python3
"""硬件断点探针（**只读探查**）：抓"谁在读/写某个地址"。

用途（限量→无限的最后一步）：上限不在 mod 数据里、也不在模板/计数表里 ⇒ 必须抓出**执行那次判断的代码**。
做法：把 CPU 的调试寄存器 DR0 指向目标地址（RW=读/写），谁碰它就在那条指令上断下来，
记下 EIP 后立刻放行。拿到几个"读者 EIP"后，用 disasm_window.py 看上下文，就能认出判定在哪。

★ 安全纪律 ★
  * 无 GUI、不抢焦点、不动键鼠；
  * 只把目标进程**短暂**停一下（每条命中都是毫秒级，记完立刻放行）；
  * **任何路径都会恢复 DR 寄存器并 DebugActiveProcessStop**（finally 里做，超时也做）；
  * 默认最长跑 --seconds 秒（到点自己脱钩）。

用法：
    python tools/hw_watch.py --addr 0x0B903CB8 --seconds 10            # 读/写都抓
    python tools/hw_watch.py --addr 0x0B903CB8 --rw write --seconds 10 # 只抓写
    python tools/hw_watch.py --self-test                               # 拿自己起的小进程自测（保险丝）
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import signal
import struct
import subprocess
import sys
import time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)

DEBUG_PROCESS = 0x00000001
EXCEPTION_DEBUG_EVENT = 1
CREATE_THREAD_DEBUG_EVENT = 2
CREATE_PROCESS_DEBUG_EVENT = 3
EXIT_THREAD_DEBUG_EVENT = 4
EXIT_PROCESS_DEBUG_EVENT = 5
LOAD_DLL_DEBUG_EVENT = 6
OUTPUT_DEBUG_STRING_EVENT = 8
EXCEPTION_SINGLE_STEP = 0x80000004
DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
THREAD_ALL_ACCESS = 0x1F03FF
# ★ 必须包含 CONTEXT_DEBUG_REGISTERS(0x10) ★：少了它，Get/SetThreadContext 根本不搬运 Dr0/Dr7
#   （第一版就是这么装了个"空气断点"：SetThreadContext 报成功、实际 DR 没写进去）
CONTEXT_i386_FULL = 0x00010000 | 0x00000010 | 0x00000002 | 0x00000001     # i386 | DEBUG_REG | CONTROL | INTEGER


class CONTEXT(ctypes.Structure):
    _fields_ = [
        ("ContextFlags", wt.DWORD),
        ("Dr0", wt.DWORD), ("Dr1", wt.DWORD), ("Dr2", wt.DWORD), ("Dr3", wt.DWORD),
        ("Dr6", wt.DWORD), ("Dr7", wt.DWORD),
        ("FloatSave", ctypes.c_byte * 112),
        ("SegGs", wt.DWORD), ("SegFs", wt.DWORD), ("SegEs", wt.DWORD), ("SegDs", wt.DWORD),
        ("Edi", wt.DWORD), ("Esi", wt.DWORD), ("Ebx", wt.DWORD), ("Edx", wt.DWORD),
        ("Ecx", wt.DWORD), ("Eax", wt.DWORD), ("Ebp", wt.DWORD), ("Eip", wt.DWORD),
        ("SegCs", wt.DWORD), ("EFlags", wt.DWORD), ("Esp", wt.DWORD), ("SegSs", wt.DWORD),
        ("ExtendedRegisters", ctypes.c_byte * 512),
    ]


class DEBUG_EVENT(ctypes.Structure):
    class _U(ctypes.Union):
        class _EXCEPTION(ctypes.Structure):
            _fields_ = [("ExceptionCode", wt.DWORD), ("ExceptionFlags", wt.DWORD),
                        ("ExceptionRecord", ctypes.c_void_p), ("ExceptionAddress", ctypes.c_void_p),
                        ("NumberParameters", wt.DWORD), ("ExceptionInformation", ctypes.c_void_p * 15)]

        class _THREAD(ctypes.Structure):
            _fields_ = [("hThread", wt.HANDLE), ("dwThreadId", wt.DWORD)]

        class _PROCESS(ctypes.Structure):
            _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE), ("dwThreadId", wt.DWORD)]

        _fields_ = [("Exception", _EXCEPTION), ("Thread", _THREAD), ("Process", _PROCESS)]

    _fields_ = [("dwDebugEventCode", wt.DWORD), ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD),
                ("u", _U)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


def thread_ids(pid):
    snap = k32.CreateToolhelp32Snapshot(0x4, 0)                  # TH32CS_SNAPTHREAD
    entry = PE32(); entry.dwSize = ctypes.sizeof(entry)
    ok = k32.Thread32First(snap, ctypes.byref(entry)); out = []
    while ok:
        if entry.th32ProcessID == pid:
            out.append(entry.th32ThreadID)
        ok = k32.Thread32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    return out


def thread_handle(tid):
    return k32.OpenThread(THREAD_ALL_ACCESS, False, tid)


class Watcher:
    def __init__(self, pid, address, length, rw):
        self.pid = pid
        self.address = address
        self.length = length
        self.rw = rw
        self.hits = []
        self.armed_threads = set()

    def dr7(self):
        # 每个槽：RW/LEN 放高 4 位（DR7 的 16-31 位），使能位在低 8 位两两成对
        rw_bits = 0b11 if self.rw == "rw" else 0b01        # 11=读/写, 01=只写
        length_bits = {1: 0b00, 2: 0b01, 4: 0b11}[self.length]
        return 0x1 | ((rw_bits | (length_bits << 2)) << 16)

    def arm(self, tid, verbose=False):
        if tid in self.armed_threads:
            return
        handle = thread_handle(tid)
        if not handle:
            return
        context = CONTEXT(); context.ContextFlags = CONTEXT_i386_FULL
        if k32.GetThreadContext(handle, ctypes.byref(context)):
            context.Dr0 = self.address
            context.Dr7 = self.dr7()
            if k32.SetThreadContext(handle, ctypes.byref(context)):
                # 读回核对：真写进去了才算装上（不核对就会"以为装了"）
                check = CONTEXT(); check.ContextFlags = CONTEXT_i386_FULL
                ok = k32.GetThreadContext(handle, ctypes.byref(check)) and check.Dr0 == self.address and check.Dr7 == context.Dr7
                if ok:
                    self.armed_threads.add(tid)
                    if verbose:
                        print(f"  · 线程 {tid} 装上断点并读回核对通过 (DR0=0x{check.Dr0:08X} DR7=0x{check.Dr7:X})")
                elif verbose:
                    print(f"  · 线程 {tid} 读回核对失败（DR0=0x{check.Dr0:08X}）⇒ 当没装")
        k32.CloseHandle(handle)

    def disarm_all(self):
        for tid in list(self.armed_threads):
            handle = thread_handle(tid)
            if not handle:
                continue
            context = CONTEXT(); context.ContextFlags = CONTEXT_i386_FULL
            if k32.GetThreadContext(handle, ctypes.byref(context)):
                context.Dr0 = context.Dr1 = context.Dr2 = context.Dr3 = context.Dr6 = context.Dr7 = 0
                k32.SetThreadContext(handle, ctypes.byref(context))
            k32.CloseHandle(handle)
        print(f"  · 已把 {len(self.armed_threads)} 个线程的调试寄存器清零")
        self.armed_threads.clear()

    def run(self, seconds, max_hits, verbose=False):
        # ★ 铁律：必须在**进程被调试器冻住之后**才能装 DR（attach 前装会被静默丢弃，第一版就是这么漏的）。
        # 冻住之后，Windows 会为每个已有线程补一个 CREATE_THREAD 事件 ⇒ 在那里逐个装。
        if k32.DebugActiveProcess(self.pid) == 0:
            raise SystemExit(f"DebugActiveProcess 失败: {ctypes.get_last_error()}"
                             "（可能已有调试器挂着，或权限不足）")
        deadline = time.time() + seconds
        events_seen = {}
        try:
            while time.time() < deadline and len(self.hits) < max_hits:
                if k32.WaitForDebugEvent(ctypes.byref(EVENT := DEBUG_EVENT()), 200) == 0:
                    continue
                code = EVENT.dwDebugEventCode
                events_seen[code] = events_seen.get(code, 0) + 1
                if code == CREATE_THREAD_DEBUG_EVENT:
                    self.arm(EVENT.dwThreadId, verbose)          # 已有线程也会补发这个事件
                event_exception = 0
                if code == EXCEPTION_DEBUG_EVENT:
                    event_exception = EVENT.u.Exception.ExceptionCode
                if code == EXCEPTION_DEBUG_EVENT and event_exception == EXCEPTION_SINGLE_STEP:
                    tid = EVENT.dwThreadId
                    handle = thread_handle(tid)
                    context = CONTEXT(); context.ContextFlags = CONTEXT_i386_FULL
                    if handle and k32.GetThreadContext(handle, ctypes.byref(context)):
                        self.hits.append((context.Eip, context.Edx, context.Esi, context.Edi))
                        if len(self.hits) <= 25:
                            print(f"  命中 #{len(self.hits)}: EIP=0x{context.Eip:08X}"
                                  f"  (edx=0x{context.Edx:08X} esi=0x{context.Esi:08X} edi=0x{context.Edi:08X})")
                        context.Dr6 = 0                             # 清掉"为什么断"的标志，否则会立刻再断
                        k32.SetThreadContext(handle, ctypes.byref(context))
                    if handle:
                        k32.CloseHandle(handle)
                if code != EXCEPTION_DEBUG_EVENT:
                    status = DBG_CONTINUE
                elif event_exception in (EXCEPTION_SINGLE_STEP, 0x80000003):      # 我们的断点 / 初次断点
                    status = DBG_CONTINUE
                else:
                    status = DBG_EXCEPTION_NOT_HANDLED                            # 别的异常还给游戏自己处理
                k32.ContinueDebugEvent(EVENT.dwProcessId, EVENT.dwThreadId, status)
        finally:
            self.disarm_all()
            k32.DebugActiveProcessStop(self.pid)
            print(f"  · 事件统计: {events_seen}")
            print(f"  · 已脱钩（进程继续正常跑）")


def self_test(target=None):
    """自测：起一个**会反复写固定地址的靶子**，写断点必然命中。

    ★ 为什么默认用 tools/HwWatchTarget 编出来的 32 位靶子：游戏是 32 位，探针用的是 32 位 CONTEXT 结构，
      拿 64 位 Python 当靶子会漏掉（DR 装不上）—— 第一版就是这么"假通过"的。没有 32 位靶子时退回 Python。
    """
    if target:
        child = subprocess.Popen([target], stdout=subprocess.PIPE, text=True)
    else:
        script = ("import ctypes,time\n"
                  "x=ctypes.c_int(0)\n"
                  "print(hex(ctypes.addressof(x)), flush=True)\n"
                  "while True:\n"
                  "    x.value = (x.value + 1) & 0xFFFF\n"
                  "    time.sleep(0.001)\n")
        child = subprocess.Popen([sys.executable, "-c", script], stdout=subprocess.PIPE, text=True)
    try:
        target = int(child.stdout.readline().strip(), 16)
        print(f"自测目标进程 pid={child.pid} 地址=0x{target:08X}")
        watcher = Watcher(child.pid, target, 4, "write")
        watcher.run(seconds=3, max_hits=5, verbose=True)
        print(f"自测结果：命中 {len(watcher.hits)} 次 ⇒ {'✓ 通过' if watcher.hits else '✗ 没命中（探针有问题）'}")
        print("进程还活着吗:", child.poll() is None, "（必须 True：脱钩后不能把它弄死）")
        return 0 if watcher.hits and child.poll() is None else 1
    finally:
        child.kill()


def main():
    parser = argparse.ArgumentParser(description="硬件断点探针（只探查，不写内存）")
    parser.add_argument("--addr", help="要盯的地址，如 0x0B903CB8")
    parser.add_argument("--len", type=int, default=4)
    parser.add_argument("--rw", choices=("rw", "write"), default="rw")
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--max", dest="max_hits", type=int, default=50)
    parser.add_argument("--pid", type=int)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--target", help="自测用的靶子 exe（32 位靶子见 tools/HwWatchTarget）")
    args = parser.parse_args()

    if args.self_test:
        raise SystemExit(self_test(args.target))
    if not args.addr or not args.pid:
        parser.error("需要 --pid 和 --addr（或 --self-test）")
    signal.signal(signal.SIGINT, lambda *_: None)
    watcher = Watcher(args.pid, int(args.addr, 0), args.len, args.rw)
    watcher.run(args.seconds, args.max_hits, verbose=True)
    print(f"共命中 {len(watcher.hits)} 次")


if __name__ == "__main__":
    main()
