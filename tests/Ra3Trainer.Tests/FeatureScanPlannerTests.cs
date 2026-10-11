using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class FeatureScanPlannerTests
{
    [Fact]
    public void CreateMarksManifestFeaturesAsScannableOrSkipped()
    {
        var manifest = TestAssets.LoadManifest();

        var items = FeatureScanPlanner.Create(manifest.Features);

        Assert.Equal(32, items.Count);
        Assert.Equal(31, items.Count(item => item.CanScan));
        var skipped = Assert.Single(items, item => !item.CanScan);
        Assert.Equal("Free Build", skipped.Feature.DisplayName);
        // 跳过项必须真的是 Unsupported，而不只是"带了一句理由"。
        Assert.Equal(FeatureScanKind.Unsupported, skipped.Kind);
        Assert.Contains("enable flag", skipped.SkipReason);
    }

    [Fact]
    public void CreateMarksBothUnsupportedBranchesExplicitly()
    {
        // 分支二：既不是 toggle 也不是 action（ValueHint 为空、没有任何 enable flag）。
        var noEntry = new TrainerFeature("none", "无入口", null, [], null, null);
        // 分支一：是 action（ValueHint 非空），但既没有 dispatch target 也没有 enable flag。
        var noTarget = new TrainerFeature("action", "缺入口动作", null, [], null, "0x05");

        var items = FeatureScanPlanner.Create([noEntry, noTarget]);

        Assert.Equal(FeatureScanKind.Unsupported, items[0].Kind);
        Assert.Equal("不是可触发的 toggle/action 功能。", items[0].SkipReason);
        Assert.Equal(FeatureScanKind.Unsupported, items[1].Kind);
        Assert.Equal("缺少 dispatch target 或 enable flag。", items[1].SkipReason);
        Assert.All(items, item => Assert.False(item.CanScan));
    }

    [Fact]
    public void CreateClassifiesToggleAndActionFeatures()
    {
        // 刻意用合成功能：清单里的 Zoom 是 null enable flags（不可扫），照搬会掩盖判据差异。
        var toggle = new TrainerFeature("Synthetic Toggle", "合成开关（非清单）", "Ctrl+F8", ["iEnable+F", "iEnable+10"], null, null);
        var action = new TrainerFeature("Synthetic Action", "合成动作（非清单）", "#219", [], "MustCode2+400", "0x05");

        var items = FeatureScanPlanner.Create([toggle, action]);

        Assert.Equal(FeatureScanKind.Toggle, items[0].Kind);
        Assert.Equal(FeatureScanKind.Action, items[1].Kind);
    }
}
