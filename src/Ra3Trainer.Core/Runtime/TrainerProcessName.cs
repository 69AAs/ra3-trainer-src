namespace Ra3Trainer.Core.Runtime;

public static class TrainerProcessName
{
    private static readonly string[] ExecutableSuffixes = [".game", ".exe"];

    /// <summary>归一成裸进程名（去路径、去 .game/.exe 后缀）。空白/null 返回空串。</summary>
    public static string ToProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(name.Trim());
        // 一直剥到没有已知后缀为止："ra3.exe.game" 原来只剥一次、还留着 ".exe"。
        while (true)
        {
            var suffix = ExecutableSuffixes.FirstOrDefault(candidate =>
                fileName.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (suffix is null || fileName.Length <= suffix.Length)
            {
                break;
            }

            fileName = fileName[..^suffix.Length];
        }

        return fileName;
    }

    /// <summary>
    /// 进程名比较（谓词语义）：null/空白一律"不匹配"，不抛异常。
    /// 两边都归一成空串时也不能算匹配 —— 那等于"没指定进程"撞上"名字读不出来"，
    /// 会让 Find 把任意一个候选当成目标。
    /// </summary>
    public static bool Matches(string? left, string? right)
    {
        var normalizedLeft = ToProcessName(left);
        var normalizedRight = ToProcessName(right);
        return normalizedLeft.Length > 0 && normalizedRight.Length > 0 &&
            normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }
}
