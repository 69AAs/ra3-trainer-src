using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerAppSettingsStoreTests
{
    /// <summary>每个用例一个独占临时目录：成功也好、断言炸了也好，都必须清掉 ——
    /// 原来好几个用例把 Directory.Delete 写成最后一句，断言一炸就漏一堆临时文件（OCR 复核点过名）。</summary>
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "ra3-settings-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void FailedSavePreservesPreviousSettingsAndLeavesNoTempFile()
    {
        var directory = NewDirectory();
        var path = Path.Combine(directory, "settings.json");
        var store = new TrainerAppSettingsStore(path);
        var replacement = new TrainerAppSettings("new.exe", "-win", 30, ResourceValueSettings.Default,
            Array.Empty<ReinforcementPreset>());
        try
        {
            // 首次 Save 也会 Directory.CreateDirectory + 落临时文件：放进 try 之内，
            // 否则它半途失败时 finally 里的 Cleanup 进不去，临时目录/文件就漏在盘上
            // （正是本用例自己的注释说要防的那类泄漏）。
            store.Save(new TrainerAppSettings("previous.exe", "-win", 30));
            var previous = File.ReadAllBytes(path);
            // 制造一次真会发生的写失败：**独占占用**目标文件（不是"只读属性" ——
            // 只读在 POSIX 上拦不住覆盖，那样这条用例会变成平台相关的，OCR 复核点过名）。
            // 契约：写盘先落临时文件，失败时旧设置一个字节都不许变、也不许留 .tmp。
            Exception? thrown;
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                thrown = Record.Exception(() => store.Save(replacement));
            }

            // 锁放开之后再读回来对：占用期间连"读"都会被拒（这正是"写失败"的来源）。
            // 独占占用在 Windows 上是共享冲突 → UnauthorizedAccessException。
            // 注意：这只是 Windows 的行为——POSIX 上 FileShare.None 是 advisory flock，File.Move(overwrite) 走 rename 会成功，
            // 所以本条只在 Windows runner 上真的制造写失败；要跨平台，得改成不可写的目标目录。
            // 只要求"必须是这两种之一"（不许静默成功），类型差是平台差、不是行为差。
            Assert.True(thrown is IOException or UnauthorizedAccessException,
                "占用中的目标文件必须让保存失败，实际: " + (thrown?.GetType().Name ?? "没抛异常"));
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Equal("previous.exe", store.Load().LauncherPath);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void LoadUsesDefaultsForNonObjectJson(string json)
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, json);
            Assert.Same(TrainerAppSettings.Default, new TrainerAppSettingsStore(path).Load());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// 坏值不许污染设置：超范围的数值、类型不对的布尔、负数超时都退回默认。
    /// （原先是按已删除的桌面浮窗 Form/Edge 枚举写的，那套枚举随桌面浮窗一起删了，
    ///   同样的契约改在仍然存在的字段上验证 —— 不是放宽，是换个还存在的对象。）
    /// </summary>
    [Fact]
    public void LoadFallsBackToDefaultsForOutOfRangeAndWrongTypedValues()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "LauncherPath": "D:\\Games\\RA3.exe",
                  "AttachTimeoutSeconds": 0,
                  "ResourceValues": { "MoneyAmount": 0, "PowerValue": 999999999, "ScPointValue": 99 },
                  "AutoFloatAfterLaunch": "yes",
                  "InspectHooksBeforeInstall": 1,
                  "AllowUnverifiedWorldPositionFeatures": null
                }
                """);

            var loaded = new TrainerAppSettingsStore(path).Load();

            Assert.Equal(30, loaded.AttachTimeoutSeconds);
            Assert.Equal(ResourceValueSettings.Default, loaded.ResourceValues);
            Assert.True(loaded.AutoFloatAfterLaunch);
            Assert.True(loaded.InspectHooksBeforeInstall);
            Assert.False(loaded.AllowUnverifiedWorldPositionFeatures);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripsPanelAndSafetyFlags()
    {
        var directory = NewDirectory();
        try
        {
            var store = new TrainerAppSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = new TrainerAppSettings(
                LauncherPath: @"D:\Games\RA3.exe",
                LauncherArguments: "-win",
                AttachTimeoutSeconds: 30,
                ResourceValues: ResourceValueSettings.Default,
                ReinforcementPresets: Array.Empty<ReinforcementPreset>(),
                AllowNonStandardVersion: true,
                InspectHooksBeforeInstall: false,
                AutoCollectEvidenceOnExit: false,
                AllowUnverifiedWorldPositionFeatures: true,
                AutoFloatAfterLaunch: false);

            store.Save(expected);
            var loaded = store.Load();

            Assert.Equal(expected, loaded);
            Assert.False(loaded.AutoFloatAfterLaunch);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>字段缺失时的默认值就是产品行为（面板要不要自动开、装前要不要校验）。</summary>
    [Fact]
    public void LoadDefaultsFlagSwitchesWhenTheyAreMissing()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{ "LauncherPath": "D:\\Games\\RA3.exe" }""");

            var loaded = new TrainerAppSettingsStore(path).Load();

            Assert.True(loaded.AutoFloatAfterLaunch);
            Assert.True(loaded.InspectHooksBeforeInstall);
            Assert.True(loaded.AutoCollectEvidenceOnExit);
            Assert.False(loaded.AllowNonStandardVersion);
            Assert.False(loaded.AllowUnverifiedWorldPositionFeatures);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void LoadCreatesDefaultSettingsFileWhenSettingsFileDoesNotExist()
    {
        var directory = NewDirectory();
        try
        {
            var path = Path.Combine(directory, "Ra3Trainer.settings.json");
            var store = new TrainerAppSettingsStore(path);

            var settings = store.Load();

            Assert.Equal(string.Empty, settings.LauncherPath);
            Assert.Equal("-win -xres 1920 -yres 1080", settings.LauncherArguments);
            Assert.Equal(30, settings.AttachTimeoutSeconds);
            Assert.Equal(ResourceValueSettings.Default, settings.ResourceValues);
            Assert.Empty(settings.ReinforcementPresets);
            Assert.True(File.Exists(path));

            var loadedAgain = new TrainerAppSettingsStore(path).Load();
            Assert.Equal(settings, loadedAgain);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void DefaultPathFallsBackToUserDataDirectoryWhenLegacyFileIsAbsent()
    {
        // 产品口径（2026-10-11 改）：**老位置没文件就写用户目录** —— 否则用户装到
        // C:\Program Files 下时设置存不住（Windows 不允许普通程序往那里写）。
        var baseDirectory = NewDirectory();

        var path = TrainerAppSettingsStore.DefaultPath(baseDirectory);

        Assert.Equal(Path.Combine(TrainerPaths.DataRoot, "Ra3Trainer.settings.json"), path);
    }

    [Fact]
    public void DefaultPathKeepsUsingLegacyFileNextToTheExeWhenPresent()
    {
        // 兼容口径：老用户 exe 旁边已经有设置文件 ⇒ 继续用那份，数据零迁移。
        var baseDirectory = NewDirectory();
        Directory.CreateDirectory(baseDirectory);
        var legacy = Path.Combine(baseDirectory, "Ra3Trainer.settings.json");
        File.WriteAllText(legacy, "{}");

        Assert.Equal(legacy, TrainerAppSettingsStore.DefaultPath(baseDirectory));
    }

    [Fact]
    public void SaveAndLoadRoundTripsLauncherSettings()
    {
        var directory = NewDirectory();
        try
        {
            var store = new TrainerAppSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = new TrainerAppSettings(
                LauncherPath: @"D:\Games\RA3.exe",
                LauncherArguments: "-win",
                AttachTimeoutSeconds: 45,
                ResourceValues: new ResourceValueSettings(123456, 234567, 9),
                ReinforcementPresets:
                [
                    new ReinforcementPreset("Yuriko", 0x6586A5A0, 8, 3),
                    new ReinforcementPreset("MCV", 0xAF4C0DA5, 2, 0)
                ]);

            store.Save(expected);
            var loaded = store.Load();

            Assert.Equal(expected.LauncherPath, loaded.LauncherPath);
            Assert.Equal(expected.LauncherArguments, loaded.LauncherArguments);
            Assert.Equal(expected.AttachTimeoutSeconds, loaded.AttachTimeoutSeconds);
            Assert.Equal(expected.ResourceValues, loaded.ResourceValues);
            Assert.Equal(expected.ReinforcementPresets, loaded.ReinforcementPresets);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void LoadMigratesLegacyGameExecutablePathAndDefaultsMissingArguments()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "GameExecutablePath": "D:\\Games\\RA3\\RA3.exe",
                  "WorkingDirectory": "D:\\Games\\RA3"
                }
                """);
            var store = new TrainerAppSettingsStore(path);

            var loaded = store.Load();

            Assert.Equal(@"D:\Games\RA3\RA3.exe", loaded.LauncherPath);
            Assert.Equal("-win -xres 1920 -yres 1080", loaded.LauncherArguments);
            Assert.Equal(30, loaded.AttachTimeoutSeconds);
            Assert.Equal(ResourceValueSettings.Default, loaded.ResourceValues);
            Assert.Empty(loaded.ReinforcementPresets);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    /// <summary>
    /// 老键和新键同时存在时的优先级（新键赢）。OCR 复核提醒：这条没钉住的话，
    /// 哪天有人调整 Normalize 的读取顺序，迁移行为会悄悄变、没人发现。
    /// </summary>
    [Fact]
    public void ModernLauncherPathWinsOverLegacyGameExecutablePath()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "LauncherPath": "D:\\Games\\RA3-新\\RA3.exe",
                  "GameExecutablePath": "D:\\Games\\RA3-旧\\RA3.exe"
                }
                """);

            var loaded = new TrainerAppSettingsStore(path).Load();

            Assert.Equal(@"D:\Games\RA3-新\RA3.exe", loaded.LauncherPath);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripsCompatibilityModeFlag()
    {
        var directory = NewDirectory();
        try
        {
            var store = new TrainerAppSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = new TrainerAppSettings(
                LauncherPath: @"D:\Games\RA3.exe",
                LauncherArguments: "-win",
                AttachTimeoutSeconds: 30,
                ResourceValues: ResourceValueSettings.Default,
                ReinforcementPresets: Array.Empty<ReinforcementPreset>(),
                AllowNonStandardVersion: true);

            store.Save(expected);
            var loaded = store.Load();

            Assert.True(loaded.AllowNonStandardVersion);
            Assert.Equal(expected, loaded);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void LoadDefaultsCompatibilityModeToDisabledWhenPropertyIsMissing()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "LauncherPath": "D:\\Games\\RA3.exe",
                  "AllowNonStandardVersion": "yes"
                }
                """);
            var store = new TrainerAppSettingsStore(path);

            var loaded = store.Load();

            Assert.False(loaded.AllowNonStandardVersion);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void LoadDefaultsFactionDisplayAndModAliasesWhenTheyAreMissing()
    {
        var directory = NewDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """{ "LauncherPath": "D:\\Games\\RA3.exe" }""");

            var loaded = new TrainerAppSettingsStore(path).Load();

            // 缺键 ⇒ 落回内置默认（四个阵营、显示名等于自身；日冕别名在）。
            Assert.Equal(UnitTaxonomy.DefaultFactionDisplays, loaded.FactionDisplay);
            Assert.Equal(new[] { "盟军", "苏联", "帝国", "神州" },
                loaded.FactionDisplay.Select(display => display.Faction).ToArray());
            Assert.Equal("日冕", loaded.ModSourceAliases["corona"]);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SaveAndLoadRoundTripsConfigurableFactionDisplayAndModAliases()
    {
        var directory = NewDirectory();
        try
        {
            var store = new TrainerAppSettingsStore(Path.Combine(directory, "settings.json"));
            var expected = new TrainerAppSettings(
                LauncherPath: @"D:\Games\RA3.exe",
                LauncherArguments: "-win",
                AttachTimeoutSeconds: 30,
                ResourceValues: ResourceValueSettings.Default,
                ReinforcementPresets: Array.Empty<ReinforcementPreset>(),
                FactionDisplay:
                [
                    new UnitTaxonomy.FactionDisplay("盟军", "盟军"),
                    new UnitTaxonomy.FactionDisplay("苏联", "苏联"),
                    new UnitTaxonomy.FactionDisplay("帝国", "帝国"),
                    new UnitTaxonomy.FactionDisplay("神州", "神州"),
                    new UnitTaxonomy.FactionDisplay("暗影", "暗影军团"),
                ],
                ModSourceAliases: new Dictionary<string, string> { ["corona"] = "日冕", ["armod"] = "ARKMOD" });

            store.Save(expected);
            var loaded = store.Load();

            // 显示名/顺序可配：暗影排最后、显示名"暗影军团"原样往返。
            Assert.Equal(new[] { "盟军", "苏联", "帝国", "神州", "暗影军团" },
                loaded.FactionDisplay.Select(display => display.Label).ToArray());
            Assert.Equal("暗影", loaded.FactionDisplay[^1].Faction);
            Assert.Equal("ARKMOD", loaded.ModSourceAliases["armod"]);
            Assert.Equal("日冕", loaded.ModSourceAliases["corona"]);

            // 数组简写也能读（手改友好）：["盟军","盟"] 等价于 {"Faction":"盟军","Label":"盟"}。
            File.WriteAllText(Path.Combine(directory, "settings.json"), """
                {
                  "FactionDisplay": [ ["盟军","联军"], ["苏联","苏联"] ]
                }
                """);
            var shorthand = store.Load();
            Assert.Equal(new[] { "联军", "苏联" }, shorthand.FactionDisplay.Select(d => d.Label).ToArray());
        }
        finally
        {
            Cleanup(directory);
        }
    }
}
