#!/usr/bin/env python3
"""单点代码补丁（**会写**）：把引擎里那一次"能不能造"的比较改掉之后，用这个装/撤。

纪律（按用户口径）：
  * 一次只改**一处**、字节数一致（不改长度）；
  * 写前把原字节存进 `artifacts/code-patches/<tag>-before.json`（撤回就靠它）；
  * 写完**立刻读回**确认，不一致就自动写回原值并报错；
  * 任何一步失败都不留半成品。

用法：
    python tools/code_patch.py show  <addr> <len>                 # 只读现在是什么字节
    python tools/code_patch.py apply <addr> <hex字节> --tag 名     # 装（先备份原字节 + 读回）
    python tools/code_patch.py revert artifacts/code-patches/名-before.json   # 撤
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import code_ref_scan as crs

TARGET = "ra3_1.12.game"
PATCH_DIR = os.path.join("artifacts", "code-patches")
PROCESS_ALL = 0x1F0FFF


class WriteMem(crs.Mem):
    """带写权限的句柄（只在 apply/revert 时用；读取一律走父类的只读句柄）。"""

    def __init__(self, pid):
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.OpenProcess.restype = wt.HANDLE
        self.h = k32.OpenProcess(PROCESS_ALL, False, pid)
        if not self.h:
            raise SystemExit("OpenProcess(写) 失败: %d" % ctypes.get_last_error())
        self.k32 = k32

    def write(self, addr, blob):
        written = ctypes.c_size_t()
        ok = self.k32.WriteProcessMemory(self.h, ctypes.c_void_p(addr), blob, len(blob), ctypes.byref(written))
        if not ok or written.value != len(blob):
            raise SystemExit("WriteProcessMemory 失败: %d（写了 %d/%d）" % (ctypes.get_last_error(), written.value, len(blob)))


def pid_or_die():
    pid = crs.find_pid()
    if not pid:
        raise SystemExit(f"没找到进程 {TARGET}")
    return pid


def show(addr, length):
    mem = crs.Mem(pid_or_die())
    blob = mem.raw(addr, length)
    print(f"0x{addr:08X} 现字节: {blob.hex() if blob else '(读不到)'}")


def apply_patch(addr, hex_bytes, tag):
    payload = bytes.fromhex(hex_bytes.replace(" ", ""))
    reader = crs.Mem(pid_or_die())
    before = reader.raw(addr, len(payload))
    if before is None or len(before) != len(payload):
        raise SystemExit("读原字节失败 ⇒ 不装")

    os.makedirs(PATCH_DIR, exist_ok=True)
    record = os.path.join(PATCH_DIR, f"{tag}-before.json")
    with open(record, "w", encoding="utf-8") as handle:
        json.dump({"addr": addr, "before": before.hex(), "after": payload.hex()}, handle, indent=1)

    writer = WriteMem(crs.find_pid())
    writer.write(addr, payload)
    after = crs.Mem(crs.find_pid()).raw(addr, len(payload))
    if after != payload:
        writer.write(addr, before)                      # 回滚，绝不半成品
        raise SystemExit(f"读回不一致（拿到 {after.hex() if after else 'None'}）⇒ 已自动写回原字节")
    print(f"✓ 已装 {tag}: 0x{addr:08X} {before.hex()} → {payload.hex()}")
    print(f"  原字节存于 {record}（撤：python tools/code_patch.py revert {record}）")


def revert(record_path):
    with open(record_path, encoding="utf-8") as handle:
        record = json.load(handle)
    addr = int(record["addr"])
    before = bytes.fromhex(record["before"])
    writer = WriteMem(pid_or_die())
    writer.write(addr, before)
    after = crs.Mem(crs.find_pid()).raw(addr, len(before))
    ok = after == before
    print(("✓ 已撤回 " if ok else "⚠ 撤回后读回不一致，请立刻告诉我 ") + f"0x{addr:08X} = {after.hex() if after else 'None'}")


def main():
    parser = argparse.ArgumentParser(description="单点代码补丁（先用 limit_hunt 定位，再动这里）")
    sub = parser.add_subparsers(dest="command", required=True)
    p_show = sub.add_parser("show"); p_show.add_argument("addr"); p_show.add_argument("len", type=lambda s: int(s, 0))
    p_apply = sub.add_parser("apply"); p_apply.add_argument("addr"); p_apply.add_argument("hex"); p_apply.add_argument("--tag", required=True)
    p_revert = sub.add_parser("revert"); p_revert.add_argument("record")
    args = parser.parse_args()

    if args.command == "show":
        show(int(args.addr, 0), args.len)
    elif args.command == "apply":
        apply_patch(int(args.addr, 0), args.hex, args.tag)
    else:
        revert(args.record)


if __name__ == "__main__":
    main()
