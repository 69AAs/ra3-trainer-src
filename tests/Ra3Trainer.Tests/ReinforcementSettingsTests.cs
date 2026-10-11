using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ReinforcementSettingsTests
{
    [Theory]
    [InlineData("0x6586A5A0")]
    [InlineData("6586a5a0")]
    [InlineData("6586A5A0")]
    [InlineData("  0X6586a5a0  ")]
    public void ParseUnitIdAcceptsHexWithOrWithoutPrefixAndAnyCase(string unitIdText)
    {
        var settings = ReinforcementSettings.Parse(unitIdText, "8", "3");

        Assert.Equal(0x6586A5A0u, settings.UnitId);
    }

    [Theory]
    [InlineData("0x00000000")] // 单位 ID 为 0：构造函数拒绝，Parse 统一包成 FormatException
    [InlineData("0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyz")]
    [InlineData("0x")]
    [InlineData("1FFFFFFFF")]  // 超出 uint 范围
    public void ParseRejectsMalformedOrZeroUnitIds(string unitIdText)
    {
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse(unitIdText, "8", "3"));
    }

    [Fact]
    public void ParseRejectsCountAndRankJustOutsideTheirRanges()
    {
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse("0x6586A5A0", "0", "3"));
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse(
            "0x6586A5A0", (ReinforcementSettings.MaxCount + 1).ToString(), "3"));
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse("0x6586A5A0", "", "3"));
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse("0x6586A5A0", "8", "x"));
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse("0x6586A5A0", "8", "-1"));
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse(
            "0x6586A5A0", "8", (ReinforcementSettings.MaxRank + 1).ToString()));
    }

    [Fact]
    public void ParseAcceptsTheClosedRangeEndpointsForCountAndRank()
    {
        var min = ReinforcementSettings.Parse("0x6586A5A0",
            ReinforcementSettings.MinCount.ToString(), ReinforcementSettings.MinRank.ToString());
        Assert.Equal(ReinforcementSettings.MinCount, min.Count);
        Assert.Equal(ReinforcementSettings.MinRank, min.Rank);

        var max = ReinforcementSettings.Parse("0x6586A5A0",
            ReinforcementSettings.MaxCount.ToString(), ReinforcementSettings.MaxRank.ToString());
        Assert.Equal(ReinforcementSettings.MaxCount, max.Count);
        Assert.Equal(ReinforcementSettings.MaxRank, max.Rank);
    }
}
