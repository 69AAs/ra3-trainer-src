using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class FeatureSafetyPolicyTests
{
    [Theory]
    // displayName 刻意与策略分支的文案**不同**：三个坐标功能的分支用的是 actionName
    // （给玩家基地车/呼叫战场增援/复制选择的建筑物/单位给玩家），不是 feature.DisplayName。
    // 原来把入参当期望值回显，等于验不了"走的是哪个分支"。
    [InlineData("Free Build", "随地建造", "地形", "随地建造")]
    [InlineData("Get Me Base", "基地车（界面名）", "有限值", "给玩家基地车")]
    [InlineData("We Need Back", "增援（界面名）", "有限值", "呼叫战场增援")]
    [InlineData("Select Unit Copy For Me", "复制（界面名）", "有限值", "复制选择的建筑物/单位给玩家")]
    public void UnvalidatedPlacementIsRejectedWithSpecificReason(
        string rawName, string displayName, string reason, string branchText)
    {
        var feature = new TrainerFeature(rawName, displayName, null, [], null, null);
        var error = Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(feature, true));
        Assert.Contains(branchText, error.Message);
        Assert.Contains(reason, error.Message);
        Assert.Contains("地图边界", error.Message);
        FeatureSafetyPolicy.Validate(feature, false);
    }

    [Fact]
    public void ValidateRejectsNullFeature()
    {
        Assert.Throws<ArgumentNullException>(() => FeatureSafetyPolicy.Validate(null!, true));
    }

    [Fact]
    public void BoundedResourceFeatureRemainsAvailable()
    {
        // 仅"没抛异常"等于什么也没验（空实现也能过）：同一启用状态下换成受限制功能必须抛，
        // 这个对照才说明"放行"是有内容的结果。
        var allowed = new TrainerFeature("SC POINT", "无限秘密协议点数", null, ["iEnable+A"], null, null);
        FeatureSafetyPolicy.Validate(allowed, true);

        var restricted = new TrainerFeature("Select Unit HP MAX", "选择的建筑物/单位无限生命值", null, [], "MustCode2+400", "0x05");
        Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(restricted, true));
    }

    [Theory]
    [InlineData("Select Unit Ammo MAX 2", "0x00")]
    [InlineData("Select Unit Ammo MAX 2", "0X00")]   // 大小写不同也算对象标志恢复
    [InlineData("Restore Danger Level Normal", "0x00")]
    public void ObjectFlagRecoveryBypassesRestrictionRegardlessOfHintCasing(string rawName, string hint)
    {
        var feature = new TrainerFeature(rawName, rawName, null, [], null, hint);

        Assert.True(FeatureSafetyPolicy.IsObjectFlagRecovery(feature));
        FeatureSafetyPolicy.Validate(feature, true);
    }

    [Fact]
    public void ObjectFlagRecoveryDoesNotCoverUnrelatedFeaturesWithZeroHint()
    {
        // 只有清单里那三条是"恢复/撤下对象标志"；别的功能带 0x00 仍是高风险，必须拦。
        var feature = new TrainerFeature("Select Unit Change ID", "改变单位编号", null, [], null, "0x00");

        Assert.False(FeatureSafetyPolicy.IsObjectFlagRecovery(feature));
        Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(feature, true));
    }

    [Fact]
    public void EnabledHighRiskFeatureWithOptInSkipsRestriction()
    {
        // 已开启"高风险功能"（opt-in）时不拦：这是面板上的显式授权路径。
        var feature = new TrainerFeature("Select Unit HP MAX", "选中单位血量拉满", null, [], "MustCode2+400", "0x05");

        Assert.True(FeatureSafetyPolicy.RequiresWorldPositionOptIn(feature));
        FeatureSafetyPolicy.Validate(feature, enabled: true, allowUnverifiedWorldPositionFeatures: true);
    }

    [Fact]
    public void DisabledHighRiskFeatureIsNotRestricted()
    {
        var feature = new TrainerFeature("Free Build", "随地建造", null, [], null, "0x01");

        FeatureSafetyPolicy.Validate(feature, enabled: false);
    }
}
