using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class FeatureControllerStopTests
{
    [Fact]
    public async Task StopCancelsInFlightAndQueuedActionsWithoutFurtherAccess()
    {
        var memory = new CountingMemory();
        var controller = Create(memory);
        var action = new TrainerFeature("action", "动作", null, [], "MustCode2+400", "0x05");
        var first = controller.TriggerActionAndWaitForConsumptionAsync(action,
            timeout: TimeSpan.FromMinutes(1), pollInterval: TimeSpan.FromSeconds(10));
        var second = controller.TriggerActionAndWaitForConsumptionAsync(action);
        controller.Stop();
        var accesses = memory.Accesses;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Throws<OperationCanceledException>(() => controller.ReadActionDispatch());
        Assert.Throws<OperationCanceledException>(() => controller.TriggerAction(action));
        Assert.Equal(accesses, memory.Accesses);
    }

    [Fact]
    public async Task StopWaitsForCurrentNativeAccessBeforeReturning()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var stopRequested = new ManualResetEventSlim();
        var memory = new CountingMemory { OnRead = () => { entered.Set(); release.Wait(); } };
        var controller = Create(memory);
        var read = Task.Run(controller.ReadActionDispatch);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task stop;
        try
        {
            stop = Task.Run(() => { stopRequested.Set(); controller.Stop(); });
            Assert.True(stopRequested.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(stop.IsCompleted);
        }
        finally { release.Set(); }
        await read;
        await stop;
        Assert.Throws<OperationCanceledException>(() => controller.ReadActionDispatch());
        Assert.Equal(1, memory.Accesses);
    }

    private static FeatureController Create(IProcessMemory memory) => new(memory,
        new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));

    private sealed class CountingMemory : IProcessMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private int _accesses;

        /// <summary>计数器会被后台线程（Task.Run 的 ReadActionDispatch 与异步续体）与测试线程同时碰到，
        /// 所以用 Interlocked 递增 / Volatile 读取，避免丢更新造成随机红绿。</summary>
        public int Accesses => Volatile.Read(ref _accesses);

        public Action? OnRead { get; init; }
        public byte[] ReadBytes(nint address, int count)
        {
            Interlocked.Increment(ref _accesses);
            OnRead?.Invoke();
            return _inner.ReadBytes(address, count);
        }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Interlocked.Increment(ref _accesses);
            _inner.WriteBytes(address, bytes);
        }
    }
}
