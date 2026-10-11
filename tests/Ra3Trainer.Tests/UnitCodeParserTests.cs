using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class UnitCodeParserTests
{
    [Theory]
    [InlineData("6586A5A0", 0x6586A5A0u)]
    [InlineData("6586a5a0", 0x6586A5A0u)]
    [InlineData("0x6586A5A0", 0x6586A5A0u)]
    [InlineData("0X6586a5a0", 0x6586A5A0u)]
    [InlineData("  0x6586A5A0  ", 0x6586A5A0u)]
    [InlineData("0X1", 0x1u)]
    [InlineData("0x80000000", 0x80000000u)]      // 高位为 1：uint 而不是 int
    [InlineData("ffffffff", uint.MaxValue)]      // uint 上界
    [InlineData("0", 0u)]
    public void TryParseAcceptsHexWithOrWithoutPrefix(string text, uint expected)
    {
        Assert.True(UnitCodeParser.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x")]                 // 只有前缀
    [InlineData("0x0x0E")]             // 两个前缀
    [InlineData("1A2B3C4D5E")]         // 9 位：超出 uint 的十六进制宽度
    [InlineData("100000000")]          // 溢出 uint
    [InlineData("65 86A5A0")]          // 中间空格
    [InlineData("6586A5A0zz")]         // 尾部垃圾
    [InlineData("-1")]                 // 有符号输入不接受
    [InlineData("xyz")]
    public void TryParseRejectsMalformedOrOutOfRangeInput(string? text)
    {
        Assert.False(UnitCodeParser.TryParse(text, out var value));
        Assert.Equal(0u, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1A2B3C4D5E")]
    [InlineData("xyz")]
    public void ParseThrowsFormatExceptionForBadInput(string? text)
    {
        Assert.Throws<FormatException>(() => UnitCodeParser.Parse(text));
    }

    [Fact]
    public void FormatRoundTripsThroughTryParse()
    {
        Assert.Equal("0x6586A5A0", UnitCodeParser.Format(0x6586A5A0));
        Assert.Equal("0x00000000", UnitCodeParser.Format(0));

        var text = UnitCodeParser.Format(0xAF4C0DA5);
        Assert.True(UnitCodeParser.TryParse(text, out var value));
        Assert.Equal(0xAF4C0DA5u, value);
    }
}
