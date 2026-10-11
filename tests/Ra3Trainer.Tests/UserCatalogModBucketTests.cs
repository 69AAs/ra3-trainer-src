using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 用户清单里的 mod 单位要**按 mod 建桶**，不再一律挤进「其他」。
/// 背景（2026-10-11 用户）："这么多单位混在一起，别人也不好找，我们日冕都能做个单独的页面，为什么其他 mod 不行？"
/// 根因：存盘只写 `代号 名字` ⇒ 类别丢失；重新加载时 DeriveCategory 把 `新单位 0x…` 推成「其他 · 单位」。
/// 这里钉住：占位桶被换成「&lt;mod 显示名&gt; · 单位」；真阵营分类绝不被改写；mod 显示名有别名（remix → REMIX，corona → 日冕）。
/// </summary>
public sealed class UserCatalogModBucketTests
{
    private static ReinforcementUnitEntry Entry(uint code, string name, string category, string mod = "通用") =>
        new(code, name, category, mod);

    [Fact]
    public void PlaceholderBucketBecomesTheModBucket()
    {
        var units = new[]
        {
            Entry(0xB31A4632, "新单位 0xB31A4632", "其他 · 单位"),   // 扫描学到的那种
            Entry(0x0257E867, "新单位 0x0257E867", "当前模组 · 单位"), // 刚学时写的占位
            Entry(0x12345678, "新单位 0x12345678", string.Empty),
        };

        var stamped = ReinforcementUnitCatalog.StampModCategory(units, "REMIX");

        Assert.All(stamped, entry => Assert.Equal("REMIX · 单位", entry.Category));
        Assert.All(stamped, entry => Assert.Equal("REMIX", entry.ModSource));
    }

    [Fact]
    public void RealFactionCategoriesAreNeverRewritten()
    {
        // 人工清单可以自己写前缀改归属（"盟军 · 我的新兵"）—— 那些绝不能被 mod 桶盖掉。
        var units = new[]
        {
            Entry(0x11111111, "盟军 · 我的新兵", "盟军 · 单位"),
            Entry(0x22222222, "神州 · 单位", "神州 · 建筑"),
            Entry(0x33333333, "新单位 0x33333333", "其他 · 单位"),
        };

        var stamped = ReinforcementUnitCatalog.StampModCategory(units, "REMIX");

        Assert.Equal("盟军 · 单位", stamped[0].Category);
        Assert.Equal("神州 · 建筑", stamped[1].Category);
        Assert.Equal("REMIX · 单位", stamped[2].Category);
        Assert.Equal("通用", stamped[0].ModSource);
    }

    [Fact]
    public void UnknownModLabelLeavesTheEntriesAlone()
    {
        var units = new[] { Entry(0x44444444, "新单位 0x44444444", "其他 · 单位") };

        var stamped = ReinforcementUnitCatalog.StampModCategory(units, "   ");

        Assert.Equal("其他 · 单位", stamped[0].Category);
    }

    [Theory]
    [InlineData("remix", "REMIX")]
    [InlineData("remix037", "REMIX")]
    [InlineData("corona", "日冕")]
    [InlineData("armod", "armod")]
    public void ModLabelsComeFromTheAliasTable(string modKey, string expectedLabel)
    {
        Assert.Equal(expectedLabel, UnitModScope.LabelFor(modKey));
    }
}
