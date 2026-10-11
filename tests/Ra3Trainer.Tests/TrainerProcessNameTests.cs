using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerProcessNameTests
{
    [Theory]
    [InlineData("ra3_1.12.game", "ra3_1.12")]
    [InlineData("ra3_1.12", "ra3_1.12")]
    [InlineData("RA3.exe", "RA3")]
    public void ToProcessNameOnlyRemovesExecutableSuffixes(string input, string expected)
    {
        Assert.Equal(expected, TrainerProcessName.ToProcessName(input));
    }

    [Theory]
    [InlineData("ra3_1.12", "ra3_1.12.game")]
    [InlineData("ra3_1.12.game", "ra3_1.12")]
    public void MatchesTreatsGameExtensionAsExecutableSuffix(string left, string right)
    {
        Assert.True(TrainerProcessName.Matches(left, right));
        // 对称性：两个方向都要成立，否则后缀处理是单向的。
        Assert.True(TrainerProcessName.Matches(right, left));
    }

    [Theory]
    [InlineData("ra3_1.12", "ra3_1.12.game")]
    [InlineData("ra3_1.12.game", "ra3_1.12.game")]
    [InlineData("RA3.exe", "ra3.exe")]
    public void MatchesIsReflexiveAfterNormalisation(string left, string right)
    {
        Assert.True(TrainerProcessName.Matches(left, right));
    }

    [Theory]
    [InlineData("ra3_1.12", "ra3_1.13")]      // 基名不同
    [InlineData("ra3_1.12.exe", "ra3_1.13.game")] // 扩展名不同、基名也不同
    [InlineData("ra3_1.12", "ra3_1.12help")]
    public void MatchesRejectsDifferentBaseNames(string left, string right)
    {
        Assert.False(TrainerProcessName.Matches(left, right));
    }

    [Theory]
    [InlineData("ra3_1.12.GAME", "ra3_1.12")]
    [InlineData("RA3.EXE", "RA3")]
    [InlineData("C:\\Games\\ra3_1.12.game", "ra3_1.12")] // 带路径：先取文件名再剥后缀
    [InlineData("  ra3_1.12.game  ", "ra3_1.12")]          // 前后空白
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ToProcessNameStripsSuffixCaseInsensitivelyAndReducesToFileName(string input, string expected)
    {
        Assert.Equal(expected, TrainerProcessName.ToProcessName(input));
    }

    [Theory]
    [InlineData("ra3_1.12.game", "ra3_1.12")]
    [InlineData("ra3_1.12", "ra3_1.12")]
    [InlineData("RA3.exe", "RA3")]
    [InlineData("ra3_1.12.GAME", "ra3_1.12")]
    [InlineData("RA3.EXE", "RA3")]
    [InlineData("Game.exe", "Game")]       // 只有真扩展名才被剥掉
    [InlineData("my.game.data", "my.game.data")]
    public void ToProcessNameOnlyRemovesExecutableSuffixesCaseInsensitively(string input, string expected)
    {
        Assert.Equal(expected, TrainerProcessName.ToProcessName(input));
    }
}
