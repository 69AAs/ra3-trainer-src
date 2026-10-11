namespace Ra3Trainer.Core.Runtime;

public sealed class GameProcessWaiter
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<string, TrainerTarget?> _find;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<long> _tickCount;

    public GameProcessWaiter()
        : this(new TrainerProcessLocator().Find)
    {
    }

    /// <param name="find">进程查找（<see cref="TrainerProcessLocator.Find"/>）；必须不抛，找不到就返回 null。</param>
    /// <param name="delay">等待（测试注入用）。</param>
    /// <param name="tickCount">单调毫秒计数（测试注入用）。默认 <see cref="Environment.TickCount64"/>：
    /// 原来用的是墙钟，NTP 校时/手动改时间往回跳会把等待拖过 timeout。</param>
    public GameProcessWaiter(
        Func<string, TrainerTarget?> find,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<long>? tickCount = null)
    {
        _find = find;
        _delay = delay ?? Task.Delay;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
    }

    public async Task<TrainerTarget?> WaitForAsync(
        string processName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        var deadline = _tickCount() + (long)timeout.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = _find(processName);
            if (target is not null)
            {
                return target;
            }

            var remaining = deadline - _tickCount();
            if (remaining <= 0)
            {
                return null;
            }

            await _delay(Min(DefaultPollInterval, TimeSpan.FromMilliseconds(remaining)), cancellationToken);
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second)
    {
        return first <= second ? first : second;
    }
}
