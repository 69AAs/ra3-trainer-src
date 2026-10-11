using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ra3Trainer.Core.Runtime;

/// <summary>A module the target process had loaded when the tool attached (raw data, no mod names).</summary>
public sealed record TargetModuleEntry(string Name, string Path, string FileVersion);

/// <summary>One hook site as written into the evidence archive.</summary>
public sealed record HookSiteFileEntry(
    string Address,
    string SectionTitle,
    string State,
    string ExpectedHex,
    string ActualHex);

public sealed record CrashEvidenceRequest(
    string TargetProcessName,
    int? ProcessId,
    string? TargetModulePath,
    string? SessionLogPath,
    string? ExitDescription,
    IReadOnlyList<string> Notes,
    CrashContextSnapshot? Context = null,
    string? ProbeDirectory = null);

public sealed record CrashEvidenceResult(
    string ArchivePath,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Bundles the evidence needed to explain "the game died while the tool was attached" into one zip:
/// the tool's own session log, the hook-site check, the module list of the target process, the
/// Windows Error Reporting report for that executable, the entries the system logged about it, and
/// whichever log files under the game directory were modified right before the failure.
/// Everything is keyed off the target executable name and paths taken at attach time, so no mod is
/// baked in — swap mods and the same collector still produces a usable bundle.
/// </summary>
public sealed class CrashEvidenceCollector
{
    private static readonly string[] RecentFileExtensions = [".log", ".txt", ".ini", ".cfg", ".json"];

    private readonly string _werRoot;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string?> _windowsEventLogReader;
    private readonly TimeSpan _recentWindow;
    private readonly int _maxRecentFiles;
    private readonly long _maxRecentFileBytes;
    private readonly int _maxWerReports;
    private readonly Func<string, IReadOnlyList<string>> _probeFiles;

    public CrashEvidenceCollector(
        string? werRoot = null,
        Func<DateTimeOffset>? clock = null,
        Func<string?>? windowsEventLogReader = null,
        TimeSpan? recentWindow = null,
        int maxRecentFiles = 15,
        long maxRecentFileBytes = 2 * 1024 * 1024,
        int maxWerReports = 3,
        Func<string, IReadOnlyList<string>>? probeFiles = null)
    {
        _werRoot = werRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Windows",
            "WER");
        _clock = clock ?? (() => DateTimeOffset.Now);
        _windowsEventLogReader = windowsEventLogReader ?? ReadApplicationEventLog;
        _recentWindow = recentWindow ?? TimeSpan.FromMinutes(60);
        _maxRecentFiles = maxRecentFiles;
        _maxRecentFileBytes = maxRecentFileBytes;
        _maxWerReports = maxWerReports;
        _probeFiles = probeFiles ?? ProbeLog.Files;
    }

    public CrashEvidenceResult Collect(
        string outputDirectory,
        CrashEvidenceRequest request,
        IReadOnlyList<HookSiteFileEntry> hookSites,
        IReadOnlyList<TargetModuleEntry> modules)
    {
        var included = new List<string>();
        var warnings = new List<string>();
        var staging = Path.Combine(Path.GetTempPath(), $"ra3trainer-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            CopySessionLog(staging, request, included, warnings);
            if (request.ProbeDirectory is { } probeDirectory)
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(staging, "trainer", "probe"));
                    foreach (var file in _probeFiles(probeDirectory))
                    {
                        try
                        {
                            CopyFileShared(file, Path.Combine(staging, "trainer", "probe", Path.GetFileName(file)));
                            included.Add("trainer/probe/" + Path.GetFileName(file));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { warnings.Add("复制采样失败：" + ex.Message); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { warnings.Add("读取采样目录失败，其余取证继续：" + ex.Message); }
            }
            if (request.Context is { } context)
            {
                WriteJson(Path.Combine(staging, "trainer", "context.json"), context, included, "trainer/context.json");
                File.WriteAllText(Path.Combine(staging, "trainer", "context.txt"), context.ToReadableText(), Encoding.UTF8);
                included.Add("trainer/context.txt");
            }
            WriteJson(Path.Combine(staging, "trainer", "hooks.json"), hookSites, included, "trainer/hooks.json");
            WriteJson(Path.Combine(staging, "trainer", "modules.json"), modules, included, "trainer/modules.json");
            CopyWerReports(staging, request, included, warnings);
            CopyRecentTargetFiles(staging, request, included, warnings);
            CopyWindowsEventLog(staging, request, included, warnings);
            WriteSummary(staging, request, hookSites, modules, included, warnings);

            Directory.CreateDirectory(outputDirectory);
            var archivePath = Path.Combine(
                outputDirectory,
                $"evidence-{Sanitize(request.TargetProcessName)}-{_clock():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");

            ZipFile.CreateFromDirectory(staging, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
            included.Insert(0, Path.GetFileName(archivePath));
            return new CrashEvidenceResult(archivePath, included, warnings);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private void WriteSummary(
        string staging,
        CrashEvidenceRequest request,
        IReadOnlyList<HookSiteFileEntry> hookSites,
        IReadOnlyList<TargetModuleEntry> modules,
        IReadOnlyList<string> included,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("RA3 Trainer 取证包");
        builder.AppendLine("上下文快照不是进程转储，不能据此证明修改器无责。WER 按进程名收集，可能属于旧会话或尚未生成，需核对时间与身份。");
        // 用注入的时钟：归档名用的是 _clock()，摘要却写死 Now ⇒ 固定时钟的测试/复现里两者对不上。
        builder.AppendLine($"生成时间      : {_clock():yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"目标进程      : {request.TargetProcessName}");
        builder.AppendLine($"进程 ID       : {request.ProcessId?.ToString() ?? "(未知)"}");
        builder.AppendLine($"模块路径      : {request.TargetModulePath ?? "(未知)"}");
        builder.AppendLine($"退出描述      : {request.ExitDescription ?? "(未知)"}");
        builder.AppendLine($"会话日志      : {request.SessionLogPath ?? "(无)"}");
        builder.AppendLine();
        builder.AppendLine("hook 点状态（与 patch 清单的原始字节比对，与 mod 无关）:");
        if (hookSites.Count == 0)
        {
            builder.AppendLine("  (未采集：附加未成功或未执行检查)");
        }
        else
        {
            foreach (var site in hookSites)
            {
                builder.AppendLine($"  {site.Address,-28} {site.SectionTitle,-28} {site.State}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"目标进程附加时加载的模块（{modules.Count} 个，前 40）:");
        foreach (var module in modules.Take(40))
        {
            builder.AppendLine($"  {module.Name,-28} {module.FileVersion,-20} {module.Path}");
        }

        if (request.Notes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("备注:");
            foreach (var note in request.Notes)
            {
                builder.AppendLine($"  - {note}");
            }
        }

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("采集时的告警:");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"  - {warning}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("包内文件:");
        foreach (var item in included)
        {
            builder.AppendLine($"  {item}");
        }

        File.WriteAllText(Path.Combine(staging, "summary.txt"), builder.ToString(), Encoding.UTF8);
    }

    private static void CopySessionLog(
        string staging,
        CrashEvidenceRequest request,
        List<string> included,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(request.SessionLogPath))
        {
            warnings.Add("没有会话日志路径。");
            return;
        }

        Directory.CreateDirectory(Path.Combine(staging, "trainer"));
        if (File.Exists(request.SessionLogPath))
        {
            var target = Path.Combine(staging, "trainer", Path.GetFileName(request.SessionLogPath));
            try
            {
                CopyFileShared(request.SessionLogPath, target);
                included.Add($"trainer/{Path.GetFileName(request.SessionLogPath)}");
            }
            catch (IOException ex)
            {
                warnings.Add($"复制会话日志失败：{ex.Message}");
            }
            return;
        }

        // The log lives next to the executable; take whatever sessions are there instead.
        var directory = Path.GetDirectoryName(request.SessionLogPath);
        if (directory is null || !Directory.Exists(directory))
        {
            warnings.Add("会话日志不存在。");
            return;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(directory, $"{TrainerSessionLog.FileNamePrefix}*.jsonl")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(3))
        {
            try
            {
                CopyFileShared(file, Path.Combine(staging, "trainer", Path.GetFileName(file)));
                included.Add($"trainer/{Path.GetFileName(file)}");
                copied++;
            }
            catch (IOException ex)
            {
                warnings.Add($"复制 {Path.GetFileName(file)} 失败：{ex.Message}");
            }
        }

        // 目录存在但没有可用的 session-*.jsonl：原来这里静默空手而归，
        // 取证包里没有会话日志却看不出为什么。补一条告警让缺失可见。
        if (copied == 0)
        {
            warnings.Add("会话日志目录里没有可用的 session-*.jsonl，取证包不含会话日志。");
        }
    }

    private static void WriteJson<T>(string path, T payload, List<string> included, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            Encoding.UTF8);
        included.Add(label);
    }

    /// <summary>
    /// Copies a file another process (or this one) may still hold open. Plain File.Copy shares only
    /// FileShare.Read and therefore fails with a sharing violation while the tool is running — which
    /// is precisely when evidence gets collected.
    /// </summary>
    private static void CopyFileShared(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private void CopyWerReports(
        string staging,
        CrashEvidenceRequest request,
        List<string> included,
        List<string> warnings)
    {
        if (!Directory.Exists(_werRoot))
        {
            warnings.Add($"系统崩溃报告目录不存在：{_werRoot}");
            return;
        }

        var reports = new List<DirectoryInfo>();
        foreach (var subDirectory in new[] { "ReportArchive", "ReportQueue" })
        {
            var directory = Path.Combine(_werRoot, subDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                reports.AddRange(new DirectoryInfo(directory)
                    .EnumerateDirectories()
                    .Where(report => report.Name.Contains(request.TargetProcessName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(report => report.LastWriteTimeUtc));
            }
            catch (IOException ex)
            {
                warnings.Add($"枚举 {subDirectory} 失败：{ex.Message}");
            }
        }

        if (reports.Count == 0)
        {
            warnings.Add($"系统崩溃报告里没有该进程的记录（{request.TargetProcessName}）。");
            return;
        }

        foreach (var report in reports.Take(_maxWerReports))
        {
            var destination = Path.Combine(staging, "wer", report.Name);
            Directory.CreateDirectory(destination);
            FileInfo[] files;
            try
            {
                // WER 会主动清理 ReportQueue：枚举和复制之间目录可能已被删/轮转。
                // 单条报告不可访问只记一条告警、continue，不能中断其余报告的采集。
                files = report.EnumerateFiles().ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"枚举崩溃报告 {report.Name} 失败：{ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    File.Copy(file.FullName, Path.Combine(destination, file.Name), overwrite: true);
                    included.Add($"wer/{report.Name}/{file.Name}");
                }
                catch (IOException ex)
                {
                    warnings.Add($"复制 {file.Name} 失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Grabs log files that changed right before the failure, searching the target module's own
    /// directory and its parent (shallow, extension- and time-filtered). Identifies files by
    /// freshness instead of by mod name, so it works with whatever logs a mod writes.
    /// </summary>
    private void CopyRecentTargetFiles(
        string staging,
        CrashEvidenceRequest request,
        List<string> included,
        List<string> warnings)
    {
        // 同名嵌套根目录会重复命中同一个候选文件：按源路径去重（见下方 continue）。
        var collectedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roots = new List<string>();
        if (request.TargetModulePath is { Length: > 0 } modulePath)
        {
            var moduleDirectory = Path.GetDirectoryName(modulePath);
            if (!string.IsNullOrEmpty(moduleDirectory) && Directory.Exists(moduleDirectory))
            {
                roots.Add(moduleDirectory);
                var parent = Path.GetDirectoryName(moduleDirectory);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    roots.Add(parent);
                }
            }
        }

        if (roots.Count == 0)
        {
            warnings.Add("不知道目标模块路径，跳过最近日志采集。");
            return;
        }

        var cutoff = _clock() - _recentWindow;
        var candidates = new List<(string Path, DateTime LastWrite)>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var file in EnumerateShallow(root, maxDepth: 2))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length > _maxRecentFileBytes)
                    {
                        continue;
                    }

                    if (!RecentFileExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (info.LastWriteTime >= cutoff.LocalDateTime)
                    {
                        candidates.Add((info.FullName, info.LastWriteTime));
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        if (candidates.Count == 0)
        {
            warnings.Add($"目标目录里 {_recentWindow.TotalMinutes:F0} 分钟内没有改动过的日志文件。");
            return;
        }

        foreach (var candidate in candidates.OrderByDescending(item => item.LastWrite).Take(_maxRecentFiles))
        {
            // 前缀匹配必须落在目录边界上，并且取最长匹配：
            // 否则 roots=["C:\game"] 会吞掉 "C:\games\x.log"，GetRelativePath 出来的是 "..\games\x.log"，
            // 打包时就能把根目录之外的文件塞进归档（路径穿越）。
            // 嵌套根目录（模块目录 + 它的父目录）会命中同一个候选文件：同一份内容收两遍，
            // 归档里出现两份、清单里也重复。按"源路径"去重。
            if (!collectedSources.Add(Path.GetFullPath(candidate.Path)))
            {
                continue;
            }

            var root = roots
                .Where(item => IsUnderRoot(candidate.Path, item))
                .OrderByDescending(item => item.Length)
                .FirstOrDefault();
            if (root is null)
            {
                warnings.Add($"{candidate.Path} 不属于任何已知根目录，未打包。");
                continue;
            }

            var relative = Path.GetRelativePath(root, candidate.Path);
            var destination = Path.Combine(staging, "target-logs", Sanitize(Path.GetFileName(root)), relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CopyFileShared(candidate.Path, destination);
                included.Add($"target-logs/{Sanitize(Path.GetFileName(root))}/{relative}");
            }
            catch (IOException ex)
            {
                warnings.Add($"复制 {candidate.Path} 失败：{ex.Message}");
            }
        }
    }

    // internal 供单元测试直接钉边界（测试程序集有 InternalsVisibleTo）。
    internal static bool IsUnderRoot(string path, string root)
    {
        if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == root.Length)
        {
            return true;
        }

        var boundary = path[root.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private void CopyWindowsEventLog(
        string staging,
        CrashEvidenceRequest request,
        List<string> included,
        List<string> warnings)
    {
        string? text;
        try
        {
            text = _windowsEventLogReader();
        }
        catch (Exception ex)
        {
            warnings.Add($"读取系统事件日志失败：{ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            warnings.Add("未取得系统事件日志条目（可能权限不足或命令不可用）。");
            return;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var picked = new List<string>();
        var lastWritten = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(request.TargetProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var from = Math.Max(0, index - 12);
            var to = Math.Min(lines.Length - 1, index + 12);
            // 相邻命中行的窗口会重叠：只写"上次之后"的部分，否则同一行会被重复追加，
            // 报告里看着像是出现了多次（假线索）。
            if (from <= lastWritten)
            {
                from = lastWritten + 1;
            }

            for (var inner = from; inner <= to; inner++)
            {
                picked.Add(lines[inner]);
            }

            lastWritten = to;
            picked.Add(new string('-', 60));
        }

        var body = picked.Count > 0
            ? string.Join(Environment.NewLine, picked)
            : text[..Math.Min(text.Length, 40000)];
        var path = Path.Combine(staging, "windows-eventlog", "application.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body, Encoding.UTF8);
        included.Add("windows-eventlog/application.txt");
    }

    private static IEnumerable<string> EnumerateShallow(string root, int maxDepth)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = depth < maxDepth ? Directory.GetDirectories(directory) : [];
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in children)
            {
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static string? ReadApplicationEventLog()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
        if (!File.Exists(executable))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(executable, "qe Application /c:200 /rd:true /f:text")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var reader = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            if (!reader.Wait(TimeSpan.FromSeconds(5)))
            {
                return null;
            }

            var output = reader.Result;
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "target" : builder.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
