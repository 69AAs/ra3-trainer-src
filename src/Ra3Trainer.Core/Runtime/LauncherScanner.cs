namespace Ra3Trainer.Core.Runtime;

/// <summary>启动器的种类：玩 mod 的人通常点的是"mod 启动器"，不是游戏本体。</summary>
public enum LauncherKind
{
    /// <summary>mod 自己的启动器（日冕 CoronaLauncher 等）</summary>
    ModLauncher,

    /// <summary>对战平台/战网类启动器（Ra3BattlezoneHub 等）</summary>
    PlatformLauncher,

    /// <summary>游戏本体 RA3.exe</summary>
    GameExecutable,

    /// <summary>其它可执行文件（最后兜底）</summary>
    Other
}

public sealed record LauncherCandidate(string Path, string DisplayName, LauncherKind Kind);

/// <summary>
/// 扫描本机的"启动方式"候选：mod 启动器 / 平台启动器 / 游戏本体。
/// 用户反馈（2026-09-28）："玩 mod 都不是直接点 RA3.exe，是用专门的 mod 启动器" ——
/// 所以界面不该只让人填 RA3.exe，而应该把扫到的候选列出来挑。
/// </summary>
public static class LauncherScanner
{
    private static readonly string[] RelevantTokens =
    [
        "ra3", "red alert", "redalert", "corona", "hongjing", "battlezone", "battlenet"
    ];

    private static readonly string[] ExcludedTokens =
    [
        "uninst", "aria2c", "dotnet", "zstd", "patchapplier", "registry-helper",
        "cnc3reader", "videoparser", "ra3tool", "fpsunlocking", "ra3gametimer",
        "ra3trainer", "vcredist", "dxsetup", "redist", "updater", "crashreport"
    ];

    /// <summary>从若干根目录出发找可执行文件（限深度，避免全盘扫描）。</summary>
    public static IReadOnlyList<LauncherCandidate> Scan(IEnumerable<string> roots, int maxDepth = 2)
    {
        var found = new Dictionary<string, LauncherCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in EnumerateExecutables(root, maxDepth))
            {
                var name = Path.GetFileName(path);
                if (IsExcluded(name))
                {
                    continue;
                }

                var kind = Classify(name);
                if (kind == LauncherKind.Other)
                {
                    continue;
                }

                // 在这里就按相关性挡掉：原来只在最后对结果做一次 Where 过滤，
                // 而"一个相关的都没有"时会回退成未过滤的全量 —— 机器上只装了
                // NewWorldBuilderLauncher.exe 时，选择器就会推荐地图编辑器。
                if (!IsRelevant(path))
                {
                    continue;
                }

                if (!found.ContainsKey(path))
                {
                    found[path] = new LauncherCandidate(path, BuildDisplayName(path, kind), kind);
                }
            }
        }

        return found.Values
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 相关性过滤：本机实测直接扫描会带出无关启动器（WeGame、Rockstar、Steam…），
    /// 所以只保留路径里带 RA3/红警/日冕/战网这类关键词的候选。
    /// </summary>
    public static bool IsRelevant(string path)
    {
    var lower = path.ToLowerInvariant();
        if (lower.Contains("newworldbuilder") || lower.Contains("wblauncher"))
        {
            return false;   // 地图编辑器，不是启动游戏的
        }

        return RelevantTokens.Any(token => lower.Contains(token));
    }

    /// <summary>按文件名判断种类。</summary>
    public static LauncherKind Classify(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        if (name.Contains("corona") || name.Contains("launcher") || name.Contains("quickload"))
        {
            return LauncherKind.ModLauncher;
        }

        if (name.Contains("hub") || name.Contains("battlezone") || name.Contains("battlenet") || name.Contains("platform"))
        {
            return LauncherKind.PlatformLauncher;
        }

        if (name == "ra3.exe")
        {
            return LauncherKind.GameExecutable;
        }

        return LauncherKind.Other;
    }

    /// <summary>
    /// 由当前配置推导要扫描的根目录：路径所在目录 → 各级祖先 → 每级祖先下的子目录（深度 1）。
    /// 这样 D:\Games\Red Alert 3\RA3.exe 能带出同级的 D:\Games\CoronaLauncher\CoronaLauncher.exe。
    /// </summary>
    public static IReadOnlyList<string> BuildRoots(string? launcherPath, int maxAncestors = 3, int maxRoots = 60)
    {
        var roots = new List<string>();
        var current = (launcherPath ?? string.Empty).Trim().Trim('"');

        // 没配置过路径时也要能找到启动器：扫各固定盘的一级目录，只留名字相关的（如 D:\Games）
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(drive.RootDirectory.FullName))
                {
                    if (IsRelevant(directory))
                    {
                        Add(roots, directory, maxRoots);
                    }
                }
            }
        }
        catch
        {
            // 盘符枚举失败不影响后面的路径推导
        }

        if (current.Length > 0)
        {
            var directory = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            var level = 0;
            while (!string.IsNullOrEmpty(directory) && level <= maxAncestors)
            {
                Add(roots, directory, maxRoots);

                try
                {
                    // 盘符根目录（如 E:\）不展开：那会把整个盘的软件目录都扫一遍
                    var isDriveRoot = string.Equals(
                        Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar),
                        directory.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);

                    if (!isDriveRoot)
                    {
                        foreach (var child in Directory.EnumerateDirectories(directory))
                        {
                            Add(roots, child, maxRoots);
                            if (roots.Count >= maxRoots)
                            {
                                return roots;
                            }
                        }
                    }
                }
                catch
                {
                    // 无权限的目录跳过
                }

                directory = Path.GetDirectoryName(directory);
                level++;
            }
        }

        return roots;

        static void Add(List<string> list, string path, int maxRoots)
        {
            if (list.Count < maxRoots && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(path);
            }
        }
    }

    private static readonly string[] GenericFolders = ["tools", "bin", "game", "app", "client", "resources"];

    private static string BuildDisplayName(string path, LauncherKind kind)
    {
        // 下拉框宽度有限：用"最能认出是谁"的短名字，完整路径放在 ToolTip 里
        var stem = Path.GetFileNameWithoutExtension(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var parent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path) ?? string.Empty) ?? string.Empty);

        var label = folder.Length == 0
            ? stem
            : GenericFolders.Contains(folder.ToLowerInvariant()) && parent.Length > 0
                ? $"{parent}\\{stem}"
                : folder;

        var prefix = kind switch
        {
            LauncherKind.ModLauncher => "mod 启动器",
            LauncherKind.PlatformLauncher => "平台启动器",
            LauncherKind.GameExecutable => "游戏本体",
            _ => "程序"
        };

        return $"{prefix} · {label}";
    }

    private static bool IsExcluded(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        return ExcludedTokens.Any(token => name.Contains(token));
    }

    private static IEnumerable<string> EnumerateExecutables(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.exe");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                queue.Enqueue((child, depth + 1));
            }
        }
    }
}
