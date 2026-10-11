namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// Which RA3 builds the embedded hook addresses are valid for. Every hook address in the
/// patch manifest was derived from one specific build, so an exact match is the safe default.
/// <see cref="IsSameProductLine"/> exists only so the UI can opt into a compatibility mode for
/// builds that still report the 1.12 family (patched game binaries keep the stock version
/// resource, so the exact string is not always proof either way).
/// </summary>
public static class TrainerVersionPolicy
{
    /// <summary>The build the manifest hook addresses were derived from.</summary>
    public const string ExpectedVersionText = "1.12.3444.25830";

    private const string ProductLineMajor = "1";
    private const string ProductLineMinor = "12";

    public static bool IsExact(string? fileVersion)
    {
        return Normalize(fileVersion).Equals(ExpectedVersionText, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameProductLine(string? fileVersion)
    {
        // 逐段 Trim：IsExact 是整体 Trim 后比较，这里原来不 Trim，
        // "1. 12.3444.25830" 会在这里失败却在 IsExact 也不成立，两处判定对同一份输入的解释不一致。
        var parts = Normalize(fileVersion).Split('.');
        return parts.Length >= 2
            && parts[0].Trim().Equals(ProductLineMajor, StringComparison.Ordinal)
            && parts[1].Trim().Equals(ProductLineMinor, StringComparison.Ordinal);
    }

    /// <summary>Strict match, or any 1.12.x build when the user enabled compatibility mode.</summary>
    public static bool IsSupported(string? fileVersion, bool allowNonStandardVersion)
    {
        return IsExact(fileVersion) || (allowNonStandardVersion && IsSameProductLine(fileVersion));
    }

    private static string Normalize(string? fileVersion)
    {
        return fileVersion?.Trim() ?? string.Empty;
    }
}
