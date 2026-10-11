using System.Globalization;

namespace Ra3Trainer.Core.Features;

public sealed record ResourceValueSettings
{
    public const int DefaultMoneyAmount = 100000;
    public const int DefaultPowerValue = 100000;
    public const int DefaultScPointValue = 15;
    public const int MinResourceValue = 1;
    public const int MaxResourceValue = 99999999;
    public const int MinScPointValue = 0;
    public const int MaxScPointValue = 15;

    public static ResourceValueSettings Default { get; } =
        new(DefaultMoneyAmount, DefaultPowerValue, DefaultScPointValue);

    public ResourceValueSettings(int moneyAmount, int powerValue, int scPointValue)
    {
        ValidateRange(moneyAmount, MinResourceValue, MaxResourceValue, nameof(moneyAmount));
        ValidateRange(powerValue, MinResourceValue, MaxResourceValue, nameof(powerValue));
        ValidateRange(scPointValue, MinScPointValue, MaxScPointValue, nameof(scPointValue));

        MoneyAmount = moneyAmount;
        PowerValue = powerValue;
        ScPointValue = scPointValue;
    }

    public int MoneyAmount { get; }

    public int PowerValue { get; }

    public int ScPointValue { get; }

    public static ResourceValueSettings Parse(string moneyAmountText, string powerValueText, string scPointValueText)
    {
        return new ResourceValueSettings(
            ParseInt(moneyAmountText, nameof(moneyAmountText), MinResourceValue, MaxResourceValue),
            ParseInt(powerValueText, nameof(powerValueText), MinResourceValue, MaxResourceValue),
            ParseInt(scPointValueText, nameof(scPointValueText), MinScPointValue, MaxScPointValue));
    }

    private static int ParseInt(string text, string parameterName, int min, int max)
    {
        // 解析失败与越界都归到同一个字段名、同一种异常：原来越界要靠构造函数抛，
        // 报出来的是 moneyAmount（值参数名）而不是钱那一栏的输入（moneyAmountText），
        // 调用方按名字/按类型都分不清是哪一栏错了。
        if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{parameterName} must be a decimal integer.");
        }

        if (value < min || value > max)
        {
            throw new FormatException($"{parameterName} must be between {min} and {max}.");
        }

        return value;
    }

    private static void ValidateRange(int value, int min, int max, string parameterName)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value must be between {min} and {max}.");
        }
    }
}
