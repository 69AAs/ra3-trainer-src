using Ra3Trainer.Core.Codegen;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class AaNumberParserTests
{
    [Theory]
    [InlineData("#100000", 100000)]
    [InlineData("000186A0", 0x186A0)]
    [InlineData("A", 0xA)]
    [InlineData("d", 0xD)]
    [InlineData("0x10", 0x10)]
    public void ParseUsesCheatEngineNumberSemantics(string input, int expected)
    {
        Assert.Equal(expected, AaNumberParser.ParseInt32(input));
    }

    [Fact]
    public void ParseUInt32KeepsHighBitConstants()
    {
        Assert.Equal(0xAF4C0DA5u, AaNumberParser.ParseUInt32("AF4C0DA5"));
        Assert.Equal(99_999_999u, AaNumberParser.ParseUInt32("#99999999"));
    }

    [Fact]
    public void ParseRejectsEmptyHexPrefixedAndMalformedInput()
    {
        // 空串、只有前缀、非十六进制字符都必须按契约抛 FormatException，不能被静默吞掉或当成 0。
        Assert.Throws<FormatException>(() => AaNumberParser.ParseUInt32(""));
        Assert.Throws<FormatException>(() => AaNumberParser.ParseUInt32("#"));
        Assert.Throws<FormatException>(() => AaNumberParser.ParseUInt32("0x"));
        Assert.Throws<FormatException>(() => AaNumberParser.ParseUInt32("ZZ"));
    }

    [Fact]
    public void ParseRejectsValuesBeyondUInt32Range()
    {
        // 十进制 # 前缀走 uint.Parse(Integer)：溢出必须抛 OverflowException，而不是回绕。
        Assert.Throws<OverflowException>(() => AaNumberParser.ParseUInt32("#4294967296"));
        Assert.Throws<OverflowException>(() => AaNumberParser.ParseUInt32("#99999999999999999999"));
    }

    [Theory]
    [InlineData("  0x10  ", 0x10u)]
    [InlineData("  1F  ", 0x1Fu)]
    [InlineData("\tA\n", 0xAu)]
    [InlineData("  #100  ", 100u)]
    public void ParseTrimsSurroundingWhitespaceBeforeDecoding(string input, uint expected)
    {
        Assert.Equal(expected, AaNumberParser.ParseUInt32(input));
    }

    [Fact]
    public void ParseInt32WrapsHighBitConstantsToTwosComplement()
    {
        // ParseInt32 是 unchecked((int)ParseUInt32(...))：高位置 1 的常量必须按二进制补码回绕成负数。
        Assert.Equal(unchecked((int)0xAF4C0DA5u), AaNumberParser.ParseInt32("AF4C0DA5"));
        Assert.Equal(int.MinValue, AaNumberParser.ParseInt32("80000000"));
        Assert.Equal(-1, AaNumberParser.ParseInt32("FFFFFFFF"));
    }
}
