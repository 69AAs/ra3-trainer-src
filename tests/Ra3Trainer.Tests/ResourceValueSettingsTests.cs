using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ResourceValueSettingsTests
{
    [Fact]
    public void DefaultValuesMatchBootstrapConstants()
    {
        var settings = ResourceValueSettings.Default;

        // 引用公开常量：常量一改这里跟着走，断的是"Default 接线"而不是重抄一遍字面量。
        Assert.Equal(ResourceValueSettings.DefaultMoneyAmount, settings.MoneyAmount);
        Assert.Equal(ResourceValueSettings.DefaultPowerValue, settings.PowerValue);
        Assert.Equal(ResourceValueSettings.DefaultScPointValue, settings.ScPointValue);
    }

    [Theory]
    [InlineData("1", "1", "0", 1, 1, 0)]
    [InlineData("99999999", "99999999", "15", 99999999, 99999999, 15)]
    [InlineData(" 100000 ", " 200000 ", " 9 ", 100000, 200000, 9)]
    public void ParseAcceptsValidUiInput(
        string moneyText,
        string powerText,
        string scPointText,
        int money,
        int power,
        int scPoint)
    {
        var settings = ResourceValueSettings.Parse(moneyText, powerText, scPointText);

        Assert.Equal(money, settings.MoneyAmount);
        Assert.Equal(power, settings.PowerValue);
        Assert.Equal(scPoint, settings.ScPointValue);
    }

    [Theory]
    [InlineData("", "100000", "15")]
    [InlineData("0", "100000", "15")]
    [InlineData("100000000", "100000", "15")]
    [InlineData("100000", "0", "15")]
    [InlineData("100000", "100000000", "15")]
    [InlineData("100000", "100000", "-1")]
    [InlineData("100000", "100000", "16")]
    [InlineData("many", "100000", "15")]
    public void ParseRejectsInvalidUiInput(string moneyText, string powerText, string scPointText)
    {
        // 契约是"输入不合法 ⇒ FormatException"（解析失败与越界同一种，见 ResourceValueSettings.ParseInt）：
        // ThrowsAny<Exception> 会连 NRE/OverflowException 一起放过，这里必须钉死异常类型。
        Assert.Throws<FormatException>(() => ResourceValueSettings.Parse(moneyText, powerText, scPointText));
    }

    [Fact]
    public void ParseTreatsNullAsEmptyInputAndStillRejectsIt()
    {
        // 绑定层可能送出 null：必须按空输入处理并抛 FormatException，而不是 NRE。
        Assert.Throws<FormatException>(() => ResourceValueSettings.Parse(null!, "100000", "15"));
        Assert.Throws<FormatException>(() => ResourceValueSettings.Parse("100000", null!, "15"));
        Assert.Throws<FormatException>(() => ResourceValueSettings.Parse("100000", "100000", null!));
    }
}
