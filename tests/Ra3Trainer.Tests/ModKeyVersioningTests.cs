using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 「mod 升级一次就换一个键」的机制问题（2026-10-11 机制盘点）：
/// 键带版本号 ⇒ 玩家已学会的单位清单和名字缓存全部对不上、从头再长。
/// 这里钉住：① 版本尾巴被去掉（`remix037` → `remix`）② 去掉后仍像模像样的键不被裁坏
/// ③ 老清单能自动搬过来 ④ 名字缓存按源包指纹失效。
/// </summary>
public sealed class ModKeyVersioningTests
{
    [Theory]
    [InlineData("remix037", "remix")]
    [InlineData("corona3258", "corona")]
    [InlineData("armod", "armod")]
    [InlineData("default", "default")]
    [InlineData("mod3", "mod")]
    [InlineData("", "")]
    public void VersionSuffixIsStripped(string input, string expected)
    {
        Assert.Equal(expected, UnitModScope.StripVersionSuffix(input));
    }

    [Fact]
    public void TheRealPipelineNormalizesThenStripsTheVersion()
    {
        // 真实链路：skudef/目录名 → NormalizeToken（去掉下划线、点、小写）→ 去版本号。
        foreach (var raw in new[] { "Remix_0.3.7", "Remix 0.3.7", "REMIX_0.3.7" })
        {
            Assert.Equal("remix", UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken(raw)));
        }

        Assert.Equal("corona", UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken("corona_3.258")));
        Assert.Equal("armod", UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken("ARMod")));
    }

    [Fact]
    public void ShortOrDigitOnlyKeysAreLeftAlone()
    {
        foreach (var key in new[] { "ab", "a1", "123", "x" })
        {
            Assert.Equal(key, UnitModScope.StripVersionSuffix(key));
        }
    }

    [Fact]
    public void OldVersionCatalogIsCarriedOver()
    {
        var directory = Directory.CreateTempSubdirectory("ra3-carry");
        try
        {
            var old = Path.Combine(directory.FullName, "units-remix037.txt");
            File.WriteAllText(old, "0xB31A4632 电磁压缩炮\n");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
            var older = Path.Combine(directory.FullName, "units-remix036.txt");
            File.WriteAllText(older, "0x1 旧版本\n");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));

            var sibling = ReinforcementUnitCatalog.FindVersionSiblingCatalog(directory.FullName, "remix");

            Assert.Equal(old, sibling);                       // 挑最近改的那份
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void CurrentKeyHasNoSibling()
    {
        var directory = Directory.CreateTempSubdirectory("ra3-carry2");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "units-remix.txt"), "0x1 甲\n");

            Assert.Null(ReinforcementUnitCatalog.FindVersionSiblingCatalog(directory.FullName, "remix"));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void NameTableCacheCarriesTheSourceFingerprint()
    {
        var directory = Directory.CreateTempSubdirectory("ra3-cache");
        try
        {
            var table = new ModNameTable("remix", @"X:\mod.big",
                new Dictionary<string, string> { ["SovietNewTower"] = "电磁压缩炮" }, 812681778, 638500000000000000);
            var path = ModNameTable.CachePathFor(directory.FullName, "remix");
            table.Save(path);

            var loaded = ModNameTable.Load(path, "remix");

            Assert.Equal("电磁压缩炮", loaded.Names["SovietNewTower"]);
            Assert.True(loaded.MatchesSource(812681778, 638500000000000000));   // 源包没变 ⇒ 缓存可用
            Assert.False(loaded.MatchesSource(1, 1));                           // mod 更新过 ⇒ 缓存失效、重挖
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
