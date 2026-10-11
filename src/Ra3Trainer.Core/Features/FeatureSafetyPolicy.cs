using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Features;

/// <summary>Product safety checks applied before dispatching the low-level feature protocol.</summary>
public static class FeatureSafetyPolicy
{
    // 功能名集合统一在这里维护（原来四个方法各自写一串 `is "A" or "B"`，加一项容易只加一处，
    // 悄悄改掉"哪些检查会跑"）。名字必须与 trainer_report.json 的 name 逐字一致（此处大小写不敏感）。
    private static readonly HashSet<string> CoordinateActionNames =
        new(StringComparer.OrdinalIgnoreCase) { "WE NEED BACK", "GET ME BASE", "SELECT UNIT COPY FOR ME" };

    private static readonly HashSet<string> CoordinateActionTargets =
        new(StringComparer.OrdinalIgnoreCase) { "MUSTCODE2+A00", "MUSTCODE2+B00", "MUSTCODE2+C00", "MUSTCODE2+D00" };

    private static readonly HashSet<string> WorldPositionOptInNames =
        new(StringComparer.OrdinalIgnoreCase) { "ZOOM", "FREE BUILD", "GET ME BASE", "WE NEED BACK", "SELECT UNIT COPY FOR ME" };

    private static readonly HashSet<string> DangerLevelNames =
        new(StringComparer.OrdinalIgnoreCase) { "DANGER LEVEL MAX", "DANGER LEVEL MIN", "RESTORE DANGER LEVEL NORMAL" };

    private static readonly HashSet<string> SelectedObjectValidationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT UNIT LEVEL UP", "SELECT UNIT SUPER SPEED", "SELECT UNIT SLOW SPEED", "SELECT UNIT FREEZE",
        "RESTORE SELECT UNIT SPEED", "SELECT UNIT HP MAX", "SELECT UNIT HP MIN", "RESTORE SELECT UNIT NORMAL HP",
        "SELECT UNIT AMMO MAX", "SELECT UNIT AMMO MAX 2", "SELECT UNIT CHANGE ID", "DESTORY SELECT UNIT",
        "RESTORE SELECT ORE MINE"
    };

    private static readonly HashSet<string> ObjectFlagRecoveryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT UNIT AMMO MAX", "SELECT UNIT AMMO MAX 2", "RESTORE DANGER LEVEL NORMAL"
    };

    public static bool IsCoordinateAction(TrainerFeature feature) =>
        IsNamed(feature, CoordinateActionNames) ||
        (feature.DispatchTarget is not null && CoordinateActionTargets.Contains(feature.DispatchTarget)) ||
        IsCoordinateOpcode(feature.ValueHint);

    private static bool IsNamed(TrainerFeature feature, HashSet<string> names) =>
        feature.RawName is not null && names.Contains(feature.RawName);

    private static bool IsCoordinateOpcode(string? hint)
    {
        if (hint is not { Length: > 2 }) return false;
        // 跟 DispatchAction 的解析保持一致（同样只切掉两字符前缀再按十六进制取字节）。
        try { return Convert.ToByte(hint[2..], 16) is >= 0x0B and <= 0x0E; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static bool RequiresWorldPositionOptIn(TrainerFeature feature) =>
        IsNamed(feature, WorldPositionOptInNames) || RequiresSelectedObjectValidation(feature) ||
        IsNamed(feature, DangerLevelNames);

    public static bool RequiresSelectedObjectValidation(TrainerFeature feature) =>
        IsNamed(feature, SelectedObjectValidationNames);

    public const string RiskLabel = "校验通过才执行；经验边界不保证运行时安全";
    public const string RestrictionHelp = "这些是默认关闭的高风险功能（单位操作、威胁等级、矿点）。需要时在「诊断」页开启「高风险功能」；开启后每项仍会做安全检查，缺少完整前置条件时不会执行。";

    public static bool IsObjectFlagRecovery(TrainerFeature feature) =>
        feature.DispatchTarget is null &&
        string.Equals(feature.ValueHint, "0x00", StringComparison.OrdinalIgnoreCase) &&
        IsNamed(feature, ObjectFlagRecoveryNames);

    public static void Validate(TrainerFeature feature, bool enabled, bool allowUnverifiedWorldPositionFeatures = false)
    {
        ArgumentNullException.ThrowIfNull(feature);
        // 未启用 / 已开启安全校验授权 / 对象标志恢复：都不是"要派发的高风险动作"，直接放行。
        if (allowUnverifiedWorldPositionFeatures || !enabled || IsObjectFlagRecovery(feature)) return;
        if (RequiresSelectedObjectValidation(feature) || IsNamed(feature, DangerLevelNames))
            throw new InvalidOperationException($"{feature.DisplayName}暂受限制：需先开启安全校验授权，校验通过才执行。" + RestrictionHelp);
        if (feature.RawName.Equals("Zoom", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("无限缩放暂受限制：需先开启安全校验授权，并检查相机根链和上下限；仍可关闭此功能。" + RestrictionHelp);
        if (feature.RawName.Equals("Free Build", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("随地建造暂受限制：现有代码绕过放置条件，无法校验地图边界与地形是否合法；仍可关闭此功能。" + RestrictionHelp);
        var actionName = feature.RawName.ToUpperInvariant() switch
        {
            "GET ME BASE" => "给玩家基地车",
            "WE NEED BACK" => "呼叫战场增援",
            "SELECT UNIT COPY FOR ME" => "复制选择的建筑物/单位给玩家",
            _ => null
        };
        if (actionName is not null)
            throw new InvalidOperationException($"{actionName}暂受限制：需先开启安全校验授权；转换完成后仅在世界坐标新鲜、为有限值且处于经验范围、对象根链通过时派发生成；经验范围不等于地图边界或地形合法性。" + RestrictionHelp);
    }
}
