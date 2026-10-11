namespace Ra3Trainer.Core.Runtime;

public static class DiagnosticTimeoutBudget
{
    private const int FinalGraceSeconds = 10;
    private const int DiagnosticIterationOverheadSeconds = 2;

    public static TimeSpan Calculate(
        int attachTimeoutSeconds,
        int monitorSeconds,
        bool prefixScan,
        int hookCount)
    {
        if (attachTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attachTimeoutSeconds));
        }

        if (monitorSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorSeconds));
        }

        if (hookCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hookCount));
        }

        // hookCount 用 long 加 1：int.MaxValue 时 int 运算会绕成负数，进而被下游误报成 diagnosticIterations 非法。
        var diagnosticIterations = prefixScan ? (long)hookCount + 1 : 1L;
        return CalculateCore(attachTimeoutSeconds, monitorSeconds, diagnosticIterations, prefixScan);
    }

    public static TimeSpan Calculate(
        int attachTimeoutSeconds,
        int monitorSeconds,
        int diagnosticIterations,
        bool includeIterationOverhead)
    {
        if (attachTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attachTimeoutSeconds));
        }

        if (monitorSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorSeconds));
        }

        if (diagnosticIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticIterations));
        }

        return CalculateCore(attachTimeoutSeconds, monitorSeconds, diagnosticIterations, includeIterationOverhead);
    }

    // 全程用 long：int 运算在 monitorSeconds/hookCount 较大时会静默溢出成负数，
    // TimeSpan.FromSeconds 会拿到一个错误（甚至为负）的预算，而不是快速失败。
    private static TimeSpan CalculateCore(
        int attachTimeoutSeconds,
        int monitorSeconds,
        long diagnosticIterations,
        bool includeIterationOverhead)
    {
        var perIteration = (long)monitorSeconds + (includeIterationOverhead ? DiagnosticIterationOverheadSeconds : 0);
        var seconds = (long)attachTimeoutSeconds + perIteration * diagnosticIterations + FinalGraceSeconds;
        if (seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnosticIterations),
                $"诊断超时预算 {seconds} 秒超出 TimeSpan 可表示范围。");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
