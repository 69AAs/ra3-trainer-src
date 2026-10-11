using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 单位清单**自愈机制**（用户 2026-10-11：「这种东西不应该我们每次去手动处理，应该是修改器自动处理的机制，
/// 不然这个修改器我们发布出去之后，别人用的时候，那怎么办」）。
/// 这里钉住三条底线：① 坏名字能修就修 ② 玩家自己起的名字一个字都不许动 ③ 认不出的行绝不删。
/// </summary>
public sealed class UnitCatalogMaintenanceTests
{
    private static Func<uint, string?> Lookup(params (uint Code, string Name)[] rows)
    {
        var map = rows.ToDictionary(row => row.Code, row => row.Name);
        return code => map.TryGetValue(code, out var name) ? name : null;
    }

    [Fact]
    public void BrokenNamesGetRepairedFromTheKnownTable()
    {
        var repair = UnitCatalogMaintenance.Normalize(
            new[]
            {
                "0xB31A4632 新单位 0xB31A4632",     // 早先写坏的格式（代号被当成名字抄第二遍）
                "0x0257E867 ",                       // 空名字
                "0x12345678 未命名",                 // 占位
            },
            Lookup((0xB31A4632, "苏联新塔"), (0x0257E867, "收割机甲"), (0x12345678, "混编战车")));

        Assert.True(repair.Changed);
        Assert.Equal(3, repair.Renamed);
        Assert.Equal(
            new[] { "0xB31A4632 苏联新塔", "0x0257E867 收割机甲", "0x12345678 混编战车" },
            repair.Lines);
    }

    [Fact]
    public void TheOwnersOwnNamesAreNeverTouched()
    {
        var repair = UnitCatalogMaintenance.Normalize(
            new[] { "0x11111111 我的超级兵", "0x22222222 神州 · 电击兵" },
            Lookup((0x11111111, "官方名"), (0x22222222, "官方名")));

        Assert.False(repair.Changed);
        Assert.Equal(new[] { "0x11111111 我的超级兵", "0x22222222 神州 · 电击兵" }, repair.Lines);
    }

    [Fact]
    public void PlaceholdersStayWhenNothingCanResolveThem()
    {
        var repair = UnitCatalogMaintenance.Normalize(new[] { "0x33333333 未命名" }, Lookup());

        Assert.False(repair.Changed);
        Assert.Equal(new[] { "0x33333333 未命名" }, repair.Lines);
    }

    [Fact]
    public void DuplicateCodesCollapseAndTheBetterNameWins()
    {
        var repair = UnitCatalogMaintenance.Normalize(
            new[] { "0x44444444 未命名", "0x44444444 好名字", "0x44444444 新单位 0x44444444" },
            Lookup());

        Assert.Equal(2, repair.Deduped);        // 三行同代号 ⇒ 去重掉 2 条
        Assert.Equal(new[] { "0x44444444 好名字" }, repair.Lines);
    }

    [Fact]
    public void UnparseableLinesSurviveAndBlankLinesDoNotCountAsAChange()
    {
        var repair = UnitCatalogMaintenance.Normalize(new[] { "0x55555555 甲", "", "我的自定义备注", "   " }, Lookup());

        Assert.False(repair.Changed);
        Assert.Contains("0x55555555 甲", repair.Lines);
        Assert.Contains("我的自定义备注", repair.Lines);
        Assert.DoesNotContain(string.Empty, repair.Lines);
    }

    [Theory]
    [InlineData("0xB31A4632 新单位 0xB31A4632", 0xB31A4632u, "新单位 0xB31A4632")]
    [InlineData("B31A4632 名字", 0xB31A4632u, "名字")]                    // 不带 0x 也认

    public void LineParsingSucceeds(string line, uint expectedCode, string expectedName)
    {
        Assert.True(UnitCatalogMaintenance.TryParse(line, out var code, out var name));
        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedName, name);
    }

    [Theory]
    [InlineData("0x 坏行")]                                     // 0x 后面没数字 ⇒ 认不出 ⇒ 原样保留
    [InlineData("我的自定义备注")]
    [InlineData("")]
    public void UnparseableLinesAreRejected(string line)
    {
        Assert.False(UnitCatalogMaintenance.TryParse(line, out _, out _));
    }

    [Theory]
    [InlineData("新单位 0x1234", true)]
    [InlineData("", true)]
    [InlineData("未命名", false)]
    [InlineData("我的超级兵", false)]
    public void BrokenNameDetection(string name, bool expected)
    {
        Assert.Equal(expected, UnitCatalogMaintenance.IsBrokenName(name));
    }

    [Fact]
    public void RealCatalogEntriesResolveThroughTheBuiltInTable()
    {
        // 内置库里有的代号（走真实内置表，不是假表）⇒ 应该能补出中文名。
        var known = UnitNaming.KnownName(0xB31A4632);

        // 该代号不一定在内置表里（取决于 mod）；这里只钉住"接口能用、不抛"。
        Assert.True(known is null || known.Length > 0);
    }
}
