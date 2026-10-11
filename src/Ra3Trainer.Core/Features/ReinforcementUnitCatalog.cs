namespace Ra3Trainer.Core.Features;

using System.IO;
using System.Reflection;
using Ra3Trainer.Core.Runtime;

public static class ReinforcementUnitCatalog
{
    private static readonly Lazy<IReadOnlyList<ReinforcementUnitEntry>> BuiltInUnits =
        new(LoadBuiltInCore);

    /// <summary>
    /// 地图杂物/中立设施（信标、箱子、平民建筑、医院油井…）。
    /// **默认不进增援清单** —— 选择器里的"显示地图杂物"开关打开时才并进来。
    /// </summary>
    public static IReadOnlyList<ReinforcementUnitEntry> LoadBuiltInProps()
    {
        try
        {
            return ParseFullCatalog(ReadResourceLines("unit-catalog-props.txt"));
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<ReinforcementUnitEntry>();
        }
    }

    public static IReadOnlyList<ReinforcementUnitEntry> Load(string path)
    {
        return Parse(File.ReadLines(path));
    }

    public static IReadOnlyList<ReinforcementUnitEntry> LoadBuiltIn()
    {
        return BuiltInUnits.Value;
    }

    public static IReadOnlyList<ReinforcementUnitEntry> Merge(
        IEnumerable<ReinforcementUnitEntry> primary,
        IEnumerable<ReinforcementUnitEntry> secondary)
    {
        var merged = new List<ReinforcementUnitEntry>();
        var byCode = new Dictionary<uint, int>();
        var byName = new Dictionary<(string Category, string Name), int>();

        foreach (var entry in primary.Concat(secondary))
        {
            // 同一个代号在两边都有：人工清单优先（显示名和分类是用户校对过的），
            // 但内部名只要人工这条缺就把它补上 —— 兵种分类（陆/空/海）全靠内部名读，
            // 丢了它，最常用的那 85 个单位反而会落进"特殊"。
            if (byCode.TryGetValue(entry.Code, out var sameCode))
            {
                FillEditorName(sameCode, entry);
                continue;
            }

            // 同一个分类里同名 = 同一个单位的两个来源（人工清单的代号和官网的代号不一样）。
            // 不并掉的话，用户在选择器里就会看到"海啸坦克"两条，然后又来问"为什么这么多重复的"。
            if (byName.TryGetValue((entry.Category, entry.Name), out var sameName))
            {
                FillEditorName(sameName, entry);
                continue;
            }

            byCode[entry.Code] = merged.Count;
            byName[(entry.Category, entry.Name)] = merged.Count;
            merged.Add(entry);

            void FillEditorName(int at, ReinforcementUnitEntry donor)
            {
                var kept = merged[at];
                if (kept.EditorName.Length == 0 && donor.EditorName.Length > 0)
                {
                    merged[at] = kept with { EditorName = donor.EditorName };
                }
            }
        }

        return merged;
    }

    /// <summary>用户清单文件名：放在 exe 旁边，记录"从游戏里读到并记住"的单位（可直接手改名字）。</summary>
    public const string UserCatalogFileName = "units.txt";

    /// <summary>
    /// 从启动器路径推"当前模组键"。mod 之间单位不互通（各 mod 自研单位的代号由它自己分配），
    /// 所以"读到就记住"要分档存：units-corona.txt / units-armorrush.txt …，互不污染。
    /// 认不出就是 default（存 units-default.txt，另外永远优先兼容读老的 units.txt）。
    /// </summary>
    public static string ModKeyFromLauncherPath(string? launcherPath)
    {
        var path = (launcherPath ?? string.Empty).Trim().Trim('"');
        if (path.Length == 0)
        {
            return "default";
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        foreach (var suffix in new[] { "launcher", "starter", "启动器", "loader" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length > suffix.Length)
            {
                stem = stem[..^suffix.Length];
            }
        }

        var key = new string(stem.Where(char.IsLetterOrDigit).ToArray());
        return key.Length == 0 ? "default" : key;
    }

    /// <summary>当前模组的用户清单路径（exe 旁边 units-&lt;mod&gt;.txt）。</summary>
    public static string UserCatalogPathForMod(string baseDirectory, string? launcherPath)
    {
        return TrainerPaths.Resolve(baseDirectory, $"units-{ModKeyFromLauncherPath(launcherPath)}.txt");
    }

    /// <summary>
    /// 当前模组的用户清单路径 —— 按**识别出来的 mod 键**（而不是启动器路径）。
    /// 硬信号认出的 mod（游戏正在跑的日冕）也能把"读到就记住"的新单位存进 units-日冕那一档。
    /// </summary>
    public static string UserCatalogPathForModKey(string baseDirectory, string? modKey)
    {
        var key = UnitModScope.NormalizeToken(modKey);
        return TrainerPaths.Resolve(baseDirectory, $"units-{(key.Length == 0 ? "default" : key)}.txt");
    }

    /// <summary>
    /// 找同 mod 但**带版本号的旧清单**（例如键现在是 `remix`，盘上还留着 `units-remix037.txt`）。
    /// mod 升级后键会变（版本号被去掉）⇒ 这里把旧清单**搬运**过来，玩家不用重新学一遍。
    /// 只在目标文件不存在、且旧文件确实存在时返回；挑**最近修改**的那份。
    /// </summary>
    public static string? FindVersionSiblingCatalog(string baseDirectory, string? modKey)
    {
        var key = UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken(modKey));
        if (key.Length == 0) return null;

        try
        {
            return TrainerPaths.SearchDirectories(baseDirectory)
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "units-*.txt"))
                .Select(path => (Path: path, Stem: Path.GetFileNameWithoutExtension(path)["units-".Length..]))
                .Where(row => string.Equals(UnitModScope.StripVersionSuffix(row.Stem), key, StringComparison.OrdinalIgnoreCase))
                .Where(row => !string.Equals(row.Stem, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => File.GetLastWriteTimeUtc(row.Path))
                .Select(row => row.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string DefaultUserCatalogPath(string baseDirectory)
    {
        return TrainerPaths.Resolve(baseDirectory, UserCatalogFileName);
    }

    /// <summary>
    /// 把用户清单里的**占位分类**（空 / 其他 / 当前模组 / 待核对）换成**按 mod 建桶**的分类：`&lt;mod 显示名&gt; · 单位`。
    ///
    /// <para><b>2026-10-11 用户</b>：「这么多单位混在一起，别人也不好找，我们日冕都能做个单独的页面，为什么其他 mod 不行？」
    /// 根因：<see cref="SaveUserCatalog"/> 只写 `代号 名字` ⇒ **类别在存盘时就丢了**；重新加载时
    /// <see cref="DeriveCategory"/> 遇到 `新单位 0x…` 这种没有「阵营 · 」前缀的名字，一律推成「其他 · 单位」
    /// ⇒ 所有 mod 的自研单位全挤在一个桶里。日冕之所以看起来"有单独一页"，是因为它的单位**带真实阵营
    /// （神州等）**进了内置库，本来就不算「其他」。</para>
    ///
    /// <para>口径：**只动占位桶**，已经带真阵营的分类绝不改写（人工清单可以自己写前缀改归属）。</para>
    /// </summary>
    public static IReadOnlyList<ReinforcementUnitEntry> StampModCategory(
        IEnumerable<ReinforcementUnitEntry> units, string? modLabel)
    {
        var label = (modLabel ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            return units.ToArray();
        }

        return units
            .Select(entry => IsPlaceholderCategory(entry.Category)
                ? entry with { Category = $"{label} · 单位", ModSource = label }
                : entry)
            .ToArray();
    }

    private static bool IsPlaceholderCategory(string? category)
    {
        var text = (category ?? string.Empty).Trim();
        return text.Length == 0
            || text.StartsWith("其他", StringComparison.Ordinal)
            || text.StartsWith("当前模组", StringComparison.Ordinal)
            || text.StartsWith("待核对", StringComparison.Ordinal);
    }

    /// <summary>读用户清单；文件不存在就是空的。</summary>
    public static IReadOnlyList<ReinforcementUnitEntry> LoadUserCatalog(string path)
    {
        return File.Exists(path) ? Parse(File.ReadLines(path)) : Array.Empty<ReinforcementUnitEntry>();
    }

    /// <summary>写用户清单（按代号排序，格式与内置清单一致）。</summary>
    public static void SaveUserCatalog(string path, IEnumerable<ReinforcementUnitEntry> units)
    {
        var lines = units
            .Where(unit => unit.Code != 0)
            .OrderBy(unit => unit.Code)
            .Select(unit => $"{unit.CodeText} {unit.Name}");
        File.WriteAllLines(path, lines);
    }

    /// <summary>内置的"内部英文名 → 中文名"对照（来自日冕 WorldBuilder），用于把游戏里的单位自动命名。</summary>
    public static IReadOnlyDictionary<string, string> LoadBuiltInNameMap()
    {
        using var stream = OpenBuiltInResourceStream("unit-names.txt");
        using var reader = new StreamReader(stream);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var space = trimmed.IndexOfAny([' ', '\t']);
            if (space <= 0 || space >= trimmed.Length - 1)
            {
                continue;
            }

            map[trimmed[..space]] = trimmed[(space + 1)..].Trim();
        }

        return map;
    }

    /// <summary>按代号查单位；找不到返回 null。界面用它把游戏给的代号翻译成兵种名。</summary>
    public static ReinforcementUnitEntry? FindByCode(IEnumerable<ReinforcementUnitEntry> units, uint code)
    {
        return units.FirstOrDefault(unit => unit.Code == code);
    }

    public static IReadOnlyList<ReinforcementUnitEntry> Filter(
        IEnumerable<ReinforcementUnitEntry> units,
        string? searchText)
    {
        var normalized = (searchText ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return units.ToArray();
        }

        return units
            .Where(unit => MatchesSearch(unit, normalized))
            .ToArray();
    }

    public static IReadOnlyList<ReinforcementUnitEntry> Parse(IEnumerable<string> lines)
    {
        return lines
            .Select(ParseLine)
            .Where(entry => entry is not null)
            .Cast<ReinforcementUnitEntry>()
            .ToArray();
    }

    /// <summary>
    /// 内置清单 = 人工校对的 code.txt（84 条，代号是契约）+ 官方单位库（日冕官网 game_objects，
    /// 630 条，含日冕新阵营"神州"与原版全部单位/建筑）。按代号去重，人工那条优先。
    /// 来源验证：官方库的 type_info_instance_id 与已知代号 6/6 一致。
    /// </summary>
    /// <summary>
    /// 从"阵营 · 单位名"里推出分类（人工清单 84 条全是单位）。
    /// 官方库那种带显式分类的走 ParseFullCatalog。
    /// </summary>
    private static string DeriveCategory(string name)
    {
        var separator = name.IndexOf('·');
        return separator > 0 ? $"{name[..separator].Trim()} · 单位" : "其他 · 单位";
    }

    /// <summary>官方库格式：代号 &lt;TAB&gt; 分类（阵营 · 类型）&lt;TAB&gt; 显示名。</summary>
    public static IReadOnlyList<ReinforcementUnitEntry> ParseFullCatalog(IEnumerable<string> lines)
    {
        var units = new List<ReinforcementUnitEntry>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length < 3 || !UnitCodeParser.TryParse(parts[0].Trim(), out var code) || code == 0)
            {
                continue;
            }

            var category = parts[1].Trim();
            var name = parts[2].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var source = parts.Length > 3 && parts[3].Trim().Length > 0 ? parts[3].Trim() : "待核对";
            // 第 5 列是游戏自己的内部名（JapanAntiNavyShipTech3 这种）：兵种分类就是从这里读的，
            // 没有它就只能退回中文显示名猜，所以能带就带上。
            var editorName = parts.Length > 4 ? parts[4].Trim() : string.Empty;
            units.Add(new ReinforcementUnitEntry(
                code,
                name,
                category.Length > 0 ? category : DeriveCategory(name),
                source,
                editorName));
        }

        return units;
    }

    private static IReadOnlyList<ReinforcementUnitEntry> LoadBuiltInCore()
    {
        var curated = StripBuiltInFactionPrefix(Parse(ReadResourceLines()));

        IReadOnlyList<ReinforcementUnitEntry> full;
        try
        {
            full = ParseFullCatalog(ReadResourceLines("unit-catalog-full.txt"));
        }
        catch (InvalidOperationException)
        {
            // 资源缺失时退回只用人工清单，绝不让程序起不来
            full = Array.Empty<ReinforcementUnitEntry>();
        }

        return Merge(curated, full);
    }

    private static ReinforcementUnitEntry? ParseLine(string line)
    {
        var trimmed = (line ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var firstSpace = trimmed.IndexOfAny([' ', '\t']);
        if (firstSpace <= 0 || firstSpace >= trimmed.Length - 1)
        {
            return null;
        }

        var codeText = trimmed[..firstSpace];
        var name = trimmed[(firstSpace + 1)..].Trim();
        if (name.Length == 0 || !UnitCodeParser.TryParse(codeText, out var code))
        {
            return null;
        }

        return new ReinforcementUnitEntry(code, name, DeriveCategory(name));
    }

    /// <summary>
    /// 内置人工清单写的是"苏联 · 铁锤坦克"。用户自己那份 units.txt 也是这个写法，但要原样保留
    /// （见 UserCatalogRoundTripsThroughSaveAndLoad），所以只剥**内置**这 85 条的名字：
    /// 官方库的名字不带阵营前缀，对齐之后同一个单位在面板上才不会既显示"帝国 · 帝国武士"
    /// 又显示"帝国武士"。分类仍然从原名推（"苏联 · 单位"）。
    /// </summary>
    // internal 而非 private：单元测试必须走同一条链（测试程序集有 InternalsVisibleTo）。
    internal static IReadOnlyList<ReinforcementUnitEntry> StripBuiltInFactionPrefix(
        IEnumerable<ReinforcementUnitEntry> units)
    {
        var normalized = new List<ReinforcementUnitEntry>();
        foreach (var unit in units)
        {
            var separator = unit.Name.IndexOf('·');
            normalized.Add(separator > 0 && separator < unit.Name.Length - 1
                ? unit with { Name = unit.Name[(separator + 1)..].Trim() }
                : unit);
        }

        return normalized;
    }

    private static bool MatchesSearch(ReinforcementUnitEntry unit, string searchText)
    {
        return unit.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            unit.Category.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            unit.CodeText.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
            unit.CodeText[2..].Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private static Stream OpenBuiltInResourceStream(string fileNameSuffix = "code.txt")
    {
        var assembly = typeof(ReinforcementUnitCatalog).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Built-in unit code resource code.txt was not found.");
        }

        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Unable to open embedded resource {resourceName}.");
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is string line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// 读嵌入资源。**必须**在这里读完再返回：原来的写法是把 new StreamReader(...) 交给惰性
    /// ReadLines，reader/流要等 GC 终结才释放（每次开选择器都漏一个清单流的非托管副本）。
    /// 先 ToArray 落地，using 才能真的把流关掉。
    /// </summary>
    private static IReadOnlyList<string> ReadResourceLines(string fileNameSuffix = "code.txt")
    {
        // 注意：这里不能写 ReadResourceLines(null) —— 默认参数只在"不传实参"时生效，
        // 显式传 null 会绕过它， EndsWith(null) 当场 ArgumentNullException（实测踩过）。
        using var reader = new StreamReader(OpenBuiltInResourceStream(fileNameSuffix));
        return ReadLines(reader).ToArray();
    }
}

public sealed record ReinforcementUnitEntry(uint Code, string Name, string Category = "", string ModSource = "通用", string EditorName = "")
{
    public string CodeText => UnitCodeParser.Format(Code);

    /// <summary>兵种类别（陆/空/海/建筑/特殊），来自游戏自己的命名，见 <see cref="UnitTaxonomy"/>。</summary>
    public UnitDomain Domain => UnitTaxonomy.Domain(EditorName, Name);

    /// <summary>
    /// 兵种真阵营（盟军/苏联/帝国/神州…），来自分类「阵营 · 类型」的阵营段，见 <see cref="UnitTaxonomy.Faction"/>。
    /// 认不出归「其他」——面板找兵第 1 层用的就是它（用户 2026-10-07 口径 B：第一层要是真阵营，不是军种）。
    /// </summary>
    public string Faction => UnitTaxonomy.Faction(Category);

    /// <summary>兵种用途（反步兵/防空/反舰…），同上。</summary>
    public UnitRole Role => UnitTaxonomy.Role(EditorName, Name);

    public string DomainLabel => UnitTaxonomy.DomainLabel(Domain);

    public string RoleLabel => UnitTaxonomy.RoleLabel(Role);

    /// <summary>
    /// 来源标注（"仅日冕"）：非原版通用、非杂物才有值；原版单位为空。
    /// 面板/选择器用它标出"这条单位来自哪个 mod"，换 mod 时一眼能分。
    /// </summary>
    public string ModBadge => UnitModScope.SourceBadge(ModSource);
}
