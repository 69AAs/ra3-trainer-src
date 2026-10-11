using System.Globalization;

namespace Ra3Trainer.Core.Features;

public sealed record ReinforcementSettings
{
    public const uint DefaultUnitId = 0x6586A5A0;
    public const int DefaultCount = 8;
    public const int DefaultRank = 3;
    public const int MinCount = 1;
    public const int MaxCount = EmpiricalSafetyBounds.MaximumReinforcementCount;
    public const int MinRank = 0;
    public const int MaxRank = 3;

    public static ReinforcementSettings Default { get; } =
        new(DefaultUnitId, DefaultCount, DefaultRank);

    public static ReinforcementSettings Parse(string unitIdText, string countText, string rankText)
    {
        var unitId = UnitCodeParser.Parse(unitIdText);
        var count = ParseDecimalInt(countText, "增援数量");
        var rank = ParseDecimalInt(rankText, "星级");
        try
        {
            return new ReinforcementSettings(unitId, count, rank);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Parse 的契约是"输入不合法 ⇒ FormatException"：区间越界也走同一种异常，
            // 调用方不必同时猜 FormatException 与 ArgumentOutOfRangeException（消息原文保留）。
            throw new FormatException(ex.Message, ex);
        }
    }

    public ReinforcementSettings(uint unitId, int count, int rank)
    {
        if (unitId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitId), "Unit id must be non-zero.");
        }
        if (count is < MinCount or > MaxCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"增援数量须为 {MinCount}–{MaxCount}（沿用旧参数范围，非已验证容量）；真实采样缺批次完成数量及帧耗时，尚未开放更高数量，本次拒绝派发。");
        }
        if (rank is < MinRank or > MaxRank)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), $"Rank must be between {MinRank} and {MaxRank}.");
        }

        UnitId = unitId;
        Count = count;
        Rank = rank;
    }

    public uint UnitId { get; }

    public int Count { get; }

    public int Rank { get; }

    private static int ParseDecimalInt(string text, string fieldLabel)
    {
        // 按"区域设置无关"解析：入参来自面板/预设序列化，两端用同一套规则（原来用当前文化，
        // 且标签是从变量名 nameof(countText) 反推出来的，换名字就会报错到另一个字段上）。
        if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{fieldLabel}请输入十进制整数，不能留空或超出整数范围。");
        }

        return value;
    }
}
