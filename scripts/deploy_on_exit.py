"""等改造器的实例退出后，把「新包」顶到正式路径上（不改动运行中的实例）。

为什么需要它：单文件 WPF 应用运行时还会按需读自己的文件 —— 直接覆盖正在跑的那份 exe，
他那扇窗口会当场变成一片纯黑（实测 nearBlack=100%）。所以正确做法是：
新包先落在旁边（`<名字>-新版待覆盖-*.exe`），等他的实例真的退出、再原子替换。

用法（用 pythonw 跑 = 完全无窗口）：
    pythonw scripts/deploy_on_exit.py <新包> <目标exe> <日志> <新包sha256> [最多等几小时]

行为：
  · 每 15 秒查一次「路径落在目标目录里」的实例（只读进程表，绝不结束任何进程）；
  · 一个都不剩时用 os.replace 原子替换（同盘 = 一条目录项改名），随后回读 sha 核对；
  · 替换失败（还被占着）就继续等；查不出进程状态时**按有人在跑处理**（宁可等）。
"""

import hashlib
import os
import subprocess
import sys
import time

CREATE_NO_WINDOW = 0x08000000


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def instances_using(target):
    """只数「可执行文件路径落在目标同一目录」的实例；查不出来就当成 1（按有人在跑处理）。"""
    # 注意：WMI 的 ExecutablePath 用反斜杠 —— 传进来的是正斜杠路径，必须归一化，
    # 否则 -like 永不匹配、查询「成功」地返回 0（我第一版就这么踩了：他在跑却被判成没人跑）。
    folder = os.path.dirname(target).replace("/", "\\").replace("'", "''")
    script = (
        "Get-CimInstance Win32_Process -Filter \"Name='Ra3Trainer.App.exe'\" | "
        f"Where-Object {{ $_.ExecutablePath -like '*{folder}*' }} | "
        "Measure-Object | Select-Object -ExpandProperty Count"
    )
    try:
        done = subprocess.run(
            ["powershell", "-NoProfile", "-Command", script],
            capture_output=True, text=True, timeout=60, creationflags=CREATE_NO_WINDOW)
        text = (done.stdout or "").strip().splitlines()
        return int(text[-1]) if text and text[-1].isdigit() else 1
    except Exception:
        return 1


def main():
    if len(sys.argv) < 5:
        print(__doc__)
        return 2
    staged, target, log_path, expected_sha = sys.argv[1:5]
    max_hours = float(sys.argv[5]) if len(sys.argv) > 5 else 8.0
    # 轮询间隔（秒）。默认 15：他关掉后**很快**重开时，15 秒的采样窗口会整整错过
    #（2026-10-11 实测：19:40:18 重开，守望从头到尾没采到"零实例"，新包一直没换上）。
    poll_seconds = float(sys.argv[6]) if len(sys.argv) > 6 else 15.0

    def note(message):
        with open(log_path, "a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {message}\n")

    if sha256(staged) != expected_sha.lower():
        note(f"ABORT staged sha mismatch: {staged}")
        return 3
    note(f"ARMED staged={staged} target={target} sha={expected_sha[:12]} maxHours={max_hours}")

    deadline = time.time() + max_hours * 3600
    waiting_noted = False
    while time.time() < deadline:
        running = instances_using(target)
        if running and not waiting_noted:
            note("WAIT 检测到实例仍在运行，等它退出（只等，不碰进程）")
            waiting_noted = True
        if running == 0:
            try:
                os.replace(staged, target)
            except OSError as error:
                # 文件还被占着（实例刚退出/正在启动）—— 继续等，绝不强来。
                if not waiting_noted:
                    note(f"WAIT replace 被拒（{error}），继续等")
                    waiting_noted = True
                time.sleep(poll_seconds)
                continue
            landed = sha256(target)
            if landed == expected_sha.lower():
                note(f"DONE swapped in {target} sha={landed[:12]}")
            else:
                note(f"WARN landed sha={landed} expected={expected_sha}")
            return 0
        time.sleep(poll_seconds)
    note("TIMEOUT 等不到实例退出；新包仍在待覆盖位置，未替换")
    return 4


if __name__ == "__main__":
    raise SystemExit(main())
