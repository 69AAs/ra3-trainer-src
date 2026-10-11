using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerRuntimeAssetsTests
{
    [Fact]
    public void LoadManifestReadsEmbeddedTrainerReport()
    {
        var manifest = TrainerRuntimeAssets.LoadManifest();

        Assert.Equal("ra3_1.12.game", manifest.TargetProcess);
        Assert.NotEmpty(manifest.Features);
        Assert.NotEmpty(manifest.PatchManifest.Hooks);
        // 更强的清单钉子：只断言"非空"的话，清单退化成一条占位项也照样绿。
        Assert.Equal(32, manifest.Features.Count);
        Assert.Equal(22, manifest.PatchManifest.Hooks.Count);
        var money = Assert.Single(manifest.Features, feature => feature.RawName == "Moeny");
        Assert.Equal(new[] { "iEnable+8" }, money.EnableFlags);
        Assert.Contains(manifest.PatchManifest.Hooks, hook => hook.Address == "ra3_1.12.game+FF95B");
    }

    [Fact]
    public void ReadBootstrapLinesReadsEmbeddedBootstrapScript()
    {
        var lines = TrainerRuntimeAssets.ReadBootstrapLines();

        // 脚本被截断/切分成空也得能看出来：先钉住"确实是一段有内容的脚本"。
        Assert.True(lines.Count > 1, "bootstrap 脚本应当有多行");
        Assert.DoesNotContain(lines, line => line is null);
        Assert.Contains(lines, line => line.Contains("Game Version : 1.12.3444.25830", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Equals("iEnable+30:", StringComparison.Ordinal));
    }
}
