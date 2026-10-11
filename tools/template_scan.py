#!/usr/bin/env python3
"""按类型码找"模板对象"，并把两个单位的模板对齐比较（**只读**）。

为什么这么做：
  * 模板对象有个铁特征 —— **模板 +8 = 类型码**（守卫代码就是这么读的：`Read(template + 8)`）；
  * 于是只要在内存里搜类型码这 4 个字节，命中处 -8 就是模板（还要校验可读、对齐、周围像对象）；
  * 两个单位（塔 0xB31A4632 上限 3 / 收割机甲 0x0257E867 上限 1）的模板**对齐一比**，
    凡是"一个 3、一个 1"的小整数字段，就是"生产上限"的强候选 —— 不用猜偏移。

用法：
    python tools/template_scan.py find 0xB31A4632            # 找这个类型码的模板 + 列出 0x400 内的小整数
    python tools/template_scan.py compare 0xB31A4632 0x0257E867
全程只读（PROCESS_VM_READ），不写一个字节。
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
TARGET = "ra3_1.12.game"
BASE = 0x400000

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.ReadProcessMemory.restype = wt.BOOL


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__pad", wt.DWORD), ("RegionSize", ctypes.c_size_t),
                ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__pad2", wt.DWORD)]


class PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.POINTER(ctypes.c_ulong)), ("th32ModuleID", wt.DWORD),
                ("cntThreads", wt.DWORD), ("th32ParentProcessID", wt.DWORD),
                ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD), ("szExeFile", ctypes.c_char * 260)]


def find_pid():
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)
    entry = PE32(); entry.dwSize = ctypes.sizeof(entry)
    ok = k32.Process32First(snap, ctypes.byref(entry)); pid = None
    while ok:
        if entry.szExeFile.decode("latin1", "replace").rstrip("\x00").lower() == TARGET.lower():
            pid = entry.th32ProcessID; break
        ok = k32.Process32Next(snap, ctypes.byref(entry))
    k32.CloseHandle(snap)
    return pid


class Mem:
    def __init__(self, pid):
        self.h = k32.OpenProcess(0x0400 | 0x0010, False, pid)     # QUERY_INFORMATION | VM_READ
        if not self.h:
            raise SystemExit("OpenProcess 失败: %d" % ctypes.get_last_error())

    def raw(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr & 0xFFFFFFFF), ctypes.byref(buf), n, ctypes.byref(got)):
            return None
        return buf.raw[:got.value]

    def u32(self, addr, off=0):
        blob = self.raw(addr, off + 4)
        return None if blob is None or len(blob) < off + 4 else struct.unpack_from("<I", blob, off)[0]

    def regions(self, max_size=512 * 1024 * 1024):
        """枚举已提交、可读的区域（跳过镜像/保留/巨大区域）。"""
        address = 0; out = []
        k32.VirtualQueryEx.restype = ctypes.c_size_t
        while address < 0x80000000:
            mbi = MBI()
            if k32.VirtualQueryEx(self.h, ctypes.c_void_p(address), ctypes.byref(mbi), ctypes.sizeof(mbi)) == 0:
                address += 0x1000; continue
            base = mbi.BaseAddress or 0
            size = mbi.RegionSize
            if mbi.State == 0x1000 and 0 < size <= max_size and (mbi.Protect & (0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80)):
                out.append((base, size))
            address = (base + size) & 0xFFFFFFFF
            if size == 0:
                address += 0x1000
        return out


def find_templates(mem, type_code, progress=False):
    """搜类型码 ⇒ 命中处 -8 当模板候选（校验对齐 + 可读 + 模板 +8 仍然是这个类型码）。

    分块扫描（每块 4MB、相邻块重叠 3 字节），用 ``bytes.find``（C 速度）——
    不要逐字节循环 Python，几百 MB 会跑到天荒地老。
    """
    pattern = struct.pack("<I", type_code)
    found = []
    chunk = 4 * 1024 * 1024
    regions = mem.regions()
    for index, (base, size) in enumerate(regions, 1):
        if progress:
            print(f"  扫区域 {index}/{len(regions)} 0x{base:08X} ({size // 1024} KB)", flush=True)
        offset = 0
        carry = b""
        while offset < size:
            take = min(chunk, size - offset)
            blob = mem.raw(base + offset, take)
            if blob is None:
                offset += take
                carry = b""
                continue
            window = carry + blob
            window_base = base + offset - len(carry)
            cursor = 0
            while True:
                idx = window.find(pattern, cursor)
                if idx < 0:
                    break
                cursor = idx + 1
                _consider(mem, type_code, window_base + idx, found)
            carry = window[-3:]
            offset += take
    return found


def _consider(mem, type_code, hit, found):
    """命中处-8 当模板候选：必须 4 字节对齐、可读、且 (模板+8) 仍等于类型码。"""
    template = hit - 8
    if template & 3 or template < 0x10000:
        return
    if mem.u32(template + 8) != type_code:
        return
    if mem.raw(template, 0x40) is None:
        return
    if template not in found:
        found.append(template)


def nearby_strings(mem, address, span=0x400, max_len=64):
    """模板附近的"指向可打印字符串的指针"（内有取经过：塔 = SovietNewTower、收割机甲 = SovietHeavyWalkerVehicle）。"""
    out = set()
    blob = mem.raw(address, span) or b""
    for off in range(0, len(blob) - 3, 4):
        value = struct.unpack_from("<I", blob, off)[0]
        if value < 0x10000 or value > 0xFFFEFFFF or (value & 3):
            continue
        text = mem.raw(value, max_len)
        if not text:
            continue
        end = text.find(b"\x00")
        if end < 3:
            continue
        candidate = text[:end]
        if all(32 <= c < 127 for c in candidate) and (b"A" <= candidate[:1] <= b"z"):
            out.add(candidate.decode("latin1"))
    return out


def small_ints(mem, template, span=0x400, limit=64):
    blob = mem.raw(template, span) or b""
    out = []
    for off in range(0, len(blob) - 3, 4):
        value = struct.unpack_from("<I", blob, off)[0]
        if 0 < value <= limit:
            out.append((off, value))
    return out


def main():
    if len(sys.argv) < 3:
        print(__doc__); return
    command = sys.argv[1]
    pid = find_pid()
    if not pid:
        raise SystemExit(f"没找到进程 {TARGET}（先把游戏跑起来）")
    mem = Mem(pid)

    if command == "find":
        code = int(sys.argv[2], 0)
        want_name = sys.argv[3] if len(sys.argv) > 3 else None      # 用内部名过滤出"真模板"
        templates = find_templates(mem, code)
        print(f"类型 0x{code:08X} 的候选对象：{len(templates)} 个")
        if want_name:
            keep = []
            for template in templates:
                names = nearby_strings(mem, template, 0x400)
                if want_name in names:
                    keep.append((template, sorted(names)[:4]))
            print(f"其中附近字符串含 \"{want_name}\" 的：{len(keep)} 个  ← 这才是真模板")
            for template, names in keep[:6]:
                print(f"\n  模板 0x{template:08X}  附近名字: {names}")
                for off, value in small_ints(mem, template):
                    print(f"    +0x{off:04X} = {value}")
        else:
            for template in templates[:6]:
                print(f"  候选 0x{template:08X}  附近: {sorted(nearby_strings(mem, template, 0x200))[:3]}")
    elif command == "compare":
        a, b = int(sys.argv[2], 0), int(sys.argv[3], 0)
        name_a = sys.argv[4] if len(sys.argv) > 4 else None
        name_b = sys.argv[5] if len(sys.argv) > 5 else None

        def pick(code, want):
            for template in find_templates(mem, code):
                if want is None or want in nearby_strings(mem, template, 0x400):
                    return template
            return None

        ta, tb = pick(a, name_a), pick(b, name_b)
        print(f"A 0x{a:08X} 模板: {hex(ta) if ta else None}   B 0x{b:08X} 模板: {hex(tb) if tb else None}")
        if not ta or not tb:
            print("有一边没认出来 ⇒ 加内部名参数（如 SovietHeavyWalkerVehicle），或先在游戏里选中它"); return
        ma = dict(small_ints(mem, ta)); mb = dict(small_ints(mem, tb))
        print(f"\n对齐比较（模板 A 0x{ta:08X} vs B 0x{tb:08X}）：两边都是小整数但**值不同**的偏移")
        for off in sorted(set(ma) & set(mb)):
            if ma[off] != mb[off]:
                print(f"  +0x{off:04X}   A={ma[off]}   B={mb[off]}   ★ 上限候选")
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
