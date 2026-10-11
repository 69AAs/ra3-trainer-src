using Ra3Trainer.Core.Features;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ReinforcementUnitCatalogTests
{
    [Fact]
    public void BuiltInCatalogIncludesExecutionerWithObservedTypeCode()
    {
        // Firsthand WBData.big extraction names 450BD734; round19 samples observe it 322 times.
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        var executioner = Assert.Single(units, unit => unit.Code == 0x450BD734u);

        Assert.Equal("将军刽子手", executioner.Name);
        Assert.Equal("帝国 · 单位", executioner.Category);
        Assert.Equal("通用", executioner.ModSource);
        Assert.Contains(executioner, ReinforcementUnitCatalog.Filter(units, "将军刽子手"));
        Assert.Contains(executioner, ReinforcementUnitCatalog.Filter(units, "450BD734"));
        var names = ReinforcementUnitCatalog.LoadBuiltInNameMap();
        Assert.Equal(executioner.Name, names["JapanMechaKing"]);
        Assert.Equal(executioner.Name, names["JAPANMECHAKING"]);
    }
    [Fact]
    public void ParseReadsCodesAndNamesFromCodeListLines()
    {
        var units = ReinforcementUnitCatalog.Parse(new[]
        {
            "DDFC28DE 警犬",
            "",
            "0x6586A5A0 奥米茄百合子",
            "d741d327 恐怖机器人"
        });

        Assert.Collection(
            units,
            unit =>
            {
                Assert.Equal(0xDDFC28DEu, unit.Code);
                Assert.Equal("警犬", unit.Name);
                Assert.Equal("0xDDFC28DE", unit.CodeText);
            },
            unit =>
            {
                Assert.Equal(0x6586A5A0u, unit.Code);
                Assert.Equal("奥米茄百合子", unit.Name);
                Assert.Equal("0x6586A5A0", unit.CodeText);
            },
            unit =>
            {
                Assert.Equal(0xD741D327u, unit.Code);
                Assert.Equal("恐怖机器人", unit.Name);
                Assert.Equal("0xD741D327", unit.CodeText);
            });
    }

    [Fact]
    public void FindByCodeReturnsMatchingUnitAndNullWhenMissing()
    {
        var units = ReinforcementUnitCatalog.Parse(new[] { "DDFC28DE 警犬", "AF4C0DA5 MCV" });

        Assert.Equal("MCV", ReinforcementUnitCatalog.FindByCode(units, 0xAF4C0DA5u)?.Name);
        Assert.Null(ReinforcementUnitCatalog.FindByCode(units, 0x11111111u));
    }

    [Fact]
    public void ParseSkipsMalformedLines()
    {
        var units = ReinforcementUnitCatalog.Parse(new[]
        {
            "not-a-code",
            "12345",
            "  ",
            "AF4C0DA5 MCV"
        });

        Assert.Single(units);
        Assert.Equal(0xAF4C0DA5u, units[0].Code);
        Assert.Equal("MCV", units[0].Name);
    }

    [Fact]
    public void LoadBuiltInContainsRepresentativeEntries()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();

        Assert.NotEmpty(units);

        // 名字带阵营前缀（"盟军 · 警犬"），代号是稳定契约，只断言代号 + 名字包含兵种名
        Assert.Contains(units, unit => unit.Code == 0x6586A5A0 && unit.Name.Contains("奥米茄百合子"));
        Assert.Contains(units, unit => unit.Code == 0x28DA574E && unit.Name.Contains("MCV"));
        Assert.Contains(units, unit => unit.Code == 0xDDFC28DE && unit.Name.Contains("警犬"));
        Assert.All(units, unit => Assert.False(string.IsNullOrWhiteSpace(unit.Name)));
    }

    [Fact]
    public void UserCatalogSaveAndLoadRoundTripsCodeAndNameOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"units-{Guid.NewGuid():N}.txt");
        try
        {
            ReinforcementUnitCatalog.SaveUserCatalog(path, new[]
            {
                new ReinforcementUnitEntry(0x22222222, "日冕 · 测试单位"),
                new ReinforcementUnitEntry(0x11111111, "新单位 0x11111111")
            });

            // 落盘格式真的只有 "{CodeText} {Name}" 两列 —— 直接钉住文件内容，
            // 而不是用 "RoundTrips" 这个名号暗示整条记录（Category/ModSource/EditorName）都能原样往返。
            // 这三个字段 SaveUserCatalog 不落盘、LoadUserCatalog 会用名字重推 DeriveCategory：
            // 要断言它们必须先改 src 的序列化口径，本用例因此把范围收窄到真实的“代号 + 显示名”契约。
            Assert.Equal(new[] { "0x11111111 新单位 0x11111111", "0x22222222 日冕 · 测试单位" }, File.ReadAllLines(path));

            var loaded = ReinforcementUnitCatalog.LoadUserCatalog(path);

            Assert.Equal(2, loaded.Count);
            Assert.Equal(0x11111111u, loaded[0].Code);   // 按代号排序
            Assert.Equal("新单位 0x11111111", loaded[0].Name);
            Assert.Equal("日冕 · 测试单位", loaded[1].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadUserCatalogReturnsEmptyWhenFileMissing()
    {
        Assert.Empty(ReinforcementUnitCatalog.LoadUserCatalog(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt")));
    }

    [Fact]
    public void BuiltInNameMapMapsInternalNamesToChineseNames()
    {
        var map = ReinforcementUnitCatalog.LoadBuiltInNameMap();

        // 精确规模，而不是 >100 的下界：丢几十条对照也照样过 >100，分不清“并对了”和“并得差不多”。
        Assert.Equal(145, map.Count);
        Assert.Equal("警犬", map["AlliedScoutInfantry"]);
        Assert.Equal("天启坦克", map["SovietAntiVehicleVehicleTech3"]);
    }

    [Fact]
    public void MergeKeepsBuiltInEntriesAndAppendsOnlyNewCodes()
    {
        var merged = ReinforcementUnitCatalog.Merge(
            new[]
            {
                new ReinforcementUnitEntry(0x11111111, "A"),
                new ReinforcementUnitEntry(0x22222222, "B")
            },
            new[]
            {
                new ReinforcementUnitEntry(0x22222222, "B-override"),
                new ReinforcementUnitEntry(0x33333333, "C")
            });

        Assert.Collection(
            merged,
            unit => Assert.Equal((0x11111111u, "A"), (unit.Code, unit.Name)),
            unit => Assert.Equal((0x22222222u, "B"), (unit.Code, unit.Name)),
            unit => Assert.Equal((0x33333333u, "C"), (unit.Code, unit.Name)));
    }

    [Fact]
    public void MergeDeduplicatesByCategoryAndNameAcrossCodesAndBackfillsEditorName()
    {
        // 官网那条与人工那条 (Category, Name) 相同、代号不同 → 必须并成一条（否则选择器里出现重复行）；
        // 保留人工那条（代号是契约），并把官网带的内部名回填进去 —— 没内部名兵种分类会落“特殊”。
        var merged = ReinforcementUnitCatalog.Merge(
            new[] { new ReinforcementUnitEntry(0x11111111, "海啸坦克", "帝国 · 单位", "通用") },
            new[] { new ReinforcementUnitEntry(0x99999999, "海啸坦克", "帝国 · 单位", "日冕", "JapanTsunamiTank") });

        var unit = Assert.Single(merged);
        Assert.Equal(0x11111111u, unit.Code);
        Assert.Equal("JapanTsunamiTank", unit.EditorName);
    }

    [Fact]
    public void MergeKeepsExistingEditorNameAndIgnoresDonorValue()
    {
        // FillEditorName 只在保留条目内部名为空时回填：已有内部名不许被后来的来源覆盖。
        var merged = ReinforcementUnitCatalog.Merge(
            new[] { new ReinforcementUnitEntry(0x11111111, "海啸坦克", "帝国 · 单位", "通用", "OriginalName") },
            new[] { new ReinforcementUnitEntry(0x99999999, "海啸坦克", "帝国 · 单位", "日冕", "DonorName") });

        var unit = Assert.Single(merged);
        Assert.Equal("OriginalName", unit.EditorName);
    }

    [Fact]
    public void ParseFullCatalogKeepsCategoryAndFilterMatchesIt()
    {
        var units = ReinforcementUnitCatalog.ParseFullCatalog(
        [
            "# 注释行",
            "59908E62\t帝国 · 单位\t鬼王",
            "8F32A13F\t神州 · 建筑\t揽星亭",
            "坏行",
        ]);

        Assert.Equal(2, units.Count);
        Assert.Equal("帝国 · 单位", units[0].Category);
        Assert.Equal("神州 · 建筑", units[1].Category);

        // 搜"神州"能把整个阵营列出来（搜索同时匹配分类）
        Assert.Single(ReinforcementUnitCatalog.Filter(units, "神州"));
        Assert.Single(ReinforcementUnitCatalog.Filter(units, "鬼王"));
    }

    [Fact]
    public void CuratedEntriesGetUnitCategoryFromNamePrefix()
    {
        var units = ReinforcementUnitCatalog.Parse(["DDFC28DE 盟军 · 警犬"]);
        Assert.Equal("盟军 · 单位", units[0].Category);
    }

    [Fact]
    public void PropsCatalogIsSeparateFromTheDefaultList()
    {
        var props = ReinforcementUnitCatalog.LoadBuiltInProps();

        // 精确规模，而不是 >50 的下界（丢一半杂物也照样过）。
        Assert.Equal(135, props.Count);
        Assert.All(props, unit => Assert.Equal("杂物", unit.ModSource));
        // 默认清单里不许混进杂物
        var units = ReinforcementUnitCatalog.LoadBuiltIn();
        Assert.DoesNotContain(units, unit => unit.ModSource == "杂物");
        Assert.DoesNotContain(units, unit => unit.Category.StartsWith("杂物"));
    }

    [Fact]
    public void ParseFullCatalogReadsModSourceSoOtherModsCanBeFiltered()
    {
        var units = ReinforcementUnitCatalog.ParseFullCatalog(
        [
            "59908E62\t帝国 · 单位\t鬼王\t通用",
            "8F32A13F\t神州 · 建筑\t揽星亭\t日冕",
            "AFA6371C\t盟军 · 单位\t某单位\t待核对",
        ]);

        Assert.Equal(["通用", "日冕", "待核对"], units.Select(unit => unit.ModSource).ToArray());
    }

    [Fact]
    public void ModKeyFromLauncherPathSeparatesModCatalogs()
    {
        // mod 之间单位不互通 → "读到就记住"必须分档：corona / armorrush / default
        Assert.Equal("corona", ReinforcementUnitCatalog.ModKeyFromLauncherPath(@"D:\Games\CoronaLauncher\CoronaLauncher.exe"));
        Assert.Equal("armorrush", ReinforcementUnitCatalog.ModKeyFromLauncherPath(@"D:\mods\ArmorRushLauncher.exe"));
        // 直接开游戏本体 → 独立档案 ra3（和任何 mod 都不混）
        Assert.Equal("ra3", ReinforcementUnitCatalog.ModKeyFromLauncherPath(@"D:\Games\Red Alert 3\RA3.exe"));
        Assert.Equal("default", ReinforcementUnitCatalog.ModKeyFromLauncherPath(null));

        var path = ReinforcementUnitCatalog.UserCatalogPathForMod(@"C:\app", @"D:\Games\CoronaLauncher\CoronaLauncher.exe");
        Assert.EndsWith("units-corona.txt", path);
    }

    /// <summary>
    /// 内置清单现在 = 人工 code.txt（84 条）+ 日冕官网单位库（630 条，含神州阵营）。
    /// 代号验证：官网 game_objects 的 type_info_instance_id 与 6 个已知单位实测一致。
    /// </summary>
    [Fact]
    public void BuiltInCatalogMergesOfficialGameObjectDatabase()
    {
        var units = ReinforcementUnitCatalog.LoadBuiltIn();

        // 精确钉住合并后的规模（人工 84 条 + 官网 495 条，按代号/(分类,名字)去重后 500），
        // 而不是 >450 的下界 —— 几百条 official 被丢掉、或去重过头，也照样高于下界。
        Assert.Equal(500, units.Count);
        // 官网独有的代号必须在（人工清单里没有这些）：否则“并进来了”只是条数上的巧合。
        Assert.Contains(units, unit => unit.Code == 0x87711A73u && unit.Name == "一式磁轨炮重巡");
        Assert.Contains(units, unit => unit.Code == 0xECCE3CA4u && unit.Name.Contains("凌波护卫战车"));
        // 人工清单的代号是契约，必须还在且名字没被官网数据覆盖
        var ghost = Assert.Single(units.Where(unit => unit.Code == 0x59908E62u));
        Assert.Contains("鬼王", ghost.Name);
        // 官网带来的日冕专有阵营
        Assert.Contains(units, unit => unit.Name.Contains("神州"));
        // 已滤掉地图杂物（信标/箱子/平民建筑）
        Assert.DoesNotContain(units, unit => unit.Name.Contains("属性箱") || unit.Name.Contains("平民建筑物"));
        // 同一分类里不许有同名（重名会让用户没法选："为什么这么多重复的"）
        var duplicateNames = units
            .GroupBy(unit => (unit.Category, unit.Name))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Category}/{group.Key.Name}")
            .ToArray();
        Assert.Empty(duplicateNames);
        // 代号不重复
        Assert.Equal(units.Count, units.Select(unit => unit.Code).Distinct().Count());
    }
}


