using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class FeatureToggleTransactionTests
{
    private static readonly TrainerFeature Feature = new("test", "测试", null,
        ["flags", "flags+1"], null, null,
        [new TrainerFeatureBytePatch("code", [0xEB, 0x0C], [0x75, 0x0C])]);

    private static FeatureController Controller(FailingMemory memory) => new(memory,
        new AddressResolver(0, new Dictionary<string, nint> { ["flags"] = 0x5000, ["code"] = 0x6000 }));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void FailureRestoresActualOldBytesIncludingPartiallyWrittenRange(int failAt, bool enabled)
    {
        var memory = new FailingMemory { FailWrites = [failAt] };
        var controller = Controller(memory);

        Assert.Throws<IOException>(() => controller.SetToggle(Feature, enabled));

        Assert.Equal(new byte[] { 7, 9 }, memory.Inner.ReadBytes(0x5000, 2));
        Assert.Equal(new byte[] { 0x74, 0x12 }, memory.Inner.ReadBytes(0x6000, 2));
        Assert.Equal(0, controller.PendingToggleRestoreCount);
        var addresses = new nint[] { 0x5000, 0x5001, 0x6000 };
        Assert.Equal(addresses.Take(failAt).Concat(addresses.Take(failAt).Reverse()), memory.Writes);
    }

    [Fact]
    public void SnapshotFailureDoesNotWriteAnything()
    {
        var memory = new FailingMemory { FailReadAt = 0x6000 };
        Assert.Throws<IOException>(() => Controller(memory).SetToggle(Feature, true));
        Assert.Empty(memory.Writes);
    }

    [Fact]
    public void FailedRollbackKeepsBothErrorsAndCanBeRetried()
    {
        var memory = new FailingMemory { FailWrites = [3, 4, 5] };
        var controller = Controller(memory);
        var error = Assert.Throws<AggregateException>(() => controller.SetToggle(Feature, true));
        Assert.Equal(3, error.Flatten().InnerExceptions.Count);
        Assert.Equal(2, controller.PendingToggleRestoreCount);
        Assert.Equal(7, memory.Inner.ReadByte(0x5000)); // Still attempts earlier restores.

        controller.RetryPendingToggleRestores();

        Assert.Equal(0, controller.PendingToggleRestoreCount);
        Assert.Equal(new byte[] { 7, 9 }, memory.Inner.ReadBytes(0x5000, 2));
        Assert.Equal(new byte[] { 0x74, 0x12 }, memory.Inner.ReadBytes(0x6000, 2));
    }

    [Fact]
    public void UnresolvedRollbackBlocksNewWritesAndPreservesBaseline()
    {
        var memory = new FailingMemory { FailWrites = [2, 3, 5] };
        var controller = Controller(memory);
        Assert.Throws<AggregateException>(() => controller.SetToggle(Feature, true));
        Assert.Equal(1, controller.PendingToggleRestoreCount);
        Assert.Throws<AggregateException>(() => controller.SetToggle(Feature, false));
        Assert.Equal(5, memory.Writes.Count); // Only retries the pending restore.
        controller.RetryPendingToggleRestores();
        Assert.Equal(new byte[] { 7, 9 }, memory.Inner.ReadBytes(0x5000, 2));
    }

    [Fact]
    public void OverlappingRangesRestoreSnapshotTakenBeforeAnyWrite()
    {
        var memory = new FailingMemory { FailWrites = [3] };
        var feature = Feature with
        {
            ToggleBytePatches = [new TrainerFeatureBytePatch("flags", [2, 3], [0, 0])]
        };
        Assert.Throws<IOException>(() => Controller(memory).SetToggle(feature, true));
        Assert.Equal(new byte[] { 7, 9 }, memory.Inner.ReadBytes(0x5000, 2));
    }

    [Fact]
    public void NewToggleRetriesPendingRestoresBeforeStartingAnotherTransaction()
    {
        var memory = new FailingMemory { FailWrites = [2, 3] };
        var controller = Controller(memory);
        Assert.Throws<AggregateException>(() => controller.SetToggle(Feature, true));
        controller.SetToggle(Feature, true);
        Assert.Equal(0, controller.PendingToggleRestoreCount);
        Assert.Equal(new byte[] { 1, 1 }, memory.Inner.ReadBytes(0x5000, 2));
        Assert.Equal(new byte[] { 0xEB, 0x0C }, memory.Inner.ReadBytes(0x6000, 2));
        Assert.Equal((nint)0x5001, memory.Writes[4]); // Recovery precedes the new flag writes.
    }

    [Fact]
    public async Task TimeoutDoesNotWaitForAnEntireLongPollInterval()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var feature = new TrainerFeature("action", "动作", null, [], "MustCode2+400", "0x05");
        var result = await controller.TriggerActionAndWaitForConsumptionAsync(feature,
            timeout: TimeSpan.FromMilliseconds(20), pollInterval: TimeSpan.FromMinutes(1))
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(ActionDispatchResult.TimedOut, result);
        // 地址由生产布局常量推导，不再写死 0x5020：布局偏移一变这里会直接红，而不是读到 0。
        Assert.Equal((nint)0x5020, resolver.Resolve(RemoteStateLayout.ActionDispatch));
        Assert.Equal(5, memory.ReadByte(resolver.Resolve(RemoteStateLayout.ActionDispatch)));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(10, -1)]
    [InlineData(10, 0)]
    [InlineData(10, 4294967295)]
    public async Task InvalidWaitArgumentsNeverDispatch(double timeoutMs, double pollMs)
    {
        var memory = new FailingMemory();
        var feature = new TrainerFeature("action", "动作", null, ["flags"], null, "0x02");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Controller(memory)
            .TriggerActionAndWaitForConsumptionAsync(feature,
                timeout: TimeSpan.FromMilliseconds(timeoutMs), pollInterval: TimeSpan.FromMilliseconds(pollMs)));
        Assert.Empty(memory.Writes);
    }

    [Fact]
    public void ShortDispatchSlotReadIsRejectedAsBusyNotIdle()
    {
        // 4 字节派发槽只回 3 字节：绝不能被当成"空槽"，必须按"上一动作尚未消费"拒绝派发。
        var memory = new FailingMemory { ShortReadAt = 0x5020 };
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var feature = new TrainerFeature("action", "动作", null, [], "MustCode2+400", "0x05");

        var error = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature));

        Assert.Contains("上一动作尚未被游戏消费", error.Message);
        Assert.Empty(memory.Writes);
    }

    private sealed class FailingMemory : IProcessMemory
    {
        public FakeProcessMemory Inner { get; } = new();
        public HashSet<int> FailWrites { get; init; } = [];
        public nint? FailReadAt { get; init; }

        /// <summary>该地址的读取只回 count-1 字节，模拟"读不完整"（生产端多处要求长度恰好为 4）。</summary>
        public nint? ShortReadAt { get; init; }
        public List<nint> Writes { get; } = [];

        public FailingMemory()
        {
            Inner.WriteBytes(0x5000, [7, 9]);
            Inner.WriteBytes(0x6000, [0x74, 0x12]);
        }

        public byte[] ReadBytes(nint address, int count)
        {
            if (address == FailReadAt) throw new IOException("读取失败");
            var bytes = Inner.ReadBytes(address, count);
            return address == ShortReadAt ? bytes[..Math.Max(0, count - 1)] : bytes;
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Writes.Add(address);
            if (FailWrites.Contains(Writes.Count))
            {
                // 零长度写不能崩在 bytes[..1]（ArgumentOutOfRangeException 会掩盖真正的 IOException）。
                if (bytes.Length > 0) Inner.WriteBytes(address, bytes[..1]);
                throw new IOException($"写入失败 #{Writes.Count}");
            }
            Inner.WriteBytes(address, bytes);
        }
    }
}
