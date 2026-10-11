using System.Reflection;

namespace Ra3Trainer.Core.Features;

/// <summary>单位内部名（游戏里的英文类型名）+ 显示名。</summary>
public sealed record UnitInternalName(string InternalName, string DisplayName, string Source);

/// <summary>
/// 内置的"单位内部名清单"（日冕 mod 单位 + 战役隐藏单位 + 原版单位）。
/// 用途：从游戏内存里把内部名对应的"代号"（运行时类型 ID）挖出来 —— 隐藏单位（如 JapanMechaKing）
/// 的代号数据文件里没有、也算不出来，只能这样读。
/// </summary>
public static class UnitInternalNameCatalog
{
    public static IReadOnlyList<UnitInternalName> LoadBuiltIn()
    {
        var assembly = typeof(UnitInternalNameCatalog).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("unit-internal-names.txt", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded resource unit-internal-names.txt was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded resource {resourceName}.");
        using var reader = new StreamReader(stream);

        var units = new List<UnitInternalName>();
        while (reader.ReadLine() is string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length == 0 || parts[0].Trim().Length == 0)
            {
                continue;
            }

            units.Add(new UnitInternalName(
                parts[0].Trim(),
                parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : parts[0].Trim(),
                parts.Length > 2 ? parts[2].Trim() : string.Empty));
        }

        return units;
    }

    /// <summary>已知代号表（code.txt）：用来在校准日志里认出"这就是类型表"。</summary>
    public static IReadOnlyDictionary<uint, string> KnownCodes()
    {
        // 原来用 ToDictionary：code.txt 里出现重复代号（人工清单 + 全量目录合并、
        // 或手改过的 units-*.txt）就抛 ArgumentException，扫描单位名表整条链当场失败，
        // 而"代号唯一"这个前提是在另一个类（ReinforcementUnitCatalog.Merge）里维持的。
        var map = new Dictionary<uint, string>();
        foreach (var unit in ReinforcementUnitCatalog.LoadBuiltIn())
        {
            map.TryAdd(unit.Code, unit.Name);
        }

        return map;
    }
}
