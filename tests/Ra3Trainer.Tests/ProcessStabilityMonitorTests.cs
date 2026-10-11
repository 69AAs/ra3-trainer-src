using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ProcessStabilityMonitorTests
{
    [Fact]
    public async Task MonitorAsyncReturnsStableWhenProcessStaysAliveForDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new ProcessStabilityMonitor(
            _ => ProcessStabilityObservation.Alive,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now);

        var result = await monitor.MonitorAsync(1234, TimeSpan.FromSeconds(1));

        Assert.True(result.StayedAlive);
        Assert.Equal(1234, result.ProcessId);
        Assert.Null(result.ExitCode);
        Assert.True(result.ObservedFor >= TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MonitorAsyncRejectsNonPositiveDurations(int seconds)
    {
        var monitor = new ProcessStabilityMonitor(_ => ProcessStabilityObservation.Alive);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            monitor.MonitorAsync(1, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task MonitorAsyncReportsMissingProcessAsNotStayedAlive()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new ProcessStabilityMonitor(
            _ => ProcessStabilityObservation.Missing,
            (delay, _) => { now += delay; return Task.CompletedTask; },
            () => now);

        var result = await monitor.MonitorAsync(5555, TimeSpan.FromSeconds(30));

        // 进程消失（GetProcessById 抛 Argument/InvalidOperation/Win32）必须算"没活住"，且没有退出码。
        Assert.False(result.StayedAlive);
        Assert.Null(result.ExitCode);
        Assert.Equal(5555, result.ProcessId);
    }

    [Fact]
    public async Task MonitorAsyncStopsOnCancellation()
    {
        var now = DateTimeOffset.UtcNow;
        using var cancellation = new CancellationTokenSource();
        var monitor = new ProcessStabilityMonitor(
            _ => ProcessStabilityObservation.Alive,
            (delay, token) => { cancellation.Cancel(); return Task.CompletedTask; },
            () => now);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            monitor.MonitorAsync(9, TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task MonitorAsyncDeclaresStableExactlyAtTheDeadline()
    {
        var now = DateTimeOffset.UtcNow;
        var observations = 0;
        var monitor = new ProcessStabilityMonitor(
            _ => { observations++; return ProcessStabilityObservation.Alive; },
            (delay, _) => { now += delay; return Task.CompletedTask; },
            () => now);

        var result = await monitor.MonitorAsync(7, TimeSpan.FromSeconds(1));

        // 正好走到 deadline（remaining == 0）时必须按 Stable 收尾，不能多轮一次或提前判死。
        Assert.True(result.StayedAlive);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ObservedFor);
        Assert.Equal(3, observations);
    }

    [Fact]
    public async Task MonitorAsyncReturnsExitDetailsWhenProcessExitsBeforeDuration()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var monitor = new ProcessStabilityMonitor(
            _ => ++calls == 3
                ? ProcessStabilityObservation.Exited(3221225477)
                : ProcessStabilityObservation.Alive,
            (delay, _) =>
            {
                now += delay;
                return Task.CompletedTask;
            },
            () => now);

        var result = await monitor.MonitorAsync(4321, TimeSpan.FromSeconds(30));

        Assert.False(result.StayedAlive);
        Assert.Equal(4321, result.ProcessId);
        Assert.Equal(3221225477, result.ExitCode);
        Assert.True(result.ObservedFor < TimeSpan.FromSeconds(30));
    }
}
