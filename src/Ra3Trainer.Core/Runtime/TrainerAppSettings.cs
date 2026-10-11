using System.Text.Json;
using Ra3Trainer.Core.Features;

namespace Ra3Trainer.Core.Runtime;



/// <summary>
/// Panel-facing pair for the float-window form picker: the display name is what the owner reads, the
/// value is what the window switches on. Kept separate so a renamed label can never change behaviour.
/// </summary>


public sealed record TrainerAppSettings
{
    public TrainerAppSettings(
        string LauncherPath,
        string LauncherArguments,
        int AttachTimeoutSeconds)
        : this(
            LauncherPath,
            LauncherArguments,
            AttachTimeoutSeconds,
            ResourceValueSettings.Default,
            Array.Empty<ReinforcementPreset>(),
            false,
            true,
            true)
    {
    }

    public TrainerAppSettings(
        string LauncherPath,
        string LauncherArguments,
        int AttachTimeoutSeconds,
        ResourceValueSettings ResourceValues,
        IReadOnlyList<ReinforcementPreset> ReinforcementPresets,
        bool AllowNonStandardVersion = false,
        bool InspectHooksBeforeInstall = true,
        bool AutoCollectEvidenceOnExit = true,
        bool AllowUnverifiedWorldPositionFeatures = false, bool AutoFloatAfterLaunch = true,
        IReadOnlyList<UnitTaxonomy.FactionDisplay>? FactionDisplay = null,
        IReadOnlyDictionary<string, string>? ModSourceAliases = null,
        IReadOnlyList<ModFingerprint>? ModFingerprints = null)
    {
        this.LauncherPath = LauncherPath;
        this.LauncherArguments = LauncherArguments;
        this.AttachTimeoutSeconds = AttachTimeoutSeconds;
        this.ResourceValues = ResourceValues;
        this.ReinforcementPresets = ReinforcementPresets.ToArray();
        this.AllowNonStandardVersion = AllowNonStandardVersion;
        this.InspectHooksBeforeInstall = InspectHooksBeforeInstall;
        this.AutoCollectEvidenceOnExit = AutoCollectEvidenceOnExit;
        this.AllowUnverifiedWorldPositionFeatures = AllowUnverifiedWorldPositionFeatures;
        this.AutoFloatAfterLaunch = AutoFloatAfterLaunch;
        // 缺省／空 → 落回静态默认实例。为什么必须落回**同一个实例**：record 的自动相等对引用类型列表
        // 走引用比较，测试里 Assert.Equal(settings, loadedAgain) 才成立（Normalize 也会在对上默认时落回静态）。
        this.FactionDisplay = FactionDisplay is { Count: > 0 } ? FactionDisplay : UnitTaxonomy.DefaultFactionDisplays;
        this.ModSourceAliases = ModSourceAliases is { Count: > 0 } ? ModSourceAliases : UnitModScope.DefaultModSourceAliases;
        this.ModFingerprints = ModFingerprints is { Count: > 0 } ? ModFingerprints : ModIdentifier.DefaultFingerprints;
    }

    public static TrainerAppSettings Default { get; } =
        new(string.Empty, "-win -xres 1920 -yres 1080", 30);

    public string LauncherPath { get; }

    public string LauncherArguments { get; }

    public int AttachTimeoutSeconds { get; }

    public ResourceValueSettings ResourceValues { get; }

    public IReadOnlyList<ReinforcementPreset> ReinforcementPresets { get; }

    /// <summary>
    /// When true the trainer also attaches to 1.12.x builds other than the one its hook addresses
    /// were derived from (see <see cref="TrainerVersionPolicy"/>). Default false = strict match.
    /// </summary>
    public bool AllowNonStandardVersion { get; }

    /// <summary>Explicit user acceptance of unverified world-position/camera risks; default off.</summary>
    public bool AllowUnverifiedWorldPositionFeatures { get; }

    public bool AutoFloatAfterLaunch { get; }

    /// <summary>
    /// When true (default) every hook site is compared against the patch manifest before anything is
    /// written, and an install is refused when none of them matches. Turn it off to install blind.
    /// </summary>
    public bool InspectHooksBeforeInstall { get; }

    /// <summary>
    /// When true (default) a crash-evidence bundle is written automatically if the target process
    /// dies with a non-zero exit code while patches were installed.
    /// </summary>
    public bool AutoCollectEvidenceOnExit { get; }

    /// <summary>
    /// 阵营的显示顺序与显示名（面板找兵第 1 层）。默认 = 内置四阵营（盟军/苏联/帝国/神州，显示名等于自身）。
    /// 配置里**没有**的阵营不会丢：它们从数据里自动出现、排在已知阵营之后；「其他」恒最后。
    /// schema：数组，每项 <c>[数据里的阵营名, 显示名]</c>，数组顺序 = 展示顺序。
    /// 例：<c>"FactionDisplay": [["盟军","盟军"],["苏联","苏联"],["帝国","帝国"],["神州","神州"]]</c>
    /// </summary>
    public IReadOnlyList<UnitTaxonomy.FactionDisplay> FactionDisplay { get; }

    /// <summary>
    /// 已知 mod 的额外别名：启动器键（归一化后）→ 数据里的 ModSource 显示名。默认只有日冕（corona→日冕）。
    /// 归一化后同名的 mod 不用配；中文名 mod 想自动对上当前环境时在这里加一条即可（加 mod 不改代码）。
    /// schema：对象，<c>"ModSourceAliases": { "armod": "ARKMOD" }</c>
    /// </summary>
    public IReadOnlyDictionary<string, string> ModSourceAliases { get; }

    /// <summary>
    /// 已知 mod 的**盘上/进程里**指纹：归一化后的路径段（目录名）→ mod 键。默认 = 内置两条（日冕、ARMod）。
    /// 只在**完整路径的目录段**上匹配，所以"两个 mod 带同名 DLL（EnhancerCorona.dll）"不会互相误判。
    /// 装新 mod 时在这里加一条即可，不改代码。schema：数组，每项
    /// <c>{ "Key": "armod", "PathTokens": ["armod","ra3wic301a"] }</c> 或二元数组简写 <c>["armod", ["armod"]]</c>。
    /// </summary>
    public IReadOnlyList<ModFingerprint> ModFingerprints { get; }

}


public sealed class TrainerAppSettingsStore
{
    public const string SettingsFileName = "Ra3Trainer.settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public TrainerAppSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public TrainerAppSettings Load()
    {
        if (!File.Exists(_path))
        {
            TrySave(TrainerAppSettings.Default);
            return TrainerAppSettings.Default;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream);
            return Normalize(document.RootElement);
        }
        catch (JsonException)
        {
            return TrainerAppSettings.Default;
        }
        catch (IOException)
        {
            return TrainerAppSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return TrainerAppSettings.Default;
        }
    }

    public void Save(TrainerAppSettings settings)
    {
        SaveToPath(_path, settings);
    }

    public static string DefaultPath()
    {
        return DefaultPath(AppContext.BaseDirectory);
    }

    public static string DefaultPath(string baseDirectory)
    {
        // 产品口径：设置写用户目录（装到 Program Files 也能存住）；老位置已有设置就继续用老的。
        return TrainerPaths.Resolve(baseDirectory, SettingsFileName);
    }

    private void TrySave(TrainerAppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void SaveToPath(string path, TrainerAppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Finish serialization before touching the last valid settings file.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { /* Cleanup must not hide the original save failure. */ }
            catch (UnauthorizedAccessException) { /* A later cleanup can remove the orphan temp file. */ }
        }
    }

    private static TrainerAppSettings Normalize(JsonElement root)
    {
        var defaults = TrainerAppSettings.Default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return defaults;
        }
        var launcherPath = ReadString(root, nameof(TrainerAppSettings.LauncherPath))
            ?? ReadString(root, "GameExecutablePath")
            ?? defaults.LauncherPath;
        var launcherArguments = ReadString(root, nameof(TrainerAppSettings.LauncherArguments))
            ?? defaults.LauncherArguments;
        var attachTimeoutSeconds = ReadInt32(root, nameof(TrainerAppSettings.AttachTimeoutSeconds))
            ?? defaults.AttachTimeoutSeconds;
        var resourceValues = ReadResourceValues(root) ?? defaults.ResourceValues;
        var reinforcementPresets = ReadReinforcementPresets(root);
        var allowNonStandardVersion = ReadBoolean(root, nameof(TrainerAppSettings.AllowNonStandardVersion))
            ?? defaults.AllowNonStandardVersion;
        var inspectHooksBeforeInstall = ReadBoolean(root, nameof(TrainerAppSettings.InspectHooksBeforeInstall))
            ?? defaults.InspectHooksBeforeInstall;
        var autoCollectEvidenceOnExit = ReadBoolean(root, nameof(TrainerAppSettings.AutoCollectEvidenceOnExit))
            ?? defaults.AutoCollectEvidenceOnExit;

        if (string.IsNullOrWhiteSpace(launcherArguments))
        {
            launcherArguments = defaults.LauncherArguments;
        }

        if (attachTimeoutSeconds <= 0)
        {
            attachTimeoutSeconds = defaults.AttachTimeoutSeconds;
        }

        return new TrainerAppSettings(
            launcherPath,
            launcherArguments,
            attachTimeoutSeconds,
            resourceValues,
            reinforcementPresets,
            allowNonStandardVersion,
            inspectHooksBeforeInstall,
            autoCollectEvidenceOnExit,
            // 其它字段都从 defaults 兜底，这两个原来写死了 false/true：
            // 一旦默认值改了，只有它们两个不跟着改（读写口径不一致）。
            ReadBoolean(root, nameof(TrainerAppSettings.AllowUnverifiedWorldPositionFeatures))
                ?? defaults.AllowUnverifiedWorldPositionFeatures,
            ReadBoolean(root, nameof(TrainerAppSettings.AutoFloatAfterLaunch)) ?? defaults.AutoFloatAfterLaunch,
            ReadFactionDisplay(root, defaults.FactionDisplay),
            ReadModSourceAliases(root, defaults.ModSourceAliases),
            ReadModFingerprints(root, defaults.ModFingerprints));
    }

    /// <summary>
    /// 读阵营显示表。接受两种写法（都能手改）：
    /// 对象 <c>{"Faction":"盟军","Label":"盟军"}</c>（序列化出来的原生形态）或二元数组 <c>["盟军","盟军"]</c>（简写）。
    /// 数组顺序 = 展示顺序。缺省 / 空 / 全非法 → 落回 <paramref name="fallback"/>；
    /// 读到的正好等于默认表时也落回同一个静态实例（record 相等靠引用比较，见构造函数注释）。
    /// </summary>
    private static IReadOnlyList<UnitTaxonomy.FactionDisplay> ReadFactionDisplay(
        JsonElement root, IReadOnlyList<UnitTaxonomy.FactionDisplay> fallback)
    {
        if (!root.TryGetProperty(nameof(TrainerAppSettings.FactionDisplay), out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var parsed = new List<UnitTaxonomy.FactionDisplay>();
        foreach (var item in value.EnumerateArray())
        {
            string? faction = null;
            string? label = null;
            if (item.ValueKind == JsonValueKind.Object)
            {
                faction = item.TryGetProperty(nameof(UnitTaxonomy.FactionDisplay.Faction), out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString() : null;
                label = item.TryGetProperty(nameof(UnitTaxonomy.FactionDisplay.Label), out var l) && l.ValueKind == JsonValueKind.String
                    ? l.GetString() : null;
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                var parts = item.EnumerateArray().ToArray();
                faction = parts.Length > 0 && parts[0].ValueKind == JsonValueKind.String ? parts[0].GetString() : null;
                label = parts.Length > 1 && parts[1].ValueKind == JsonValueKind.String ? parts[1].GetString() : null;
            }

            faction = faction?.Trim();
            label = label?.Trim();
            if (string.IsNullOrWhiteSpace(faction)) continue;
            parsed.Add(new UnitTaxonomy.FactionDisplay(faction, string.IsNullOrWhiteSpace(label) ? faction : label));
        }

        if (parsed.Count == 0) return fallback;
        return parsed.SequenceEqual(UnitTaxonomy.DefaultFactionDisplays) ? UnitTaxonomy.DefaultFactionDisplays : parsed;
    }

    /// <summary>读 mod 别名表（对象，启动器键 → ModSource 显示名）。缺省 / 空 → 落回默认（corona→日冕）。</summary>
    private static IReadOnlyDictionary<string, string> ReadModSourceAliases(
        JsonElement root, IReadOnlyDictionary<string, string> fallback)
    {
        if (!root.TryGetProperty(nameof(TrainerAppSettings.ModSourceAliases), out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return fallback;
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var key = property.Name.Trim();
            var label = property.Value.GetString()?.Trim() ?? string.Empty;
            if (key.Length == 0 || label.Length == 0) continue;
            parsed[key] = label;
        }

        if (parsed.Count == 0) return fallback;
        // 正好等于默认表 → 落回静态实例（record 相等用）。
        if (parsed.Count == UnitModScope.DefaultModSourceAliases.Count &&
            parsed.All(pair => UnitModScope.DefaultModSourceAliases.TryGetValue(pair.Key, out var label) &&
                string.Equals(label, pair.Value, StringComparison.Ordinal)))
        {
            return UnitModScope.DefaultModSourceAliases;
        }

        return parsed;
    }

    /// <summary>
    /// 读 mod 指纹表。接受对象 <c>{ "Key": "corona", "PathTokens": ["corona"] }</c> 或数组简写
    /// <c>["corona", ["corona"]]</c>（手改友好）。缺省 / 空 / 全非法 → 落回内置默认；
    /// 读到的正好等于默认表时也落回同一静态实例（record 相等靠引用比较，见构造函数注释）。
    /// </summary>
    private static IReadOnlyList<ModFingerprint> ReadModFingerprints(
        JsonElement root, IReadOnlyList<ModFingerprint> fallback)
    {
        if (!root.TryGetProperty(nameof(TrainerAppSettings.ModFingerprints), out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var parsed = new List<ModFingerprint>();
        foreach (var item in value.EnumerateArray())
        {
            string? key = null;
            var tokens = new List<string>();
            if (item.ValueKind == JsonValueKind.Object)
            {
                key = item.TryGetProperty(nameof(ModFingerprint.Key), out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString() : null;
                if (item.TryGetProperty(nameof(ModFingerprint.PathTokens), out var t) && t.ValueKind == JsonValueKind.Array)
                {
                    tokens.AddRange(t.EnumerateArray()
                        .Where(token => token.ValueKind == JsonValueKind.String)
                        .Select(token => token.GetString() ?? string.Empty));
                }
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                var parts = item.EnumerateArray().ToArray();
                key = parts.Length > 0 && parts[0].ValueKind == JsonValueKind.String ? parts[0].GetString() : null;
                if (parts.Length > 1 && parts[1].ValueKind == JsonValueKind.Array)
                {
                    tokens.AddRange(parts[1].EnumerateArray()
                        .Where(token => token.ValueKind == JsonValueKind.String)
                        .Select(token => token.GetString() ?? string.Empty));
                }
            }

            var normalizedKey = UnitModScope.NormalizeToken(key);
            if (normalizedKey.Length == 0)
            {
                continue;
            }

            var cleanTokens = tokens
                .Select(UnitModScope.NormalizeToken)
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            // 只写 Key 不写 tokens 时，用 Key 本身兜住（最少能按目录名匹配）。
            parsed.Add(new ModFingerprint(normalizedKey, cleanTokens.Length > 0 ? cleanTokens : [normalizedKey]));
        }

        if (parsed.Count == 0)
        {
            return fallback;
        }

        // 正好等于默认表 → 落回静态实例（record 相等用）。
        if (parsed.Count == ModIdentifier.DefaultFingerprints.Count &&
            parsed.Zip(ModIdentifier.DefaultFingerprints).All(pair =>
                string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) &&
                pair.First.PathTokens.SequenceEqual(pair.Second.PathTokens, StringComparer.Ordinal)))
        {
            return ModIdentifier.DefaultFingerprints;
        }

        return parsed;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
    private static int? ReadInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static bool? ReadBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : null;
    }

    private static ResourceValueSettings? ReadResourceValues(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(TrainerAppSettings.ResourceValues), out var value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var defaults = ResourceValueSettings.Default;
        var moneyAmount = ReadInt32(value, nameof(ResourceValueSettings.MoneyAmount)) ?? defaults.MoneyAmount;
        var powerValue = ReadInt32(value, nameof(ResourceValueSettings.PowerValue)) ?? defaults.PowerValue;
        var scPointValue = ReadInt32(value, nameof(ResourceValueSettings.ScPointValue)) ?? defaults.ScPointValue;
        try
        {
            return new ResourceValueSettings(moneyAmount, powerValue, scPointValue);
        }
        catch (ArgumentOutOfRangeException)
        {
            return defaults;
        }
    }


    private static IReadOnlyList<ReinforcementPreset> ReadReinforcementPresets(JsonElement root)
    {
        if (!root.TryGetProperty(nameof(TrainerAppSettings.ReinforcementPresets), out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ReinforcementPreset>();
        }

        var presets = new List<ReinforcementPreset>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(item, nameof(ReinforcementPreset.Name)) ?? string.Empty;
            var unitId = ReadUInt32(item, nameof(ReinforcementPreset.UnitId));
            var count = ReadInt32(item, nameof(ReinforcementPreset.Count));
            var rank = ReadInt32(item, nameof(ReinforcementPreset.Rank));
            if (unitId is null || count is null || rank is null)
            {
                continue;
            }

            try
            {
                presets.Add(new ReinforcementPreset(name, unitId.Value, count.Value, rank.Value));
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return presets;
    }

    private static uint? ReadUInt32(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var result)
            ? result
            : null;
    }

}
