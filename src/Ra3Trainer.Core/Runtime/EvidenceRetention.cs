namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// 诊断产物的保留策略。证据包/退出快照"出事时才有用"，但不设上限会一直涨 ——
/// 真机实测 crashdumps 曾到 405MB（36 个证据包 zip，每个约 15MB）。
/// 策略保守：保留最新 N 份，且不保留超过 maxAge 天的；当天产生的一律保留。
/// 删除清单返回给调用方记日志 —— 不静默删东西。
/// </summary>
public static class EvidenceRetention
{
    public const int DefaultKeepNewest = 10;

    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    /// <summary>
    /// 产品自己产出的诊断产物命名（连同写进会话日志用的中文名）。清理只认这几个模式，
    /// 同目录里用户的文件（settings 备份、日志等）一根汗毛都不碰。
    /// 采样导出包 probe-export-*.zip 原先没被覆盖 ⇒ crashdumps 里它只增不减，一并纳入。
    /// </summary>
    public static readonly (string Pattern, string Label)[] ProductArtifacts =
    [
        ("evidence-*.zip", "证据包"),
        ("exit-context-*.txt", "退出快照"),
        ("probe-export-*.zip", "采样导出包")
    ];

    /// <summary>
    /// 只处理 <paramref name="searchPattern"/> 命中的条目（精确到 evidence-*.zip / exit-context-*.txt /
    /// probe-export-*.zip，不碰同目录里别的东西）。删除失败（被占用/无权限）就留到下次，不抛。
    /// </summary>
    public static IReadOnlyList<string> Prune(string directory, string searchPattern, DateTimeOffset now,
        int keepNewest = DefaultKeepNewest, TimeSpan? maxAge = null)
    {
        var limit = maxAge ?? DefaultMaxAge;
        var removed = new List<string>();
        if (!Directory.Exists(directory)) return removed;
        if (keepNewest < 0) keepNewest = 0;

        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(directory).GetFileSystemInfos(searchPattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 只兜运行时 IO/权限失败；searchPattern 非法（ArgumentException）是调用方的编程错误，
            // 吞掉会让“永远没清理”无声无息，正是本类要防的问题。
            return removed;
        }

        // 时间戳并列（批量复制/恢复会给出相同时间）时再加名字次序，保留结果才确定，
        // 否则谁来吃掉 keepNewest 之外的槽位取决于文件系统枚举次序，每次都可能不同。
        var ordered = entries
            .OrderByDescending(entry => entry.LastWriteTimeUtc)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        // 当天产生的诊断产物一律保留：出事当天用户最可能马上要看它，哪怕 keepNewest / maxAge
        // 被调得很紧也不删。（按本地日历日判定——"当天"是用户这边的当天。）
        var today = now.ToLocalTime().Date;
        for (var i = 0; i < ordered.Length; i++)
        {
            var entry = ordered[i];
            if (ToLocalDate(entry.LastWriteTimeUtc) == today) continue;
            var tooOld = now - entry.LastWriteTimeUtc > limit;
            var beyondKeep = i >= keepNewest;
            if (!tooOld && !beyondKeep) continue;

            try
            {
                if (entry is DirectoryInfo nested)
                {
                    nested.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }

                removed.Add(entry.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 被占用就留到下次启动；保留策略本身不该让任何东西失败。
            }
        }

        return removed;
    }

    private static DateTime ToLocalDate(DateTime lastWrite)
    {
        var utc = lastWrite.Kind == DateTimeKind.Utc ? lastWrite : DateTime.SpecifyKind(lastWrite, DateTimeKind.Utc);
        return utc.ToLocalTime().Date;
    }
}
