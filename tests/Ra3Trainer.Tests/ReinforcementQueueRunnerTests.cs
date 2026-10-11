using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ReinforcementQueueRunnerTests
{
    [Theory]
    [InlineData("consume")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task QueuePreservesConsumptionTimeoutAndCancellationForVerifiedHealthAction(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var memory = new HealthDispatchMemory(resolver, mode, cancellation);
        var controller = new FeatureController(memory, resolver);
        // Use the actual supported health action and its complete selected-object fixture.
        // No reinforcement name is changed to bypass its missing-world-root rejection.
        var health = new TrainerFeature("Select Unit HP MAX", "选中单位无限血量", null, [], "MustCode2+400", "0x05");
        var run = ReinforcementQueueRunner.ExecuteAsync(
            [new ReinforcementQueueEntry("first", "11111111", "2", "1"), new ReinforcementQueueEntry("second", "22222222", "3", "2")],
            controller, health, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1), cancellation.Token);
        if (mode == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(1, memory.DispatchCount);
        }
        else
        {
            var results = await run;
            Assert.Equal(new[] { "first", "second" }, results.Select(r => r.Entry.Name));
            Assert.All(results, r => Assert.Equal(mode == "consume" ? ReinforcementQueueItemStatus.Executed : ReinforcementQueueItemStatus.TimedOut, r.Status));
            Assert.Equal(mode == "consume" ? 2 : 1, memory.DispatchCount);
        }
        Assert.Equal(memory.DispatchCount, memory.WriteCount);
    }

    private sealed class HealthDispatchMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private readonly string _mode;
        private readonly CancellationTokenSource _cancellation;
        public int WriteCount { get; private set; }
        public int DispatchCount { get; private set; }
        public HealthDispatchMemory(AddressResolver resolver, string mode, CancellationTokenSource cancellation)
        {
            _mode = mode;
            _cancellation = cancellation;
            SelectedObjectSafetyTests.Seed(_inner, resolver);
        }
        public Win32MemoryRegion? Query(nint address) => _inner.Query(address);
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            WriteCount++;
            _inner.WriteBytes(address, bytes);
            if (address != 0x5020 || bytes.Length != 1 || bytes[0] != 5) return;
            DispatchCount++;
            if (_mode == "cancel") _cancellation.Cancel();
            if (_mode != "consume") return;
            _inner.WriteBytes(0x70004, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
            _inner.WriteBytes(0x7000C, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
            _inner.WriteBytes(address, [0]);
        }
    }

    [Fact]
    public async Task MissingRootRefusesBeforeDispatchCanCancelLastEntry()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new CancelOnDispatchMemory(cancellation);
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;

        var results = await ReinforcementQueueRunner.ExecuteAsync(
            [new ReinforcementQueueEntry("last", "11111111", "2", "1")],
            controller, ReinforcementFeature, cancellationToken: cancellation.Token);
        Assert.Equal(ReinforcementQueueItemStatus.Failed, Assert.Single(results).Status);
        Assert.Contains("根链", results[0].Message);
        // 关键：拒绝的具体理由必须是「对象指针为空」（真的读到了空根），而不是包装层那句
        // 「内存读取器不支持提交状态与页面保护查询」——两者都带「根链」前缀，只查「根链」等于没查。
        Assert.Contains("对象指针为空、越界或未对齐", results[0].Message);
        Assert.Equal(results[0].Message, Assert.Single(safetyMessages));
        Assert.Equal(0, memory.WriteCount);
        Assert.False(cancellation.IsCancellationRequested);
    }

    // 必须同时实现 IProbeMemory：只实现 IProcessMemory 时 FeatureMemoryAccess.Query 会直接抛
    // 「不支持提交状态查询」，预检永远走不到「读根指针」那一步（旧版测试正是这样假绿的）。
    private sealed class CancelOnDispatchMemory(CancellationTokenSource cancellation) : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        public int WriteCount => _inner.WriteCount;
        public Win32MemoryRegion? Query(nint address) => _inner.Query(address);
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            _inner.WriteBytes(address, bytes);
            if (address == 0x5020)
            {
                cancellation.Cancel();
            }
        }
    }

    private static readonly TrainerFeature ReinforcementFeature =
        new("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");

    [Fact]
    public async Task ExecuteAsyncRefusesValidParametersWithoutWorldRoot()
    {
        var memory = new DispatchConsumingMemory(0x5020);
        var controller = new FeatureController(
            memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var entries = new[]
        {
            new ReinforcementQueueEntry("first", "0x11111111", "2", "1"),
            new ReinforcementQueueEntry("second", "0x22222222", "3", "2")
        };

        var results = await ReinforcementQueueRunner.ExecuteAsync(
            entries,
            controller,
            ReinforcementFeature,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1));

        Assert.All(results, result => { Assert.Equal(ReinforcementQueueItemStatus.Failed, result.Status); Assert.Contains("根链", result.Message); });
        // 拒绝的具体理由必须是「对象指针为空」（真的读到了空的世界根），而不是包装层那句
        // 「内存读取器不支持提交状态与页面保护查询」——两者都带「根链」，只查根链等于没查。
        Assert.All(results, result => Assert.Contains("对象指针为空、越界或未对齐", result.Message));
        Assert.Equal(results.Select(r => r.Message), safetyMessages);
        Assert.Equal(2, results.Count);
        Assert.Empty(memory.UnitWrites);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public async Task ExecuteAsyncSkipsInvalidEntriesAndContinues()
    {
        var memory = new DispatchConsumingMemory(0x5020);
        var controller = new FeatureController(
            memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var entries = new[]
        {
            new ReinforcementQueueEntry("bad", "0x0", "2", "1"),
            new ReinforcementQueueEntry("good", "0x22222222", "3", "2")
        };

        var results = await ReinforcementQueueRunner.ExecuteAsync(
            entries,
            controller,
            ReinforcementFeature,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(ReinforcementQueueItemStatus.Skipped, results[0].Status);
        Assert.Contains("Unit id", results[0].Message);
        Assert.Equal(ReinforcementQueueItemStatus.Failed, results[1].Status);
        Assert.Contains("根链", results[1].Message);
        Assert.Contains("对象指针为空、越界或未对齐", results[1].Message);
        Assert.Equal(results[1].Message, Assert.Single(safetyMessages));
        Assert.Empty(memory.UnitWrites);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public async Task ExecuteAsyncRefusesMissingRootBeforeTimeoutAndContinues()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(
            memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var entries = new[]
        {
            new ReinforcementQueueEntry("slow", "0x11111111", "2", "1"),
            new ReinforcementQueueEntry("also-slow", "0x22222222", "3", "2")
        };

        var results = await ReinforcementQueueRunner.ExecuteAsync(
            entries,
            controller,
            ReinforcementFeature,
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, results.Count);
        Assert.All(results, result => { Assert.Equal(ReinforcementQueueItemStatus.Failed, result.Status); Assert.Contains("根链", result.Message); });
        Assert.All(results, result => Assert.Contains("对象指针为空、越界或未对齐", result.Message));
        Assert.Equal(results.Select(r => r.Message), safetyMessages);
        Assert.Equal(0, memory.WriteCount);
    }

    // 必须同时实现 IProbeMemory：只实现 IProcessMemory 时 FeatureMemoryAccess.Query 会直接抛
    // 「内存读取器不支持提交状态与页面保护查询」（该文案里也带「根链」），预检永远走不到「读根指针」那一步，
    // 于是测试假绿。把 Query 委托给内层 FakeProcessMemory，跟 HealthDispatchMemory / CancelOnDispatchMemory 一致。
    private sealed class DispatchConsumingMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private readonly nint _dispatchAddress;
        public int WriteCount => _inner.WriteCount;

        public DispatchConsumingMemory(nint dispatchAddress)
        {
            _dispatchAddress = dispatchAddress;
        }

        public List<uint> UnitWrites { get; } = [];

        public Win32MemoryRegion? Query(nint address) => _inner.Query(address);

        public byte[] ReadBytes(nint address, int count)
        {
            return _inner.ReadBytes(address, count);
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            _inner.WriteBytes(address, bytes);
            if (address == 0x5024 && bytes.Length == 4)
            {
                UnitWrites.Add(BitConverter.ToUInt32(bytes));
            }

            if (address == _dispatchAddress && bytes.Length == 1 && bytes[0] != 0)
            {
                _inner.WriteBytes(address, [0x00]);
            }
        }
    }
}
