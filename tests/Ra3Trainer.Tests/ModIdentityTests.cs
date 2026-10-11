using System.IO;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 「当前玩的是哪个 mod」的**硬信号**识别（用户 2026-10-07：「可以搞」）。
///
/// <para>上一轮只能靠配置里选中的启动器 exe 名推（<c>ModKeyFromLauncherPath</c>）；本机实测（游戏在跑）：
/// 原版与所有 mod 的进程名都是 <c>ra3_1.12.game</c>，但**命令行**（<c>-modConfig &lt;skudef&gt;</c>）与
/// **已加载模块**（<c>...\CoronaLauncher\tools\EnhancerCorona.dll</c>）认得出是哪个 mod。</para>
///
/// <para>这里钉识别器的优先级（命令行 &gt; 已加载模块 &gt; 启动器配置 &gt; default 宽容）、
/// 两种 mod 指纹互相不串（复用 <see cref="UnitModScope"/> 路径）、以及三情形文案。
/// 每条都写清"旧口径会得到什么"——旧代码跑这些断言必红。</para>
/// </summary>
public sealed class ModIdentityTests
{
    /// <summary>本机 2026-10-07 实测抓到的**真**命令行（日冕，游戏在跑 PID 669056）。</summary>
    private const string RealCoronaCommandLine =
        "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -xres 1920 -yres 1080 " +
        "%Lanyi.Apt.LoadLuaBridgeDefine=hansummon " +
        "-modConfig D:\\Games\\CoronaLauncher\\CoronaData\\Corona\\corona_3.258.skudef " +
        "-config D:\\Games\\CoronaLauncher\\CoronaData\\RA3_english_1.12.Skudef -runver 1.12";

    /// <summary>本机 2026-10-07 实测抓到的**真**已加载模块路径（日冕的注入链）。</summary>
    private static readonly string[] RealCoronaModules =
    [
        @"D:\Games\CoronaLauncher\bin\Lyi.dll",
        @"D:\Games\CoronaLauncher\tools\RA3LuaBridge.dll",
        @"D:\Games\CoronaLauncher\tools\EnhancerCorona.dll",
    ];

    // ARMod（本机装成 RA3_WIC_3.01A）：启动器 ARModLauncher.exe / 数据 Armor Rush\WIC_3.01A。
    // 关键坑：它**也带一个同名 DLL** EnhancerCorona.dll —— 只有按路径段认才分得开。
    private const string ArmodLauncherPath = @"D:\Games\RA3_WIC_3.01A\ARModLauncher.exe";
    private const string ArmodModConfig = @"D:\Games\RA3_WIC_3.01A\Data\Armor Rush\WIC_3.01A.skudef";
    private const string ArmodModule = @"D:\Games\RA3_WIC_3.01A\tools\EnhancerCorona.dll";
    private const string CoronaLauncherPath = @"D:\Games\CoronaLauncher\CoronaLauncher.exe";

    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["corona"] = "日冕", ["armod"] = "ARKMOD" };

    // ---- 假数据：同一个单位库，三种来源（原版通用 / 日冕 / ARMod）----
    private static ReinforcementUnitEntry BaseUnit(uint code, string name) => new(code, name, "盟军 · 单位", "通用");
    private static ReinforcementUnitEntry CoronaUnit(uint code, string name) => new(code, name, "盟军 · 单位", "日冕");
    private static ReinforcementUnitEntry ArmodUnit(uint code, string name) => new(code, name, "暗影 · 单位", "ARKMOD");

    private static readonly ReinforcementUnitEntry[] Catalogue =
    [
        BaseUnit(0xA0000001, "原版警犬"),
        CoronaUnit(0xB0000001, "日冕机甲"),
        ArmodUnit(0xC0000001, "装甲突击兵"),
    ];

    // ===== 优先级：命令行 > 已加载模块 > 启动器配置 > default =====

    [Fact]
    public void TheRunningGameCommandLineBeatsTheConfiguredLauncher()
    {
        // 配置里选的启动器是日冕，但这一局游戏进程的命令行点名了 ARMod 的 -modConfig。
        var snapshot = new ModSignalSnapshot(
            LauncherPath: CoronaLauncherPath,
            TargetCommandLine: "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -xres 1920 -yres 1080 " +
                               $"-modConfig {ArmodModConfig} -runver 1.12");

        var identity = ModIdentifier.Identify(snapshot, Aliases);

        Assert.Equal("armod", identity.ModKey);
        Assert.Equal(ModSignalKind.ProcessCommandLine, identity.Signal);
        Assert.False(identity.IsVanilla);

        // 旧口径（只看配置里的启动器 exe 名）会认成 corona —— 与硬信号相反。这就是新旧分界。
        Assert.Equal("corona", ReinforcementUnitCatalog.ModKeyFromLauncherPath(snapshot.LauncherPath));
    }

    [Fact]
    public void TheRealCoronaCommandLineIdentifiesCorona()
    {
        // 用本机实测抓到的那条真命令行：不出 -modConfig 之外的信息也必须认出日冕。
        var identity = ModIdentifier.Identify(new ModSignalSnapshot(TargetCommandLine: RealCoronaCommandLine), Aliases);

        Assert.Equal("corona", identity.ModKey);
        Assert.Equal("日冕", identity.Label);
        Assert.Equal(ModSignalKind.ProcessCommandLine, identity.Signal);
    }

    [Fact]
    public void TheGamesLoadedModulesBeatTheConfiguredLauncher()
    {
        // 命令行拿不到（比如没权限），但游戏进程里挂着 ARMod 的注入 DLL；配置里选的却是日冕。
        var snapshot = new ModSignalSnapshot(
            LauncherPath: CoronaLauncherPath,
            TargetModulePaths: [ArmodModule, @"C:\WINDOWS\SYSTEM32\ntdll.dll"]);

        var identity = ModIdentifier.Identify(snapshot, Aliases);

        Assert.Equal("armod", identity.ModKey);
        Assert.Equal(ModSignalKind.ProcessModules, identity.Signal);

        // 真日冕的模块路径认日冕。
        var corona = ModIdentifier.Identify(
            new ModSignalSnapshot(LauncherPath: ArmodLauncherPath, TargetModulePaths: RealCoronaModules), Aliases);
        Assert.Equal("corona", corona.ModKey);
        Assert.Equal(ModSignalKind.ProcessModules, corona.Signal);
    }

    [Fact]
    public void TheGameRunningWithoutAModConfigIsVanillaEvenWhenAModLauncherIsConfigured()
    {
        // 游戏进程就是游戏本体、命令行里没有 -modConfig ⇒ 原版（硬信号压过"配置里写着日冕启动器"）。
        var snapshot = new ModSignalSnapshot(
            LauncherPath: CoronaLauncherPath,
            TargetCommandLine: "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -xres 1920 -yres 1080 " +
                               "-config \"D:\\Games\\Red Alert 3\\RA3_chinese_t_1.12.SkuDef\" -runver 1.12",
            TargetModulePaths: [@"D:\\Games\\Red Alert 3\\Data\\Lyi.dll"]);

        var identity = ModIdentifier.Identify(snapshot, Aliases);

        Assert.True(identity.IsVanilla);
        Assert.Equal(ModSignalKind.ProcessCommandLine, identity.Signal);
        Assert.Equal("当前 mod：原版", identity.DisplayText);
    }

    [Fact]
    public void WithNoHardSignalItFallsBackToTheConfiguredLauncher()
    {
        // 没有命令行、没有模块表 ⇒ 回退到旧口径（启动器路径），行为不变。
        var identity = ModIdentifier.Identify(new ModSignalSnapshot(LauncherPath: CoronaLauncherPath), Aliases);
        Assert.Equal("corona", identity.ModKey);
        Assert.Equal(ModSignalKind.LauncherPath, identity.Signal);
        Assert.Equal("日冕", identity.Label);

        // 启动器是游戏本体 ⇒ 原版（同样落 LauncherPath 这一路）。
        var vanilla = ModIdentifier.Identify(new ModSignalSnapshot(LauncherPath: @"D:\Games\Red Alert 3\RA3.exe"), Aliases);
        Assert.True(vanilla.IsVanilla);
        Assert.Equal(ModSignalKind.LauncherPath, vanilla.Signal);
    }

    [Fact]
    public void WithNoSignalAtAllEverythingIsPermissiveSoNoUnitIsEverLost()
    {
        // 全缺 ⇒ default（宽容）：不过滤，保持旧行为、绝不因为"当前 mod 未知"就丢单位。
        var identity = ModIdentifier.Identify(ModSignalSnapshot.Empty, Aliases);

        Assert.False(identity.IsKnown);
        Assert.False(identity.IsVanilla);
        Assert.Equal(UnitModScope.UnknownModKey, identity.ModKey);
        Assert.Equal(ModSignalKind.None, identity.Signal);

        var scope = UnitModScope.ForModKey(identity.ModKey, Aliases);
        Assert.False(scope.Filter);
        Assert.All(Catalogue, unit => Assert.True(scope.Allows(unit)));
    }

    // ===== 假数据造两种 mod 指纹：识别为 A 时不显示 B 的单位（复用 UnitModScope 路径）=====

    [Fact]
    public void IdentifiedModAHidesModBsUnitsAndTheOtherWayRound()
    {
        // 启动器配置写着 ARMod，但游戏进程里挂的是**日冕**的模块 ⇒ 硬信号说了算：认出日冕。
        var coronaSnapshot = new ModSignalSnapshot(
            LauncherPath: ArmodLauncherPath,
            TargetModulePaths: RealCoronaModules);
        var corona = ModIdentifier.Identify(coronaSnapshot, Aliases);
        Assert.Equal("corona", corona.ModKey);

        var underCorona = Catalogue.Where(unit => UnitModScope.ForModKey(corona.ModKey, Aliases).Allows(unit)).ToArray();
        Assert.Contains(underCorona, unit => unit.Code == 0xB0000001);          // 日冕的兵在
        Assert.DoesNotContain(underCorona, unit => unit.Code == 0xC0000001);    // ARMod 的兵不出现
        Assert.Contains(underCorona, unit => unit.Code == 0xA0000001);          // 原版通用仍在

        // 反向：模块来自 ARMod 目录（启动器配置写着日冕）⇒ 认出 ARMod，日冕的单位消失。
        var armodSnapshot = new ModSignalSnapshot(
            LauncherPath: CoronaLauncherPath,
            TargetModulePaths: [ArmodModule, @"C:\WINDOWS\SYSTEM32\ntdll.dll"]);
        var armod = ModIdentifier.Identify(armodSnapshot, Aliases);
        Assert.Equal("armod", armod.ModKey);

        var underArmod = Catalogue.Where(unit => UnitModScope.ForModKey(armod.ModKey, Aliases).Allows(unit)).ToArray();
        Assert.Contains(underArmod, unit => unit.Code == 0xC0000001);
        Assert.DoesNotContain(underArmod, unit => unit.Code == 0xB0000001);
        Assert.Contains(underArmod, unit => unit.Code == 0xA0000001);

        // 旧口径（只看启动器路径）在"日冕模块在跑"时会放 ARMod 的单位、藏日冕的单位 —— 认反。
        // 这就是"识别为 A 时不显示 B"这条钉子能区分新旧的地方。
        var legacy = UnitModScope.ForLauncher(ArmodLauncherPath, Aliases);
        Assert.False(legacy.Allows("日冕"));
        Assert.True(legacy.Allows("ARKMOD"));
    }

    // ===== 文案：三情形各一条 =====

    [Fact]
    public void DisplayTextNamesCoronaTheVanillaGameAndTheUnknownCase()
    {
        var corona = ModIdentifier.Identify(new ModSignalSnapshot(TargetCommandLine: RealCoronaCommandLine), Aliases);
        Assert.Equal("当前 mod：日冕", corona.DisplayText);

        var vanilla = ModIdentifier.Identify(new ModSignalSnapshot(LauncherPath: @"D:\Games\Red Alert 3\RA3.exe"), Aliases);
        Assert.Equal("当前 mod：原版", vanilla.DisplayText);

        var unknown = ModIdentity.Unknown.DisplayText;
        Assert.Equal("当前 mod：未知（未过滤，避免藏掉你的单位）", unknown);
        // 文案必须**明确**写出未过滤，别让人以为单位凭空少了。
        Assert.Contains("未过滤", unknown);
        Assert.Contains("避免藏掉你的单位", unknown);
    }

    // ===== 解析细节 =====

    [Fact]
    public void ExtractModConfigPathUnderstandsBothSpellingsAndQuotes()
    {
        Assert.Equal(@"E:\a\corona_3.258.skudef",
            ModIdentifier.ExtractModConfigPath("\"x.exe\" -modConfig E:\\a\\corona_3.258.skudef -runver 1.12"));
        Assert.Equal(@"E:\b\mod.skudef",
            ModIdentifier.ExtractModConfigPath("x.exe -modConfig=\"E:\\b\\mod.skudef\""));
        // 没有 -modConfig 时退而看 -config（原版靠它指游戏 SkuDef）。
        Assert.Equal(@"E:\c\RA3_english_1.12.SkuDef",
            ModIdentifier.ExtractModConfigPath("x.exe -config E:\\c\\RA3_english_1.12.SkuDef"));
        // 路径带空格又没加引号时会被切成两个参数：要拼回一个路径（ARMod 的 skudef 目录就带空格）。
        Assert.Equal(@"E:\d\Armor Rush\WIC_3.01A.skudef",
            ModIdentifier.ExtractModConfigPath("ra3_1.12.game -modConfig E:\\d\\Armor Rush\\WIC_3.01A.skudef -runver 1.12"));
        Assert.Null(ModIdentifier.ExtractModConfigPath("-xres 1920 -yres 1080"));
        Assert.Null(ModIdentifier.ExtractModConfigPath(null));
    }

    [Fact]
    public void SameNamedDllInAnotherModFolderIsNotMistakenForCorona()
    {
        // ARMod 也带 EnhancerCorona.dll：按**文件名的子串**认会把 ARMod 认成日冕（本机实测踩过）。
        Assert.Equal("armod", ModIdentifier.MatchPath(ArmodModule)?.Key);
        Assert.Equal("corona", ModIdentifier.MatchPath(@"D:\Games\CoronaLauncher\tools\EnhancerCorona.dll")?.Key);
        Assert.Null(ModIdentifier.MatchPath(@"D:\Games\Red Alert 3\Data\RA3_1.12.game"));
    }

    // ===== 配置：新增键带默认值、缺键不报错、可扩展 =====

    [Fact]
    public void ModFingerprintsDefaultWhenTheKeyIsMissingAndCanBeExtendedInSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ra3-modfingerprint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "settings.json");

            // 老 settings.json（没有 ModFingerprints 键）⇒ 落回内置默认，不报错。
            File.WriteAllText(path, """{ "LauncherPath": "D:\\Games\\RA3.exe" }""");
            var loaded = new TrainerAppSettingsStore(path).Load();
            Assert.Equal(ModIdentifier.DefaultFingerprints, loaded.ModFingerprints);
            Assert.Contains(loaded.ModFingerprints, fingerprint => fingerprint.Key == "corona");
            Assert.Contains(loaded.ModFingerprints, fingerprint => fingerprint.Key == "armod");

            // 装新 mod 不改代码：加一条指纹（数组简写）就能认出来。
            File.WriteAllText(path,
                """{ "LauncherPath": "D:\\Games\\RA3.exe", "ModFingerprints": [["mymod", ["mymodlauncher", "MyMod Data"]]] }""");
            var custom = new TrainerAppSettingsStore(path).Load();
            var fingerprint = Assert.Single(custom.ModFingerprints);
            Assert.Equal("mymod", fingerprint.Key);
            Assert.Contains("mymodlauncher", fingerprint.PathTokens);
            Assert.Contains("mymoddata", fingerprint.PathTokens);   // "MyMod Data" 归一化

            var identity = ModIdentifier.Identify(
                new ModSignalSnapshot(TargetCommandLine: "ra3_1.12.game -modConfig E:\\x\\MyMod Data\\mymod.skudef"),
                fingerprints: custom.ModFingerprints);
            Assert.Equal("mymod", identity.ModKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // ===== REMIX（★ 2026-10-11 ★ 用户实玩的 mod；在此之前只能落到"启动器路径"那一路，被认成 armod）=====

    /// <summary>本机 2026-10-11 实测：整合包里的 REMIX 舞台路径 + 真正的数据根目录。</summary>
    private const string RemixModConfig =
        @"D:\Users\user\Documents\Red Alert 3\Mods\Remix037\Remix_0.3.7.skudef";

    [Fact]
    public void RemixIsRecognizedFromTheGameCommandLine()
    {
        // 配置里选的启动器仍是 ARMod（两者共用同一个整合包目录 RA3_WIC_3.01A），
        // 但这一局命令行的 -modConfig 点名了 REMIX ⇒ 必须认 remix。
        var snapshot = new ModSignalSnapshot(
            LauncherPath: ArmodLauncherPath,
            TargetCommandLine: "\"D:\\Games\\Red Alert 3\\Data\\RA3_1.12.game\" -xres 1920 -yres 1080 " +
                               $"-modConfig {RemixModConfig} -runver 1.12");

        var identity = ModIdentifier.Identify(snapshot, Aliases);

        Assert.Equal("remix", identity.ModKey);
        Assert.Equal(ModSignalKind.ProcessCommandLine, identity.Signal);
    }

    [Fact]
    public void RemixStagedModFolderBeatsTheSharedLauncherFolder()
    {
        // 命令行没点名时，整合包摊在 <启动器目录>\Data\Temp\Mod\<mod>\ 里的当前 mod 才是真信号；
        // 它必须压过"启动器目录带 wic/armod 字样"的旧口径。
        var root = Path.Combine(Path.GetTempPath(), "ra3-modid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stage = Path.Combine(root, "Data", "Temp", "Mod", "Remix_0.3.7");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "Remix_0.3.7.skudef"), "mod-game 1.12");
            File.WriteAllBytes(Path.Combine(root, "ARModLauncher.exe"), [0x4D, 0x5A]);

            var identity = ModIdentifier.Identify(
                new ModSignalSnapshot(LauncherPath: Path.Combine(root, "ARModLauncher.exe")), Aliases);

            Assert.Equal("remix", identity.ModKey);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [Fact]
    public void StagedModFolderWithSeveralModsIsNeverGuessed()
    {
        // 同一个舞台目录摊了多个 mod ⇒ 说不准就不认 remix（宁可回落到旧口径，也不猜）。
        var root = Path.Combine(Path.GetTempPath(), "ra3-modid-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var name in new[] { "Remix_0.3.7", "SomeOtherMod_1.0" })
            {
                var stage = Path.Combine(root, "Data", "Temp", "Mod", name);
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, name + ".skudef"), "mod-game 1.12");
            }
            File.WriteAllBytes(Path.Combine(root, "ARModLauncher.exe"), [0x4D, 0x5A]);

            var identity = ModIdentifier.Identify(
                new ModSignalSnapshot(LauncherPath: Path.Combine(root, "ARModLauncher.exe")), Aliases);

            Assert.NotEqual("remix", identity.ModKey);
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }
}
