using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class FeatureControllerTests
{
    internal static void SeedCamera(IProcessMemory memory, AddressResolver resolver)
    {
        memory.WriteBytes(resolver.Resolve("ra3_1.12.game+8DB7B4"), BitConverter.GetBytes(0xE0000u));
        memory.WriteBytes(0xE0000, BitConverter.GetBytes(0xF0000u));
        memory.WriteBytes(0xE0044, BitConverter.GetBytes(800f));
        memory.WriteBytes(0xE2700, BitConverter.GetBytes(40f));
        memory.WriteBytes(0xE2704, BitConverter.GetBytes(800f));
    }

    [Fact]
    public async Task QuiesceCancelsPollingAndRejectsNewWritesButAllowsRecovery()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var toggle = new TrainerFeature("test", "测试", null, ["iEnable+1"], null, null);
        var action = new TrainerFeature("test", "测试", null, [], "MustCode2+400", "0x05");
        controller.SetToggle(toggle, true);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = controller.TriggerActionAndWaitForConsumptionAsync(action, onDispatched: () => dispatched.SetResult());
        await dispatched.Task;
        controller.QuiesceActions();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.ThrowsAny<OperationCanceledException>(() => controller.TriggerAction(action));
        Assert.ThrowsAny<OperationCanceledException>(() => controller.WriteResourceValues(ResourceValueSettings.Default));
        Assert.ThrowsAny<OperationCanceledException>(() => controller.WriteReinforcementSettings(ReinforcementSettings.Default));
        controller.SetToggle(toggle, false);
        controller.Reset(action);
        Assert.Equal(0, memory.ReadByte(0x5001));
        Assert.Equal(0, memory.ReadByte(0x5020));
    }

    [Fact]
    public void MultiFlagActionRollsBackWhenLaterWriteFails()
    {
        var memory = new ParameterFaultMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        memory.WriteBytes(0x5001, [7, 8]);
        memory.WritesBeforeFailure = 2;
        var feature = new TrainerFeature("test", "测试", null, ["iEnable+1", "iEnable+2"], null, "0x02");
        Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature));
        Assert.Equal(new byte[] { 7, 8 }, memory.ReadBytes(0x5001, 2));
        Assert.Equal(0, controller.PendingToggleRestoreCount);
    }

    [Fact]
    public void UnboundedZoomRejectsEnableButAllowsRecovery()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("Zoom", "无限缩放", null, ["iEnable+F", "iEnable+10"], null, null);
        var error = Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(feature, true));
        Assert.Contains("上下限", error.Message);
        // FeatureSafetyPolicy.Validate 是纯判据、不碰内存：它之后读 0x500F 恒为 0，等于没读。
        // “拒绝启用”必须走产品真正用的 controller.SetToggle 路径，并断言一个字节都没写。
        var rejected = Assert.Throws<InvalidOperationException>(() => controller.SetToggle(feature, true));
        Assert.Contains("拒绝", rejected.Message);
        Assert.Equal(0, memory.ReadByte(0x500F));
        Assert.Equal(0, memory.ReadByte(0x5010));
        memory.WriteBytes(0x500F, [1, 1]);
        FeatureSafetyPolicy.Validate(feature, false);
        controller.SetToggle(feature, false);
        Assert.Equal(0, memory.ReadByte(0x500F));
        Assert.Equal(0, memory.ReadByte(0x5010));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterWriteFailureRestoresEntirePreviousSettings(bool reinforcement)
    {
        var memory = new ParameterFaultMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        controller.WriteResourceValues(ResourceValueSettings.Default);
        controller.WriteReinforcementSettings(ReinforcementSettings.Default);
        var baseline = memory.ReadBytes(0x5000, 256);
        memory.WritesBeforeFailure = 2;

        Assert.Throws<InvalidOperationException>(() =>
        {
            if (reinforcement) controller.WriteReinforcementSettings(new ReinforcementSettings(123, 2, 1));
            else controller.WriteResourceValues(new ResourceValueSettings(42, 43, 2));
        });

        Assert.Equal(baseline, memory.ReadBytes(0x5000, 256));
        Assert.Equal(0, controller.PendingToggleRestoreCount);
    }

    [Fact]
    public void SynchronousActionCannotOverwritePendingDispatch()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");
        var rejected = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature));
        // 同步入口对坐标动作先拒：“根链”两字只是碰巧出现在坐标拒绝文案里，包含匹配证明不了任何
        // 待派发/根链条件。这里断言原文，把它钉死为坐标拒绝。
        Assert.Equal("坐标动作必须通过异步两阶段根链校验接口；已拒绝同步派发，未写入。", rejected.Message);
        Assert.Contains(rejected.Message, safetyMessages);
        Assert.Equal(0, memory.WriteCount);
        memory.WriteBytes(0x5020, [0x0C]); // Existing game-side pending dispatch, not an authorized reinforcement.
        var baseline = memory.ReadBytes(0x5000, 256);
        var writes = memory.WriteCount;
        Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature, new ReinforcementSettings(123, 1, 0)));
        Assert.Equal(baseline, memory.ReadBytes(0x5000, 256));
        Assert.Equal(writes, memory.WriteCount);
    }

    private sealed class ParameterFaultMemory : IProcessMemory
    {
        private readonly FakeProcessMemory _inner = new();
        public int WritesBeforeFailure { get; set; } = -1;
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            if (--WritesBeforeFailure == 0)
            {
                _inner.WriteBytes(address, bytes[..1]);
                throw new InvalidOperationException("模拟部分写入失败");
            }
            _inner.WriteBytes(address, bytes);
        }
    }

    [Fact]
    public async Task PendingActionKeepsItsDispatchAndReinforcementParameters()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");
        var error = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature, new ReinforcementSettings(0x11111111, 2, 1)));
        // 同步入口对坐标动作先拒：这句里的“根链”只是坐标拒绝文案的一部分，包含匹配证明不了任何
        // 待派发/根链条件（本用例真正有效的是后面 0x5020 预置 + TimedOut 的检查）。断言原文把它钉死。
        Assert.Equal("坐标动作必须通过异步两阶段根链校验接口；已拒绝同步派发，未写入。", error.Message);
        Assert.Contains(error.Message, safetyMessages);
        Assert.Equal(0, memory.WriteCount);
        memory.WriteBytes(0x5020, [0x0C]);
        memory.WriteBytes(0x5024, BitConverter.GetBytes(0x11111111u));
        var before = memory.ReadBytes(0x5020, 16);
        var writes = memory.WriteCount;

        var result = await controller.TriggerActionAndWaitForConsumptionAsync(feature,
            new ReinforcementSettings(0x22222222, 3, 2));

        Assert.Equal(ActionDispatchResult.TimedOut, result);
        Assert.Equal(before, memory.ReadBytes(0x5020, 16));
        Assert.Equal(writes, memory.WriteCount);
    }

    [Fact]
    public async Task ConcurrentActionsDoNotOverwriteUnconsumedDispatch()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var first = new TrainerFeature("first", "动作一", null, [], "MustCode2+400", "0x05");
        var second = new TrainerFeature("second", "动作二", null, [], "MustCode2+500", "0x06");
        Task<ActionDispatchResult>? secondTask = null;
        var secondDispatched = false;

        var firstResult = await controller.TriggerActionAndWaitForConsumptionAsync(first, onDispatched: () =>
        {
            secondTask = controller.TriggerActionAndWaitForConsumptionAsync(second, onDispatched: () =>
            {
                secondDispatched = true;
                memory.WriteBytes(0x5020, [0]);
            });
            Assert.False(secondDispatched);
            Assert.Equal(5, memory.ReadByte(0x5020));
            memory.WriteBytes(0x5020, [0]);
        });

        Assert.Equal(ActionDispatchResult.Consumed, firstResult);
        Assert.NotNull(secondTask);
        Assert.Equal(ActionDispatchResult.Consumed, await secondTask!);
        Assert.True(secondDispatched);
    }

    [Fact]
    public async Task AlreadyCanceledActionDoesNotWriteDispatchOrSettings()
    {
        var memory = new FakeProcessMemory();
        var controller = new FeatureController(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.TriggerActionAndWaitForConsumptionAsync(feature, cancellationToken: cancellation.Token));

        Assert.Equal(new byte[16], memory.ReadBytes(0x5020, 16));
    }

    [Fact]
    public void IsToggleFeatureRequiresEnableFlagsWithoutValueHint()
    {
        var zoom = new TrainerFeature("Zoom", "Zoom", "Ctrl+F8", ["iEnable+F", "iEnable+10"], null, null);
        var danger = new TrainerFeature("Danger Level MIN", "Danger Level MIN", "#190", ["iEnable+13"], null, "0x02");

        Assert.True(FeatureController.IsToggleFeature(zoom));
        Assert.False(FeatureController.IsToggleFeature(danger));
        Assert.True(FeatureController.IsActionFeature(danger));
    }

    [Fact]
    public void ToggleWritesAllEnableFlags()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("Zoom", "Zoom", "Ctrl+F8", ["iEnable+F", "iEnable+10"], null, null);

        if (feature.RawName == "Zoom") SeedCamera(memory, resolver);
        controller.SetToggle(feature, true);

        Assert.Equal(1, memory.ReadByte(0x500F));
        Assert.Equal(1, memory.ReadByte(0x5010));

        controller.SetToggle(feature, false);

        Assert.Equal(0, memory.ReadByte(0x500F));
        Assert.Equal(0, memory.ReadByte(0x5010));
    }

    [Fact]
    public void FreeBuildRefusesRemoteBytePatchEnableButAllowsRecovery()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x7000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature(
            "Free Build",
            "建筑物可随地建造",
            "L",
            [],
            null,
            null,
            [new TrainerFeatureBytePatch("MustCode+1201", [0xEB, 0x0C], [0x75, 0x0C])]);

        var error = Assert.Throws<InvalidOperationException>(() => controller.SetToggle(feature, true));
        Assert.Contains("根链", error.Message);
        Assert.Contains(error.Message, safetyMessages);
        Assert.Equal(0, memory.WriteCount);

        controller.SetToggle(feature, false);

        Assert.Equal(0x75, memory.ReadByte(0x8201));
        Assert.Equal(0x0C, memory.ReadByte(0x8202));
    }

    [Fact]
    public void TriggerActionWritesDispatchValueToIEnable20()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        SelectedObjectSafetyTests.Seed(memory, resolver);
        var feature = new TrainerFeature("Select Unit HP MAX", "Select Unit HP MAX", "#219", [], "MustCode2+400", "0x05");

        controller.TriggerAction(feature);

        Assert.Equal(0x05, memory.ReadByte(0x5020));
    }

    [Fact]
    public void RemoteStateLayoutMatchesBootstrapOffsets()
    {
        Assert.Equal("iEnable+20", RemoteStateLayout.ActionDispatch);
        Assert.Equal("iEnable+24", RemoteStateLayout.ReinforcementUnitId);
        Assert.Equal("iEnable+28", RemoteStateLayout.ReinforcementCount);
        Assert.Equal("iEnable+2C", RemoteStateLayout.ReinforcementRank);
        Assert.Equal("iEnable+30", RemoteStateLayout.MoneyAmount);
        Assert.Equal("iEnable+34", RemoteStateLayout.PowerValue);
        Assert.Equal("iEnable+38", RemoteStateLayout.ScPointValue);
        Assert.Equal("ra3_1.12.game+8E9838", RemoteStateLayout.SelectedUnitCode);
    }

    [Fact]
    public void ReadActionDispatchReadsIEnable20()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        memory.WriteBytes(0x5020, [0x0C]);

        var value = controller.ReadActionDispatch();

        Assert.Equal(0x0C, value);
    }

    [Fact]
    public async Task TriggerActionAndWaitForConsumptionReturnsConsumedWhenDispatchIsCleared()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        SelectedObjectSafetyTests.Seed(memory, resolver);
        var feature = new TrainerFeature("Select Unit HP MAX", "Select Unit HP MAX", "#219", [], "MustCode2+400", "0x05");

        var result = await controller.TriggerActionAndWaitForConsumptionAsync(
            feature,
            reinforcementSettings: null,
            timeout: TimeSpan.FromMilliseconds(500),
            pollInterval: TimeSpan.FromMilliseconds(10),
            onDispatched: () =>
            {
                memory.WriteBytes(0x70004, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
                memory.WriteBytes(0x7000C, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
                memory.WriteBytes(0x5020, [0x00]);
            });

        Assert.Equal(ActionDispatchResult.Consumed, result);
        Assert.Equal(0x00, memory.ReadByte(0x5020));
    }

    [Fact]
    public async Task TriggerActionAndWaitForConsumptionReturnsTimedOutWhenDispatchStaysPending()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        SelectedObjectSafetyTests.Seed(memory, resolver);
        var feature = new TrainerFeature("Select Unit HP MAX", "Select Unit HP MAX", "#219", [], "MustCode2+400", "0x05");

        var result = await controller.TriggerActionAndWaitForConsumptionAsync(
            feature,
            reinforcementSettings: null,
            timeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(5));

        Assert.Equal(ActionDispatchResult.TimedOut, result);
        Assert.Equal(0x05, memory.ReadByte(0x5020));
    }

    [Fact]
    public async Task MissingObjectRootRefusesFlagActionWithoutWrites()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("Danger Level MIN", "Danger Level MIN", "#190", ["iEnable+13"], null, "0x02");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(
            feature,
            reinforcementSettings: null,
            timeout: TimeSpan.FromMilliseconds(20),
            pollInterval: TimeSpan.FromMilliseconds(5)));

        Assert.Contains("根链", error.Message);
        Assert.Contains(error.Message, safetyMessages);
        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(0, memory.ReadByte(0x5013));
        Assert.Equal(0x00, memory.ReadByte(0x5020));
    }

    [Fact]
    public async Task MissingWorldRootRefusesReinforcementBeforeSettingsOrDispatch()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");
        var settings = new ReinforcementSettings(0x12345678, 12, 2);

        // 同步入口对坐标动作先拒：这句“根链”来自坐标拒绝文案，证明不了“世界根缺失被检查过”。
        // 断言原文，把它钉死成坐标拒绝。
        var syncRejection = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature, settings));
        Assert.Equal("坐标动作必须通过异步两阶段根链校验接口；已拒绝同步派发，未写入。", syncRejection.Message);
        Assert.Contains(syncRejection.Message, safetyMessages);

        // 真正的“缺世界根”只有在异步两阶段路径里才走得到：鼠标世界根 ra3_1.12.game+8DAEFC 未 seed，
        // SelectedObjectGuard 读到空根指针而拒 —— 这正是本用例标题要覆盖的路径与理由。
        var rootError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.TriggerActionAndWaitForConsumptionAsync(feature, settings));
        Assert.Contains("对象指针为空、越界或未对齐", rootError.Message);

        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(new byte[16], memory.ReadBytes(0x5020, 16));
    }

    [Fact]
    public void WriteResourceValuesWritesRuntimeParameters()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var settings = new ResourceValueSettings(123456, 234567, 9);

        controller.WriteResourceValues(settings);

        Assert.Equal(BitConverter.GetBytes(123456), memory.ReadBytes(0x5030, 4));
        Assert.Equal(BitConverter.GetBytes(234567), memory.ReadBytes(0x5034, 4));
        Assert.Equal(BitConverter.GetBytes(9), memory.ReadBytes(0x5038, 4));
    }

    [Fact]
    public void ReadSelectedUnitCodeReadsBlueprintHashFromTargetModule()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint>());
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        memory.WriteBytes(0xCE9838, [0xA0, 0xA5, 0x86, 0x65]);

        var code = controller.ReadSelectedUnitCode();

        Assert.Equal(0x6586A5A0u, code);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0x6586A5A0, 0, 3)]
    [InlineData(0x6586A5A0, 51, 3)]
    [InlineData(0x6586A5A0, 8, 4)]
    public void ReinforcementSettingsRejectsInvalidValues(uint unitId, int count, int rank)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReinforcementSettings(unitId, count, rank));
    }

    [Theory]
    [InlineData("0x6586A5A0", "8", "3", 0x6586A5A0u, 8, 3)]
    [InlineData("6586A5A0", "1", "0", 0x6586A5A0u, 1, 0)]
    [InlineData(" 0x12345678 ", "50", "2", 0x12345678u, 50, 2)]
    public void ReinforcementSettingsParsesUiInput(
        string unitIdText,
        string countText,
        string rankText,
        uint unitId,
        int count,
        int rank)
    {
        var settings = ReinforcementSettings.Parse(unitIdText, countText, rankText);

        Assert.Equal(unitId, settings.UnitId);
        Assert.Equal(count, settings.Count);
        Assert.Equal(rank, settings.Rank);
    }

    [Theory]
    [InlineData("", "8", "3")]
    [InlineData("xyz", "8", "3")]
    [InlineData("0x6586A5A0", "many", "3")]
    [InlineData("0x6586A5A0", "8", "max")]
    public void ReinforcementSettingsRejectsInvalidUiInput(string unitIdText, string countText, string rankText)
    {
        Assert.Throws<FormatException>(() => ReinforcementSettings.Parse(unitIdText, countText, rankText));
    }

    [Fact]
    public void MissingObjectRootRefusesValueHintFlagWithoutWrites()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("Danger Level MIN", "Danger Level MIN", "#190", ["iEnable+13"], null, "0x02");

        var error = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(feature));
        Assert.Contains("根链", error.Message);
        Assert.Contains(error.Message, safetyMessages);
        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(0, memory.ReadByte(0x5013));
        Assert.Equal(0x00, memory.ReadByte(0x5020));
    }

    [Fact]
    public void ResetClearsToggleFlags()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        var feature = new TrainerFeature("Zoom", "Zoom", "Ctrl+F8", ["iEnable+F", "iEnable+10"], null, null);
        if (feature.RawName == "Zoom") SeedCamera(memory, resolver);
        controller.SetToggle(feature, true);

        controller.Reset(feature);

        Assert.Equal(0, memory.ReadByte(0x500F));
        Assert.Equal(0, memory.ReadByte(0x5010));
    }

    [Fact]
    public void ResetClearsDispatchAndActionFlags()
    {
        var memory = new FakeProcessMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 });
        var controller = new FeatureController(memory, resolver);
        var safetyMessages = new List<string>();
        controller.SafetyMessage += safetyMessages.Add;
        SelectedObjectSafetyTests.Seed(memory, resolver);
        var dispatch = new TrainerFeature("Select Unit HP MAX", "Select Unit HP MAX", "#219", [], "MustCode2+400", "0x05");
        var flagAction = new TrainerFeature("Danger Level MIN", "Danger Level MIN", "#190", ["iEnable+13"], null, "0x02");
        controller.TriggerAction(dispatch);
        var writes = memory.WriteCount;
        var refusal = Assert.Throws<InvalidOperationException>(() => controller.TriggerAction(flagAction));
        Assert.Contains("根链", refusal.Message);
        Assert.Contains(refusal.Message, safetyMessages);
        Assert.Equal(writes, memory.WriteCount);
        memory.WriteBytes(0x5013, [2]); // Seed an existing flag so recovery remains covered.

        controller.Reset(dispatch);
        controller.Reset(flagAction);

        Assert.Equal(0, memory.ReadByte(0x5020));
        Assert.Equal(0, memory.ReadByte(0x5013));
    }

}
