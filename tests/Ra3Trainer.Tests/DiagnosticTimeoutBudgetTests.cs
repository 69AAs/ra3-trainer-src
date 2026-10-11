using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class DiagnosticTimeoutBudgetTests
{
    [Fact]
    public void CalculateForSingleProbeKeepsLegacyBudget()
    {
        var budget = DiagnosticTimeoutBudget.Calculate(
            attachTimeoutSeconds: 30,
            monitorSeconds: 5,
            prefixScan: false,
            hookCount: 22);

        Assert.Equal(TimeSpan.FromSeconds(45), budget);
    }

    [Fact]
    public void CalculateForPrefixScanAllowsEveryHookPrefixToBeMonitored()
    {
        var budget = DiagnosticTimeoutBudget.Calculate(
            attachTimeoutSeconds: 30,
            monitorSeconds: 5,
            prefixScan: true,
            hookCount: 22);

        Assert.Equal(TimeSpan.FromSeconds(201), budget);
    }

    [Fact]
    public void PrefixScanWithoutHooksIsTheMinimumOneIterationBudget()
    {
        // hookCount 0 时 diagnosticIterations = hookCount + 1 = 1，正是 off-by-one 的边界。
        var budget = DiagnosticTimeoutBudget.Calculate(
            attachTimeoutSeconds: 30,
            monitorSeconds: 5,
            prefixScan: true,
            hookCount: 0);

        Assert.Equal(TimeSpan.FromSeconds(47), budget);
        Assert.Equal(budget, DiagnosticTimeoutBudget.Calculate(
            attachTimeoutSeconds: 30, monitorSeconds: 5, diagnosticIterations: 1, includeIterationOverhead: true));
    }

    [Theory]
    [InlineData(-1, 5, false, 0)]
    [InlineData(30, -1, false, 0)]
    [InlineData(30, 5, true, -1)]
    public void CalculateRejectsNegativeInputs(int attach, int monitor, bool prefixScan, int hookCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticTimeoutBudget.Calculate(attach, monitor, prefixScan, hookCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateRejectsNonPositiveDiagnosticIterations(int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagnosticTimeoutBudget.Calculate(30, 5, iterations, includeIterationOverhead: false));
    }

    [Fact]
    public void CalculateForDiagnosticIterationsAllowsEveryFeatureToBeMonitored()
    {
        var budget = DiagnosticTimeoutBudget.Calculate(
            attachTimeoutSeconds: 30,
            monitorSeconds: 5,
            diagnosticIterations: 32,
            includeIterationOverhead: true);

        Assert.Equal(TimeSpan.FromSeconds(264), budget);
    }
}
