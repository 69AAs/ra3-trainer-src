using System.Linq;
using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 兵种分类照游戏自己的命名读，不另编一套。用户指的路：
/// "这游戏兵种是不是都有分类，比如这是海军啊、空军啊、陆军啊" —— 是的，而且
/// 分类就写在单位库的 editor_name 里（&lt;阵营&gt;&lt;用途&gt;&lt;类别&gt;）。
/// 每条断言都对着真实单位库里的真实单位。
/// </summary>
public sealed class UnitTaxonomyTests
{
    [Theory]
    // editor_name · 真实中文名 · 期望类别 · 期望用途
    [InlineData("JapanAntiNavyShipTech3", "太刀重巡洋舰", UnitDomain.Ship, UnitRole.AntiNavy)]
    [InlineData("JapanYumiAircraftCarrier", "千鸟特攻母舰", UnitDomain.Ship, UnitRole.General)]
    [InlineData("JapanAntiStructureShip", "将军战列舰", UnitDomain.Ship, UnitRole.AntiStructure)]
    [InlineData("SovietAntiVehicleVehicleTech3", "天启坦克", UnitDomain.Vehicle, UnitRole.AntiVehicle)]
    [InlineData("JapanAntiVehicleVehicleTech3", "鬼王", UnitDomain.Vehicle, UnitRole.AntiVehicle)]
    [InlineData("JapanAntiAirVehicleTech1", "打击者-VX", UnitDomain.Vehicle, UnitRole.AntiAir)]
    [InlineData("SovietAntiInfantryInfantry", "征召兵", UnitDomain.Infantry, UnitRole.AntiInfantry)]
    [InlineData("AlliedScoutInfantry", "警犬", UnitDomain.Infantry, UnitRole.Scout)]
    [InlineData("JapanEngineer", "工兵", UnitDomain.Infantry, UnitRole.Engineer)]
    [InlineData("JapanPowerPlantEgg", "发电机核心", UnitDomain.Structure, UnitRole.General)]
    [InlineData("JapanNavalYard", "帝国码头", UnitDomain.Structure, UnitRole.General)]
    [InlineData("JapanMCV_Naval", "基地车（Naval）", UnitDomain.Vehicle, UnitRole.General)]
    public void ReadsDomainAndRoleFromTheGamesOwnNaming(
        string editorName, string displayName, UnitDomain domain, UnitRole role)
    {
        Assert.Equal(domain, UnitTaxonomy.Domain(editorName, displayName));
        Assert.Equal(role, UnitTaxonomy.Role(editorName, displayName));
    }

    [Fact]
    public void AircraftCarrierIsANavyShipAndAAVehicleIsNotAnAircraft()
    {
        // 这两条是最容易错的两类：航母名字里有 Aircraft，防空车的名字里有 Air。
        // 判错的话"海军"里没有航母、"陆军"里没有防空车，用户按类找兵就找不到。
        Assert.Equal(UnitDomain.Ship, UnitTaxonomy.Domain("JapanYumiAircraftCarrier", "千鸟特攻母舰"));
        Assert.Equal(UnitDomain.Vehicle, UnitTaxonomy.Domain("JapanAntiAirVehicleTech1", "打击者-VX"));
        Assert.Equal(UnitDomain.Structure, UnitTaxonomy.Domain("JapanNavalYard", "帝国码头"));
    }

    [Fact]
    public void FallsBackToTheChineseNameWhenTheEditorNameIsMissing()
    {
        // 人工清单 code.txt 只有代号+中文名，没有内部名，这时候只能靠中文名兜底。
        Assert.Equal(UnitDomain.Vehicle, UnitTaxonomy.Domain(string.Empty, "天启坦克"));
        Assert.Equal(UnitDomain.Ship, UnitTaxonomy.Domain(string.Empty, "太刀重巡洋舰"));
        Assert.Equal(UnitDomain.Structure, UnitTaxonomy.Domain(string.Empty, "发电机核心"));
        Assert.Equal(UnitDomain.Infantry, UnitTaxonomy.Domain(string.Empty, "维和步兵"));
        // 认不出中文名又认不出类别的落"特殊"，不硬塞进陆军。
        Assert.Equal(UnitDomain.Special, UnitTaxonomy.Domain(string.Empty, "某个还没见过的单位"));
        // 中文名兜底会踩的坑：苏联英雄"娜塔莎"名字里有个"塔"字 —— 人工清单没有内部名，
        // 只能靠异常表钉住，否则她会跑进"建筑"里。
        Assert.Equal(UnitDomain.Infantry, UnitTaxonomy.Domain(string.Empty, "苏联 · 娜塔莎"));
    }

    [Fact]
    public void NoUnitIsLeftWithoutADomainLabel()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        Assert.True(units.Count >= 400, $"目录条数异常：{units.Count}");
        Assert.All(units, unit =>
        {
            Assert.False(string.IsNullOrWhiteSpace(unit.DomainLabel));
            Assert.False(string.IsNullOrWhiteSpace(unit.RoleLabel));
        });
    }

    [Fact]
    public void TheCatalogueSplitsIntoGroupsThatFitInASinglePanelScreen()
    {
        // 需求原话："上百个单位，难道也让我一格一格的在那里挑到我心仪的吗？"
        // 分类只有在能把全库拆成"一屏看得完"的几组时才有意义。旧的"阵营 · 单位"
        // 分类最大一组 103 条，所以这里钉死：最大的兵种类别不许超过全库的 45%。
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        var byDomain = units.GroupBy(unit => unit.Domain).ToDictionary(group => group.Key, group => group.Count());

        foreach (var domain in UnitTaxonomy.DomainOrder)
        {
            Assert.True(byDomain.TryGetValue(domain, out var count) && count > 0, $"{domain} 一条都没有");
        }

        var biggest = byDomain.Max(pair => pair.Value);
        Assert.True(biggest <= units.Count * 45 / 100,
            $"最大一组 {biggest}/{units.Count} 太胖，用户又要一格一格爬。分布：" +
            string.Join("、", UnitTaxonomy.DomainOrder.Select(d => $"{UnitTaxonomy.DomainLabel(d)}={byDomain[d]}")));

        // 空/海必须是"少而准"：判错会把它们撑大（航母算飞机、防空车算飞机都会撞这里）。
        Assert.InRange(byDomain[UnitDomain.Aircraft], 20, 120);
        Assert.InRange(byDomain[UnitDomain.Ship], 20, 120);
    }

    [Fact]
    public void RealCatalogueUnitsCarryTheExpectedDomain()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        Assert.Equal(UnitDomain.Ship, First(units, "太刀重巡洋舰").Domain);
        Assert.Equal(UnitDomain.Vehicle, First(units, "天启坦克").Domain);
        Assert.Equal(UnitDomain.Vehicle, First(units, "铁锤坦克").Domain);
        Assert.Equal(UnitDomain.Infantry, First(units, "帝国武士").Domain);
        Assert.Equal(UnitDomain.Infantry, First(units, "战熊").Domain);
        Assert.Equal(UnitDomain.Ship, First(units, "千鸟特攻母舰").Domain);

        // 用途也要落到真实单位上：用户打起来想的是"我要防空"，不是"我要第 47 条"。
        // 注意名字以目录里的实际写法为准：人工清单里是"打击者VX"，官方库是"打击者-VX"，
        // 合并时人工那条优先（代号是契约）。
        Assert.Equal(UnitRole.AntiAir, First(units, "打击者VX").Role);
        Assert.Equal(UnitRole.AntiNavy, First(units, "太刀重巡洋舰").Role);
        Assert.Equal(UnitRole.AntiVehicle, First(units, "天启坦克").Role);
        Assert.Equal(UnitRole.AntiInfantry, First(units, "帝国武士").Role);
    }

    [Fact]
    public void VariantSuffixesAndTheChineseNameShareTheSameDomain()
    {
        // "（Enhanced）""（Naval）"是同一个单位的变体，分类必须一致，
        // 否则同一辆天启会在两个类别里各出现一次。
        Assert.Equal(
            UnitTaxonomy.Domain("SovietAntiVehicleVehicleTech3", "天启坦克"),
            UnitTaxonomy.Domain("SovietAntiVehicleVehicleTech3_Enhanced", "天启坦克（Enhanced）"));
        Assert.Equal(
            UnitTaxonomy.Domain("JapanMCV", "基地车"),
            UnitTaxonomy.Domain("JapanMCV_Enhanced_Naval", "基地车（Enhanced_Naval）"));
    }

    /// <summary>
    /// 人工清单（code.txt）没有内部名，全靠中文名兜底。这 85 条是用户最早校对过的
    /// "常用单位"，一条都不许落进"特殊"，否则用户按类别找兵时就是找不到。
    /// </summary>
    [Fact]
    public void EveryManualListEntryLandsInARealDomain()
    {
        var units = ManualUnits();
        Assert.True(units.Count >= 80, $"人工清单条数异常：{units.Count}");
        var unclassified = units
            .Where(unit => unit.Domain == UnitDomain.Special)
            .Select(unit => unit.Name)
            .ToList();
        Assert.True(unclassified.Count == 0, "这些人工条目没有归类：" + string.Join("、", unclassified));
    }

    [Theory]
    [InlineData("警犬", UnitDomain.Infantry)]
    [InlineData("谭雅", UnitDomain.Infantry)]
    [InlineData("战熊", UnitDomain.Infantry)]
    [InlineData("娜塔莎", UnitDomain.Infantry)]
    [InlineData("鬼王", UnitDomain.Vehicle)]
    [InlineData("MCV", UnitDomain.Vehicle)]
    [InlineData("海豚", UnitDomain.Ship)]
    [InlineData("基洛夫飞船", UnitDomain.Aircraft)]
    // 人工清单里这条真实名字带“（起义时刻）”后缀；精确匹配必须用全名，否则会空手而归。
    [InlineData("先锋武装战艇机（起义时刻）", UnitDomain.Aircraft)]
    public void ManualListNamesLandWhereThePlayerWouldLook(string name, UnitDomain domain)
    {
        // ManualUnits() 已按生产链（Parse → StripBuiltInFactionPrefix）剥掉阵营前缀，
        // 这里用「剥前缀后的精确名」匹配，而不是 Contains + 按名字长度排序：
        // Contains 会把“狂热帝国武士”这类更长的名字误配到“帝国武士”上，最短名 tiebreak 又依赖顺序，
        // 两者都可能掩盖真实分类冲突。
        var matches = ManualUnits()
            .Where(unit => WithoutFaction(unit.Name) == name)
            .ToList();
        Assert.True(matches.Count > 0, $"人工清单里没有「{name}」这条单位");
        Assert.Equal(domain, matches[0].Domain);
        // 人工条目没有内部名，Domain 完全由显示名决定 —— 同名必然同分类；若有分歧说明分类表自相矛盾。
        Assert.All(matches, unit => Assert.Equal(domain, unit.Domain));
    }

    private static System.Collections.Generic.IReadOnlyList<ReinforcementUnitEntry> ManualUnits()
    {
        var assembly = typeof(ReinforcementUnitCatalog).Assembly;
        var resource = assembly
            .GetManifestResourceNames()
            .Single(resourceName => resourceName.EndsWith("code.txt", System.StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new System.IO.StreamReader(stream);
        var lines = new System.Collections.Generic.List<string>();
        while (reader.ReadLine() is string line)
        {
            lines.Add(line);
        }

        // 生产路径是 StripBuiltInFactionPrefix(Parse(...))（见 ReinforcementUnitCatalog.LoadBuiltInCore）；
        // 这里把同一条链走一遍，否则测的是「带阵营前缀的名字」，与上线分类的输入不是同一个。
        return ReinforcementUnitCatalog.StripBuiltInFactionPrefix(ReinforcementUnitCatalog.Parse(lines));
    }

    /// <summary>人工清单的名字带"阵营 · "前缀（"帝国 · 帝国武士"），官方库的不带。</summary>
    private static string WithoutFaction(string name)
    {
        var separator = name.IndexOf('·');
        return separator < 0 ? name.Trim() : name[(separator + 1)..].Trim();
    }

    private static ReinforcementUnitEntry First(System.Collections.Generic.IReadOnlyList<ReinforcementUnitEntry> units, string name)
    {
        // 按"去掉阵营前缀后的全名"精确找 —— 用包含匹配会串到"狂热帝国武士"这类别的单位上。
        var matches = units
            .Where(unit => WithoutFaction(unit.Name) == name)
            .OrderBy(unit => unit.Name.Length)
            .ToList();
        Assert.True(matches.Count > 0, $"目录里找不到「{name}」");
        return matches[0];
    }
}
