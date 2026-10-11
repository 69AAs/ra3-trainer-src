using System.Globalization;

namespace Ra3Trainer.Core.Features;

public sealed record ReinforcementPreset
{
    public ReinforcementPreset(string name, uint unitId, int count, int rank)
    {
        var settings = new ReinforcementSettings(unitId, count, rank);
        Name = string.IsNullOrWhiteSpace(name) ? $"0x{unitId:X8}" : name.Trim();
        UnitId = settings.UnitId;
        Count = settings.Count;
        Rank = settings.Rank;
    }

    public string Name { get; }

    public uint UnitId { get; }

    public int Count { get; }

    public int Rank { get; }

    public ReinforcementSettings ToSettings()
    {
        return new ReinforcementSettings(UnitId, Count, Rank);
    }

    public ReinforcementQueueEntry ToQueueEntry()
    {
        // 队列条目是字符串进、字符串出（ReinforcementQueueRunner 再 Parse 回来）：
        // 两端都用不变文化，避免依赖当前区域设置。
        return new ReinforcementQueueEntry(
            Name,
            $"0x{UnitId:X8}",
            Count.ToString(CultureInfo.InvariantCulture),
            Rank.ToString(CultureInfo.InvariantCulture));
    }
}
