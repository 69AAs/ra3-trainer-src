using System.IO.Compression;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class GameProcessWaiterTests
{
    [Fact]
    public async Task WaitForAsyncReturnsTargetWhenProcessAppearsBeforeTimeout()
    {
        var ticks = 0L;
        var calls = 0;
        var expected = new TrainerTarget("ra3_1.12.game", 0x400000, true, true);
        var askedNames = new List<string?>();
        var waiter = new GameProcessWaiter(
            name =>
            {
                askedNames.Add(name);
                return ++calls == 3 ? expected : null;
            },
            (delay, _) =>
            {
                ticks += (long)delay.TotalMilliseconds;
                return Task.CompletedTask;
            },
            () => ticks);

        var actual = await waiter.WaitForAsync("ra3_1.12.game", TimeSpan.FromSeconds(30));

        Assert.Same(expected, actual);
        Assert.Equal(3, calls);
        // 等待的必须是"这个进程"：以前这个断言没有，_find 传错名字也照样过。
        Assert.All(askedNames, name => Assert.Equal("ra3_1.12.game", name));
        Assert.Equal("ra3_1.12.game", actual!.ProcessName);
    }

    [Fact]
    public async Task WaitForAsyncReturnsNullWhenTimeoutExpires()
    {
        var ticks = 0L;
        var waiter = new GameProcessWaiter(
            _ => null,
            (delay, _) =>
            {
                ticks += (long)delay.TotalMilliseconds;
                return Task.CompletedTask;
            },
            () => ticks);

        var actual = await waiter.WaitForAsync("ra3_1.12.game", TimeSpan.FromSeconds(1));

        Assert.Null(actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WaitForAsyncRejectsNonPositiveTimeout(int milliseconds)
    {
        var waiter = new GameProcessWaiter(_ => null, (_, _) => Task.CompletedTask, () => 0L);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => waiter.WaitForAsync("ra3_1.12.game", TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task WaitForAsyncHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var waiter = new GameProcessWaiter(
            _ => null,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(TimeSpan.Zero, token);
            },
            () => 0L);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitForAsync("ra3_1.12.game", TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task WaitForAsyncThrowsImmediatelyWhenTokenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var waiter = new GameProcessWaiter(_ => { calls++; return null; }, (_, _) => Task.CompletedTask, () => 0L);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitForAsync("ra3_1.12.game", TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.Equal(0, calls);   // 取消后一次都不该再查进程
    }
}
