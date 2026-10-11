using System.IO.Compression;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class CrashEvidenceCollectorTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    /// <summary>逐个回收本测试创建的临时根目录；断言抛异常时 xunit 仍会调用 Dispose。</summary>
    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void SameTimestampCollectionsPreserveBothArchives()
    {
        var root = TempDirectory();
        try
        {
            var now = DateTimeOffset.Now;
            var collector = new CrashEvidenceCollector(werRoot: Path.Combine(root, "missing"),
                clock: () => now, windowsEventLogReader: () => null);
            var request = new CrashEvidenceRequest("ra3_1.12.game", null, null, null, "first", []);
            var first = collector.Collect(root, request, [], []);
            var previousBytes = File.ReadAllBytes(first.ArchivePath);
            var second = collector.Collect(root, request with { ExitDescription = "second" }, [], []);

            Assert.NotEqual(first.ArchivePath, second.ArchivePath);
            Assert.Equal(previousBytes, File.ReadAllBytes(first.ArchivePath));
            Assert.True(File.Exists(second.ArchivePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CollectBundlesSessionLogHookSitesModulesWerAndFreshTargetLogs()
    {
        var root = TempDirectory();
        var now = DateTimeOffset.Now;
        var gameDirectory = Path.Combine(root, "game", "Data");
        Directory.CreateDirectory(gameDirectory);
        var modulePath = Path.Combine(gameDirectory, "ra3_1.12.game");
        File.WriteAllText(modulePath, "binary-placeholder");

        var freshLog = Path.Combine(gameDirectory, "anything-recent.log");
        File.WriteAllText(freshLog, "fresh");
        var staleLog = Path.Combine(gameDirectory, "anything-old.log");
        File.WriteAllText(staleLog, "stale");
        File.SetLastWriteTime(staleLog, now.AddHours(-5).LocalDateTime);
        var freshBinary = Path.Combine(gameDirectory, "some-module.dll");
        File.WriteAllText(freshBinary, "not-a-log");

        var werRoot = Path.Combine(root, "WER");
        var matchingReport = Path.Combine(werRoot, "ReportArchive", "AppCrash_ra3_1.12.game_c0ffee");
        Directory.CreateDirectory(matchingReport);
        File.WriteAllText(Path.Combine(matchingReport, "Report.wer"), "wer-body");
        var unrelatedReport = Path.Combine(werRoot, "ReportArchive", "AppCrash_other.exe_1234");
        Directory.CreateDirectory(unrelatedReport);
        File.WriteAllText(Path.Combine(unrelatedReport, "Report.wer"), "other-body");

        var sessionLog = Path.Combine(root, "logs", "session-20260927.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionLog)!);
        File.WriteAllText(sessionLog, "{\"event\":\"attach\"}");

        var collector = new CrashEvidenceCollector(
            werRoot: werRoot,
            clock: () => now,
            windowsEventLogReader: () => null,
            recentWindow: TimeSpan.FromMinutes(60));

        var result = collector.Collect(
            Path.Combine(root, "out"),
            new CrashEvidenceRequest(
                TargetProcessName: "ra3_1.12.game",
                ProcessId: 4242,
                TargetModulePath: modulePath,
                SessionLogPath: sessionLog,
                ExitDescription: "退出码 0xC0000005",
                Notes: ["崩前状态：已装 patch"]),
            [
                new HookSiteFileEntry("ra3_1.12.game+FF95B", "Script", "Original", "01 02", "01 02")
            ],
            [
                new TargetModuleEntry("ra3_1.12.game", modulePath, "1.12.3444.25830")
            ]);

        Assert.True(File.Exists(result.ArchivePath));
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains("summary.txt", names);
        Assert.Contains("trainer/hooks.json", names);
        Assert.Contains("trainer/modules.json", names);
        Assert.Contains("trainer/session-20260927.jsonl", names);
        Assert.Contains(names, name => name.StartsWith("wer/AppCrash_ra3_1.12.game_c0ffee/", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("other.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, name => name.EndsWith("anything-recent.log", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("anything-old.log", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("some-module.dll", StringComparison.OrdinalIgnoreCase));

        var summary = ReadEntry(archive, "summary.txt");
        Assert.Contains("退出码 0xC0000005", summary);
        Assert.Contains("崩前状态：已装 patch", summary);
        Assert.Contains("1.12.3444.25830", summary);

        var hooks = ReadEntry(archive, "trainer/hooks.json");
        Assert.Contains("ra3_1.12.game+FF95B", hooks);
    }

    [Fact]
    public void CollectStillProducesAnArchiveWhenWerAndEventLogAreUnavailable()
    {
        var root = TempDirectory();
        var collector = new CrashEvidenceCollector(
            werRoot: Path.Combine(root, "no-such-wer"),
            clock: () => DateTimeOffset.Now,
            windowsEventLogReader: () => null);

        var result = collector.Collect(
            Path.Combine(root, "out"),
            new CrashEvidenceRequest("ra3_1.12.game", null, null, null, null, []),
            [],
            []);

        Assert.True(File.Exists(result.ArchivePath));
        Assert.Contains(result.Warnings, warning => warning.Contains("系统崩溃报告目录不存在", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("系统事件日志", StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Assert.Contains("summary.txt", archive.Entries.Select(entry => entry.FullName));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.Entries.Single(item => item.FullName == name);
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }
}
