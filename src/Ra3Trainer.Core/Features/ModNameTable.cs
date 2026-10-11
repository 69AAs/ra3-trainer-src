using System.Buffers.Binary;
using System.Text;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 某个 mod **自己带的**单位名字表（内部名 → 它自己的显示名）。
///
/// <para><b>用户 2026-10-11</b>：「如果市面上又出现新的 mod 了，那我们修改器，名字能对上吗？
/// 有没有说在我们修改器启动的时候会去扫一眼这个 mod？」</para>
///
/// <para><b>结论（改动前）</b>：只认 mod 身份，不读 mod 内容 ⇒ 内置表里没有的 mod，单位名字只能显示内部名，
/// 对不上中文。</para>
///
/// <para><b>机制（本次新增）</b>：模组包 <c>.big</c> 里就有 <c>data\gamestrings.csf</c>（游戏的字符串表），
/// 里面每个单位有两把键：<c>TYPE:&lt;内部名&gt;</c> 与 <c>NAME:&lt;内部名&gt;</c>。所以**不需要认识那个 mod**：
/// 启动时按当前 mod 找到它的 <c>.skudef</c> → 读 <c>add-big &lt;包名&gt;</c> → 在包里取出 csf → 建出名字表 ⇒
/// 名字永远和那个 mod 自己的一致（它出中文就是中文）。解析只在启动/换 mod 时做一次，结果缓存成
/// <c>unit-names-&lt;mod&gt;.txt</c>。</para>
/// </summary>
public sealed record ModNameTable(
    string ModKey,
    string SourcePath,
    IReadOnlyDictionary<string, string> Names,
    long SourceSize = 0,
    long SourceTicks = 0)
{
    public static readonly ModNameTable Empty =
        new(string.Empty, string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>缓存里记的"源包指纹"是否还对得上（对不上就重挖：mod 更新后名字要跟着变）。</summary>
    public bool MatchesSource(long size, long ticks) => SourceSize == size && SourceTicks == ticks;

    /// <summary>缓存文件名（exe 旁边）：`unit-names-<mod>.txt`。</summary>
    public static string CachePathFor(string cacheDirectory, string modKey)
    {
        var safe = new string((modKey ?? string.Empty)
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
        // 产品口径：名字缓存写用户目录；老位置已有就用老的（老用户零迁移）。
        return TrainerPaths.Resolve(cacheDirectory, $"unit-names-{(safe.Length > 0 ? safe : "default")}.txt");
    }

    public void Save(string path)
    {
        var lines = Names
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}\t{pair.Value}");
        var header = $"# source {SourceSize} {SourceTicks}";
        File.WriteAllLines(path, new[] { header }.Concat(lines), new UTF8Encoding(false));
    }

    public static ModNameTable Load(string path, string modKey)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        long ticks = 0;
        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.StartsWith("# source ", StringComparison.Ordinal))
            {
                var parts = text[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    long.TryParse(parts[0], out size);
                    long.TryParse(parts[1], out ticks);
                }

                continue;
            }

            if (text.Length == 0 || text.StartsWith('#')) continue;
            var tab = text.IndexOf('\t');
            if (tab <= 0 || tab >= text.Length - 1) continue;
            map.TryAdd(text[..tab].Trim(), text[(tab + 1)..].Trim());
        }

        return new ModNameTable(modKey, path, map, size, ticks);
    }
}

/// <summary>从 mod 的 `.big` 里把名字表挖出来（**只读**：只读索引 + 只读 csf，不写任何游戏文件）。</summary>
public static class ModNameTableBuilder
{
    /// <summary>包里的字符串表路径（BIG 索引用的反斜杠形式）。</summary>
    public const string GameStringsEntry = @"data\gamestrings.csf";

    /// <summary>csf 最大接受多大（930KB 实测；给足余量，防呆）。</summary>
    private const int MaxCsfBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 按 mod 找到并读出名字表；能加载缓存就直接用缓存。任何失败返回 null + error（绝不影响启动）。
    /// </summary>
    public static ModNameTable? LoadOrBuild(string modKey, string launcherPath, string cacheDirectory, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(modKey))
        {
            error = "没有识别出 mod";
            return null;
        }

        var cachePath = ModNameTable.CachePathFor(cacheDirectory, modKey);
        try
        {
            if (!File.Exists(cachePath))
            {
                // mod 升级会把键里的版本号去掉（`remix037` → `remix`）⇒ 老缓存搬到新名字上，别重挖一遍。
                var sibling = FindVersionSiblingCache(cacheDirectory, modKey);
                if (sibling is not null) File.Copy(sibling, cachePath, overwrite: false);
            }

            if (File.Exists(cachePath))
            {
                var cached = ModNameTable.Load(cachePath, modKey);
                if (cached.Names.Count > 0 && IsCacheFresh(cached, cacheDirectory, modKey))
                {
                    return cached;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"读缓存失败：{ex.Message}";
        }

        try
        {
            var big = FindBigForMod(modKey, launcherPath);
            if (big is null)
            {
                error = "没找到这个 mod 的 .big（.skudef 里没有 add-big）";
                return null;
            }

            var names = ReadNamesFromBig(big, out var entry);
            if (names.Count == 0)
            {
                error = $"包里没挖到名字（{entry}）";
                return null;
            }

            long size = 0;
            long ticks = 0;
            try
            {
                var info = new FileInfo(big);
                size = info.Length;
                ticks = info.LastWriteTimeUtc.Ticks;
            }
            catch { /* 拿不到指纹就当没有：只是不会自动失效 */ }

            var table = new ModNameTable(modKey, big, names, size, ticks);
            try { table.Save(cachePath); } catch { /* 缓存写不了不影响本次使用 */ }
            return table;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>缓存是否还对得上源包（对不上 = mod 更新过 ⇒ 重挖名字）。</summary>
    private static bool IsCacheFresh(ModNameTable cached, string cacheDirectory, string modKey)
    {
        if (cached.SourceSize == 0 && cached.SourceTicks == 0) return false;   // 老格式缓存（没指纹）⇒ 重建一次
        try
        {
            var big = FindBigForMod(modKey, null);
            if (big is null) return true;                                       // 找不到包就先用着（别把已有名字丢掉）
            var info = new FileInfo(big);
            return cached.MatchesSource(info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>同 mod 但带版本号的老缓存（`unit-names-remix037.txt`）—— 搬过来，省一次 812MB 的开包。</summary>
    private static string? FindVersionSiblingCache(string cacheDirectory, string modKey)
    {
        var key = UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken(modKey));
        if (key.Length == 0) return null;
        try
        {
            return TrainerPaths.SearchDirectories(cacheDirectory)
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "unit-names-*.txt"))
                .Select(path => (Path: path, Stem: Path.GetFileNameWithoutExtension(path)["unit-names-".Length..]))
                .Where(row => string.Equals(UnitModScope.StripVersionSuffix(row.Stem), key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(row => File.GetLastWriteTimeUtc(row.Path))
                .Select(row => row.Path)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 找一个 mod 的 `.big`：① 舞台目录 `&lt;启动器&gt;\Data\Temp\Mod\*\*.skudef` 里的 `add-config` 指向真 skudef；
    /// ② 真 skudef 的 `add-big &lt;名字&gt;` + 它所在目录。两条都对不上就扫 `Documents\Red Alert 3\Mods` 兜底。
    /// </summary>
    public static string? FindBigForMod(string modKey, string? launcherPath)
    {
        foreach (var skudef in CandidateSkudefs(launcherPath))
        {
            foreach (var big in BigsFromSkudef(skudef))
            {
                if (File.Exists(big)) return big;
            }
        }

        // 兜底：直接把 Mods 目录下的包按"名字像不像这个 mod"挑一个。
        var modsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Red Alert 3", "Mods");
        if (!Directory.Exists(modsRoot)) return null;
        var token = new string((modKey ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        if (token.Length == 0) return null;

        return Directory.EnumerateFiles(modsRoot, "*.big", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: new string(Path.GetFileName(path).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray())))
            .Where(row => row.Name.Contains(token, StringComparison.Ordinal))
            .OrderByDescending(row => row.Name.StartsWith(token, StringComparison.Ordinal))
            .Select(row => row.Path)
            .FirstOrDefault();
    }

    /// <summary>候选 skudef：整合包的 mod 舞台目录 + 用户 Mods 目录。</summary>
    public static IEnumerable<string> CandidateSkudefs(string? launcherPath)
    {
        var results = new List<string>();
        if (!string.IsNullOrWhiteSpace(launcherPath))
        {
            var root = Path.GetDirectoryName(launcherPath);
            var stage = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Data", "Temp", "Mod");
            if (stage is not null && Directory.Exists(stage))
            {
                try
                {
                    results.AddRange(Directory.EnumerateDirectories(stage).SelectMany(d => Directory.EnumerateFiles(d, "*.skudef")));
                }
                catch { /* 枚举失败就当没有 */ }
            }
        }

        var modsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Red Alert 3", "Mods");
        if (Directory.Exists(modsRoot))
        {
            try { results.AddRange(Directory.EnumerateFiles(modsRoot, "*.skudef", SearchOption.AllDirectories)); }
            catch { /* 同上 */ }
        }

        return results;
    }

    /// <summary>顺着 skudef 的 `add-config`（舞台）与 `add-big`（真包）找出 .big 的绝对路径。</summary>
    public static IEnumerable<string> BigsFromSkudef(string skudefPath)
    {
        var lines = ReadSkudefLines(skudefPath);
        var directory = Path.GetDirectoryName(skudefPath) ?? string.Empty;

        // 舞台 skudef：add-config <真 skudef> ⇒ 递归一次。
        foreach (var config in lines.Where(l => l.Key == "add-config").Select(l => l.Value))
        {
            if (!File.Exists(config)) continue;
            foreach (var nested in BigsFromSkudef(config)) yield return nested;
        }

        foreach (var name in lines.Where(l => l.Key == "add-big").Select(l => l.Value))
        {
            var candidate = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
            yield return candidate;
        }
    }

    private static List<KeyValuePair<string, string>> ReadSkudefLines(string path)
    {
        var results = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                var space = line.IndexOf(' ');
                if (space <= 0) continue;
                results.Add(new KeyValuePair<string, string>(line[..space].Trim().ToLowerInvariant(), line[(space + 1)..].Trim()));
            }
        }
        catch { /* 读不了就没有 */ }

        return results;
    }

    /// <summary>从 .big 里取出 csf 并解析成「内部名 → 显示名」。</summary>
    public static Dictionary<string, string> ReadNamesFromBig(string bigPath, out string entryName)
    {
        entryName = GameStringsEntry;
        using var stream = new FileStream(bigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var csf = ReadEntry(stream, GameStringsEntry, out var found);
        if (csf is null)
        {
            // 有些包把数据放在 data/ 下的别的语言表里：退一步做**全索引模糊匹配**。
            foreach (var candidate in ListEntries(stream).Where(n => n.EndsWith(".csf", StringComparison.OrdinalIgnoreCase)))
            {
                csf = ReadEntry(stream, candidate, out found);
                if (csf is not null)
                {
                    entryName = candidate;
                    break;
                }
            }
        }
        else
        {
            entryName = found;
        }

        return csf is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                           : ParseCsf(csf);
    }

    /// <summary>列出包里的文件名（BIG4 索引：magic + 总大小 + 条数，之后每条 = offset + size + NUL 结尾名字）。</summary>
    public static IReadOnlyList<string> ListEntries(Stream stream)
    {
        var names = new List<string>();
        if (!TryReadIndex(stream, out var entries)) return names;
        names.AddRange(entries.Select(entry => entry.Name));
        return names;
    }

    private static byte[]? ReadEntry(Stream stream, string name, out string found)
    {
        found = name;
        if (!TryReadIndex(stream, out var entries)) return null;
        var match = entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match.Name is null) return null;
        if (match.Size <= 0 || match.Size > MaxCsfBytes) return null;

        stream.Seek(match.Offset, SeekOrigin.Begin);
        var buffer = new byte[match.Size];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0) break;
            read += got;
        }

        found = match.Name;
        return read == buffer.Length ? buffer : null;
    }

    private readonly record struct BigEntry(string Name, uint Offset, uint Size);

    private static bool TryReadIndex(Stream stream, out List<BigEntry> entries)
    {
        entries = new List<BigEntry>();
        if (stream.Length < 16) return true;
        stream.Seek(0, SeekOrigin.Begin);
        var header = new byte[16];
        if (stream.Read(header, 0, 16) < 16) return true;

        var magic = Encoding.ASCII.GetString(header, 0, 4);
        if (magic is not ("BIG4" or "BIGF" or "BIG5")) return false;

        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (count == 0 || count > 200_000) return true;                 // 条数离谱 ⇒ 当没索引

        stream.Seek(16, SeekOrigin.Begin);
        var nameBytes = new List<byte>(64);
        for (uint i = 0; i < count; i++)
        {
            var eight = new byte[8];
            if (stream.Read(eight, 0, 8) < 8) break;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(eight.AsSpan(0, 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(eight.AsSpan(4, 4));

            nameBytes.Clear();
            while (nameBytes.Count < 512)
            {
                var one = stream.ReadByte();
                if (one <= 0) break;
                nameBytes.Add((byte)one);
            }

            if (nameBytes.Count == 0) break;
            entries.Add(new BigEntry(Encoding.ASCII.GetString(nameBytes.ToArray()), offset, size));
        }

        return true;
    }

    /// <summary>
    /// 解析 CSF（游戏的字符串表）：一串 `" LBL" + flag(4) + 标签长度(4) + 标签(单字节 ANSI)
    /// + " RTS" + 值长度(4) + 值`。只取 <c>NAME:&lt;内部名&gt;</c> 这把键（另一把 <c>TYPE:</c> 是类型说明）。
    ///
    /// <para>★ 实测（2026-10-11，拿 REMIX 0.3.7 的 930KB csf 验的）：**标签是单字节 ANSI，值是 UTF-16LE 且逐字节取反（XOR 0xFF）**。
    /// 例如 <c>NAME:SovietHeavyWalkerVehicle</c> 的原始字节 <c>ca8a3e8774acd680518f</c> 取反后是
    /// <c>收割机甲</c> —— 和玩家自己在游戏里看到的叫法一致，说明解析没错位。</para>
    ///
    /// <para>解析策略：认不对就往下找下一个 <c>" LBL"</c> 重新对齐（别用固定步长硬算，格式里每项末尾还有 4 字节）。</para>
    /// </summary>
    public static Dictionary<string, string> ParseCsf(byte[] csf)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (csf.Length < 20) return map;

        var position = 16;                                          // 头部：签名 + 版本 + 条数 + 标签数
        while (true)
        {
            position = IndexOfText(csf, " LBL", position);
            if (position < 0 || position + 12 > csf.Length) break;
            var labelLength = (int)BitConverter.ToUInt32(csf, position + 8);
            var labelStart = position + 12;
            if (labelLength <= 0 || labelStart + labelLength > csf.Length)
            {
                position += 4;
                continue;
            }

            var label = Encoding.Latin1.GetString(csf, labelStart, labelLength);
            var afterLabel = labelStart + labelLength;
            if (afterLabel + 8 > csf.Length || !Matches(csf, afterLabel, " RTS"))
            {
                position += 4;
                continue;
            }

            var valueLength = (int)BitConverter.ToUInt32(csf, afterLabel + 4);
            var valueStart = afterLabel + 8;
            if (valueLength <= 0 || valueStart + valueLength * 2 > csf.Length)
            {
                position += 4;
                continue;
            }

            var value = DecodeValue(csf, valueStart, valueLength * 2);
            if (label.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                map.TryAdd(label[5..].Trim(), value);
            }

            position = valueStart + valueLength * 2;
        }

        return map;
    }

    /// <summary>值 = UTF-16LE 逐字节取反（实测口径，见 <see cref="ParseCsf"/>）。</summary>
    private static string DecodeValue(byte[] buffer, int offset, int count)
    {
        var plain = new byte[count];
        for (var i = 0; i < count; i++)
        {
            plain[i] = (byte)~buffer[offset + i];
        }

        return Encoding.Unicode.GetString(plain).Trim();
    }

    private static bool Matches(byte[] buffer, int offset, string text)
    {
        if (offset + text.Length > buffer.Length) return false;
        for (var i = 0; i < text.Length; i++)
        {
            if (buffer[offset + i] != (byte)text[i]) return false;
        }

        return true;
    }

    private static int IndexOfText(byte[] buffer, string text, int from)
    {
        for (var i = Math.Max(0, from); i + text.Length <= buffer.Length; i++)
        {
            if (Matches(buffer, i, text)) return i;
        }

        return -1;
    }
}
