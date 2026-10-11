using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerVersionPolicyTests
{
    [Theory]
    [InlineData("1.12.3444.25830")]
    [InlineData(" 1.12.3444.25830 ")]
    public void IsExactAcceptsOnlyTheBuildTheHooksWereDerivedFrom(string version)
    {
        Assert.True(TrainerVersionPolicy.IsExact(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.12.5000.12345")]
    [InlineData("1.13.0.1")]
    public void IsExactRejectsEverythingElse(string version)
    {
        Assert.False(TrainerVersionPolicy.IsExact(version));
    }

    [Theory]
    [InlineData("1.12.3444.25830")]
    [InlineData("1.12.9999.99999")]
    [InlineData("1.12")]
    public void IsSameProductLineAcceptsAny112Build(string version)
    {
        Assert.True(TrainerVersionPolicy.IsSameProductLine(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.1.2.3")]
    [InlineData("1.120.1")]
    [InlineData("1.13.0.1")]
    [InlineData("11.12.0.1")]
    public void IsSameProductLineRejectsOtherProductLines(string version)
    {
        Assert.False(TrainerVersionPolicy.IsSameProductLine(version));
    }

    [Fact]
    public void IsSupportedStaysStrictUntilCompatibilityModeIsEnabled()
    {
        Assert.False(TrainerVersionPolicy.IsSupported("1.12.5000.12345", allowNonStandardVersion: false));
        Assert.True(TrainerVersionPolicy.IsSupported("1.12.5000.12345", allowNonStandardVersion: true));
        Assert.True(TrainerVersionPolicy.IsSupported("1.12.3444.25830", allowNonStandardVersion: false));
        Assert.False(TrainerVersionPolicy.IsSupported("1.13.0.1", allowNonStandardVersion: true));
    }

    [Fact]
    public void CompatibilityModeIsExactlyForNonExactSameProductLineBuilds()
    {
        // 兼容模式存在的唯一理由：打了补丁的 1.12 二进制（版本号不是原版那个完整串）。
        Assert.False(TrainerVersionPolicy.IsSupported("1.12.9999.99999", allowNonStandardVersion: false));
        Assert.True(TrainerVersionPolicy.IsSupported("1.12.9999.99999", allowNonStandardVersion: true));
        Assert.True(TrainerVersionPolicy.IsSupported("1.12", allowNonStandardVersion: true));
        // 别的产品线开了兼容也不放行。
        Assert.False(TrainerVersionPolicy.IsSupported("1.13.0.1", allowNonStandardVersion: true));
        Assert.False(TrainerVersionPolicy.IsSupported("1.11.0.1", allowNonStandardVersion: true));
    }

    [Theory]
    [InlineData("1.12a")]      // 段不是精确的 "12"
    [InlineData("1.120.0.0")]  // 段是 "120" 而不是 "12"
    [InlineData("11.12.0.0")]  // 主段不是 "1"
    public void SameProductLineNeedsExactSegmentsNotPrefixes(string version)
    {
        // 逐段精确相等（不用 StartsWith）：前缀式匹配会把 1.120/1.12a 这类构建也放进来。
        Assert.False(TrainerVersionPolicy.IsSameProductLine(version));
    }

    [Theory]
    [InlineData("1.12.0.0")]
    [InlineData("1.12.")]
    [InlineData("1.12")]
    public void SameProductLineAcceptsAnyBuildWithTheExactFirstTwoSegments(string version)
    {
        Assert.True(TrainerVersionPolicy.IsSameProductLine(version));
    }

    [Theory]
    [InlineData("\t1.12.3444.25830\n")]   // FileVersion 常带尾随控制字符
    [InlineData(" 1.12.3444.25830")]
    public void IsExactTrimsSurroundingWhitespaceIncludingControlCharacters(string version)
    {
        Assert.True(TrainerVersionPolicy.IsExact(version));
    }

    [Fact]
    public void IsExactRejectsInternalWhitespace()
    {
        Assert.False(TrainerVersionPolicy.IsExact("1.12. 3444.25830"));
    }
}
