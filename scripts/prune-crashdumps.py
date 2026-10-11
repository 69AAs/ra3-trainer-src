#!/usr/bin/env python3
"""清理产品 crashdumps 目录，套用产品自身的保留策略（与
src/Ra3Trainer.Core/Runtime/EvidenceRetention.cs 一致）。

策略：
- 只处理产品命名的三类产物：evidence-*.zip / exit-context-*.txt / probe-export-*.zip
  （同目录里用户的文件一律不碰）。
- 每个模式独立：按最后写入时间倒序（平局按名字），最新 10 份保留。
- 超过 30 天的一律不保留（age 是硬上限）。
- 当天（本地日历日）产生的一律保留。

先打印【待删清单】（模式 / 文件名 / 大小 / 时间），再（--apply）执行删除；
删完打印每个模式删除/保留的份数与字节数。默认 dry-run，不加 --apply 不动文件。
"""
import fnmatch
import os
import sys
import time

KEEP_NEWEST = 10
MAX_AGE_SECONDS = 30 * 24 * 3600
PATTERNS = ["evidence-*.zip", "exit-context-*.txt", "probe-export-*.zip"]


def fmt(ts):
    return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts))


def human(n):
    return f"{n / 1024 / 1024:.2f} MB"


def plan(directory):
    now = time.time()
    today = time.strftime("%Y-%m-%d", time.localtime(now))
    to_delete = []
    kept = []
    for pat in PATTERNS:
        names = [n for n in os.listdir(directory) if fnmatch.fnmatch(n, pat)]
        entries = []
        for name in names:
            path = os.path.join(directory, name)
            st = os.stat(path)
            entries.append((st.st_mtime, name, st.st_size, path))
        entries.sort(key=lambda e: (-e[0], e[1]))
        for index, (mtime, name, size, path) in enumerate(entries):
            local_day = time.strftime("%Y-%m-%d", time.localtime(mtime))
            written_today = local_day == today
            too_old = (now - mtime) > MAX_AGE_SECONDS
            beyond_keep = index >= KEEP_NEWEST
            if written_today:
                kept.append((pat, name, size, "当天产生"))
            elif too_old or beyond_keep:
                reason = []
                if too_old:
                    reason.append("超30天")
                if beyond_keep:
                    reason.append(f"超出最新{KEEP_NEWEST}份")
                to_delete.append((pat, name, size, mtime, "+".join(reason)))
            else:
                kept.append((pat, name, size, f"最新{KEEP_NEWEST}份内且<30天"))
    return to_delete, kept


def main():
    if len(sys.argv) < 2:
        print("usage: prune-crashdumps.py <crashdumps-dir> [--apply]")
        return 2
    directory = sys.argv[1]
    apply = "--apply" in sys.argv
    if not os.path.isdir(directory):
        print(f"目录不存在: {directory}")
        return 2

    to_delete, kept = plan(directory)

    print(f"目录: {directory}")
    print(f"时间: {fmt(time.time())}  策略: 每个模式保留最新 {KEEP_NEWEST} 份 / 不保留 >30 天 / 当天一律保留")
    print()
    print(f"=== 待删清单（{len(to_delete)} 份 / {human(sum(e[2] for e in to_delete))}）===")
    if not to_delete:
        print("  (空 —— 现有条目全部落在保留范围内)")
    for pat, name, size, mtime, reason in to_delete:
        print(f"  [{pat}] {name}  {human(size)}  {fmt(mtime)}  ({reason})")
    print()
    print(f"=== 保留清单（{len(kept)} 份 / {human(sum(e[2] for e in kept))}）===")
    for pat, name, size, reason in kept:
        print(f"  [{pat}] {name}  {human(size)}  ({reason})")
    print()

    removed_bytes = 0
    removed_count = 0
    if apply:
        for pat, name, size, mtime, reason in to_delete:
            try:
                os.remove(os.path.join(directory, name))
                removed_count += 1
                removed_bytes += size
            except OSError as exc:  # 被占用/无权限就留到下次
                print(f"  删除失败(留到下次): {name} -> {exc}")
    print(f"实际删除: {removed_count} 份 / {human(removed_bytes)}"
          f"{'' if apply else '   [dry-run：未删除，加 --apply 才执行]'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
