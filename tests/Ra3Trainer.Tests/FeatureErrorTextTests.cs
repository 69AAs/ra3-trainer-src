using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 守卫层的技术拒绝文案 → 用户话（2026-10-11 文案评审）。
/// 口径：**技术原文照旧进日志**，只有给人看的那一层翻译；认不出来的一律原样返回，绝不吞信息。
/// </summary>
public sealed class FeatureErrorTextTests
{
    [Theory]
    [InlineData("单位类型不在已知单位目录中，无法校验类型，已拒绝生成（type=0xB31A4632）", "还没被识别")]
    [InlineData("建造目标与地形合法性缺少可只读校验的对象根链：已定位的网格判定依赖游戏上下文", "读稳")]
    [InlineData("生命值不是有限正数（读到 -1，地址 0x0A1B2C3D）——对象可能已死亡或链读歪", "血量")]
    [InlineData("当前没有选中单位", "选中一个单位")]
    [InlineData("世界坐标仍为零哨兵，本次转换不新鲜或未完整写入，已拒绝生成", "鼠标")]
    public void TechnicalRejectionBecomesUserText(string technical, string expectedFragment)
    {
        var text = FeatureErrorText.ToUserText(technical);

        Assert.Contains(expectedFragment, text, StringComparison.Ordinal);
        // 用户文案里不许再出现这些内部词。
        foreach (var jargon in new[] { "对象根链", "零哨兵", "链读歪", "理智线", "两阶段", "type=0x", "指针" })
        {
            Assert.DoesNotContain(jargon, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnknownTechnicalTextIsPassedThroughUnchanged()
    {
        const string technical = "某个还没归类的技术原因（读到 0x1234）";

        Assert.Equal(technical, FeatureErrorText.ToUserText(technical));
    }

    [Fact]
    public void EmptyTextStillProducesAnAnswer()
    {
        Assert.False(string.IsNullOrWhiteSpace(FeatureErrorText.ToUserText(null)));
        Assert.False(string.IsNullOrWhiteSpace(FeatureErrorText.ToUserText("   ")));
    }
}
