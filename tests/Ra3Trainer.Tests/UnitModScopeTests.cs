using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 单位分类地基加固 —— 按 mod 分层（用户 2026-10-07 两问）：
/// ① 从游戏学到的新单位怎么归阵营 / 多 mod 额外阵营有没有地基；
/// ② 换 mod 后，某个 mod 往三阵营里加的单位，别的 mod 用不了 —— 怎么安排。
///
/// 这里钉的是**数据层口径**（<see cref="UnitModScope"/> + <see cref="UnitTaxonomy.OrderFactions"/>）；
/// 面板级行为（找兵三层随当前 mod 收敛）由 tools/Round37PanelFixture 的 M8 段钉。
/// </summary>
public sealed class UnitModScopeTests
{
    // ---- 假数据：造两个 mod（一个往盟军里加兵、一个自带新阵营）+ 原版通用 ----
    private static ReinforcementUnitEntry BaseAllied(uint c, string n) => new(c, n, "盟军 · 单位", "通用");
    private static ReinforcementUnitEntry ModAAllied(uint c, string n) => new(c, n, "盟军 · 单位", "MODA");     // 往盟军里加
    private static ReinforcementUnitEntry ModBNewFaction(uint c, string n) => new(c, n, "暗影 · 单位", "MODB"); // 自带新阵营

    private static readonly ReinforcementUnitEntry[] Catalogue =
    [
        BaseAllied(0xA0000001, "原版警犬"),
        BaseAllied(0xA0000002, "原版维和步兵"),
        ModAAllied(0xB0000001, "模A陆军"),
        ModBNewFaction(0xC0000001, "暗影武士"),
    ];

    [Fact]
    public void FactionOrderUnknownFactionsEnterTheTableAfterTheKnownListAndOtherIsAlwaysLast()
    {
        // 数据里出现两个**未知阵营**（新 mod 带来的）与收容桶「其他」。
        var present = new[] { "其他", "暗影", "变异体", "盟军", "苏联", "帝国", "神州" };
        var counts = new Dictionary<string, int>
        {
            ["盟军"] = 1, ["苏联"] = 1, ["帝国"] = 1, ["神州"] = 1,
            ["暗影"] = 9, ["变异体"] = 5, ["其他"] = 1,
        };

        var ordered = UnitTaxonomy.OrderFactions(present, faction => counts[faction]);

        // 已知四阵营在最前且顺序固定；未知阵营自动入表、按出现次数降序（暗影 9 > 变异体 5）；「其他」恒最后。
        Assert.Equal(
            new[] { "盟军", "苏联", "帝国", "神州", "暗影", "变异体", "其他" },
            ordered.Select(display => display.Faction).ToArray());

        // 未知阵营的显示名 = 原文（不丢、不并进「其他」）。
        Assert.Equal("暗影", ordered.Single(d => d.Faction == "暗影").Label);
        // 「其他」只有一个、且在末位。
        Assert.Equal(UnitTaxonomy.OtherFaction, ordered[^1].Faction);

        // 旧口径（把其余阵营按字典序排、其余含「其他」一起排）会得到「变异体 / 暗影」的顺序 ——
        // 与本条断言的 暗影 > 变异体 相反 ⇒ 老代码跑这条必红。这是新旧行为的分界点。
        var legacyExtras = new[] { "变异体", "暗影" }.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEqual(legacyExtras, new[] { "暗影", "变异体" });
    }

    [Fact]
    public void OrderFactionsHonoursConfiguredDisplayNamesAndOrder()
    {
        // B：阵营显示名/顺序可配（加 mod 阵营不改代码，改名/排序也不改代码）。
        var configured = new[]
        {
            new UnitTaxonomy.FactionDisplay("苏联", "苏"),
            new UnitTaxonomy.FactionDisplay("盟军", "盟"),
        };

        var ordered = UnitTaxonomy.OrderFactions(new[] { "盟军", "苏联", "暗影" }, null, configured);

        Assert.Equal(new[] { "苏", "盟", "暗影" }, ordered.Select(d => d.Label).ToArray());
        Assert.Equal(new[] { "苏联", "盟军", "暗影" }, ordered.Select(d => d.Faction).ToArray());
    }

    [Fact]
    public void SwitchingTheCurrentModHidesTheOtherModsUnitsAndConvergesTheFactionList()
    {
        var modA = UnitModScope.ForLauncher(@"D:\mods\MODALauncher.exe");
        var modB = UnitModScope.ForLauncher(@"D:\mods\MODBLauncher.exe");

        // 当前 = MODA：原版通用 + MODA 可见；MODB 的（新阵营暗影）不可见。
        var underA = Catalogue.Where(unit => modA.Allows(unit)).ToArray();
        Assert.Contains(underA, unit => unit.Code == 0xB0000001);   // MODA 往盟军里加的那个
        Assert.DoesNotContain(underA, unit => unit.Code == 0xC0000001);  // MODB 的
        Assert.Equal(new[] { "盟军" }, underA.Select(u => u.Faction).Distinct().ToArray());  // 阵营随之收敛：没有「暗影」

        // 当前 = MODB：MODA 的不可见，MODB 的新阵营出现。
        var underB = Catalogue.Where(unit => modB.Allows(unit)).ToArray();
        Assert.DoesNotContain(underB, unit => unit.Code == 0xB0000001);
        Assert.Contains(underB, unit => unit.Code == 0xC0000001);

        var factionsB = UnitTaxonomy.OrderFactions(underB.Select(u => u.Faction), null, UnitTaxonomy.DefaultFactionDisplays);
        Assert.Equal(new[] { "盟军", "暗影" }, factionsB.Select(d => d.Faction).ToArray());  // 暗影自动进表、排在盟军后
    }

    [Fact]
    public void OriginalGameEnvironmentShowsNoModFactions()
    {
        // 原版环境：直接开 RA3.exe（不是任何 mod 启动器）。
        var baseScope = UnitModScope.ForLauncher(@"D:\Games\Red Alert 3\RA3.exe");
        Assert.True(baseScope.Filter);

        var visible = Catalogue.Where(unit => baseScope.Allows(unit)).ToArray();

        Assert.All(visible, unit => Assert.Equal("通用", unit.ModSource));          // 只剩原版通用
        Assert.DoesNotContain(visible, unit => unit.Faction == "暗影");              // 没有 mod 新阵营
        Assert.DoesNotContain(visible, unit => unit.Code is 0xB0000001 or 0xC0000001); // 两个 mod 的单位都不出现
    }

    [Fact]
    public void UnconfiguredLauncherIsPermissiveSoNoUnitIsEverLost()
    {
        // 没配启动器（空 / null）⇒ 宽松（不过滤），保持旧行为、绝不因为"当前 mod 未知"就丢单位。
        foreach (var scope in new[]
                 {
                     UnitModScope.ForLauncher(null),
                     UnitModScope.ForLauncher(string.Empty),
                     UnitModScope.ForLauncher("   "),
                 })
        {
            Assert.False(scope.Filter);
            Assert.All(Catalogue, unit => Assert.True(scope.Allows(unit)));
        }

        // 认得出的陌生启动器（不是原版、不是日冕）⇒ 当成它自己的 mod 作用域：原版通用仍可见，别的 mod 单位被挡。
        var mystery = UnitModScope.ForLauncher(@"C:\whatever\mystery.exe");
        Assert.True(mystery.Filter);
        Assert.True(mystery.Allows("通用"));
        Assert.False(mystery.Allows("MODA"));
    }

    [Fact]
    public void CoronaLauncherMapsToTheCoronaSourceSoShenzhouStaysVisibleInCorona()
    {
        // 日冕是真 mod：CoronaLauncher.exe → 日冕，所以内置库里 ModSource=日冕（=神州阵营）的单位在日冕环境可见。
        var corona = UnitModScope.ForLauncher(@"D:\Games\CoronaLauncher\CoronaLauncher.exe");
        Assert.True(corona.Allows("日冕"));      // 当前 mod 的来源
        Assert.True(corona.Allows("通用"));
        Assert.True(corona.Allows("待核对"));
        Assert.False(corona.Allows("ARKMOD"));   // 别的 mod 挡掉
    }

    [Fact]
    public void ModBadgeMarksOnlyNonBaseSourcesAndNormalisesLearnedSources()
    {
        Assert.Equal("仅日冕", UnitModScope.SourceBadge("日冕"));
        Assert.Equal("仅MODB", ModBNewFaction(0xC0000002, "x").ModBadge);
        Assert.Equal(string.Empty, UnitModScope.SourceBadge("通用"));
        Assert.Equal(string.Empty, UnitModScope.SourceBadge("待核对"));
        Assert.Equal(string.Empty, UnitModScope.SourceBadge("杂物"));
        Assert.Equal(string.Empty, UnitModScope.SourceBadge(null));

        // E：游戏里学到的新单位默认归「本局/当前 mod」，来源记成当前 mod 的显示名；认不出就是"待核对"。
        Assert.Equal("日冕", UnitModScope.LearnedSourceFor(@"D:\Games\CoronaLauncher\CoronaLauncher.exe"));
        Assert.Equal("待核对", UnitModScope.LearnedSourceFor(null));
        Assert.Equal("modb", UnitModScope.LearnedSourceFor(@"D:\mods\MODBLauncher.exe"));

        // 学到的单位的来源一定落在那个 mod 的作用域里 ⇒ 在当前作用域内可见。
        var learned = new ReinforcementUnitEntry(0xD0000001, "新单位 0xD0000001", "当前模组 · 单位",
            UnitModScope.LearnedSourceFor(@"D:\mods\MODBLauncher.exe"));
        Assert.True(UnitModScope.ForLauncher(@"D:\mods\MODBLauncher.exe").Allows(learned));
        Assert.False(UnitModScope.ForLauncher(@"D:\Games\Red Alert 3\RA3.exe").Allows(learned));
    }

    [Fact]
    public void NormalisationStripsNoteSuffixesSoLearnedRowsStillMatchTheirMod()
    {
        // 旧格式的学习来源带「（本机读到）」尾巴；归一化要把它去掉才能对上当前 mod。
        Assert.Equal("corona", UnitModScope.NormalizeToken("corona（本机读到）"));
        Assert.Equal("日冕", UnitModScope.NormalizeToken("日冕"));
        Assert.Equal("arkmod", UnitModScope.NormalizeToken("ARKMOD"));
        Assert.True(UnitModScope.ForLauncher(@"D:\mods\ArkModLauncher.exe").Allows("ARKMOD"));
    }
}
