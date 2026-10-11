#!/usr/bin/env python3
"""把修改器面板里的数值同步到游戏侧 Lua 配置（第 3 步：面板 ↔ Lua 打通）。

数据流：
  Ra3Trainer.settings.json  ──(本脚本)──▶  D:\\Games\\R3Lua\\hs-config.lua  ──(游戏里 hs reload)──▶  生效

为什么这样做：不去改 WPF 面板（零风险、不用重编），面板里"资源数值/预设"本来就是配置，
把它们同步成 Lua 认的配置即可。以后要自动化，把这个脚本挂到面板的"保存设置"之后就行。

用法：
  python tools/sync-hs-config.py            # 读默认位置并写入
  python tools/sync-hs-config.py --show     # 只看当前两边的内容
"""
import argparse
import io
import json
import os
import sys
import time

SETTINGS = r"D:\Games\Ra3Trainer\build-test\Ra3Trainer.settings.json"
TARGET = r"D:\Games\R3Lua\hs-config.lua"


def read_settings(path):
    with io.open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def guess_unit_type(settings):
    """面板预设名里常带引擎类型名（例：海啸坦克（TsunamiAssaultTank_Naval）×8）⇒ 优先用它。
    纯数字的 UnitId 不能直接给 Lua 库（它要的是类型名，不是码），所以只作注释保留。"""
    import re
    for preset in settings.get("ReinforcementPresets") or []:
        name = preset.get("Name") or ""
        match = re.search(r"[\(（]([A-Za-z][A-Za-z0-9_]{3,})[\)）]", name)
        if match:
            return match.group(1), preset.get("UnitId")
    return None, None


def build_lua(settings, command=None, player=None):
    values = settings.get("ResourceValues", {}) or {}
    money = int(values.get("MoneyAmount", 100000))
    power = int(values.get("PowerValue", 500))
    sp = int(values.get("ScPointValue", 15))
    unit_type, raw_id = guess_unit_type(settings)
    if unit_type is None:
        # 面板里没写引擎名 ⇒ 退回官方例子里用过的那条，避免给出不能用的值
        unit_type = "AlliedAntiVehicleVehicleTech3"
    lines = [
        "-- 由 tools/sync-hs-config.py 自动生成（源：Ra3Trainer.settings.json）",
        "-- 游戏里输入  hs reload  即可生效；中文只用注释，值一律 ASCII。",
    ]
    if raw_id is not None:
        lines.append(f"-- 面板预设的数字码（Lua 库用不上，仅备忘）: {raw_id}")
    lines += [
        "return {",
        f"  money = {money},",
        f"  power = {power},",
        f"  sp = {sp},",
        '  waypoint = "SELF_START",',
        f'  unit_type = "{unit_type}",',
    ]
    if command:
        # 第三条触发：游戏里 update.lua 每 30 帧读这个文件，command_id 变了就执行一次
        lines.append(f'  command = "{command}",')
        lines.append(f"  command_id = {int(time.time()) % 2000000000},")
        lines.append(f'  command_player = "{player or "Player_1"}",')
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--settings", default=SETTINGS)
    parser.add_argument("--target", default=TARGET)
    parser.add_argument("--show", action="store_true")
    parser.add_argument("--command", help="写一条命令给游戏执行（money/power/sp/unit/…）")
    parser.add_argument("--player", help="命令作用在哪个玩家（默认 Player_1）")
    args = parser.parse_args()

    if not os.path.exists(args.settings):
        print(f"找不到面板设置: {args.settings}")
        return 1
    settings = read_settings(args.settings)
    text = build_lua(settings, args.command, args.player)

    if args.show:
        print("面板设置里的资源数值:", json.dumps(settings.get("ResourceValues", {}), ensure_ascii=False))
        print("当前 hs-config.lua:")
        if os.path.exists(args.target):
            print(io.open(args.target, encoding="utf-8").read())
        else:
            print("  (不存在)")
        return 0

    os.makedirs(os.path.dirname(args.target), exist_ok=True)
    with io.open(args.target, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print(f"已写入 {args.target}")
    print(text)
    print("游戏里输入:  hs reload")
    return 0


if __name__ == "__main__":
    sys.exit(main())
