using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ReinforcementUnitPickerSearchTests
{
    /// <summary>共用一份夹具：三处各自 new 一遍的话，ReinforcementUnitEntry 构造函数一变就会悄悄走形。</summary>
    private static readonly ReinforcementUnitEntry[] Units =
    [
        new(0xDDFC28DE, "警犬"),
        new(0x6586A5A0, "奥米茄百合子"),
        new(0xAF4C0DA5, "MCV")
    ];

    [Fact]
    public void FilterSearchesByNameOrCodeCaseInsensitively()
    {
        var filtered = ReinforcementUnitCatalog.Filter(Units, "mcv");

        Assert.Single(filtered);
        Assert.Equal(0xAF4C0DA5u, filtered[0].Code);

        filtered = ReinforcementUnitCatalog.Filter(Units, "6586a5a0");

        Assert.Single(filtered);
        Assert.Equal("奥米茄百合子", filtered[0].Name);

        filtered = ReinforcementUnitCatalog.Filter(Units, "0xDDFC28DE");

        Assert.Single(filtered);
        Assert.Equal("警犬", filtered[0].Name);
    }

    [Fact]
    public void FilterReturnsEmptyWhenNothingMatches()
    {
        // 过滤器最重要的边界：搜不到就回空表，既不能回 null 也不能回退成全量。
        var filtered = ReinforcementUnitCatalog.Filter(Units, "does-not-exist");

        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterMatchesOnCategoryTextAsWell()
    {
        ReinforcementUnitEntry[] withCategories =
        [
            new(0x1, "铁锤坦克", "苏联 · 单位"),
            new(0x2, "天启坦克", "苏联 · 单位"),
            new(0x3, "警犬", "盟军 · 单位")
        ];

        var filtered = ReinforcementUnitCatalog.Filter(withCategories, "盟军");

        // 分类文本也是搜索面（与 RefreshFilter 的分类过滤是两件事，这里把语义钉死）。
        Assert.Equal("警犬", Assert.Single(filtered).Name);
    }

    [Theory]
    [InlineData("0XAF4C0DA5")]   // 大写 0X 前缀
    [InlineData("af4c0da5")]
    [InlineData("AF4C0DA5")]
    [InlineData(" MCV ")]        // 前后空白会被 Filter 先 Trim
    public void FilterMatchesCodePrefixAndCaseVariants(string searchText)
    {
        Assert.Equal(0xAF4C0DA5u, Assert.Single(ReinforcementUnitCatalog.Filter(Units, searchText)).Code);
    }

    [Theory]
    [InlineData("zzzz")]      // 非十六进制垃圾
    [InlineData("0xZZ")]      // 带前缀的垃圾
    public void FilterReturnsEmptyForNonHexGarbage(string searchText)
    {
        Assert.Empty(ReinforcementUnitCatalog.Filter(Units, searchText));
    }

    [Fact]
    public void FilterReturnsAllUnitsForBlankSearch()
    {
        var filtered = ReinforcementUnitCatalog.Filter(new[]
        {
            new ReinforcementUnitEntry(0xDDFC28DE, "警犬"),
            new ReinforcementUnitEntry(0xAF4C0DA5, "MCV")
        }, "  ");

        Assert.Equal(2, filtered.Count);
    }
}
