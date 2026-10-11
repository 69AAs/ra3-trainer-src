using System;
using System.IO;
using System.Linq;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 产品级数据目录口径：**老位置（exe 旁边）已有就继续用，没有就落到用户目录**。
/// 这条规则一旦被改错，用户的设置/清单/日志就会丢或写不进去（装到 Program Files 的用户尤其明显）。
/// </summary>
public sealed class TrainerPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ra3-paths-" + Guid.NewGuid().ToString("N")[..8]);

    public TrainerPathsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 老位置有文件就用老位置() 
    {
        var legacy = Path.Combine(_dir, "units-remix.txt");
        File.WriteAllText(legacy, "0x12345678 测试单位");

        Assert.Equal(legacy, TrainerPaths.Resolve(_dir, "units-remix.txt"));
    }

    [Fact]
    public void 老位置没有就落到用户目录()
    {
        var resolved = TrainerPaths.Resolve(_dir, "units-nothing.txt");

        Assert.StartsWith(TrainerPaths.DataRoot, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("units-nothing.txt", resolved, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(TrainerPaths.DataRoot));
    }

    [Fact]
    public void 目录为空也落到用户目录()
    {
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerPaths.Resolve(null, TrainerAppSettingsStore.SettingsFileName), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerPaths.Resolve(string.Empty, TrainerAppSettingsStore.SettingsFileName), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 搜索目录两边都认且不重复()
    {
        var dirs = TrainerPaths.SearchDirectories(_dir).ToArray();

        Assert.Equal(2, dirs.Length);
        Assert.Equal(_dir, dirs[0]);
        Assert.Equal(TrainerPaths.DataRoot, dirs[1]);
        Assert.Single(TrainerPaths.SearchDirectories(TrainerPaths.DataRoot).ToArray());
    }

    [Fact]
    public void 设置文件跟随同一条口径()
    {
        var settings = Path.Combine(_dir, TrainerAppSettingsStore.SettingsFileName);
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerAppSettingsStore.DefaultPath(_dir), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(settings, "{}");
        Assert.Equal(settings, TrainerAppSettingsStore.DefaultPath(_dir));
    }

    [Fact]
    public void 单位清单与名字缓存都跟随同一条口径()
    {
        Assert.StartsWith(TrainerPaths.DataRoot, ReinforcementUnitCatalog.DefaultUserCatalogPath(_dir), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TrainerPaths.DataRoot, ReinforcementUnitCatalog.UserCatalogPathForModKey(_dir, "remix"), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TrainerPaths.DataRoot, ModNameTable.CachePathFor(_dir, "remix"), StringComparison.OrdinalIgnoreCase);

        var catalog = ReinforcementUnitCatalog.UserCatalogPathForModKey(_dir, "remix");
        File.WriteAllText(catalog, "0x1 甲");
        Assert.Equal(catalog, ReinforcementUnitCatalog.UserCatalogPathForModKey(_dir, "remix"));
    }

    [Fact]
    public void 日志与现场记录都在用户目录下()
    {
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerPaths.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerPaths.EvidenceDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TrainerPaths.DataRoot, TrainerPaths.ArtifactsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 老位置的表兄弟文件也能被找到()
    {
        var legacy = Path.Combine(_dir, "units-remix037.txt");
        File.WriteAllText(legacy, "0x1 甲");

        Assert.Equal(legacy, ReinforcementUnitCatalog.FindVersionSiblingCatalog(_dir, "remix"));
    }
}
