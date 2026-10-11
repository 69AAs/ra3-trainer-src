using System.Linq;
using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 面板找兵第 1 层 = **真阵营**（用户 2026-10-07 口径 B："第一层不是先选阵营吗" → 要帝国/苏联/盟军/神州）。
/// 阵营不是另编的，是单位库分类「阵营 · 类型」里的阵营段（官方库第 2 列实测：
/// 帝国 · 单位/建筑、盟军 · …、神州 · …、苏联 · …，没有"中立/其他"这种字面阵营）。
/// 认不出阵营的落「其他」，保证一个都不丢。
/// </summary>
public sealed class UnitFactionTests
{
    [Theory]
    [InlineData("盟军 · 单位", "盟军")]
    [InlineData("盟军 · 建筑", "盟军")]
    [InlineData("帝国 · 单位", "帝国")]
    [InlineData("神州 · 建筑", "神州")]
    [InlineData("苏联 · 单位", "苏联")]
    [InlineData("  盟军 · 警犬  ", "盟军")]          // 段间有空白也认
    [InlineData("", UnitTaxonomy.OtherFaction)]     // 空分类 → 其他
    [InlineData("没有分隔符的分类", UnitTaxonomy.OtherFaction)]  // 非"阵营 · 类型"形态 → 其他
    [InlineData(" · 单位", UnitTaxonomy.OtherFaction)]           // 阵营段是空 → 其他
    public void FactionIsTheSegmentBeforeTheSeparator(string category, string expected)
    {
        Assert.Equal(expected, UnitTaxonomy.Faction(category));
    }

    [Fact]
    public void FactionOrderListsTheFourRealFactions()
    {
        // 顺序就是面板第一层的顺序（已知阵营在前，其余含「其他」排后面）。
        Assert.Equal(new[] { "盟军", "苏联", "帝国", "神州" }, UnitTaxonomy.FactionOrder);
    }

    [Fact]
    public void BuiltInCatalogHasExactlyTheFourRealFactionsWithKnownCounts()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();

        // 官方库快照：500 条，四个阵营，一个不落。
        Assert.Equal(500, units.Count);

        var byFaction = units
            .GroupBy(unit => unit.Faction)
            .ToDictionary(group => group.Key, group => group.Count());
        // 恰好就是这四个阵营（不排序比较：中文排序与"展示顺序"无关，这里只钉"取值集合"）。
        Assert.Equal(4, byFaction.Count);
        Assert.All(UnitTaxonomy.FactionOrder, faction => Assert.True(byFaction.ContainsKey(faction)));
        Assert.Equal(113, byFaction["盟军"]);
        Assert.Equal(130, byFaction["苏联"]);
        Assert.Equal(109, byFaction["帝国"]);
        Assert.Equal(148, byFaction["神州"]);

        // 内置库里没有单位落「其他」（"其他"只可能出现于玩家自己导入的、名字不带阵营前缀的 units-*.txt）。
        Assert.DoesNotContain(units, unit => unit.Faction == UnitTaxonomy.OtherFaction);
    }

    [Fact]
    public void FactionAndGameTypeAreIndependentAxesSoBothFiltersCompose()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();

        // 阵营来自 Category，游戏分类来自 editor_name —— 两个独立字段 ⇒ 可以先按阵营筛、再按分类筛。
        // 每个阵营按分类拆开的总和必须等于该阵营全库（否则说明某一维丢了单位）。
        foreach (var faction in UnitTaxonomy.FactionOrder)
        {
            var scoped = units.Where(unit => unit.Faction == faction).ToArray();
            var byDomain = scoped.GroupBy(unit => unit.Domain).Sum(group => group.Count());
            Assert.Equal(scoped.Length, byDomain);

            // 官方库里每个阵营的六个分类都非空（面板"本阵营没有的分类不列"这条规则不会误伤官方数据）。
            Assert.Equal(UnitTaxonomy.DomainOrder.Length, scoped.Select(unit => unit.Domain).Distinct().Count());
        }
    }

    [Fact]
    public void EntryFactionComesFromItsCategory()
    {
        Assert.Equal("帝国", new ReinforcementUnitEntry(0x11111111, "鬼王", "帝国 · 单位").Faction);
        Assert.Equal(UnitTaxonomy.OtherFaction, new ReinforcementUnitEntry(0x22222222, "冒烟临时单位", "冒烟临时阵营").Faction);
    }

    [Fact]
    public void OrderFactionsOverTheBuiltInCatalogYieldsExactlyTheFourConfiguredFactions()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        var ordered = UnitTaxonomy.OrderFactions(
            units.Select(unit => unit.Faction),
            faction => units.Count(unit => unit.Faction == faction),
            UnitTaxonomy.DefaultFactionDisplays);

        // 内置库没有「其他」，四个阵营按配置顺序（盟军/苏联/帝国/神州）—— 与 FactionOrder 同源。
        Assert.Equal(UnitTaxonomy.FactionOrder, ordered.Select(display => display.Faction).ToArray());
        Assert.DoesNotContain(ordered, display => display.Faction == UnitTaxonomy.OtherFaction);
    }

    [Fact]
    public void OrderFactionsPutsOtherLastEvenWhenItHasTheMostUnits()
    {
        var present = new[] { "其他", "变异体", "盟军" };
        var counts = new Dictionary<string, int> { ["其他"] = 99, ["变异体"] = 7, ["盟军"] = 3 };

        var ordered = UnitTaxonomy.OrderFactions(present, faction => counts[faction]);

        Assert.Equal(new[] { "盟军", "变异体", "其他" }, ordered.Select(display => display.Faction).ToArray());
    }

    [Fact]
    public void RoleLabelsAreKeptInTheCoreEvenThoughThePanelNoLongerUsesThemAsALayer()
    {
        // 用户 2026-10-07："用途那套标签从面板流程里去掉" —— 去掉的是**面板这一层**，不是数据。
        // Role/UnitRole 仍完整保留（供描述/以后复用），这里钉住它没被顺手删掉。
        Assert.Equal("反车辆", UnitTaxonomy.RoleLabel(UnitRole.AntiVehicle));
        Assert.Equal(UnitRole.AntiVehicle, UnitTaxonomy.Role("SovietAntiVehicleVehicleTech3", "天启坦克"));
    }
}
