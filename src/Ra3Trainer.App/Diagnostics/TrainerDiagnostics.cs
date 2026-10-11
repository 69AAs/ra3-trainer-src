using System.Diagnostics;
using System.IO;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.App.Diagnostics;

/// <summary>
/// App-side diagnostics: the one place that owns the session log, the module snapshot taken when
/// attaching, the last hook-site check and the crash-evidence collector.
/// Mod-agnostic by construction — it works only from the target executable name, its module path
/// and the patch manifest, so swapping mods needs no changes here.
/// </summary>
public sealed class TrainerDiagnostics : IDisposable
{
    private readonly CrashEvidenceCollector _collector = new();
    private readonly List<TargetModuleEntry> _modules = [];
    private HookSiteReport? _hookReport;

    public TrainerDiagnostics(string baseDirectory)
    {
        LogDirectory = ResolveLogDirectory(baseDirectory);
        Log = TrainerSessionLog.Open(LogDirectory);
        EvidenceDirectory = TrainerPaths.EvidenceDirectory;
        PruneEvidence();
        Log.Write("app-start", new
        {
            version = typeof(TrainerDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown",
            logPath = Log.FilePath,
            logAvailable = Log.IsAvailable
        });
    }

    public TrainerSessionLog Log { get; }
    public ActionEvidenceIdentity? ActionIdentity { get; private set; }

    public void RecordActionIdentity(int? processId)
    {
        ActionIdentity = null;
        try
        {
            ActionIdentity = ActionEvidenceIdentity.Capture(processId, Path.Combine(LogDirectory, "evidence-context.json"));
            Log.Write("action-session-identity", ActionIdentity);
        }
        catch { /* Identity logging cannot make attach fail. */ }
    }

    /// <summary>
    /// 诊断产物保留：最新 10 份、最多 30 天，当天产生的一律保留。真机实测 crashdumps 曾到
    /// 405MB（36 个证据包 zip，每个约 15MB）—— 出事时才有用的东西不该无限涨。
    /// 只删自己产出的命名（证据包 / 退出快照 / 采样导出包），删了什么写进会话日志（不静默删）。
    /// </summary>
    private void PruneEvidence()
    {
        foreach (var (pattern, label) in EvidenceRetention.ProductArtifacts)
        {
            IReadOnlyList<string> removed;
            try
            {
                removed = EvidenceRetention.Prune(EvidenceDirectory, pattern, DateTimeOffset.Now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;   // 清理失败不能挡住启动
            }

            if (removed.Count > 0)
            {
                Log.Write("evidence-pruned", new { kind = label, removed = removed.Count, oldest = removed[^1] });
            }
        }
    }

    public string LogDirectory { get; }

    public string EvidenceDirectory { get; }

    public IReadOnlyList<TargetModuleEntry> Modules => _modules;

    public HookSiteReport? HookReport => _hookReport;

    public LastTargetExit? LastExit => TargetExitHistory.Load(LogDirectory);

    public string SaveExitSnapshot(CrashContextSnapshot context)
    {
        var path = string.Empty;
        try
        {
            Directory.CreateDirectory(EvidenceDirectory);
            path = Path.Combine(EvidenceDirectory, $"exit-context-{context.CapturedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, context.ToReadableText());
            TargetExitHistory.Save(LogDirectory, new LastTargetExit(context.CapturedAt, context.ExitDescription, path));
            Log.Write("exit-context", new { path });
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("exit-context-failed", new { error = ex.Message });
            return File.Exists(path) ? path : string.Empty;
        }
    }

    public void PersistExitPath(string description, string path)
    {
        try { TargetExitHistory.Save(LogDirectory, new LastTargetExit(DateTimeOffset.Now, description, path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Write("exit-history-failed", new { error = ex.Message }); }
    }

    public void RecordHookReport(HookSiteReport report)
    {
        _hookReport = report;
        Log.Write("hook-check", new
        {
            total = report.Total,
            original = report.OriginalCount,
            patchedByUs = report.PatchedByUsCount,
            unexpected = report.UnexpectedCount,
            unexpectedAddresses = report.UnexpectedAddresses
        });
    }

    /// <summary>
    /// Snapshot of every module the target had loaded. This is how a later report can name the mod
    /// and helper libraries that were in the process, without this tool knowing anything about them.
    /// </summary>
    public void RecordModuleSnapshot(int? processId)
    {
        _modules.Clear();
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            foreach (ProcessModule module in process.Modules)
            {
                try
                {
                    _modules.Add(new TargetModuleEntry(
                        module.ModuleName ?? string.Empty,
                        module.FileName ?? string.Empty,
                        module.FileVersionInfo?.FileVersion ?? string.Empty));
                }
                catch (Exception)
                {
                    // Some modules deny version queries; the name/path is still worth keeping.
                }
            }

            Log.Write("modules", new
            {
                count = _modules.Count,
                names = _modules.Select(module => module.Name).Take(60).ToArray()
            });
        }
        catch (Exception ex)
        {
            Log.Write("modules-failed", new { error = ex.Message });
        }
    }

    public CrashEvidenceResult CollectEvidence(CrashEvidenceRequest request)
    {
        var result = _collector.Collect(EvidenceDirectory, request, BuildHookFileEntries(), _modules.ToArray());
        Log.Write("evidence", new
        {
            archive = result.ArchivePath,
            included = result.Included.Count,
            warnings = result.Warnings
        });
        return result;
    }

    public IReadOnlyList<HookSiteFileEntry> BuildHookFileEntries()
    {
        return _hookReport is null
            ? []
            : _hookReport.Sites
                .Select(site => new HookSiteFileEntry(
                    site.Address,
                    site.SectionTitle,
                    site.State.ToString(),
                    ToHex(site.ExpectedBytes),
                    ToHex(site.ActualBytes)))
                .ToArray();
    }

    public void Dispose()
    {
        Log.Write("app-exit");
        Log.Dispose();
    }

    private static string ResolveLogDirectory(string baseDirectory)
    {
        // 产品口径：日志与现场记录写用户目录（不写 exe 旁边，装到 Program Files 也不受影响）。
        var preferred = TrainerPaths.LogDirectory;
        var probe = TrainerSessionLog.Open(preferred);
        var usable = probe.IsAvailable;
        probe.Dispose();
        if (usable)
        {
            return preferred;
        }

        return TrainerSessionLog.DefaultDirectory(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ra3Trainer"));
    }

    private static string ToHex(IReadOnlyList<byte> bytes)
    {
        return string.Join(" ", bytes.Select(value => value.ToString("X2")));
    }
}
