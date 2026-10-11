#!/usr/bin/env python3
"""对运行中的游戏做**只读**内存读取（不改一个字节、不注入、不需要重启游戏）。

为什么要它：引擎动作派发只回报"表里有没有这个名字"，不回报"效果有没有发生"；
要验证效果（钱/电力/单位），必须自己把值读回来。这是那条回读路径。

用法：
  python tools/memread.py base                     # 模块基址 + ImageBase
  python tools/memread.py table                    # 读 [基址+0x8CD190]（技能里记的动作表指针）
  python tools/memread.py ptr <十六进制VA>          # 读某个 VA 处的 dword（解指针链）
  python tools/memread.py find <十六进制值> [范围KB]  # 在模块映像里找等于该值的 dword（找引用）
  python tools/memread.py dump <十六进制VA> [长度]   # 十六进制转储
  python tools/memread.py str <十六进制VA> [长度]    # 读 C 字符串
  python tools/memread.py u32 <十六进制VA> [个数]    # 按 dword 列表打印
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import tempfile

PROCESS_QUERY_INFORMATION = 0x0400
PROCESS_VM_READ = 0x0010
TH32CS_SNAPMODULE = 0x00000008
TH32CS_SNAPMODULE32 = 0x00000010
TARGET_SUFFIX = "ra3_1.12.game"
TABLE_RVA = 0x8CD190          # 技能 references/engine-action-table-enumeration.md 里记的表指针
NAME_VA = 0x00BFB588          # "PLAYER_SET_MONEY" 名字串 VA（用于定位条目）

k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MODULEENTRY32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("th32ModuleID", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("GlblcntUsage", wt.DWORD), ("ProccntUsage", wt.DWORD), ("modBaseAddr", ctypes.c_void_p),
                ("modBaseSize", wt.DWORD), ("hModule", wt.HMODULE),
                ("szModule", ctypes.c_char * 256), ("szExePath", ctypes.c_char * 260)]


def find_process():
    snap = k32.CreateToolhelp32Snapshot(0x00000002, 0)
    if snap == -1:
        raise SystemExit("CreateToolhelp32Snapshot(进程) 失败")
    class PROCESSENTRY32(ctypes.Structure):
        _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                    ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                    ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                    ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                    ("szExeFile", ctypes.c_char * 260)]
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(entry)
    pid = None
    ok = k32.Process32First(snap, ctypes.byref(entry))
    while ok:
        name = entry.szExeFile.decode("latin1", "replace")
        if name.endswith(TARGET_SUFFIX) or "ra3_1.12" in name.lower():
            pid = entry.th32ProcessID
            print(f"找到游戏进程: {name} pid={pid}")
            break
        ok = k32.Process32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    if pid is None:
        raise SystemExit(f"没找到 {TARGET_SUFFIX} 进程（游戏没在跑？）")
    return pid


def open_process(pid):
    handle = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not handle:
        raise SystemExit(f"OpenProcess 失败 err={ctypes.get_last_error()}（权限不足？）")
    return handle


def module_base(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)
    if snap == -1:
        raise SystemExit("CreateToolhelp32Snapshot(模块) 失败")
    entry = MODULEENTRY32()
    entry.dwSize = ctypes.sizeof(entry)
    base, size = None, None
    ok = k32.Module32First(snap, ctypes.byref(entry))
    while ok:
        name = entry.szModule.decode("latin1", "replace")
        if name.endswith(TARGET_SUFFIX) or "ra3_1.12" in name.lower():
            base, size = entry.modBaseAddr, entry.modBaseSize
            print(f"模块 {name}: base=0x{base:08X} size=0x{size:X}")
            break
        ok = k32.Module32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    if base is None:
        raise SystemExit("没找到游戏模块")
    return base, size


def read(handle, addr, length):
    buf = ctypes.create_string_buffer(length)
    read_bytes = ctypes.c_size_t(0)
    if not k32.ReadProcessMemory(handle, ctypes.c_void_p(addr), buf, length, ctypes.byref(read_bytes)):
        raise SystemExit(f"ReadProcessMemory(0x{addr:X}, {length}) 失败 err={ctypes.get_last_error()}")
    return buf.raw[:read_bytes.value]


def u32s(data):
    return list(struct.unpack("<%dI" % (len(data) // 4), data[:len(data) // 4 * 4]))


def cstr(handle, addr, cap=96):
    try:
        raw = read(handle, addr, cap)
    except SystemExit:
        return "<读不到>"
    return raw.split(b"\x00")[0].decode("latin1", "replace")


def scan_ascii(handle, needle, max_regions=None):
    """在整个进程的可读已提交内存里搜 ASCII 串（只读）。返回 (地址, 所在区域基址, 区域大小)。"""
    class MEMORY_BASIC_INFORMATION(ctypes.Structure):
        _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                    ("AllocationProtect", wt.DWORD), ("RegionSize", ctypes.c_size_t),
                    ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD)]
    mbi = MEMORY_BASIC_INFORMATION()
    addr = 0
    hits = []
    regions = 0
    READABLE = (0x02, 0x04, 0x20, 0x40)      # READONLY / READWRITE / EXECUTE_READ / EXECUTE_READWRITE
    while addr < 0x7FFF0000:
        got = k32.VirtualQueryEx(handle, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi))
        if not got:
            break
        base, size, state, protect = mbi.BaseAddress or 0, mbi.RegionSize, mbi.State, mbi.Protect
        if state == 0x1000 and protect in READABLE and size:      # MEM_COMMIT
            regions += 1
            if max_regions and regions > max_regions:
                break
            offset = 0
            while offset < size:
                span = min(1 << 20, size - offset)
                try:
                    data = read(handle, base + offset, span)
                except SystemExit:
                    break
                pos = data.find(needle)
                while pos >= 0:
                    hits.append((base + offset + pos, base, size))
                    pos = data.find(needle, pos + 1)
                offset += len(data)
                if not data:
                    break
        addr = base + (size or 0x1000)
    return hits, regions


def main():
    argv = sys.argv[1:]
    if not argv:
        print(__doc__)
        return 2
    pid = find_process()
    handle = open_process(pid)
    base, size = module_base(pid)
    image_base = struct.unpack("<I", read(handle, base + 0x3C, 4))[0]
    image_base = struct.unpack("<I", read(handle, base + image_base + 0x34, 4))[0]
    print(f"PE ImageBase = 0x{image_base:08X}")

    cmd = argv[0]
    if cmd == "base":
        return 0
    if cmd == "table":
        ptr = struct.unpack("<I", read(handle, base + TABLE_RVA, 4))[0]
        print(f"[base+0x{TABLE_RVA:X}] = 0x{ptr:08X}")
        if ptr:
            words = u32s(read(handle, ptr, 128))
            print("表头 32 个 dword:")
            for i in range(0, len(words), 4):
                print("   +%02X  %s" % (i * 4, " ".join(f"{w:08X}" for w in words[i:i + 4])))
        return 0
    if cmd == "ptr":
        addr = int(argv[1], 16)
        value = struct.unpack("<I", read(handle, addr, 4))[0]
        print(f"[0x{addr:08X}] = 0x{value:08X}   -> \"{cstr(handle, value, 64)}\"")
        return 0
    if cmd == "str":
        addr = int(argv[1], 16)
        length = int(argv[2]) if len(argv) > 2 else 96
        print(cstr(handle, addr, length))
        return 0
    if cmd == "dump":
        addr = int(argv[1], 16)
        length = int(argv[2]) if len(argv) > 2 else 128
        data = read(handle, addr, length)
        for off in range(0, len(data), 16):
            chunk = data[off:off + 16]
            hexed = " ".join(f"{b:02x}" for b in chunk)
            text = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
            print(f"  {addr + off:08X}  {hexed:<47}  {text}")
        return 0
    if cmd == "u32":
        addr = int(argv[1], 16)
        count = int(argv[2]) if len(argv) > 2 else 16
        words = u32s(read(handle, addr, count * 4))
        for i, word in enumerate(words):
            print(f"  +{i * 4:04X}  0x{word:08X}")
        return 0
    if cmd == "find":
        value = int(argv[1], 16)
        kb = int(argv[2]) if len(argv) > 2 else size // 1024
        needle = struct.pack("<I", value)
        chunk = 1 << 20
        scanned = 0
        hits = []
        while scanned < min(kb, size // 1024) * 1024:
            try:
                data = read(handle, base + scanned, min(chunk, min(kb, size // 1024) * 1024 - scanned))
            except SystemExit:
                scanned += chunk
                continue
            pos = data.find(needle)
            while pos >= 0:
                hits.append(base + scanned + pos)
                pos = data.find(needle, pos + 1)
            scanned += len(data)
        print(f"命中 {len(hits)} 处（值 0x{value:08X}）:")
        for hit in hits[:40]:
            print(f"   地址 0x{hit:08X}  RVA 0x{hit - base + image_base:X}")
        return 0
    if cmd == "scan":
        needle = argv[1].encode("latin1")
        hits, regions = scan_ascii(handle, needle)
        print(f"扫描了 {regions} 个可读区域，命中 {len(hits)} 处:")
        for hit, rbase, rsize in hits[:30]:
            print(f"   0x{hit:08X}  (区域 0x{rbase:08X} 大小 0x{rsize:X})")
        return 0
    if cmd == "value":
        # 值扫描：在全部可读内存里找这个 u32 / float 值，命中写盘，供 refine 逐步收窄
        raw = argv[1]
        is_float = "." in raw
        target = float(raw) if is_float else int(raw, 0)
        needle = struct.pack("<f", target) if is_float else struct.pack("<I", target & 0xFFFFFFFF)
        hits = []
        class MEMORY_BASIC_INFORMATION(ctypes.Structure):
            _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                        ("AllocationProtect", wt.DWORD), ("RegionSize", ctypes.c_size_t),
                        ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD)]
        mbi = MEMORY_BASIC_INFORMATION()
        addr, regions = 0, 0
        while addr < 0x7FFF0000:
            if not k32.VirtualQueryEx(handle, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
                break
            rbase, rsize, state, protect = mbi.BaseAddress or 0, mbi.RegionSize, mbi.State, mbi.Protect
            if state == 0x1000 and protect in (0x02, 0x04, 0x20, 0x40) and rsize:
                regions += 1
                off = 0
                while off < rsize:
                    span = min(1 << 20, rsize - off)
                    try:
                        chunk = read(handle, rbase + off, span)
                    except SystemExit:
                        break
                    pos = chunk.find(needle)
                    while pos >= 0:
                        hits.append(rbase + off + pos)
                        pos = chunk.find(needle, pos + 1)
                    off += len(chunk)
                    if not chunk:
                        break
            addr = rbase + (rsize or 0x1000)
        out = os.path.join(tempfile.gettempdir(), "memread_hits.json")
        with open(out, "w", encoding="utf-8") as fh:
            json.dump({"target": raw, "hits": hits}, fh)
        print(f"值 {raw} 命中 {len(hits)} 处（扫了 {regions} 个区域）→ 已写 {out}")
        for a in hits[:20]:
            print(f"   0x{a:08X}")
        return 0
    if cmd == "refine":
        out = os.path.join(tempfile.gettempdir(), "memread_hits.json")
        if not os.path.exists(out):
            raise SystemExit("先跑一次 value 建立命中集")
        saved = json.load(open(out, encoding="utf-8"))
        raw = argv[1]
        is_float = "." in raw
        target = float(raw) if is_float else int(raw, 0)
        kept = []
        for a in saved["hits"]:
            try:
                got = read(handle, a, 4)
            except SystemExit:
                continue
            value = struct.unpack("<f", got)[0] if is_float else struct.unpack("<I", got)[0]
            if (abs(value - target) < 0.5) if is_float else (value == target):
                kept.append(a)
        with open(out, "w", encoding="utf-8") as fh:
            json.dump({"target": raw, "hits": kept}, fh)
        print(f"收窄到 {len(kept)} 处（值 = {raw}）")
        for a in kept[:20]:
            print(f"   0x{a:08X}  <- {saved['target']} 时命中过")
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
