namespace Ra3Trainer.Core.Features;

/// <summary>
/// 用户单位清单（`units-&lt;mod&gt;.txt`）**自愈**结果：规范化后的行 + 改了什么。
/// </summary>
public sealed record UserCatalogRepair(
    IReadOnlyList<string> Lines,
    int Renamed,
    int Deduped,
    int Dropped)
{
    /// <summary>
    /// 有没有真的动过（没动就不写文件、不留备份）。
    /// ★ 只看**名字**和**重复**：清空行不算改动 —— 否则每次启动都重写文件、堆一堆 .bak（2026-10-11 单测抓到）。
    /// </summary>
    public bool Changed => Renamed > 0 || Deduped > 0;

    /// <summary>给日志/状态行的一句话。</summary>
    public string Summary => Changed
        ? $"单位清单自愈：改好名字 {Renamed} 条 · 去重 {Deduped} 条 · 清掉空行 {Dropped} 行"
        : "单位清单自愈：没发现问题";
}

/// <summary>
/// 用户单位清单的**自动维护机制**（用户 2026-10-11：「这种东西不应该我们每次去手动处理，
/// 应该是修改器自动处理的机制，不然这个修改器我们发布出去之后，别人用的时候，那怎么办」）。
///
/// <para>每次启动/换 mod 加载清单时自动跑一遍，只做**能确定是好事的**三件事：</para>
/// <list type="number">
/// <item><b>去重</b>：同一个代号只留一条（保留名字更好的那条）；</item>
/// <item><b>修坏名字</b>：早先我写坏的 `新单位 0x代号`（代号被当成名字抄第二遍）和空名字，
/// 能被内置表/内部名解出来就换成真名字，解不出就退回 `未命名`；</item>
/// <item><b>丢坏行</b>：解析不出来的行（不是 `0x代号 名字`）只在**完全空行/注释**时丢，其余原样保留。</item>
/// </list>
///
/// <para><b>绝不越界</b>：用玩家自己起的名字（不是占位/坏格式）**一个字都不改**。
/// 改过才写盘，写盘前留 `<文件名>.bak-&lt;时间戳&gt;`，且用临时文件 + 原子替换。</para>
/// </summary>
public static class UnitCatalogMaintenance
{
    /// <summary>占位名（和 <see cref="UnitNaming.UnknownName"/> 同一个口径）。</summary>
    public const string PlaceholderName = UnitNaming.UnknownName;

    /// <summary>
    /// 规范化一份清单。不碰磁盘（调用方负责备份/原子写），方便单测。
    /// </summary>
    /// <param name="lines">现有行（原样）。</param>
    /// <param name="nameLookup">代号 → 真名字（拿不到就给 null；给了才可能改名）。</param>
    public static UserCatalogRepair Normalize(IEnumerable<string> lines, Func<uint, string?>? nameLookup = null)
    {
        var output = new List<string>();
        var seen = new Dictionary<uint, int>();
        var renamed = 0;
        var deduped = 0;
        var blank = 0;

        foreach (var raw in lines ?? Array.Empty<string>())
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                blank++;                       // 空行/注释：顺手清掉，但**不算"动过"**（否则每次启动都白写一遍文件）
                continue;
            }

            if (!TryParse(line, out var code, out var name))
            {
                output.Add(line);              // 认不出的行 ⇒ 原样保留（可能是他手写的备注），绝不删
                continue;
            }

            var fixedName = Repair(code, name, nameLookup);
            if (!string.Equals(fixedName, name, StringComparison.Ordinal)) renamed++;

            if (seen.TryGetValue(code, out var index))
            {
                deduped++;
                if (NameScore(fixedName) > NameScore(NamePart(output[index])))
                {
                    output[index] = $"0x{code:X8} {fixedName}";   // 同代号两条：留名字更好的那条
                }

                continue;
            }

            seen[code] = output.Count;
            output.Add($"0x{code:X8} {fixedName}");
        }

        return new UserCatalogRepair(output, renamed, deduped, blank);
    }

    /// <summary>取一行里的名字部分（`0x代号 名字` → 名字）。</summary>
    public static string NamePart(string line)
    {
        var text = (line ?? string.Empty).Trim();
        var space = text.IndexOf(' ');
        return space < 0 ? string.Empty : text[(space + 1)..].Trim();
    }

    /// <summary>名字好到什么程度（用来决定同代号两条留哪条）：真名字 &gt; 内部名 &gt; 未命名 &gt; 坏格式。</summary>
    private static int NameScore(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        if (IsBrokenName(name)) return 1;
        if (string.Equals(name.Trim(), PlaceholderName, StringComparison.Ordinal)) return 2;
        return 3;
    }

    /// <summary>是不是我们写坏的/占位的名字（`新单位 0x…`、含代号、空）。</summary>
    public static bool IsBrokenName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        if (text.Length == 0) return true;
        if (text.StartsWith("新单位", StringComparison.Ordinal)) return true;
        if (text.Contains("0x", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>修一个名字：坏名字/占位名才尝试用 nameLookup 解出真名字。</summary>
    private static string Repair(uint code, string name, Func<uint, string?>? nameLookup)
    {
        var text = (name ?? string.Empty).Trim();
        var needsHelp = IsBrokenName(text) || string.Equals(text, PlaceholderName, StringComparison.Ordinal);
        if (!needsHelp)
        {
            return text;                          // 他自己起的名字 ⇒ 一个字不动
        }

        var resolved = nameLookup?.Invoke(code);
        if (!string.IsNullOrWhiteSpace(resolved) && !IsBrokenName(resolved))
        {
            return resolved.Trim();
        }

        return string.IsNullOrWhiteSpace(text) || IsBrokenName(text) ? PlaceholderName : text;
    }

    /// <summary>解析 `0x代号 名字` / `代号 名字` / `0x代号 阵营 · 名字`。认不出返回 false。</summary>
    public static bool TryParse(string line, out uint code, out string name)
    {
        code = 0;
        name = string.Empty;
        var text = (line ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        var space = text.IndexOf(' ');
        var head = space < 0 ? text : text[..space];
        var tail = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        var digits = head.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? head[2..] : head;
        if (digits.Length == 0 || digits.Length > 8) return false;
        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        code = Convert.ToUInt32(digits, 16);
        name = tail;
        return true;
    }
}
