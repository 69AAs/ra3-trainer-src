using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 保留策略的钉子。用户点过头：诊断产物不能无限涨（真机 crashdumps 已 405MB），
/// 但**只许删自己产出的、且要留痕** —— 所以这里同时钉住"模式外的文件一根汗毛都不动"。
/// </summary>
public sealed class EvidenceRetentionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ra3retention-" + Guid.NewGuid().ToString("N"));

    public EvidenceRetentionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string Make(string name, int ageInDays)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageInDays));
        return path;
    }

    [Fact]
    public void PruneKeepsTheNewestEntriesAndRemovesTheOlderOnes()
    {
        for (var i = 1; i <= 15; i++) Make($"evidence-{i:00}.zip", i);

        var removed = EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.UtcNow, keepNewest: 10);

        Assert.Equal(5, removed.Count);
        Assert.Equal(10, Directory.GetFiles(_directory, "evidence-*.zip").Length);
        foreach (var gone in new[] { "evidence-11.zip", "evidence-12.zip", "evidence-13.zip", "evidence-14.zip", "evidence-15.zip" })
        {
            Assert.False(File.Exists(Path.Combine(_directory, gone)), gone);
        }

        Assert.True(File.Exists(Path.Combine(_directory, "evidence-01.zip")));
    }

    [Theory]
    [InlineData(3, 3, 0)]   // keepNewest == 命中数：一条都不删
    [InlineData(3, 10, 0)]  // keepNewest > 命中数：一条都不删
    [InlineData(3, 0, 3)]   // keepNewest == 0：命中全删
    [InlineData(3, -5, 3)]  // 负数被夹到 0（EvidenceRetention.cs:25）：命中全删
    public void KeepNewestBoundaryPinsTheRetentionCount(int fileCount, int keepNewest, int expectedRemoved)
    {
        for (var i = 1; i <= fileCount; i++) Make($"evidence-{i:00}.zip", i);

        var removed = EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.UtcNow, keepNewest: keepNewest);

        Assert.Equal(expectedRemoved, removed.Count);
        Assert.Equal(fileCount - expectedRemoved, Directory.GetFiles(_directory, "evidence-*.zip").Length);
    }

    [Fact]
    public void KeepNewestOneLeavesOnlyTheMostRecentFile()
    {
        Make("evidence-01.zip", 3);
        Make("evidence-02.zip", 2);
        Make("evidence-03.zip", 1);

        var removed = EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.UtcNow, keepNewest: 1);

        Assert.Equal(2, removed.Count);
        Assert.Equal(new[] { Path.Combine(_directory, "evidence-03.zip") }, Directory.GetFiles(_directory, "evidence-*.zip"));
    }

    [Fact]
    public void EqualTimestampsStillRemoveExactlyTheOverflow()
    {
        var stamp = DateTime.UtcNow.AddDays(-1);
        foreach (var name in new[] { "evidence-a.zip", "evidence-b.zip", "evidence-c.zip" })
            File.SetLastWriteTimeUtc(Make(name, 1), stamp);

        var removed = EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.UtcNow, keepNewest: 1);

        // 平局时"留下哪一份"由枚举顺序决定（不可断言），但删几条/留几条必须确定。
        Assert.Equal(2, removed.Count);
        Assert.Single(Directory.GetFiles(_directory, "evidence-*.zip"));
    }

    [Fact]
    public void AgeLimitRemovesEvenOneThatIsInsideTheNewestSet()
    {
        Make("exit-context-fresh.txt", 1);
        Make("exit-context-ancient.txt", 40);

        var removed = EvidenceRetention.Prune(_directory, "exit-context-*.txt", DateTimeOffset.UtcNow,
            keepNewest: 10, maxAge: TimeSpan.FromDays(30));

        Assert.Single(removed);
        Assert.True(File.Exists(Path.Combine(_directory, "exit-context-fresh.txt")));
        Assert.False(File.Exists(Path.Combine(_directory, "exit-context-ancient.txt")));
    }

    [Fact]
    public void PruneNeverTouchesFilesOutsideThePattern()
    {
        Make("evidence-a.zip", 1);
        Make("evidence-b.zip", 2);
        var keep = Make("settings-backup.json", 99);
        var keepTxt = Make("notes.txt", 99);
        // 近失配名：模式一旦退化成子串/前缀匹配，这些用户文件会被误删，所以逐个钉住。
        var keepNoDash = Make("evidence.zip", 99);
        var keepForeign = Make("my-evidence-a.zip", 99);
        var keepBackup = Make("evidence-a.zip.bak", 99);

        EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.UtcNow, keepNewest: 0);

        Assert.False(File.Exists(Path.Combine(_directory, "evidence-a.zip")));
        Assert.False(File.Exists(Path.Combine(_directory, "evidence-b.zip")));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(keepTxt));
        Assert.True(File.Exists(keepNoDash));
        Assert.True(File.Exists(keepForeign));
        Assert.True(File.Exists(keepBackup));
    }

    [Fact]
    public void MissingDirectoryIsANoOp()
    {
        var removed = EvidenceRetention.Prune(Path.Combine(_directory, "nope"), "*.zip", DateTimeOffset.UtcNow);

        Assert.Empty(removed);
    }

    // 【二】当天产生的一律保留（用户定的规则）：哪怕 keepNewest=0 且 age 上限为 0，
    // 也不删今天写出来的那一份。旧行为没有这条 ⇒ 两份都超出 keepNewest/maxAge，都会被删。
    [Fact]
    public void TodayIsAlwaysKeptEvenWhenCountAndAgeWouldDelete()
    {
        var today = Make("evidence-today.zip", 0);
        var ancient = Make("evidence-ancient.zip", 40);

        var removed = EvidenceRetention.Prune(_directory, "evidence-*.zip", DateTimeOffset.Now,
            keepNewest: 0, maxAge: TimeSpan.Zero);

        Assert.Single(removed);
        Assert.True(File.Exists(today));
        Assert.False(File.Exists(ancient));
    }

    // 【二】采样导出包 probe-export-*.zip 也要被保留策略覆盖：它原先不在两种命名里，
    // 所以 crashdumps 里只增不减。这里同时钉住覆盖范围与"模式外的文件不动"。
    [Fact]
    public void ProductArtifactsCoverSamplingExportsAndEveryEntryHasAKind()
    {
        Assert.Equal(
            new[] { "evidence-*.zip", "exit-context-*.txt", "probe-export-*.zip" },
            EvidenceRetention.ProductArtifacts.Select(artifact => artifact.Pattern));
        Assert.Equal(EvidenceRetention.ProductArtifacts.Length,
            EvidenceRetention.ProductArtifacts.Select(artifact => artifact.Label).Distinct().Count());
    }

    [Fact]
    public void ProbeExportPatternIsPrunedAndLeavesNearMissesAlone()
    {
        for (var i = 1; i <= 12; i++) Make($"probe-export-{i:00}.zip", i);
        var nearMiss = Make("probe-exported-99.zip", 5);   // 少一个连字符：不是产品命名

        var removed = EvidenceRetention.Prune(_directory, "probe-export-*.zip", DateTimeOffset.Now, keepNewest: 10);

        Assert.Equal(2, removed.Count);
        Assert.Equal(10, Directory.GetFiles(_directory, "probe-export-*.zip").Length);
        Assert.True(File.Exists(nearMiss));
    }
}
