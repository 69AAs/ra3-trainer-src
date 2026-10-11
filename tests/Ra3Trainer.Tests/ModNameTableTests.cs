using System.Buffers.Binary;
using System.Text;
using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 「新 mod 出现时名字能不能对上」（用户 2026-10-11）。
/// 机制：启动时按当前 mod 找它的 .skudef → 读 add-big → 在 .big 里挖出 data\gamestrings.csf →
/// 解析 NAME:&lt;内部名&gt; ⇒ 名字永远和那个 mod 自己的一致，不用等我们更新内置表。
///
/// 这里钉住：① CSF 格式（标签单字节 ANSI + " RTS" 标记 + 值 UTF-16LE 逐字节取反）
/// ② .big 索引能读（BIG4）③ mod 名字表优先级最高 ④ 缓存文件名要安全。
/// </summary>
public sealed class ModNameTableTests
{
    /// <summary>按实测格式造一条 csf 记录：`" LBL" + flag + 标签长度(单字节数) + 标签 + " RTS" + 值长度(字符数) + 值(取反 UTF-16LE)`。</summary>
    private static void AppendCsfEntry(MemoryStream stream, string label, string value)
    {
        var labelBytes = Encoding.Latin1.GetBytes(label);
        var valueBytes = Encoding.Unicode.GetBytes(value);                        // UTF-16LE 字节
        for (var i = 0; i < valueBytes.Length; i++)
        {
            valueBytes[i] = (byte)~valueBytes[i];                                  // 逐字节取反（实测口径）
        }
        stream.Write([(byte)' ', (byte)'L', (byte)'B', (byte)'L']);
        stream.Write(BitConverter.GetBytes(1));
        stream.Write(BitConverter.GetBytes(labelBytes.Length));
        stream.Write(labelBytes);
        stream.Write(" RTS"u8);
        stream.Write(BitConverter.GetBytes(value.Length));                        // 长度按 UTF-16 字符数
        stream.Write(valueBytes);
        stream.Write([0, 0, 0, 0]);                                              // 实测每项末尾还有 4 字节
    }

    private static byte[] BuildCsf(params (string Label, string Value)[] entries)
    {
        var stream = new MemoryStream();
        stream.Write(" FSC"u8);
        stream.Write(BitConverter.GetBytes(3));
        stream.Write(BitConverter.GetBytes(entries.Length));
        stream.Write(BitConverter.GetBytes(entries.Length));
        stream.Write(new byte[4]);
        foreach (var (label, value) in entries)
        {
            AppendCsfEntry(stream, label, value);
        }

        return stream.ToArray();
    }

    [Fact]
    public void CsfParsingDecodesInvertedUnicodeValues()
    {
        var csf = BuildCsf(
            ("TYPE:SovietHeavyWalkerVehicle", "反地面/反空机甲"),
            ("NAME:SovietHeavyWalkerVehicle", "收割机甲"),
            ("NAME:SovietNewTower", "电磁压缩炮"),
            ("SOMETHINGELSE", "不是单位名"));

        var names = ModNameTableBuilder.ParseCsf(csf);

        Assert.Equal(2, names.Count);                                        // 只收 NAME: 这把键
        Assert.Equal("收割机甲", names["SovietHeavyWalkerVehicle"]);
        Assert.Equal("电磁压缩炮", names["SovietNewTower"]);
    }

    [Fact]
    public void ModNamesWinOverEverythingElse()
    {
        // mod 自己说叫什么，就用它自己的（它出中文就是中文）。
        var modNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SovietNewTower"] = "电磁压缩炮",
            ["RemixWonderTank"] = "奇迹坦克",
        };

        Assert.Equal("电磁压缩炮", UnitNaming.DisplayNameFor("SovietNewTower", modNames));
        Assert.Equal("奇迹坦克", UnitNaming.DisplayNameFor("RemixWonderTank", modNames));
        Assert.Equal("RemixWonderTank", UnitNaming.DisplayNameFor("RemixWonderTank", (IReadOnlyDictionary<string, string>?)null));
        Assert.Equal(UnitNaming.UnknownName, UnitNaming.DisplayNameFor(null, modNames));
    }

    [Theory]
    [InlineData("remix", "unit-names-remix.txt")]
    [InlineData("../../坏名字", "unit-names-default.txt")]
    [InlineData("", "unit-names-default.txt")]
    public void CachePathIsSafe(string modKey, string expectedFile)
    {
        var path = ModNameTable.CachePathFor(@"C:\tmp", modKey);

        Assert.Equal(expectedFile, Path.GetFileName(path));
    }

    [Fact]
    public void TableRoundTripsThroughTheCacheFile()
    {
        var directory = Directory.CreateTempSubdirectory("ra3-name-table");
        try
        {
            var table = new ModNameTable("remix", "big", new Dictionary<string, string>
            {
                ["SovietNewTower"] = "电磁压缩炮",
                ["SovietHeavyWalkerVehicle"] = "收割机甲",
            });
            var path = ModNameTable.CachePathFor(directory.FullName, "remix");
            table.Save(path);

            var loaded = ModNameTable.Load(path, "remix");
            Assert.Equal(2, loaded.Names.Count);
            Assert.Equal("收割机甲", loaded.Names["SovietHeavyWalkerVehicle"]);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    /// <summary>
    /// **真实包验证**（本机装了 REMIX 0.3.7 时才会跑）：BIG4 索引 + csf + 取反解码 一条链走通。
    /// 没装这个 mod 就跳过（不假装通过）。
    /// </summary>
    [Fact]
    public void RealModBigYieldsTheModsOwnNames()
    {
        var big = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Red Alert 3", "Mods", "Remix037", "Remix_0.3.7.big");
        if (!File.Exists(big)) return;                                       // 本机没有这个 mod：跳过

        var names = ModNameTableBuilder.ReadNamesFromBig(big, out var entry);

        Assert.True(names.Count > 500, $"只挖到 {names.Count} 条（entry={entry}）");
        Assert.Equal("收割机甲", names["SovietHeavyWalkerVehicle"]);
        Assert.Equal("电磁压缩炮", names["SovietNewTower"]);
    }

    [Fact]
    public void SkudefPointsAtTheBig()
    {
        var big = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Red Alert 3", "Mods", "Remix037", "Remix_0.3.7.big");
        var skudef = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Red Alert 3", "Mods", "Remix037", "Remix_0.3.7.skudef");
        if (!File.Exists(skudef) || !File.Exists(big)) return;

        Assert.Contains(big, ModNameTableBuilder.BigsFromSkudef(skudef), StringComparer.OrdinalIgnoreCase);
    }
}
