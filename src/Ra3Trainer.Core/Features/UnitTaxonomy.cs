using System;
using System.Collections.Generic;

namespace Ra3Trainer.Core.Features;

/// <summary>兵种类别（一级）：陆 / 空 / 海 / 建筑 / 特殊。</summary>
public enum UnitDomain
{
    Infantry,
    Vehicle,
    Aircraft,
    Ship,
    Structure,
    Special,
}

/// <summary>兵种用途（二级）：反什么、干什么。</summary>
public enum UnitRole
{
    General,
    AntiInfantry,
    AntiVehicle,
    AntiAir,
    AntiNavy,
    AntiStructure,
    AntiGround,
    Engineer,
    Scout,
    Infiltration,
    Transport,
    Hero,
    SuperWeapon,
}

/// <summary>
/// 从游戏自己的命名里读兵种分类，而不是我们另外编一套。
///
/// 依据：游戏单位库的 editor_name 就是 <c>&lt;阵营&gt;&lt;用途&gt;&lt;类别&gt;</c>——
/// 例如 <c>JapanAntiNavyShipTech3</c>（帝国·反舰·舰船·三级 = 太刀重巡洋舰）、
/// <c>SovietAntiVehicleVehicleTech3</c>（天启坦克）、<c>AlliedScoutInfantry</c>（警犬）。
/// 所以"陆军/空军/海军/建筑"这种分法不是我们发明的，是游戏自己的说法。
///
/// 规则是**先摘掉用途再判类别**，否则 <c>AntiAir</c> 会被当成飞行器、
/// <c>AntiNavy</c> 会被当成舰船（VX/天狗这类"车辆但防空"的会全错）。
/// 拿不准的一律落到 <see cref="UnitDomain.Special"/>，宁可让用户看见"特殊"，
/// 也不要把坦克塞进海军。
/// </summary>
public static class UnitTaxonomy
{
    // 出现顺序 = 优先级。建筑必须排在车辆前面（NavalYard 是码头不是船），
    // 舰船必须排在飞行器前面（YumiAircraftCarrier 是航母，不是飞机）。
    private static readonly (string[] Words, UnitDomain Domain)[] DomainRules =
    [
        (["Egg", "Structure", "Yard", "Refinery", "Barracks", "Factory", "Wall", "Tower",
          "Pylon", "Pad", "Core", "Beacon", "Defense", "Plant", "Crane", "Airfield", "AirBase", "Runway"],
         UnitDomain.Structure),
        (["Ship", "Carrier", "Vessel", "Boat", "Submarine", "Sub"], UnitDomain.Ship),
        (["Aircraft", "Plane", "Heli", "Chopper", "Interceptor", "Flying", "Zeppelin",
          "Blimp", "Bomber", "Fighter"], UnitDomain.Aircraft),
        (["Vehicle", "Tank", "Walker", "Mecha", "Mech", "Cycle", "Sled", "Truck",
          "Artillery", "MCV", "Miner", "Surveyor", "Grinder"], UnitDomain.Vehicle),
        (["Infantry", "Commando", "Engineer", "Scout"], UnitDomain.Infantry),
    ];

    private static readonly (string Word, UnitRole Role)[] RoleRules =
    [
        ("AntiInfantry", UnitRole.AntiInfantry),
        ("AntiVehicle", UnitRole.AntiVehicle),
        ("AntiAir", UnitRole.AntiAir),
        ("AntiNavy", UnitRole.AntiNavy),
        ("AntiStructure", UnitRole.AntiStructure),
        ("AntiGround", UnitRole.AntiGround),
        ("Infiltration", UnitRole.Infiltration),
        ("Transport", UnitRole.Transport),
        ("Commando", UnitRole.Hero),
        ("Scout", UnitRole.Scout),
        ("SuperWeapon", UnitRole.SuperWeapon),
        ("Engineer", UnitRole.Engineer),
    ];

    /// <summary>一级类别。标识符缺失时退回中文显示名，最后落 Special。</summary>
    public static UnitDomain Domain(string? editorName, string? displayName = null)
    {
        var tokens = Tokens(editorName);
        var stripped = StripRole(tokens);
        for (var i = stripped.Count - 1; i >= 0; --i)
        {
            foreach (var (words, domain) in DomainRules)
            {
                foreach (var word in words)
                {
                    if (string.Equals(stripped[i], word, StringComparison.OrdinalIgnoreCase))
                    {
                        return domain;
                    }
                }
            }
        }

        // 整个 editor_name 全大写时（JAPANMECHAKING / ALLIEDMCV）会被并成**一整段**缩写，
        // 逐词相等永远认不出。对这种整段再做一次"包含"匹配（仍按 DomainRules 优先级），
        // 否则 JAPANMECHAKING 只能靠中文名兜底，而"将军刽子手"里没有任何关键词 → 落到"特殊"。
        foreach (var token in stripped)
        {
            if (!IsAcronymRun(token)) continue;
            foreach (var (words, domain) in DomainRules)
            {
                foreach (var word in words)
                {
                    if (token.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        return domain;
                    }
                }
            }
        }

        return FromDisplayName(displayName);
    }

    /// <summary>整段都是大写字母/数字且够长 —— Tokens 里被并回去的缩写串。</summary>
    private static bool IsAcronymRun(string token) =>
        token.Length >= 4 && token.All(ch => char.IsUpper(ch) || char.IsDigit(ch));

    /// <summary>二级用途。</summary>
    public static UnitRole Role(string? editorName, string? displayName = null)
    {
        var tokens = Tokens(editorName);
        foreach (var (word, role) in RoleRules)
        {
            foreach (var token in tokens)
            {
                if (string.Equals(token, word, StringComparison.OrdinalIgnoreCase))
                {
                    return role;
                }
            }
        }

        // "Anti" 单独一段时，看它后面那一段。
        for (var i = 0; i + 1 < tokens.Count; ++i)
        {
            if (!string.Equals(tokens[i], "Anti", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (word, role) in RoleRules)
            {
                if (word.StartsWith("Anti", StringComparison.Ordinal) &&
                    word.AsSpan(4).Equals(tokens[i + 1], StringComparison.OrdinalIgnoreCase))
                {
                    return role;
                }
            }
        }

        // 认不出用途就是通用：宁可显示"通用"，也不要猜错用途。
        return UnitRole.General;
    }
    /// <summary>
    /// 内部名一个都认不出来时，退回中文显示名。**顺序有讲究**，全靠真实目录里的用词定的：
    /// 「先锋武装炮艇机」「基洛夫飞艇」带"艇"但是空军，所以飞行器必须排在舰船前面；
    /// 「多功能步兵战车」带"兵"但是车辆，所以车辆排在步兵前面；
    /// 「新兵训练营」「神机营」带"兵/机"但是建筑，所以建筑也排在步兵前面；
    /// 「娜塔莎」带"塔"但是英雄 —— 这条靠内部名里的 Commando 挡住（见 StripRole 的注释）。
    /// </summary>
    private static UnitDomain FromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return UnitDomain.Special;
        }

        // 人工清单（code.txt）只有代号 + 中文名，没有内部名，所以中文名兜底必须扛住。
        // 少数名字会被关键词误判，逐条钉死并写测试 —— 不允许悄悄错。
        foreach (var (name, domain) in NameExceptions)
        {
            if (Contains(displayName, name))
            {
                return domain;
            }
        }

        foreach (var (words, domain) in NameRules)
        {
            foreach (var word in words)
            {
                if (Contains(displayName, word))
                {
                    return domain;
                }
            }
        }

        return UnitDomain.Special;
    }

    private static readonly (string[] Words, UnitDomain Domain)[] NameRules =
    [
        (["飞艇", "飞船", "艇机", "直升机", "轰炸机", "航空", "战机", "无人机", "空艇", "海翼", "舰载机", "舰载"],
         UnitDomain.Aircraft),
        (["航母", "潜艇", "巡洋", "驱逐", "战列", "战舰", "登陆舰", "舰", "海豚", "水翼船", "艇"],
         UnitDomain.Ship),
        (["核心", "要塞", "船坞", "泊坞", "码头", "兵营", "训练营", "军营", "营", "碉堡", "堡垒",
          "堡", "围墙", "墙", "炮塔", "塔", "停机坪", "矿厂", "精炼厂", "发电厂", "工厂", "兵工厂",
          "装甲工厂", "主基地", "哨兵枪", "空军基地", "机场", "航台"], UnitDomain.Structure),
        (["坦克", "机甲", "战车", "运载", "载具", "机器人", "歼击车", "采矿车", "基地车", "起重机",
          "MCV", "ACV", "平定者", "粉碎者", "激流", "鬼王", "打击者", "炮", "车"], UnitDomain.Vehicle),
        (["步兵", "武士", "忍者", "工兵", "兵", "警犬", "间谍", "谭雅", "战熊", "部队", "少女",
          "浪人", "百合子", "野泉", "火箭天使"], UnitDomain.Infantry),
    ];

    /// <summary>中文名兜底会误判的条目（都是人工清单里没有内部名的）。每条都有测试钉着。</summary>
    private static readonly (string Name, UnitDomain Domain)[] NameExceptions =
    [
        ("娜塔莎", UnitDomain.Infantry),   // 名字里带"塔"，但是苏联英雄步兵
    ];

    /// <summary>拆 CamelCase / 下划线 / 数字尾巴。<c>JapanAntiNavyShipTech3_Enhanced</c> → Japan,Anti,Navy,Ship,Tech3。</summary>
    private static List<string> Tokens(string? editorName)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(editorName))
        {
            return tokens;
        }

        var cleaned = TrimVariantSuffix(editorName);
        var current = new System.Text.StringBuilder();
        foreach (var ch in cleaned)
        {
            if (ch == '_' || ch == '-' || ch == ' ' || ch == '.')
            {
                Flush(tokens, current);
                continue;
            }

            if (char.IsUpper(ch) && current.Length > 0)
            {
                Flush(tokens, current);
            }

            current.Append(ch);
        }

        Flush(tokens, current);

        // 全大写缩写会被逐字母拆开（MCV → M,C,V）：把连续的单个大写字母并回去，
        // 否则 SovietMCV（基地车）就认不出 Vehicle 这个类别，只能退回中文名兜底。
        var merged = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (merged.Count > 0 && IsSingleUpper(token) && IsSingleUpper(merged[^1]))
            {
                merged[^1] += token;
                continue;
            }

            merged.Add(token);
        }

        return merged;
    }

    private static bool IsSingleUpper(string token) => token.Length == 1 && char.IsUpper(token[0]);

    private static void Flush(List<string> tokens, System.Text.StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        // 纯数字段（Tech3 的 3 会被单独拆出来）并回去，便于按词比较。
        var text = current.ToString();
        current.Clear();
        if (tokens.Count > 0 && text.Length <= 2 && char.IsDigit(text[0]))
        {
            tokens[^1] += text;
            return;
        }

        tokens.Add(text);
    }

    /// <summary><c>_Enhanced</c>/<c>_Naval</c>/<c>_Ambush</c> 这类变体尾巴不参与分类。</summary>
    private static string TrimVariantSuffix(string name)
    {
        foreach (var suffix in VariantSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static readonly string[] VariantSuffixes =
    [
        "_Enhanced_Naval", "_Enhanced_Water", "_Enhanced_Land", "_Enhanced_Air",
        "_Enhanced", "_Ambush", "_Naval", "_Water", "_Land", "_Old",
    ];

    /// <summary>
    /// 把"用途段"摘出去，剩下的段落才用来判类别。
    ///
    /// 只摘 Anti + 它指的目标（Anti Vehicle → 摘掉 Vehicle，否则这辆车会被按"反车辆"
    /// 当成车辆类别 —— 碰巧对，但 AntiNavy 就会把船当成目标类别）。
    /// **Commando / Scout / Engineer 刻意不摘**：它们既是用途也是类别线索 ——
    /// 摘掉的话「娜塔莎」（<c>JapanCommandoTech1</c>）会落到中文名兜底，
    /// 而"娜塔莎"里有个"塔"字，就会被错判成建筑。
    /// </summary>
    private static List<string> StripRole(List<string> tokens)
    {
        var kept = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; ++i)
        {
            var token = tokens[i];
            if (string.Equals(token, "Anti", StringComparison.OrdinalIgnoreCase))
            {
                ++i;   // 连它指的目标一起摘掉
                continue;
            }

            if (StrippedRoleWords.Any(word => string.Equals(token, word, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(token);
        }

        return kept;
    }

    private static readonly string[] StrippedRoleWords = ["Infiltration", "Transport"];

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    /// <summary>面板/主窗显示用的一级标签（含用途含义）。</summary>
    public static string DomainLabel(UnitDomain domain) => domain switch
    {
        UnitDomain.Infantry => "陆军 · 步兵",
        UnitDomain.Vehicle => "陆军 · 车辆",
        UnitDomain.Aircraft => "空军 · 飞行器",
        UnitDomain.Ship => "海军 · 舰船",
        UnitDomain.Structure => "建筑 · 防御/生产",
        _ => "特殊",
    };

    /// <summary>
    /// 找兵流程**同一分类维度**统一用的短标签：只留细分类型，去掉「陆军/空军/海军」那段军种前缀
    /// （用户 2026-10-07 逐字批准：<c>盟军 · 陆军 · 步兵</c> 太长，简化成 <c>盟军 · 步兵</c>）。
    /// <para>
    /// <b>建筑/特殊 本身就是细分类型</b>，没有该去的军种前缀 ⇒ 原样保留、不改语义：
    /// 决不把「建筑 · 防御/生产」削成「防御/生产」——那样抬头就认不出这是建筑了；
    /// 「建筑」就是要显示的细分类型，「· 防御/生产」只是 <see cref="DomainLabel"/> 才带的用途备注。
    /// </para>
    /// <para>
    /// 2026-10-07 用户定：找兵面板里**同一分类维度**的文案一次性统一为短名 ⇒ 选分类提示语、
    /// 第 2 层列表项、选完兵的结果行、第 3 层抬头**四处共用这一份**。
    /// <see cref="DomainLabel"/>（带前缀的长标签）本身保留，仍供主窗/目录等其它显示点使用。
    /// </para>
    /// </summary>
    public static string ShortDomainLabel(UnitDomain domain) => domain switch
    {
        UnitDomain.Infantry => "步兵",
        UnitDomain.Vehicle => "车辆",
        UnitDomain.Aircraft => "飞行器",
        UnitDomain.Ship => "舰船",
        UnitDomain.Structure => "建筑",
        _ => "特殊",
    };

    /// <summary>面板/主窗显示用的二级标签。</summary>
    public static string RoleLabel(UnitRole role) => role switch
    {
        UnitRole.AntiInfantry => "反步兵",
        UnitRole.AntiVehicle => "反车辆",
        UnitRole.AntiAir => "防空",
        UnitRole.AntiNavy => "反舰",
        UnitRole.AntiStructure => "反建筑",
        UnitRole.AntiGround => "对地",
        UnitRole.Engineer => "工程",
        UnitRole.Scout => "侦察",
        UnitRole.Infiltration => "渗透",
        UnitRole.Transport => "运输",
        UnitRole.Hero => "英雄",
        UnitRole.SuperWeapon => "超武",
        _ => "通用",
    };

    /// <summary>一级类别的固定顺序（面板按键 1..6 按这个来）。</summary>
    public static readonly UnitDomain[] DomainOrder =
    [
        UnitDomain.Infantry,
        UnitDomain.Vehicle,
        UnitDomain.Aircraft,
        UnitDomain.Ship,
        UnitDomain.Structure,
        UnitDomain.Special,
    ];

    /// <summary>无阵营单位的收容桶（认不出阵营的都归它，保证"一个都不丢"）。</summary>
    public const string OtherFaction = "其他";

    /// <summary>
    /// 兵种阵营的固定展示顺序。**只列数据里真实存在的四个阵营**（官方库 unit-catalog-full.txt 的
    /// 分类第 2 列实测：盟军/苏联/帝国/神州 各带「· 单位」与「· 建筑」；没有"中立/其他"这种字面阵营）。
    /// 认不出阵营的单位（例如玩家自己 units-*.txt 导入的、名字不带阵营前缀的）统一落 <see cref="OtherFaction"/>，
    /// 由调用方排在这个已知表**后面** —— 用户点名要的四个阵营永远在前，杂物类收在最后。
    /// </summary>
    public static readonly string[] FactionOrder = ["盟军", "苏联", "帝国", "神州"];

    /// <summary>阵营的显示条目：<see cref="Faction"/> = 数据里的原始阵营名（匹配用，不能变），
    /// <see cref="Label"/> = 面板上显示的文案（可配置：改名/换顺序都不动代码）。</summary>
    public sealed record FactionDisplay(string Faction, string Label)
    {
        public FactionDisplay(string faction) : this(faction, faction) { }
    }

    /// <summary>默认阵营显示表：顺序与 <see cref="FactionOrder"/> 一致，显示名等于自身。</summary>
    public static readonly FactionDisplay[] DefaultFactionDisplays =
    [
        new("盟军"), new("苏联"), new("帝国"), new("神州"),
    ];

    /// <summary>
    /// 把**数据里真实出现**的阵营排成展示顺序（面板找兵第 1 层用）：
    /// <list type="number">
    ///   <item>配置里的已知阵营（默认四阵营）按配置顺序在前；</item>
    ///   <item>其余阵营**从数据里自动出现** —— 给 <paramref name="countOf"/> 就按出现次数降序（多的排前），
    ///         否则按阵营名去重排序；以后再多的 mod 阵营都自动进表、不丢、不混进「其他」；</item>
    ///   <item>「其他」恒排最后（认不出阵营的收容桶）。</item>
    /// </list>
    /// 未知阵营的显示名 = 原文（<see cref="FactionDisplay.Label"/> 等于 <see cref="FactionDisplay.Faction"/>），
    /// 需要改名/固定顺序就把它们写进配置 <paramref name="configured"/>（见 settings.json 的 FactionDisplay）。
    /// </summary>
    public static IReadOnlyList<FactionDisplay> OrderFactions(
        IEnumerable<string> presentFactions,
        Func<string, int>? countOf = null,
        IReadOnlyList<FactionDisplay>? configured = null)
    {
        var configuredList = configured is { Count: > 0 } ? configured : DefaultFactionDisplays;
        var present = presentFactions
            .Where(faction => !string.IsNullOrWhiteSpace(faction))
            .Select(faction => faction.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var ordered = new List<FactionDisplay>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var display in configuredList)
        {
            var key = display.Faction.Trim();
            if (key.Length == 0 || string.Equals(key, OtherFaction, StringComparison.Ordinal)) continue;
            if (present.Contains(key) && placed.Add(key)) ordered.Add(display);
        }

        var extras = present
            .Where(faction => !placed.Contains(faction) && !string.Equals(faction, OtherFaction, StringComparison.Ordinal))
            .ToList();
        extras = countOf is not null
            ? extras.OrderByDescending(countOf).ThenBy(faction => faction, StringComparer.Ordinal).ToList()
            : extras.OrderBy(faction => faction, StringComparer.Ordinal).ToList();
        foreach (var faction in extras) ordered.Add(new FactionDisplay(faction));

        if (present.Contains(OtherFaction)) ordered.Add(new FactionDisplay(OtherFaction));
        return ordered;
    }

    /// <summary>
    /// 从分类串里取真阵营。<b>编码依据</b>：单位库的分类恒为「&lt;阵营&gt; · &lt;类型&gt;」
    /// （官方库如「帝国 · 单位」「神州 · 建筑」；人工清单由 <c>ReinforcementUnitCatalog.DeriveCategory</c>
    /// 从名字前缀推成「盟军 · 单位」）。所以阵营 = 第一个「·」之前的那一段，去空白。
    ///
    /// 取不到（分类为空、或没有「·」这种非标准形态）一律归 <see cref="OtherFaction"/>：
    /// 宁可让它出现在「其他」里被用户找到，也不要丢掉一个单位（用户 2026-10-07 口径）。
    /// </summary>
    public static string Faction(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return OtherFaction;
        }

        var separator = category.IndexOf('·');
        if (separator <= 0)
        {
            return OtherFaction;
        }

        var faction = category[..separator].Trim();
        return faction.Length == 0 ? OtherFaction : faction;
    }

    /// <summary>阵营名本身就是显示名（数据里写的是中文），这里只做一处收口，免得各处各自拼。</summary>
    public static string FactionLabel(string? faction) =>
        string.IsNullOrWhiteSpace(faction) ? OtherFaction : faction.Trim();
}
