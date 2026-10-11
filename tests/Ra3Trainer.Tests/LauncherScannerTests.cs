using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class LauncherScannerTests
{
    [Theory]
    [InlineData("CoronaLauncher.exe", LauncherKind.ModLauncher)]
    [InlineData("RA3.QuickLoader.exe", LauncherKind.ModLauncher)]
    [InlineData("Ra3BattlezoneHub.exe", LauncherKind.PlatformLauncher)]
    [InlineData("RA3.exe", LauncherKind.GameExecutable)]
    [InlineData("ra3tool.exe", LauncherKind.Other)]
    [InlineData("cnc3reader.exe", LauncherKind.Other)]
    public void ClassifyRecognizesLauncherKinds(string fileName, LauncherKind expected)
    {
        Assert.Equal(expected, LauncherScanner.Classify(fileName));
    }

    [Fact]
    public void ScanFindsModLauncherBeforeGameExecutableAndSkipsNoise()
    {
        var root = CreateTree(
            ("Red Alert 3/RA3.exe", ""),
            ("CoronaLauncher/CoronaLauncher.exe", ""),
            ("CoronaLauncher/uninst.exe", ""),
            ("CoronaLauncher/dotnet/dotnet.exe", ""),
            ("CoronaLauncher/tools/aria2c.exe", ""));

        try
        {
            var candidates = LauncherScanner.Scan([root]);

            Assert.Collection(
                candidates,
                first =>
                {
                    Assert.Equal(LauncherKind.ModLauncher, first.Kind);
                    Assert.EndsWith("CoronaLauncher.exe", first.Path);
                    Assert.Contains("mod 启动器", first.DisplayName);
                },
                second =>
                {
                    Assert.Equal(LauncherKind.GameExecutable, second.Kind);
                    Assert.EndsWith("RA3.exe", second.Path);
                });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void BuildRootsReachesSiblingFoldersSoCoronaLauncherIsFound()
    {
        var root = CreateTree(
            ("Red Alert 3/RA3.exe", ""),
            ("CoronaLauncher/CoronaLauncher.exe", ""));

        try
        {
            var configured = Path.Combine(root, "Red Alert 3", "RA3.exe");
            var roots = LauncherScanner.BuildRoots(configured);

            // 直接断言推导出来的根目录集合：光看"找到了 CoronaLauncher.exe"分不清是靠兄弟目录推导
            // 还是靠别处的盘符扫描凑巧命中。
            Assert.Contains(Path.Combine(root, "CoronaLauncher"), roots);
            Assert.Contains(Path.Combine(root, "Red Alert 3"), roots);

            var candidates = LauncherScanner.Scan(roots);
            Assert.Contains(candidates, c => c.Path.EndsWith("CoronaLauncher.exe"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ScanDropsExcludedNamesEvenWhenTheyWouldClassifyAsALauncher()
    {
        // LauncherUpdater.exe 名字里有 launcher（会被 Classify 判成 ModLauncher），但必须被排除名单挡掉；
        // 否则 IsExcluded 与 Classify 的先后顺序一旦调换，升级器就会混进候选列表。
        var root = CreateTree(
            ("Red Alert 3/RA3.exe", ""),
            ("CoronaLauncher/CoronaLauncher.exe", ""),
            ("CoronaLauncher/LauncherUpdater.exe", ""),
            ("CoronaLauncher/uninst.exe", ""));

        try
        {
            var candidates = LauncherScanner.Scan([root]);

            Assert.DoesNotContain(candidates, c => c.Path.EndsWith("LauncherUpdater.exe", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(candidates, c => c.Path.EndsWith("uninst.exe", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(candidates, c => c.Path.EndsWith("CoronaLauncher.exe"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MapEditorLaunchersAreNeverRelevant()
    {
        var root = CreateTree(
            ("Red Alert 3/RA3.exe", ""),
            ("newWorldBuilder/newWorldBuilderLauncher.exe", ""),
            ("wblite/wblauncher.exe", ""));

        try
        {
            var candidates = LauncherScanner.Scan([root]);

            // 名单里的地图编辑器：路径再相关也不能进候选（机器上只装它时曾经推荐过它）。
            Assert.DoesNotContain(candidates, c => c.Path.Contains("newWorldBuilder", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(candidates, c => c.Path.Contains("wblauncher", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(candidates, c => c.Path.EndsWith("RA3.exe"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void BlankAndMissingRootsAreSkippedWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "launcher-scan-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(LauncherScanner.Scan([""]));
        Assert.Empty(LauncherScanner.Scan(["   "]));
        Assert.Empty(LauncherScanner.Scan([missing]));
    }

    [Fact]
    public void BuildRootsToleratesNullEmptyAndMissingInput()
    {
        var missing = Path.Combine(Path.GetTempPath(), "launcher-scan-missing-" + Guid.NewGuid().ToString("N"));

        Assert.NotNull(LauncherScanner.BuildRoots(null));
        Assert.NotNull(LauncherScanner.BuildRoots(""));
        Assert.NotNull(LauncherScanner.BuildRoots("   "));
        Assert.NotNull(LauncherScanner.BuildRoots(missing));
    }

    private static void TryDelete(string root)
    {
        // 临时目录里是空 .exe 文件，Defender/索引器可能短暂占用：清理失败不能顶掉真正的断言结果。
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string CreateTree(params (string RelativePath, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"launcher-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        return root;
    }
}
