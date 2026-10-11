using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class CrashContextSnapshotTests
{
    [Theory]
    [InlineData(0x1000, true)]
    [InlineData(0x1004, true)]
    [InlineData(0x1005, false)]
    [InlineData(0x0FFF, false)]
    public void AbsoluteAddressCorrelationUsesHalfOpenRange(ulong address, bool expected)
        => Assert.Equal(expected, new DiagnosticMemoryRange("hook", 0x1000, 5).Contains(address));

    [Fact]
    public void EvidenceArchiveIncludesReadableAndStructuredContext()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var context = new CrashContextSnapshot(DateTimeOffset.UnixEpoch, "PID=42", "unknown", [], new([], []), ["request"]);
            var collector = new CrashEvidenceCollector(werRoot: Path.Combine(directory, "missing"), windowsEventLogReader: () => null);
            var result = collector.Collect(directory, new CrashEvidenceRequest("ra3.game", 42, null, null, "unknown", [], context), [], []);
            using var zip = System.IO.Compression.ZipFile.OpenRead(result.ArchivePath);
            // 显式断言两条目都存在：否则 ! 会把"打包回归"变成难查的 NullReferenceException。
            var textEntry = zip.GetEntry("trainer/context.txt");
            var jsonEntry = zip.GetEntry("trainer/context.json");
            Assert.NotNull(textEntry);
            Assert.NotNull(jsonEntry);
            using var reader = new StreamReader(textEntry!.Open());
            Assert.Contains("PID=42", reader.ReadToEnd());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SnapshotPreservesFeatureTimelineAndHalfOpenMemoryRanges()
    {
        var snapshot = new CrashContextSnapshot(DateTimeOffset.UnixEpoch, "PID=42; version=1.12", "0xC0000005",
            ["无敌"], new SessionMemorySnapshot([new("hook", 0x1000, 5)], [new("trampoline", 0x2000, 4096)]),
            ["1970-01-01T00:00:00Z action"]);
        var text = snapshot.ToReadableText();
        Assert.Contains("无敌", text);
        Assert.Contains("[0x00001000, 0x00001005)", text);
        Assert.Contains("[0x00002000, 0x00003000)", text);
        Assert.Contains("1970-01-01T00:00:00Z action", text);
        Assert.Contains("不是进程转储", text);
        Assert.Contains("不能排除较早的数据破坏", text);
    }

    [Fact]
    public void RecentActionsRemainBoundedAndAvailableWhenDiskLogCannotOpen()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(directory, "blocks directory creation");
        try
        {
            using var log = TrainerSessionLog.Open(directory, () => DateTimeOffset.UnixEpoch);
            Assert.False(log.IsAvailable);
            for (var i = 0; i < 140; i++) log.Write("feature", new { index = i });
            Assert.Equal(128, log.RecentEntries.Count);
            Assert.Contains("\"index\":12", log.RecentEntries[0]);
            Assert.Contains("1970-01-01", log.RecentEntries[0]);
            Assert.Contains("\"index\":139", log.RecentEntries[^1]);
        }
        finally { File.Delete(directory); }
    }

    [Fact]
    public void RecentActionsCapEvictsExactlyTheOldestEntryAtTheBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        File.WriteAllText(directory, "blocks directory creation");
        try
        {
            using var log = TrainerSessionLog.Open(directory, () => DateTimeOffset.UnixEpoch);
            for (var i = 0; i < 128; i++) log.Write("feature", new { index = i });
            // 正好 128 条：一条都不许被驱逐（把 `> 128` 改成 `>= 128` 会被这里抓住）。
            Assert.Equal(128, log.RecentEntries.Count);
            Assert.Contains("\"index\":0", log.RecentEntries[0]);
            log.Write("feature", new { index = 128 });
            // 第 129 条只允许挤掉最旧的一条，容量仍是 128。
            Assert.Equal(128, log.RecentEntries.Count);
            Assert.Contains("\"index\":1", log.RecentEntries[0]);
            Assert.Contains("\"index\":128", log.RecentEntries[^1]);
        }
        finally { File.Delete(directory); }
    }

    [Fact]
    public void LastExitSurvivesRestartWithoutLiveStateAndCorruptFileIsIgnored()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var exit = new LastTargetExit(DateTimeOffset.UnixEpoch, "状态未知", "context.txt");
            TargetExitHistory.Save(directory, exit);
            Assert.Equal(exit, TargetExitHistory.Load(directory));
            File.WriteAllText(Path.Combine(directory, "last-target-exit.json"), "incomplete");
            Assert.Null(TargetExitHistory.Load(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
