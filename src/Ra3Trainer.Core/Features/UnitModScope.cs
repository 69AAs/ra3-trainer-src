using System;
using System.Collections.Generic;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 单位按 mod 分层的地基（用户 2026-10-07 问：换个 mod，那额外添加的单位其他 mod 也用不了，怎么安排）。
///
/// <para><b>两条轴</b>：</para>
/// <list type="bullet">
///   <item><b>单位属于哪个 mod</b> = 数据里的 <c>ModSource</c>（原版通用 / 日冕 / 待核对 / 杂物 …）；</item>
///   <item><b>当前在玩哪个 mod</b> = 启动器的 mod 键（<see cref="ReinforcementUnitCatalog.ModKeyFromLauncherPath"/>，
///         例如 CoronaLauncher.exe → corona）。</item>
/// </list>
///
/// <para><b>口径</b>：面板【选单位/找兵】默认只显示「当前 mod + 原版通用」的单位；其他 mod 的单位默认不出现，
/// 需要时打开"显示全部 mod 单位"开关放出来，并一律带来源标注（"仅日冕"）。原版环境（直接开 RA3.exe）下
/// 只显原版通用 ⇒ 神州这类 mod 阵营一个都不出现。</para>
///
/// <para><b>认不出启动器时（没配 / default）走宽松模式</b>：不过滤，保持旧行为、绝不因为"当前 mod 未知"就丢单位。</para>
/// </summary>
public static class UnitModScope
{
    /// <summary>视为「原版通用」的来源：任何 mod 环境都可见（原版三阵营单位 + 历史遗留的"待核对"条目）。</summary>
    public static readonly string[] BaseSources = ["通用", "待核对"];

    /// <summary>地图杂物来源：归「显示地图杂物」开关单独管，不属于任何 mod。</summary>
    public const string PropsSource = "杂物";

    /// <summary>明确的「原版游戏本体」启动器键 —— 只显原版通用，mod 阵营（神州之类）一律不出现。</summary>
    public static readonly string[] BaseModKeys = ["ra3", "redalert3", "redalert"];

    /// <summary>没配启动器 / 认不出的键：当前 mod 不确定 ⇒ 宽松（不过滤，保持旧行为、绝不丢单位）。</summary>
    public const string UnknownModKey = "default";

    /// <summary>
    /// 已知 mod：启动器键（归一化后）→ 数据里的 ModSource 显示名。
    /// 日冕目前是唯一的真 mod（CoronaLauncher.exe → 日冕）。
    /// <para>
    /// 其余 mod **多数不用改这里**：只要 launcher 名和 ModSource 归一化后同名（例如 ArkModLauncher.exe 与
    /// ModSource "ARKMOD" 都归一化成 "arkmod"），就自动对上；要在配置里加也行（见 settings.json 的 ModSourceAliases）。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultModSourceAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["corona"] = "日冕",
            ["coronamod"] = "日冕",
            // REMIX（用户实玩的 mod）：给他自己扫出来的单位一个像样的桶名，而不是挤进「其他」。
            ["remix"] = "REMIX",
            ["remix037"] = "REMIX",
        };

    /// <summary>归一化：去掉括号备注（"corona（本机读到）" → "corona"）、只留字母数字、转小写。</summary>
    /// <summary>
    /// 把 mod 键里的**版本号**去掉：`remix037` → `remix`、`corona3258` → `corona`。
    ///
    /// <para><b>为什么这是"机制"而不是小事</b>（2026-10-11 机制盘点）：认出 mod 的键是从 skudef/目录名归一化来的，
    /// 里面带版本号 ⇒ **mod 一升级，键就变了** ⇒ 玩家已经学会的单位清单（`units-&lt;键&gt;.txt`）与名字表缓存
    /// 全部对不上、要从头再长一遍 ✗。去掉版本号后同一个 mod 的各版本**共用一份清单**
    /// （单位靠代号区分，多版本合并不会串），升级无感。</para>
    ///
    /// <para>只在"剩下的前缀 ≥3 个字符且含字母"时才裁，避免把本来就像名字的键裁空。</para>
    /// </summary>
    public static string StripVersionSuffix(string? modKey)
    {
        var key = (modKey ?? string.Empty).Trim();
        if (key.Length == 0 || string.Equals(key, UnknownModKey, StringComparison.OrdinalIgnoreCase)) return key;

        var cut = key.Length;
        while (cut > 0 && (char.IsAsciiDigit(key[cut - 1]) || key[cut - 1] == '.'))
        {
            cut--;
        }

        var prefix = key[..cut];
        return prefix.Length >= 3 && prefix.Any(char.IsAsciiLetter) ? prefix : key;
    }

    public static string NormalizeToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var withoutNotes = StripParentheticals(text);
        var buffer = new System.Text.StringBuilder(withoutNotes.Length);
        foreach (var ch in withoutNotes)
        {
            if (char.IsLetterOrDigit(ch)) buffer.Append(char.ToLowerInvariant(ch));
        }
        return buffer.ToString();
    }

    private static string StripParentheticals(string text)
    {
        var buffer = new System.Text.StringBuilder(text.Length);
        var depth = 0;
        foreach (var ch in text)
        {
            if (ch is '（' or '(') { depth++; continue; }
            if (ch is '）' or ')') { if (depth > 0) depth--; continue; }
            if (depth == 0) buffer.Append(ch);
        }
        return buffer.ToString();
    }

    /// <summary>当前作用域：由启动器路径推当前 mod，再据此定可见范围。</summary>
    public static UnitScope ForLauncher(string? launcherPath, IReadOnlyDictionary<string, string>? aliases = null) =>
        ForModKey(ReinforcementUnitCatalog.ModKeyFromLauncherPath(launcherPath), aliases);

    /// <summary>当前作用域：由 mod 键（已归一化的启动器词干）推。</summary>
    public static UnitScope ForModKey(string? modKey, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var key = NormalizeToken(modKey);
        if (key.Length == 0 || string.Equals(key, UnknownModKey, StringComparison.Ordinal))
        {
            return UnitScope.Permissive;
        }

        if (Array.Exists(BaseModKeys, baseKey => string.Equals(baseKey, key, StringComparison.Ordinal)))
        {
            // 原版环境：只显原版通用（tokens 空 ⇒ 任何非原版来源都被挡）。
            return new UnitScope(filter: true, new HashSet<string>(StringComparer.Ordinal));
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal) { key };
        var map = aliases ?? DefaultModSourceAliases;
        if (map.TryGetValue(key, out var label))
        {
            var token = NormalizeToken(label);
            if (token.Length > 0) tokens.Add(token);
        }
        return new UnitScope(filter: true, tokens);
    }

    /// <summary>是不是「原版游戏本体」的键（ra3 / redalert3 / redalert）。</summary>
    public static bool IsBaseModKey(string? modKey)
    {
        var key = NormalizeToken(modKey);
        return key.Length > 0 &&
            Array.Exists(BaseModKeys, baseKey => string.Equals(baseKey, key, StringComparison.Ordinal));
    }

    /// <summary>mod 键 → 显示名（别名表里有就用别名，没有就还原样键）。</summary>
    public static string LabelFor(string? modKey, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var key = NormalizeToken(modKey);
        if (key.Length == 0) return string.Empty;
        var map = aliases is { Count: > 0 } ? aliases : DefaultModSourceAliases;
        return map.TryGetValue(key, out var label) && !string.IsNullOrWhiteSpace(label) ? label.Trim() : key;
    }

    /// <summary>
    /// 从游戏里新学到的单位该记成哪个来源 —— 按**识别出来的 mod 键**（而不是启动器路径）。
    /// 认不出就是"待核对"。
    /// </summary>
    public static string LearnedSourceForModKey(string? modKey, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var key = NormalizeToken(modKey);
        if (key.Length == 0 || string.Equals(key, UnknownModKey, StringComparison.Ordinal))
        {
            return "待核对";
        }

        return LabelFor(key, aliases);
    }

    /// <summary>从游戏里新学到的单位该记成哪个来源：当前 mod 的显示名（日冕）/ 归一化键；认不出就是"待核对"。</summary>
    public static string LearnedSourceFor(string? launcherPath, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var key = NormalizeToken(ReinforcementUnitCatalog.ModKeyFromLauncherPath(launcherPath));
        if (key.Length == 0 || string.Equals(key, UnknownModKey, StringComparison.Ordinal))
        {
            return "待核对";
        }

        var map = aliases ?? DefaultModSourceAliases;
        return map.TryGetValue(key, out var label) && label.Trim().Length > 0 ? label.Trim() : key;
    }

    /// <summary>来源标注：非原版通用、非杂物 → "仅{来源}"；否则空（原版单位不打标）。</summary>
    public static string SourceBadge(string? modSource)
    {
        var source = (modSource ?? string.Empty).Trim();
        if (source.Length == 0 || IsBaseSource(source) ||
            string.Equals(source, PropsSource, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return "仅" + source;
    }

    /// <summary>是不是「原版通用」来源（空也算，没来源标记的单位当原版）。</summary>
    public static bool IsBaseSource(string? modSource)
    {
        var source = (modSource ?? string.Empty).Trim();
        return source.Length == 0 || Array.Exists(BaseSources, baseSource => string.Equals(baseSource, source, StringComparison.Ordinal));
    }

    public static bool IsPropsSource(string? modSource) =>
        string.Equals((modSource ?? string.Empty).Trim(), PropsSource, StringComparison.Ordinal);
}

/// <summary>当前 mod 的可见范围：一条来源（<c>ModSource</c>）在当前环境下默认该不该出现在可选列表里。</summary>
public sealed class UnitScope
{
    private readonly HashSet<string> _tokens;

    internal UnitScope(bool filter, HashSet<string> tokens)
    {
        Filter = filter;
        _tokens = tokens;
    }

    /// <summary>false = 宽松（没配 / 认不出启动器，不过滤，保持旧行为）。</summary>
    public bool Filter { get; }

    /// <summary>宽松作用域：什么都可见（当前 mod 未知时的兜底）。</summary>
    public static UnitScope Permissive { get; } = new(filter: false, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>这条来源在当前作用域默认可见吗？</summary>
    public bool Allows(string? modSource)
    {
        if (!Filter) return true;
        if (UnitModScope.IsBaseSource(modSource)) return true;
        if (UnitModScope.IsPropsSource(modSource)) return false;

        var token = UnitModScope.NormalizeToken(modSource);
        return token.Length > 0 && _tokens.Contains(token);
    }

    public bool Allows(ReinforcementUnitEntry unit) => Allows(unit.ModSource);
}
