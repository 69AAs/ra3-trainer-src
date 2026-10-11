namespace Ra3Trainer.Core.Features;

/// <summary>
/// 给"自动记下来的单位"起名字：内部名（<c>SovietNewTower</c>）→ 内置表里的中文名 → 都没有就「未命名」。
///
/// <para><b>用户 2026-10-11</b>：「扫出来的名字是新单位 0x12345678 这种，能不能用模板里的内部名去查中文名？」
/// 旧的占位写法有两个坑（同一个错犯过两次）：`新单位 0x代号` 会让代号被当成名字抄第二遍；
/// 而单纯的「未命名」等于没信息。这里统一口径：**能查到就用中文名，查不到就用内部名（比 hex 有用），
/// 再不行才「未命名」**。</para>
/// </summary>
public static class UnitNaming
{
    /// <summary>实在查不出来时的占位名（用户清单里可以自己改）。</summary>
    public const string UnknownName = "未命名";

    private static readonly Lazy<IReadOnlyList<UnitInternalName>> BuiltInNames =
        new(UnitInternalNameCatalog.LoadBuiltIn, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>内置内部名表（加载一次并缓存：它有上千条，别每次选单位都重读嵌入资源）。</summary>
    public static IReadOnlyList<UnitInternalName> InternalNames => BuiltInNames.Value;

    private static readonly Lazy<Dictionary<uint, string>> BuiltInByCode =
        new(BuildByCode, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>代号 → 内置库里的名字（缓存；清单自愈时按代号补名字用）。</summary>
    public static string? KnownName(uint code) =>
        BuiltInByCode.Value.TryGetValue(code, out var name) ? name : null;

    private static Dictionary<uint, string> BuildByCode()
    {
        var map = new Dictionary<uint, string>();
        foreach (var unit in ReinforcementUnitCatalog.LoadBuiltIn())
        {
            if (string.IsNullOrWhiteSpace(unit.Name)) continue;
            map.TryAdd(unit.Code, unit.Name.Trim());        // 同代号多条：留第一条
        }

        return map;
    }

    /// <summary>内部名 → 内置表里的显示名（查不到返回 null）。</summary>
    public static string? FindDisplayName(string? internalName, IReadOnlyList<UnitInternalName>? table = null)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return null;
        var names = table ?? InternalNames;
        var hit = names.FirstOrDefault(unit =>
            string.Equals(unit.InternalName, internalName.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(hit?.DisplayName) ? null : hit!.DisplayName.Trim();
    }

    /// <summary>
    /// 显示名口径（带**当前 mod 自己的名字表**，优先级最高）：
    /// ① mod 包里的 <c>NAME:&lt;内部名&gt;</c> ⇒ 那个 mod 自己的名字（它出中文就是中文）；
    /// ② 内置表命中 ⇒ 中文名；③ 内部名像样 ⇒ 直接用内部名；④ 否则「未命名」。
    /// ★ 有了 ①，市面上出现新 mod 时名字也能对上，不用等我们更新内置表。
    /// </summary>
    public static string DisplayNameFor(string? internalName, IReadOnlyDictionary<string, string>? modNames)
    {
        var text = (internalName ?? string.Empty).Trim();
        if (text.Length > 0 && modNames is not null && modNames.TryGetValue(text, out var fromMod) &&
            !string.IsNullOrWhiteSpace(fromMod))
        {
            return fromMod.Trim();
        }

        return DisplayNameFor(internalName, (IReadOnlyList<UnitInternalName>?)null);
    }

    /// <summary>
    /// 显示名口径：内置表命中 ⇒ 中文名；否则内部名像样 ⇒ 直接用内部名；否则「未命名」。
    /// </summary>
    public static string DisplayNameFor(string? internalName, IReadOnlyList<UnitInternalName>? table = null)
    {
        var display = FindDisplayName(internalName, table);
        if (display is not null) return display;
        return UnitTemplateNameProbe.LooksLikeInternalName(internalName)
            ? internalName!.Trim()
            : UnknownName;
    }
}
