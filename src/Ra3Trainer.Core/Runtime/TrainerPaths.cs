using System;
using System.Collections.Generic;
using System.IO;

namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// 产品级数据目录：设置 / 单位清单 / 名字缓存 / 日志 / 现场记录**一律写到用户目录**，
/// 不写自己 exe 旁边 —— 否则用户把程序装到 <c>C:\\Program Files</c> 下（Windows 不允许普通程序往那里写）
/// 就会设置存不住、清单记不了、日志为空。
///
/// <para><b>兼容口径</b>：老版本把文件放在 exe 旁边。为了让已经在用的人**一行数据都不丢**，
/// <see cref="Resolve"/> 的规则是「<b>老位置已经有这个文件就继续用它</b>，没有才落到用户目录」。
/// 新装的用户（exe 旁边没有这些文件，例如装在 Program Files）自然全部落到用户目录。</para>
/// </summary>
public static class TrainerPaths
{
    /// <summary>用户数据目录名（%LocalAppData% 下）。</summary>
    public const string ProductFolderName = "Ra3Trainer";

    /// <summary>用户数据根目录，例：C:\\Users\\me\\AppData\\Local\\Ra3Trainer。</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductFolderName);

    /// <summary>运行日志目录。</summary>
    public static string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>崩溃/现场记录目录。</summary>
    public static string EvidenceDirectory => Path.Combine(DataRoot, "crashdumps");

    /// <summary>只读快照 / 归属记录等产物目录。</summary>
    public static string ArtifactsDirectory => Path.Combine(DataRoot, "artifacts");

    /// <summary>
    /// 决定某个数据文件该用哪条路径：老位置（exe 旁边）已有就沿用，否则用用户数据目录。
    /// 只会**读**老位置是否存在，不会改动它。
    /// </summary>
    public static string Resolve(string? baseDirectory, string fileName)
    {
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            try
            {
                var legacy = Path.Combine(baseDirectory, fileName);
                if (File.Exists(legacy)) return legacy;
            }
            catch
            {
                // 路径非法 / 无权限：当作没有老文件，走用户目录。
            }
        }

        EnsureDirectory(DataRoot);
        return Path.Combine(DataRoot, fileName);
    }

    /// <summary>
    /// 需要「两边都要认」的场景（例如读所有 <c>units-*.txt</c>）：老位置在前、用户目录在后，去重。
    /// </summary>
    public static IEnumerable<string> SearchDirectories(string? baseDirectory)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(baseDirectory) && seen.Add(baseDirectory))
        {
            yield return baseDirectory;
        }

        if (seen.Add(DataRoot))
        {
            yield return DataRoot;
        }
    }

    /// <summary>建目录（失败不抛：调用方按「写不进去也不影响用」处理）。</summary>
    public static void EnsureDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 忽略：写盘失败时由各自的日志路径记录，绝不因此让程序起不来。
        }
    }
}
