using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class WorldPositionOptInTests
{
    [Fact]
    public void DefaultsAndLegacyConstructorRequireExplicitOptIn()
    {
        Assert.False(TrainerAppSettings.Default.AllowUnverifiedWorldPositionFeatures);
        Assert.False(new TrainerAppSettings("", "-win", 30).AllowUnverifiedWorldPositionFeatures);
        Assert.Equal("-win -xres 1920 -yres 1080", TrainerAppSettings.Default.LauncherArguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsRoundTripPreservesExplicitChoice(bool allow)
    {
        var path = Path.Combine(Path.GetTempPath(), $"round5-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TrainerAppSettingsStore(path);
            store.Save(new TrainerAppSettings("game.exe", "-win", 30, ResourceValueSettings.Default,
                [], AllowUnverifiedWorldPositionFeatures: allow));
            Assert.Equal(allow, new TrainerAppSettingsStore(path).Load().AllowUnverifiedWorldPositionFeatures);
            Assert.Contains("\"AllowUnverifiedWorldPositionFeatures\": " + allow.ToString().ToLowerInvariant(), File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AllowUnverifiedWorldPositionFeatures\":\"true\"}")]
    [InlineData("{\"AllowUnverifiedWorldPositionFeatures\":1}")]
    [InlineData("{\"AllowUnverifiedWorldPositionFeatures\":null}")]
    public void MissingOrMalformedChoiceRemainsOff(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"round5-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);
            Assert.False(new TrainerAppSettingsStore(path).Load().AllowUnverifiedWorldPositionFeatures);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("Zoom")]
    [InlineData("Free Build")]
    [InlineData("Get Me Base")]
    [InlineData("We Need Back")]
    [InlineData("Select Unit Copy For Me")]
    public void AllFiveRequireOptInAndOnlyVerifiedCameraCanEnable(string rawName)
    {
        var feature = TrainerFeatureCatalog.CreateUiFeatures(TestAssets.LoadManifest().Features)
            .Single(f => f.RawName == rawName);
        var memory = new CountingMemory();
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000, ["MustCode"] = 0x10000 });
        if (rawName == "Zoom") memory.SeedCamera(resolver);
        var controller = new FeatureController(memory, resolver);
        void Execute(bool allow)
        {
            FeatureSafetyPolicy.Validate(feature, true, allow);
            if (FeatureController.IsToggleFeature(feature)) controller.SetToggle(feature, true);
            else controller.TriggerAction(feature);
        }

        Assert.True(FeatureSafetyPolicy.RequiresWorldPositionOptIn(feature));
        var error = Assert.Throws<InvalidOperationException>(() => Execute(false));
        Assert.Contains("暂受限制", error.Message);        // 默认关闭的高风险功能：先拦下
        Assert.Contains("校验", error.Message);             // 讲清为什么被拦（缺哪个前置条件）
        Assert.Contains("「诊断」页", error.Message);       // 开在哪里必须写清，不然用户找不到
        Assert.Contains("仍会做安全检查", error.Message);    // 开了也不等于无条件放行
        Assert.Equal(0, memory.WriteCount);
        if (rawName != "Zoom")
        {
            var logs = new List<string>();
            controller.SafetyMessage += logs.Add;
            var refusal = Assert.Throws<InvalidOperationException>(() => Execute(true));
            Assert.Contains("根链", refusal.Message);
            Assert.Contains("拒绝", refusal.Message);
            Assert.Equal(refusal.Message, Assert.Single(logs));
            Assert.Equal(0, memory.WriteCount);
            Assert.Equal(0, memory.DispatchCount);
            return;
        }
        Execute(true);
        Assert.True(memory.WriteCount > 0);
        if (FeatureController.IsToggleFeature(feature))
        {
            foreach (var flag in feature.EnableFlags) Assert.Equal(1, memory.ReadBytes(resolver.Resolve(flag), 1)[0]);
            foreach (var patch in feature.ToggleBytePatches ?? [])
                Assert.Equal(patch.EnabledBytes, memory.ReadBytes(resolver.Resolve(patch.Address), patch.EnabledBytes.Length));
            FeatureSafetyPolicy.Validate(feature, false, false);
            controller.SetToggle(feature, false);
            foreach (var flag in feature.EnableFlags) Assert.Equal(0, memory.ReadBytes(resolver.Resolve(flag), 1)[0]);
            foreach (var patch in feature.ToggleBytePatches ?? [])
                Assert.Equal(patch.DisabledBytes, memory.ReadBytes(resolver.Resolve(patch.Address), patch.DisabledBytes.Length));
        }
        else
        {
            Assert.Equal(Convert.ToByte(feature.ValueHint![2..], 16), memory.ReadBytes(resolver.Resolve(RemoteStateLayout.ActionDispatch), 1)[0]);
        }
        Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(feature, true, false));
    }

    [Fact]
    public void RiskMarkerSelectsOriginalFiveAndAllObjectFeatures()
    {
        var marked = TrainerFeatureCatalog.CreateUiFeatures(TestAssets.LoadManifest().Features)
            .Where(FeatureSafetyPolicy.RequiresWorldPositionOptIn).Select(f => f.RawName).Order().ToArray();
        Assert.Equal(new[] { "Zoom", "Free Build", "Get Me Base", "We Need Back", "Select Unit Copy For Me", "Select Unit Level UP", "Select Unit Super Speed", "Select Unit Slow Speed", "Select Unit Freeze", "Restore Select Unit Speed", "Select Unit HP MAX", "Select Unit HP MIN", "Restore Select Unit Normal HP", "Select Unit Ammo MAX", "Select Unit Change ID", "Destory Select Unit", "Restore Select Ore Mine", "Danger Level MAX", "Danger Level MIN", "Restore Danger Level Normal" }.Order(), marked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueChecksOptInBeforeAnyParameterOrDispatchWrite(bool allow)
    {
        var memory = new CountingMemory(consume: true);
        var controller = Controller(memory);
        var logs = new List<string>();
        controller.SafetyMessage += logs.Add;
        Action validate = () => FeatureSafetyPolicy.Validate(Reinforcement, true, allow);
        var run = ReinforcementQueueRunner.ExecuteAsync([Entry, Entry], controller, Reinforcement,
            validateBeforeDispatch: validate);
        if (!allow)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Equal(0, memory.WriteCount);
        }
        else
        {
            var results = await run;
            Assert.Equal(2, results.Count);
            Assert.All(results, r => { Assert.Equal(ReinforcementQueueItemStatus.Failed, r.Status); Assert.Contains("根链", r.Message); });
            Assert.Equal(0, memory.DispatchCount);
            Assert.Equal(0, memory.WriteCount);
            Assert.Equal(results.Select(r => r.Message), logs);
        }
    }

    [Fact]
    public async Task RevokingOptInStopsRemainingQueueEntries()
    {
        var memory = new CountingMemory(consume: true);
        var controller = Controller(memory);
        var checks = 0;
        var logs = new List<string>();
        controller.SafetyMessage += logs.Add;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReinforcementQueueRunner.ExecuteAsync(
            [Entry, Entry], controller, Reinforcement,
            validateBeforeDispatch: () => FeatureSafetyPolicy.Validate(Reinforcement, true, ++checks <= 2)));
        Assert.Equal(0, memory.DispatchCount);
        Assert.Equal(0, memory.WriteCount);
        Assert.Contains("根链", Assert.Single(logs));
    }

    [Fact]
    public async Task WaitingActionRechecksChoiceAfterDispatchGateOpens()
    {
        var memory = new CountingMemory();
        var controller = Controller(memory);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.SeedSelectedObject();
        var gateHolder = new TrainerFeature("Select Unit HP MAX", "选中单位无限血量", null, [], "MustCode2+400", "0x05");
        var first = controller.TriggerActionAndWaitForConsumptionAsync(gateHolder,
            onDispatched: () => dispatched.SetResult());
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var allow = true;
        var second = controller.TriggerActionAndWaitForConsumptionAsync(Reinforcement,
            validateBeforeDispatch: () => FeatureSafetyPolicy.Validate(Reinforcement, true, allow));
        // 此刻 _dispatchGate 仍被 first 持有（0x5020 还没清），second 的调用会同步地卡在 _dispatchGate.WaitAsync
        // 上、还没跑 validateBeforeDispatch —— 断言它尚未完成，把“second 已排队等门”变成显式条件。
        // 若哪天 first 提前完成，这里会立刻炸，而不是让用例按错误时序随机地假绿。
        Assert.False(second.IsCompleted);
        allow = false;
        // 现在才放 first 过关（补好读回字段并清 0x5020）；second 拿到门后用 allow=false 重新校验并拒绝。
        memory.ApplyHealth();
        memory.WriteBytes(0x5020, [0]);
        Assert.Equal(ActionDispatchResult.Consumed, await first);
        var before = memory.WriteCount;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("暂受限制", error.Message);
        Assert.Equal(before, memory.WriteCount);
        Assert.Equal(1, memory.DispatchCount);
    }

    private static readonly TrainerFeature Reinforcement = new("We Need Back", "呼叫战场增援", "J", [], "MustCode2+B00", "0x0C");
    private static readonly ReinforcementQueueEntry Entry = new("测试", "0x11111111", "2", "1");
    private static FeatureController Controller(IProcessMemory memory) => new(memory,
        new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));

    private sealed class CountingMemory(bool consume = false) : IProcessMemory, IProbeMemory
    {
        public void SeedCamera(AddressResolver resolver) => FeatureControllerTests.SeedCamera(_inner, resolver);
        public void SeedSelectedObject() => SelectedObjectSafetyTests.Seed(_inner,
            new AddressResolver(0, new Dictionary<string, nint> { ["iEnable"] = 0x5000 }));
        public void ApplyHealth()
        {
            _inner.WriteBytes(0x70004, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
            _inner.WriteBytes(0x7000C, BitConverter.GetBytes(EmpiricalSafetyBounds.ModifiedHealth));
        }
        public Win32MemoryRegion? Query(nint address) => _inner.Query(address);
        private readonly FakeProcessMemory _inner = new();
        private readonly object _gate = new();
        // 计数在后台线程（async 续体里调 WriteBytes）持有 _gate 时自增，而测试线程会直接读它们。
        // 裸 auto-property 读没有顺序保证，读到的可能是不完整的写入 ⇒ 零写入断言会与后台写入赛跑。
        // 改成“读写都在同一把 _gate 下取快照”。
        private int _writeCount;
        private int _dispatchCount;
        public int WriteCount { get { lock (_gate) return _writeCount; } }
        public int DispatchCount { get { lock (_gate) return _dispatchCount; } }
        public byte[] ReadBytes(nint address, int count) { lock (_gate) return _inner.ReadBytes(address, count); }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                _writeCount++;
                _inner.WriteBytes(address, bytes);
                if (address == 0x5020 && bytes.Length == 1 && bytes[0] != 0)
                {
                    _dispatchCount++;
                    if (consume) _inner.WriteBytes(address, [0]);
                }
            }
        }
    }
}
