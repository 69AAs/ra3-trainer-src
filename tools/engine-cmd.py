#!/usr/bin/env python3
"""引擎命令通道的发送端（离线侧）。

覆盖层 DLL 里有一条门控命令通道：%TEMP%\\ra3-engine-probe.flag 存在时才启用，
%TEMP%\\ra3-engine-cmd.txt 每追加一行 = 给游戏引擎发一条动作。

用法：
  python tools/engine-cmd.py PLAYER_SET_MONEY Player_1 10000   # 自动开门 + 追加一条命令
  python tools/engine-cmd.py --gate on|off|status
  python tools/engine-cmd.py --clear
  python tools/engine-cmd.py --tail [N]      # 打印最近的 osd-engine-cmd 判定行（默认 30 行）

约定（与 DLL 侧解析一致，写错就是写错，DLL 不会"顺手修正"）：
  * 一行一条：`动作名 参数...`；动作名全大写字母数字下划线，最多 6 个参数。
  * 参数是纯数字就当 number 压栈，否则当 string（玩家名这类）。
  * '#' 起行内注释。必须以换行结尾。
"""
import glob
import os
import sys
import tempfile
import time

TEMP = tempfile.gettempdir()
FLAG = os.path.join(TEMP, "ra3-engine-probe.flag")
CMD = os.path.join(TEMP, "ra3-engine-cmd.txt")


def gate(state: str) -> int:
    if state == "on":
        with open(FLAG, "w", encoding="ascii") as handle:
            handle.write("1\n")
        print(f"门控已开: {FLAG}")
        return 0
    if state == "off":
        if os.path.exists(FLAG):
            os.remove(FLAG)
        print(f"门控已关（文件已删）: {FLAG}")
        return 0
    print(f"门控: {'开' if os.path.exists(FLAG) else '关'}  ({FLAG})")
    print(f"命令文件: {CMD}  {'存在, %d 字节' % os.path.getsize(CMD) if os.path.exists(CMD) else '不存在'}")
    return 0


def clear() -> int:
    if os.path.exists(CMD):
        os.remove(CMD)
        print("命令文件已清空（覆盖层会把偏移归零，下次从头读）")
    else:
        print("命令文件本来就不存在")
    return 0


def tail(count: int) -> int:
    logs = sorted(glob.glob(os.path.join(TEMP, "Ra3Overlay-*.log")), key=os.path.getmtime)
    if not logs:
        print(f"{TEMP} 下没有 Ra3Overlay-*.log —— 覆盖层这一局没加载过")
        return 1
    path = logs[-1]
    print(f"看最新日志: {path}")
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        lines = [line.rstrip() for line in handle if "osd-engine-cmd" in line or "osd-engine-lua" in line]
    for line in lines[-count:]:
        print("  " + line)
    if not lines:
        print("  （没有 osd-engine-cmd / osd-engine-lua 行）")
    return 0


def send(args) -> int:
    if not args:
        print(__doc__)
        return 2
    line = " ".join(args).strip()
    name = line.split()[0]
    if not (3 <= len(name) <= 47 and name[0].isupper() and
            all(c.isupper() or c.isdigit() or c == "_" for c in name)):
        print(f"动作名形状不对，拒发: {name!r}（应为全大写字母数字下划线）")
        return 2
    if not os.path.exists(FLAG):
        with open(FLAG, "w", encoding="ascii") as handle:
            handle.write("1\n")
        print(f"门控文件不存在，已自动打开: {FLAG}")
    with open(CMD, "a", encoding="ascii") as handle:
        handle.write(line + "\n")
    print(f"已追加（{time.strftime('%H:%M:%S')}）: {line}")
    print("接下来 1~2 秒内覆盖层会写 staged → issued → completed；用 --tail 看结果")
    return 0


def main() -> int:
    argv = sys.argv[1:]
    if not argv:
        print(__doc__)
        return 2
    if argv[0] == "--gate":
        return gate(argv[1] if len(argv) > 1 else "status")
    if argv[0] == "--clear":
        return clear()
    if argv[0] == "--tail":
        return tail(int(argv[1]) if len(argv) > 1 else 30)
    return send(argv)


if __name__ == "__main__":
    sys.exit(main())
