using System.Text.Json;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerSessionLogTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 27, 22, 30, 0, TimeSpan.FromHours(8));

    private readonly List<string> _directories = [];

    /// <summary>回收本测试创建的临时目录：否则每跑一次就往 %TEMP% 里漏一个 session-*.jsonl。</summary>
    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void WriteSurvivesCyclesAndNonFiniteNumbersAndKeepsLogging()
    {
        var directory = TempDirectory();
        using var log = TrainerSessionLog.Open(directory, () => FixedNow);
        var cycle = new List<object>();
        cycle.Add(cycle);

        log.Write("cycle", cycle);
        log.Write("nan", new { value = double.NaN });
        log.Write("next", new { valid = true });

        var lines = ReadLogLines(log.FilePath);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("cycle", first.RootElement.GetProperty("event").GetString());
        Assert.Contains("日志数据无法序列化", first.RootElement.GetProperty("data").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("nan", second.RootElement.GetProperty("event").GetString());
        using var third = JsonDocument.Parse(lines[2]);
        Assert.True(third.RootElement.GetProperty("data").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void WriteAppendsOneParsableJsonLinePerEvent()
    {
        var directory = TempDirectory();
        using var log = TrainerSessionLog.Open(directory, () => FixedNow);

        log.Write("attach", new { processId = 4242, version = "1.12.3444.25830" });
        log.Write("feature", new { name = "Moeny", enabled = true });

        Assert.True(log.IsAvailable);
        Assert.EndsWith("session-20260927.jsonl", log.FilePath);
        var lines = ReadLogLines(log.FilePath);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("2026-09-27T22:30:00.0000000+08:00", first.RootElement.GetProperty("ts").GetString());
        // 再断言时间点本身：换成别的等值 ISO 写法的修改不该因为"格式变了"而假装行为回归。
        Assert.Equal(FixedNow, DateTimeOffset.Parse(first.RootElement.GetProperty("ts").GetString()!));
        Assert.Equal("attach", first.RootElement.GetProperty("event").GetString());
        Assert.Equal(4242, first.RootElement.GetProperty("data").GetProperty("processId").GetInt32());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("feature", second.RootElement.GetProperty("event").GetString());
        Assert.Equal("Moeny", second.RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public void WriteSurvivesUnserializablePayload()
    {
        var directory = TempDirectory();
        using var log = TrainerSessionLog.Open(directory, () => FixedNow);

        log.Write("weird", new { value = new IntPtr(0x1234) });

        var line = ReadLogLines(log.FilePath).Single();
        using var document = JsonDocument.Parse(line);
        Assert.Equal("weird", document.RootElement.GetProperty("event").GetString());
    }

    [Fact]
    public void UnwritableDirectoryYieldsNoOpLogInsteadOfThrowing()
    {
        var blocker = Path.Combine(TempDirectory(), "not-a-directory");
        File.WriteAllText(blocker, "x");

        using var log = TrainerSessionLog.Open(Path.Combine(blocker, "logs"), () => FixedNow);

        Assert.False(log.IsAvailable);
        log.Write("attach", null);
    }

    [Fact]
    public void OpenPrunesSessionsOlderThanRetention()
    {
        var directory = TempDirectory();
        var stale = Path.Combine(directory, "session-20260101.jsonl");
        File.WriteAllText(stale, "{}");

        using var log = TrainerSessionLog.Open(directory, () => FixedNow, retentionDays: 14);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(directory, "session-20260927.jsonl")));
    }

    [Fact]
    public void OpenKeepsTheFileDatedExactlyAtTheCutoffAndPrunesStrictlyOlderOnes()
    {
        var directory = TempDirectory();
        // FixedNow=2026-09-27、retentionDays=14 ⇒ cutoff=2026-09-13，判据是严格小于（`date < cutoff`）。
        var boundary = Path.Combine(directory, "session-20260913.jsonl");
        var older = Path.Combine(directory, "session-20260912.jsonl");
        File.WriteAllText(boundary, "{}");
        File.WriteAllText(older, "{}");

        using var log = TrainerSessionLog.Open(directory, () => FixedNow, retentionDays: 14);

        Assert.True(log.IsAvailable);
        Assert.True(File.Exists(boundary)); // 正好等于 cutoff 的那天必须留下
        Assert.False(File.Exists(older));
    }

    [Fact]
    public void ZeroRetentionDaysDisablesPruningEntirely()
    {
        var directory = TempDirectory();
        var ancient = Path.Combine(directory, "session-20200101.jsonl");
        File.WriteAllText(ancient, "{}");

        using var log = TrainerSessionLog.Open(directory, () => FixedNow, retentionDays: 0);

        Assert.True(File.Exists(ancient)); // retentionDays<=0 时根本不剪
    }

    /// <summary>
    /// The log file stays open for the whole session, so a plain File.ReadAllLines would hit a
    /// sharing violation — exactly the failure the evidence collector used to hit. Reading it the
    /// way the collector does proves the file is readable while the tool is running.
    /// </summary>
    private static string[] ReadLogLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }
}
