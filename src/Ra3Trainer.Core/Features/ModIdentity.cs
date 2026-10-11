using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 「当前玩的是哪个 mod」的**硬信号**识别（用户 2026-10-07：「可以搞」）。
///
/// <para><b>为什么不能只靠启动器路径</b>：<see cref="ReinforcementUnitCatalog.ModKeyFromLauncherPath"/>
/// 推的是「用户上次在修改器里选的那个启动器 exe 名」。那是**配置**，不是**当前这一局**：
/// 换 mod 忘了改配置、用平台启动器起 mod、直接开 RA3.exe 又拿老配置 —— 都会错判。</para>
///
/// <para><b>本机实测（2026-10-07，游戏正在跑 PID 669056）</b>：原版与所有 mod 的游戏进程名都是
/// <c>ra3_1.12.game</c>（进程名认不出 mod），但**已加载模块**与**命令行**认得出：</para>
/// <list type="bullet">
///   <item>命令行：<c>... -modConfig D:\Games\CoronaLauncher\CoronaData\Corona\corona_3.258.skudef ...</c>
///     —— 直接点名当前 mod，最硬；</item>
///   <item>已加载模块：<c>D:\Games\CoronaLauncher\tools\EnhancerCorona.dll</c>、
///     <c>...\CoronaLauncher\bin\Lyi.dll</c> —— mod 的 dx9 注入链，路径落在 mod 自己的目录里。</item>
/// </list>
///
/// <para><b>优先级</b>（强 ⇒ 弱）：进程命令行 <c>-modConfig</c> → 进程已加载模块 → 启动器配置 → default（宽容）。
/// 认不出就落回 <see cref="UnitModScope.UnknownModKey"/>（**不过滤，绝不因认不出就藏掉单位**）。</para>
///
/// <para><b>为什么按「路径段」而不是「DLL 名」认</b>：日冕与 ARMod（本机 <c>RA3_WIC_3.01A</c>）
/// **都带一个叫 <c>EnhancerCorona.dll</c> 的注入 DLL**。按文件名认会把 ARMod 认成日冕 —— 实测踩过，
/// 所以只在**完整路径的目录段**里匹配 mod 指纹。</para>
/// </summary>
public static class ModIdentifier
{
    /// <summary>已知 mod 的盘上指纹：归一化后的**路径段** → mod 键。</summary>
    public static readonly IReadOnlyList<ModFingerprint> DefaultFingerprints =
    [
        // 日冕：D:\Games\CoronaLauncher\...（CoronaData\Corona\corona_3.258.lyi / corona_3.258.skudef）
        new ModFingerprint("corona", ["corona", "coronalauncher", "coronadata", "coronamod"]),
        // REMIX（★ 2026-10-11 补 ★ 用户实玩的就是它，但表里原本没有 ⇒ 只能落到最后一路"启动器路径"，
        // 被认成 armod，单位清单也写进了 units-armod.txt）。本机实测路径：
        //   D:\Games\RA3_WIC_3.01A\Data\Temp\Mod\Remix_0.3.7\Remix_0.3.7.skudef
        //   → D:\Users\user\Documents\Red Alert 3\Mods\Remix037\Remix_0.3.7.skudef
        // REMIX 与 ARMod **共用同一个整合包目录**（RA3_WIC_3.01A），所以这条必须排在 armod 之前。
        new ModFingerprint("remix", ["remix", "remix037", "remixmod"]),
        // ARMod（本机装成 RA3_WIC_3.01A，启动器 ARModLauncher.exe，启动器内叫 Armor Rush / WIC_3.01A）。
        // 键用 "armod" —— 与 exe 名推出来的键、以及 exe 旁的 units-armod.txt 一致。
        new ModFingerprint("armod", ["armod", "armorrush", "ra3wic301a", "wic301a", "wic"]),
    ];

    /// <summary>
    /// 三路信号合成一次识别。任一信号缺失（null/空）就跳过它，交给下一路。
    /// </summary>
    public static ModIdentity Identify(
        ModSignalSnapshot snapshot,
        IReadOnlyDictionary<string, string>? aliases = null,
        IReadOnlyList<ModFingerprint>? fingerprints = null)
    {
        var map = aliases is { Count: > 0 } ? aliases : UnitModScope.DefaultModSourceAliases;
        var registry = fingerprints is { Count: > 0 } ? fingerprints : DefaultFingerprints;

        // 1) 最强：游戏进程**自己**的命令行点名了哪个 mod（-modConfig <skudef>）。
        var modConfigPath = ExtractModConfigPath(snapshot.TargetCommandLine);
        if (modConfigPath is not null)
        {
            var byCommandLine = MatchPath(modConfigPath, registry);
            if (byCommandLine is not null)
            {
                return Known(byCommandLine.Key, ModSignalKind.ProcessCommandLine, map);
            }
            // 认不出的 modConfig：不从文件名瞎猜（可能把别的 mod 认成这个），落到下一路，最后仍走宽容。
        }

        // 1.5) 整合包的「mod 舞台」：<启动器目录>\Data\Temp\Mod\<mod>\<mod>.skudef。
        //      这台机器的 ARMod/REMIX 整合包就是靠它切换 mod 的（实测该目录下只有一个 mod 子目录）。
        //      比"进程模块"更贴当前这一局，但弱于命令行点名，故放这里；归类沿用 LauncherPath。
        var staged = MatchStagedMod(snapshot.LauncherPath, registry);
        if (staged is not null)
        {
            return Known(staged.Key, ModSignalKind.LauncherPath, map);
        }

        // 2) 次强：mod 的注入 DLL 已经进了游戏进程（路径落在 mod 自己的目录里）。
        var byModules = MatchAnyPath(snapshot.TargetModulePaths, registry);
        if (byModules is not null)
        {
            return Known(byModules.Key, ModSignalKind.ProcessModules, map);
        }

        // 3) 游戏进程在跑、命令行里**没有** -modConfig 且就是游戏本体 ⇒ 原版（只对真游戏进程下这个结论）。
        if (IsGameCommandLine(snapshot.TargetCommandLine))
        {
            return Vanilla(ModSignalKind.ProcessCommandLine);
        }

        // 4) 兜底：用户配置的启动器（旧口径，保持兼容）。
        var launcherKey = UnitModScope.NormalizeToken(ReinforcementUnitCatalog.ModKeyFromLauncherPath(snapshot.LauncherPath));
        if (launcherKey.Length > 0 && !string.Equals(launcherKey, UnitModScope.UnknownModKey, StringComparison.Ordinal))
        {
            return UnitModScope.IsBaseModKey(launcherKey)
                ? Vanilla(ModSignalKind.LauncherPath)
                : Known(launcherKey, ModSignalKind.LauncherPath, map);
        }

        // 5) 什么都没认出来：default，宽容不过滤。
        return ModIdentity.Unknown;
    }

    private static ModIdentity Known(string key, ModSignalKind signal, IReadOnlyDictionary<string, string> aliases)
    {
        // 键里带版本号会让"同一个 mod 升级一次 = 换一个键"（玩家的单位清单白学）⇒ 统一去掉版本尾巴。
        var normalized = UnitModScope.StripVersionSuffix(UnitModScope.NormalizeToken(key));
        if (normalized.Length == 0 || string.Equals(normalized, UnitModScope.UnknownModKey, StringComparison.Ordinal))
        {
            return ModIdentity.Unknown;
        }

        if (UnitModScope.IsBaseModKey(normalized))
        {
            return Vanilla(signal);
        }

        var label = UnitModScope.LabelFor(normalized, aliases);
        return new ModIdentity(normalized, label, signal, IsKnown: true, IsVanilla: false);
    }

    private static ModIdentity Vanilla(ModSignalKind signal) =>
        new("ra3", "原版", signal, IsKnown: true, IsVanilla: true);

    /// <summary>
    /// 取命令行里的 mod 配置路径：先 <c>-modConfig &lt;path&gt;</c>（日冕/ARMod 都这么传），
    /// 没有再看 <c>-config &lt;path&gt;</c>（原版靠它指游戏 SkuDef；mod 会把 config 指向自己目录）。
    /// </summary>
    public static string? ExtractModConfigPath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var args = SplitArguments(commandLine);
        foreach (var option in new[] { "-modConfig", "-config" })
        {
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg.Equals(option, StringComparison.OrdinalIgnoreCase))
                {
                    var value = JoinPathArguments(args, i + 1);
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
                else if (arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg[(option.Length + 1)..].Trim();
                    if (value.Length > 0)
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 拼回被空白切开的路径。命令行里没加引号的 <c>…\Armor Rush\WIC.skudef</c> 会被切成两个参数
    /// （本机 ARMod 的 skudef 目录就带空格），一直吃到下一个 "-开关" 为止复原它 ——
    /// 调用方要的是**一个路径**，不是一个词。
    /// </summary>
    private static string JoinPathArguments(IReadOnlyList<string> args, int start)
    {
        var parts = new List<string>();
        for (var i = start; i < args.Count; i++)
        {
            if (args[i].Length > 1 && args[i][0] == '-')
            {
                break;
            }

            parts.Add(args[i]);
        }

        return string.Join(' ', parts).Trim();
    }

    /// <summary>
    /// 命令行是不是「游戏本体在跑」。只有认定是真游戏进程，才敢在没有 -modConfig 时下「原版」结论，
    /// 否则会把别的程序误判成原版、错手过滤。
    /// </summary>
    public static bool IsGameCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var args = SplitArguments(commandLine);
        if (args.Count == 0)
        {
            return false;
        }

        var exe = System.IO.Path.GetFileName(args[0].Trim().Trim('"')).ToLowerInvariant();
        return exe == "ra3.exe"
            || exe == "ra3_1.12.game"
            || (exe.StartsWith("ra3_", StringComparison.Ordinal) && exe.EndsWith(".game", StringComparison.Ordinal));
    }

    /// <summary>给定一条路径，返回命中的第一条 mod 指纹；没有就是 null。</summary>
    public static ModFingerprint? MatchPath(string? path, IReadOnlyList<ModFingerprint>? fingerprints = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var registry = fingerprints is { Count: > 0 } ? fingerprints : DefaultFingerprints;
        var tokens = PathTokens(path);
        if (tokens.Count == 0)
        {
            return null;
        }

        foreach (var fingerprint in registry)
        {
            if (fingerprint.PathTokens.Any(token => tokens.Contains(token)))
            {
                return fingerprint;
            }
        }

        return null;
    }

    /// <summary>给一组路径（如进程已加载模块），返回命中的第一条 mod 指纹；没有就是 null。</summary>
    /// <summary>
    /// 整合包的 mod 舞台目录：<c>&lt;启动器目录&gt;\Data\Temp\Mod\&lt;mod&gt;\&lt;mod&gt;.skudef</c>。
    /// 只有**恰好一个** mod 子目录时才作数（多个说不准就不猜）；目录读不到返回 null，交给下一路。
    /// </summary>
    public static ModFingerprint? MatchStagedMod(string? launcherPath, IReadOnlyList<ModFingerprint>? fingerprints = null)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            return null;
        }

        try
        {
            var launcherDir = Path.GetDirectoryName(Path.GetFullPath(launcherPath));
            if (string.IsNullOrEmpty(launcherDir))
            {
                return null;
            }

            var root = Path.Combine(launcherDir, "Data", "Temp", "Mod");
            if (!Directory.Exists(root))
            {
                return null;
            }

            var skudefs = Directory.EnumerateDirectories(root)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.skudef"))
                .ToList();
            return skudefs.Count == 1 ? MatchPath(skudefs[0], fingerprints) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ModFingerprint? MatchAnyPath(IReadOnlyList<string>? paths, IReadOnlyList<ModFingerprint>? fingerprints = null)
    {
        if (paths is null || paths.Count == 0)
        {
            return null;
        }

        var registry = fingerprints is { Count: > 0 } ? fingerprints : DefaultFingerprints;
        // 指纹在外层：注册表顺序 = 优先级（先匹配到的那个 mod 说了算）。
        foreach (var fingerprint in registry)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var tokens = PathTokens(path);
                if (tokens.Count > 0 && fingerprint.PathTokens.Any(tokens.Contains))
                {
                    return fingerprint;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 把一条路径拆成归一化 token 集：按目录分隔符切开，每段整体归一化（"Armor Rush" → "armorrush"），
    /// 再按空白拆一次（"Armor" / "Rush"）。整体 + 分词都放进去，指纹怎么写都能对上。
    /// </summary>
    public static HashSet<string> PathTokens(string path)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in path.Split(['\\', '/', ':', '\t']))
        {
            var whole = UnitModScope.NormalizeToken(segment);
            if (whole.Length > 0)
            {
                tokens.Add(whole);
            }

            foreach (var word in segment.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = UnitModScope.NormalizeToken(word);
                if (normalized.Length > 0)
                {
                    tokens.Add(normalized);
                }
            }
        }

        return tokens;
    }

    /// <summary>拆命令行（尊重引号）；引号去掉，所以拿到的就是裸路径。</summary>
    public static IReadOnlyList<string> SplitArguments(string? commandLine)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return args;
        }

        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var ch in commandLine)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }
}

/// <summary>一条 mod 指纹：归一化后的路径段（见 <see cref="ModIdentifier.PathTokens"/>）。</summary>
public sealed record ModFingerprint(string Key, IReadOnlyList<string> PathTokens);

/// <summary>识别时能拿到的三路信号快照；缺哪路给 null/空即可。</summary>
public sealed record ModSignalSnapshot(
    string? LauncherPath = null,
    string? TargetCommandLine = null,
    IReadOnlyList<string>? TargetModulePaths = null)
{
    /// <summary>三路信号都没有 —— default（宽容）那一路的输入。</summary>
    public static ModSignalSnapshot Empty { get; } = new(null, null, Array.Empty<string>());
}

/// <summary>识别结果来自哪一路信号（也说清「可信到什么程度」）。</summary>
public enum ModSignalKind
{
    /// <summary>什么都没拿到 —— default（宽容不过滤）。</summary>
    None,

    /// <summary>用户配置的启动器 exe 名（旧口径，最弱）。</summary>
    LauncherPath,

    /// <summary>游戏进程已加载模块里找到了 mod 的注入 DLL（强）。</summary>
    ProcessModules,

    /// <summary>游戏进程命令行 <c>-modConfig</c> 点名了 mod（最强）。</summary>
    ProcessCommandLine,
}

/// <summary>「当前玩的是哪个 mod」的识别结果。永远安全可用：认不出就是 <see cref="Unknown"/>（宽容）。</summary>
public sealed record ModIdentity(
    string ModKey,
    string Label,
    ModSignalKind Signal,
    bool IsKnown,
    bool IsVanilla)
{
    /// <summary>没配启动器、也拿不到任何硬信号 ⇒ default，不过滤。</summary>
    public static ModIdentity Unknown { get; } =
        new(UnitModScope.UnknownModKey, string.Empty, ModSignalKind.None, IsKnown: false, IsVanilla: false);

    /// <summary>给人看的一句：当前 mod：日冕 / 原版 / 未知（未过滤…）。</summary>
    public string DisplayText => IsVanilla
        ? "当前 mod：原版"
        : IsKnown
            ? $"当前 mod：{Label}"
            : "当前 mod：未知（未过滤，避免藏掉你的单位）";

    /// <summary>这句话是凭什么认出来的（放 tooltip / 面板右侧，别挤主文案）。</summary>
    public string EvidenceText => Signal switch
    {
        ModSignalKind.ProcessCommandLine => "识别依据：游戏进程命令行",
        ModSignalKind.ProcessModules => "识别依据：游戏进程已加载模块",
        ModSignalKind.LauncherPath => "识别依据：启动器配置",
        _ => "识别依据：无（未过滤）",
    };

    /// <summary>并入面板/摘要的一句话（主文案 + 依据）。</summary>
    public string ShortText => IsKnown || IsVanilla ? $"{DisplayText}（{EvidenceText["识别依据：".Length..]}）" : DisplayText;
}
