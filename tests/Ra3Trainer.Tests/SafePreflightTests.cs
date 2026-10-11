using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using System.Text.Json;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class SafePreflightTests
{
    public static IEnumerable<object[]> MissingRootCases =>
        from name in new[] { "We Need Back", "Get Me Base", "Select Unit Copy For Me", "Free Build" }
        from fault in new[] { "null", "unaligned", "unreadable", "out-of-range", "stale-copy", "screen-input", "unattributed-rewrite" }
        select new object[] { name, fault };

    [Theory]
    [MemberData(nameof(MissingRootCases))]
    public async Task MissingRootNeverUsesSelectedObjectOrStaleCoordinates(string name, string fault)
    {
        var memory = new CheckedMemory();
        // 坐标功能真正读的是“鼠标世界根”链（ra3_1.12.game+8DAEFC 及其字段），不是选中对象根 0x40138。
        // 夹具必须先把这条链 seed 成合法的，否则失败原因永远只是“根是空的”，跟注入的坐标毫无关系（旧版就是这个假绿）。
        var coordinate = name != "Free Build";
        if (coordinate) memory.SeedWorldRootChain();
        switch (fault)
        {
            case "null" when coordinate: memory.Word(memory.MouseWorldRoot, 0); break;
            case "unaligned" when coordinate: memory.Word(memory.MouseWorldRoot, 0xF0001); break;
            case "unreadable" when coordinate: memory.FaultAt = memory.MouseWorldRoot; break;
            // 世界坐标越界（> 经验上界 10000）：只有转换完成、epoch 推进后才会读到坐标缓冲这里。
            case "out-of-range" when coordinate: memory.Float(memory.Resolver.Resolve("MustCode+1080"), 10000.01f); break;
            case "null": memory.Word(0x40138, 0); break;
            case "unaligned": memory.Word(0x40138, 0x50001); break;
            case "unreadable": memory.FaultAt = 0x50038; break;
            case "out-of-range": memory.Float(0x50038, 8001); break;
            case "stale-copy" when coordinate:
                // 缓冲里躺着“看起来合理”的旧动作坐标：但没有本次转换的完成凭据 ⇒ 不能当新鲜的世界坐标用。
                memory.Float(memory.Resolver.Resolve("MustCode+1080"), 4000);
                memory.Float(memory.Resolver.Resolve("MustCode+1084"), 4000);
                memory.Float(memory.Resolver.Resolve("MustCode+1088"), 200);
                memory.DropConversionCredential = true;
                break;
            case "screen-input" when coordinate:
                // 真实屏幕整数也能被解码成有限、范围内的“小浮点”：没有完成凭据同样不能当世界坐标结果。
                memory.Word(memory.Resolver.Resolve("MustCode+1110"), 799);
                memory.Word(memory.Resolver.Resolve("MustCode+1114"), 458);
                memory.Word(memory.Resolver.Resolve("MustCode+111C"), 0x0CD09A98);
                memory.DropConversionCredential = true;
                break;
            case "unattributed-rewrite" when coordinate:
                // 只有归属不明的重写：没有请求身份与成功结果，替换掉哨兵不构成本次动作新鲜性的证据。
                foreach (var offset in new[] { "1080", "1084", "1088" })
                {
                    var address = memory.Resolver.Resolve("MustCode+" + offset);
                    memory.Float(address, float.NaN);
                    memory.Float(address, 200);
                }
                memory.DropConversionCredential = true;
                break;
            case "stale-copy":
            case "screen-input":
            case "unattributed-rewrite":
                break; // Free Build 与坐标无关：这些坐标注入对它没有意义，跳过。
        }
        var controller = new FeatureController(memory, memory.Resolver);
        var directory = Path.Combine(Path.GetTempPath(), "round16-refusal-" + Guid.NewGuid().ToString("N"));
        string? logPath = null;
        try
        {
            using var log = TrainerSessionLog.Open(directory);
            logPath = log.FilePath;
            Assert.True(log.IsAvailable);
            controller.SafetyMessage += message => log.Write("feature-validation", new { message });
            var feature = Feature(name);
            FeatureSafetyPolicy.Validate(feature, true, true);
            var dispatched = false;
            var error = name == "Free Build"
                ? Assert.Throws<InvalidOperationException>(() => controller.SetToggle(feature, true))
                : await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(
                    feature, name == "We Need Back" ? new ReinforcementSettings(ReinforcementSettings.DefaultUnitId, 2, 1) : null,
                    onDispatched: () => dispatched = true));
            Assert.Contains("拒绝", error.Message);
            // 断言“本条故障真的成为失败原因”，而不是任何一句碰巧含“根链”的文案。
            var expected = name == "Free Build" ? "会写入网格状态"
                : fault switch
                {
                    "null" or "unaligned" => "对象指针为空、越界或未对齐",
                    "unreadable" => "未提交、不可读或跨越内存区域",
                    "out-of-range" => "世界坐标",
                    _ => "完成凭据"
                };
            Assert.Contains(expected, error.Message);
            // 根链校验消息带“根链”；新鲜性/完成凭据的拒绝是根链校验之后的协议层抛的，不含“根链”。
            if (expected is not "完成凭据") Assert.Contains("根链", error.Message);
            // 坐标动作会先写“转换派发”(0x0E) 到动作槽：走到协议层的故障各记 1 次写；预检阶段就拒的是 0 次。
            var protocolReached = coordinate && (fault is "out-of-range" or "stale-copy" or "screen-input" or "unattributed-rewrite");
            Assert.Equal(protocolReached ? 1 : 0, memory.Writes);
            Assert.False(dispatched);
            log.Dispose();
            using var record = JsonDocument.Parse(Assert.Single(File.ReadAllLines(log.FilePath)));
            Assert.Equal("feature-validation", record.RootElement.GetProperty("event").GetString());
            Assert.Equal(error.Message, record.RootElement.GetProperty("data").GetProperty("message").GetString());
        }
        finally
        {
            if (logPath is not null) File.Delete(logPath);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("root-null")]
    [InlineData("root-unaligned")]
    [InlineData("vtable-null")]
    [InlineData("root-page")]
    [InlineData("current-page")]
    [InlineData("minimum-page")]
    [InlineData("maximum-page")]
    [InlineData("minimum-nan")]
    [InlineData("maximum-infinity")]
    [InlineData("reversed-limits")]
    // 2026-10-08：相机上下限的**绝对经验窗口 [40,800] 撤掉了**（它随 mod/分辨率变），只留结构关系
    // + 数值理智线。所以这两条改成"超理智线"，原来的 39 / 801 现在是合法值。
    [InlineData("minimum-below-sanity")]
    [InlineData("maximum-above-sanity")]
    public void CameraBrokenChainOrLimitsRejectWithZeroWritesAndLog(string fault)
    {
        var memory = new CheckedMemory();
        var root = memory.Resolver.Resolve("ra3_1.12.game+8DB7B4");
        switch (fault)
        {
            case "root-null": memory.Word(root, 0); break;
            case "root-unaligned": memory.Word(root, 0xE0001); break;
            case "vtable-null": memory.Word(0xE0000, 0); break;
            case "root-page": memory.FaultAt = root; break;
            case "current-page": memory.FaultAt = 0xE0044; break;
            case "minimum-page": memory.FaultAt = 0xE2700; break;
            case "maximum-page": memory.FaultAt = 0xE2704; break;
            case "minimum-nan": memory.Float(0xE2700, float.NaN); break;
            case "maximum-infinity": memory.Float(0xE2704, float.PositiveInfinity); break;
            case "reversed-limits": memory.Float(0xE2700, 800); memory.Float(0xE2704, 40); break;
            case "minimum-below-sanity": memory.Float(0xE2700, -1e10f); break;
            case "maximum-above-sanity": memory.Float(0xE2704, 1e10f); break;
        }
        var controller = new FeatureController(memory, memory.Resolver);
        var messages = new List<string>();
        controller.SafetyMessage += messages.Add;
        var error = Assert.Throws<InvalidOperationException>(() => controller.SetToggle(Feature("Zoom"), true));
        Assert.Contains("拒绝", error.Message);
        Assert.Equal(error.Message, Assert.Single(messages));
        Assert.Equal(0, memory.Writes);
    }

    [Theory]
    [InlineData("We Need Back")]
    [InlineData("Get Me Base")]
    [InlineData("Select Unit Copy For Me")]
    [InlineData("Free Build")]
    [InlineData("Danger Level MAX")]
    [InlineData("Danger Level MIN")]
    public void MissingWorldOrPlacementRootRejectsWithZeroWritesAndChineseLog(string name)
    {
        var memory = new CheckedMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        var logs = new List<string>();
        controller.SafetyMessage += logs.Add;
        var feature = Feature(name);
        FeatureSafetyPolicy.Validate(feature, true, true);
        var error = Assert.Throws<InvalidOperationException>(() => Execute(controller, feature));
        Assert.Contains("根链", error.Message);
        Assert.Equal(0, memory.Writes);
        Assert.Single(logs);
        Assert.Contains("拒绝", logs[0]);
    }

    [Theory]
    [InlineData(0x20050)]
    [InlineData(0x30008)]
    [InlineData(0x40138)]
    [InlineData(0x50004)]
    [InlineData(0x5033C)]
    public void EverySelectionHopNullRejectsBeforeAnyWrite(int field)
    {
        var memory = new CheckedMemory();
        memory.Word(field, 0);
        Reject(memory, "Select Unit HP MAX");
    }

    [Theory]
    [InlineData(0x20000, 0x2000, 4, 0x1000)] // reserved, not committed
    [InlineData(0x20000, 0x1000, 0x104, 0x1000)] // guard
    [InlineData(0x20000, 0x1000, 1, 0x1000)] // no access
    [InlineData(0x20000, 0x1000, 0x10, 0x1000)] // execute only
    [InlineData(0x20000, 0x1000, 4, 2)] // short region
    [InlineData(0x70004, 0x1000, 4, 2)] // field straddles region
    public void QueryMustCoverEveryReadableCommittedField(int field, int state, int protection, int size)
    {
        var memory = new CheckedMemory { FaultAt = field,
            FaultRegion = new(field, (nuint)size, (uint)protection, (uint)state, (uint)protection, 0x20000) };
        Reject(memory, "Select Unit HP MAX");
    }

    [Theory]
    // 2026-10-08：血量/单位坐标/速度这些"按 mod、地图、建筑种类变"的字段**不再用游戏经验窗口**，
    // 只留数值理智线（NumericSanityCeiling=1e9）。所以这里的"越界"取值要改成超理智线的值 ——
    // 旧取值（100001 / 10000.01 / 500.01）现在**是合法的**，留着就会变成"用旧前提撞测试"（每次改边界都要走一遍这个动作）。
    [InlineData(0x70004, 0f)]              // 0 血量 = 死亡，仍然必须拒（不是经验窗口，是语义）
    [InlineData(0x7000C, 1e10f)]           // 超理智线
    [InlineData(0x70010, float.NaN)]
    [InlineData(0x50038, -1e10f)]
    [InlineData(0x5003C, 1e10f)]
    [InlineData(0x50040, float.PositiveInfinity)]
    [InlineData(0xA0008, 1e10f)]
    [InlineData(0xA0040, -0.01f)]
    [InlineData(0xB0018, 2f)]
    public void OutOfRangeValuesRejectBeforeAnyWrite(int field, float value)
    {
        var memory = new CheckedMemory();
        memory.Float(field, value);
        Reject(memory, field >= 0xA0000 ? "Select Unit Super Speed" : "Select Unit HP MAX");
    }

    [Theory]
    [InlineData(1f, -1f, 0f)]
    [InlineData(100000f, 10000f, 500f)]
    [InlineData(125000f, 10000f, 500f)]   // 真机日志里那个满血建筑（读到 125000，边界 [1, 100000] 时被误拒）
    [InlineData(5_000_000f, 25000f, 900f)]   // 大到"我们没测过"的量级也必须合法：这正是"别再给按对象变化的值定经验上界"的理由
    public void InclusiveBoundsAllowOriginalSingleByteDispatch(float health, float coordinate, float speed)
    {
        var memory = new CheckedMemory();
        foreach (var field in new[] { 0x70004, 0x7000C, 0x70010 }) memory.Float(field, health);
        foreach (var field in new[] { 0x50038, 0x5003C, 0x50040 }) memory.Float(field, coordinate);
        memory.Float(0xA0008, speed);
        memory.Float(0xA0040, speed);
        var controller = new FeatureController(memory, memory.Resolver);
        controller.TriggerAction(Feature("Select Unit Super Speed"));
        Assert.Equal(1, memory.Writes);
        Assert.Equal((nint)0x10020, memory.LastAddress);
        Assert.Equal(new byte[] { 1 }, memory.LastBytes);
    }

    [Fact]
    public void HighX86PointerRequiresReadableDestinationAndIsAccepted()
    {
        var memory = new CheckedMemory();
        memory.Word(0x30008, 0xAD000000);
        memory.Word(unchecked((nint)(nuint)0xAD000138u), 0x50000);
        new FeatureController(memory, memory.Resolver).TriggerAction(Feature("Select Unit HP MAX"));
        Assert.Equal(1, memory.Writes);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(86u)]
    [InlineData(uint.MaxValue)]
    public void SelectionCountOnlyRequiresNonemptyHeadAndNeverControlsTraversal(uint count)
    {
        var memory = new CheckedMemory();
        memory.Word(0x2005C, count);
        if (count == 0) Reject(memory, "Select Unit HP MAX");
        else
        {
            new FeatureController(memory, memory.Resolver).TriggerAction(Feature("Select Unit HP MAX"));
            Assert.Equal(1, memory.Writes);
            Assert.Equal(new byte[] { 5 }, memory.LastBytes);
        }
    }

    [Theory]
    [InlineData(40f)]
    [InlineData(800f)]
    public void CameraBoundsPassAndFlagsAreReadBack(float zoom)
    {
        var memory = new CheckedMemory();
        memory.Float(0xE0044, zoom);
        var feature = Feature("Zoom");
        new FeatureController(memory, memory.Resolver).SetToggle(feature, true);
        Assert.Equal(2, memory.Writes);
        Assert.All(feature.EnableFlags, f => Assert.Equal(1, memory.ReadBytes(memory.Resolver.Resolve(f), 1)[0]));
    }

    [Theory]
    [InlineData(39f)]
    [InlineData(801f)]
    [InlineData(float.NaN)]
    public void CameraOutsideRangeRejectsWithoutFlagWrite(float zoom)
    {
        var memory = new CheckedMemory();
        memory.Float(0xE0044, zoom);
        Reject(memory, "Zoom");
    }

    [Fact]
    public void SilentToggleWriteFailureReportsFailedReadback()
    {
        var memory = new CheckedMemory { IgnoreWrites = true };
        var error = Assert.Throws<InvalidOperationException>(() => new FeatureController(memory, memory.Resolver).SetToggle(Feature("Zoom"), true));
        Assert.Contains("读回", error.Message);
        Assert.True(memory.Writes > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthActionConsumptionOnlySucceedsWithTargetReadback(bool apply)
    {
        var memory = new CheckedMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        var log = new List<string>();
        controller.SafetyMessage += log.Add;
        var task = controller.TriggerActionAndWaitForConsumptionAsync(Feature("Select Unit HP MAX"), onDispatched: () =>
        {
            memory.Word(0x10020, 0);
            if (apply)
            {
                memory.Float(0x70004, EmpiricalSafetyBounds.ModifiedHealth);
                memory.Float(0x7000C, EmpiricalSafetyBounds.ModifiedHealth);
            }
        });
        if (apply) Assert.Equal(ActionDispatchResult.Consumed, await task);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            Assert.Contains("写后确认失败", error.Message);
            Assert.Single(log);
        }
        Assert.Equal(1, memory.Writes); // no attempt to roll back action
    }

    [Fact]
    public void ReaderWithoutQueryCapabilityFailsClosed()
    {
        var raw = new WriteOnlyFixture();
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint> { ["iEnable"] = 0x10000 });
        var error = Assert.Throws<InvalidOperationException>(() => new FeatureController(raw, resolver).TriggerAction(Feature("Select Unit HP MAX")));
        Assert.Contains("页面保护查询", error.Message);
        Assert.Equal(0, raw.Writes);
    }

    [Theory]
    [InlineData("Select Unit Super Speed", 500f, false)]
    [InlineData("Select Unit Super Speed", 500f, true)]
    [InlineData("Select Unit Slow Speed", 10f, false)]
    [InlineData("Select Unit Slow Speed", 10f, true)]
    [InlineData("Select Unit Freeze", 0f, false)]
    [InlineData("Select Unit Freeze", 0f, true)]
    [InlineData("Restore Select Unit Speed", 65f, false)]
    [InlineData("Restore Select Unit Speed", 65f, true)]
    [InlineData("Restore Select Unit Normal HP", 100f, false)]
    [InlineData("Restore Select Unit Normal HP", 100f, true)]
    [InlineData("Select Unit Change ID", 0f, false)]
    [InlineData("Select Unit Change ID", 0f, true)]
    public async Task EveryReadableActionConfirmsTargetsWithoutRollback(string name, float expected, bool apply)
    {
        var memory = new CheckedMemory();
        var ownership = name == "Select Unit Change ID";
        var health = name == "Restore Select Unit Normal HP";
        memory.Word(0x50418, 0xD0000);
        memory.Word(memory.Resolver.Resolve("ID"), 0xD1000);
        if (health) memory.Float(0x70004, 1);
        if (name == "Restore Select Unit Speed") memory.Float(0xA0008, 500);
        var controller = new FeatureController(memory, memory.Resolver);
        var task = controller.TriggerActionAndWaitForConsumptionAsync(Feature(name), onDispatched: () =>
        {
            memory.Word(0x10020, 0);
            if (!apply) return;
            if (ownership) memory.Word(0x50418, 0xD1000);
            else if (health)
            {
                memory.Float(0x70004, expected);
                memory.Float(0x7000C, expected);
            }
            else memory.Float(0xA0008, expected);
        });
        if (apply) Assert.Equal(ActionDispatchResult.Consumed, await task);
        else Assert.Contains("写后确认失败", (await Assert.ThrowsAsync<InvalidOperationException>(() => task)).Message);
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public async Task IdentityChangeAfterConsumptionCannotConfirmStaleTarget()
    {
        var memory = new CheckedMemory();
        var controller = new FeatureController(memory, memory.Resolver);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.TriggerActionAndWaitForConsumptionAsync(
            Feature("Select Unit HP MAX"), onDispatched: () =>
            {
                memory.Word(0x10020, 0);
                memory.Float(0x70004, EmpiricalSafetyBounds.ModifiedHealth);
                memory.Float(0x7000C, EmpiricalSafetyBounds.ModifiedHealth);
                memory.Word(0x40138, 0x51000);
            }));
        Assert.Contains("身份变化", error.Message);
        Assert.Equal(1, memory.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveSecondMovementComponentMustAlsoMatchReadback(bool applySecond)
    {
        var memory = new CheckedMemory();
        memory.Float(0xB0018, 1);
        memory.Float(0xB0008, 65);
        memory.Float(0xB0040, 65);
        var task = new FeatureController(memory, memory.Resolver).TriggerActionAndWaitForConsumptionAsync(
            Feature("Select Unit Super Speed"), onDispatched: () =>
            {
                memory.Word(0x10020, 0);
                memory.Float(0xA0008, 500);
                if (applySecond) memory.Float(0xB0008, 500);
            });
        if (applySecond) Assert.Equal(ActionDispatchResult.Consumed, await task);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(1, memory.Writes);
    }

    private static void Reject(CheckedMemory memory, string name)
    {
        Assert.Throws<InvalidOperationException>(() => Execute(new FeatureController(memory, memory.Resolver), Feature(name)));
        Assert.Equal(0, memory.Writes);
    }
    private static void Execute(FeatureController controller, TrainerFeature feature)
    {
        if (FeatureController.IsToggleFeature(feature)) controller.SetToggle(feature, true);
        else controller.TriggerAction(feature);
    }
    private static TrainerFeature Feature(string name) => TrainerFeatureCatalog.CreateUiFeatures(TestAssets.LoadManifest().Features).Single(f => f.RawName == name);
    private sealed class WriteOnlyFixture : IProcessMemory
    {
        public int Writes;
        public byte[] ReadBytes(nint address, int count) => new byte[count];
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) => Writes++;
    }
    private sealed class CheckedMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        public AddressResolver Resolver { get; } = new(0x400000, new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000, ["MustCode"] = 0x120000 });
        public int Writes;
        public nint LastAddress;
        public byte[] LastBytes = [];
        public nint? FaultAt;
        public Win32MemoryRegion? FaultRegion;
        public bool IgnoreWrites;
        /// <summary>鼠标世界根：坐标动作真正读的根链（不是选中对象根 0x40138）。</summary>
        public nint MouseWorldRoot => Resolver.Resolve("ra3_1.12.game+8DAEFC");
        /// <summary>注入“转换被静默丢弃”：清动作槽但不推进转换 epoch（= 没有完成凭据）。</summary>
        public bool DropConversionCredential;
        public CheckedMemory()
        {
            SelectedObjectSafetyTests.Seed(_inner, Resolver);
            Word(0x50374, 0x80000); Word(0x80200, 0x90000);
            Word(0x90000, 0xA0000); Word(0x90004, 0xB0000);
            Float(0xA0008, 65); Float(0xA0040, 65);
            Word(Resolver.Resolve("ra3_1.12.game+8DB7B4"), 0xE0000);
            Word(0xE0000, 0xF0000);
            Float(0xE2700, 40); Float(0xE2704, 800); Float(0xE0044, 800);
        }
        public void Word(nint address, uint value) => _inner.WriteBytes(address, BitConverter.GetBytes(value));
        public void Float(nint address, float value) => Word(address, BitConverter.SingleToUInt32Bits(value));
        /// <summary>把坐标动作真正读的“鼠标世界根”链 seed 成合法值（夹具合法的前提）。</summary>
        public void SeedWorldRootChain()
        {
            Word(MouseWorldRoot, 0xF0000);
            Word(0xF0000, 0x1000);
            Float(0xF003C, 0); Float(0xF0040, 0); Float(0xF0044, 0);
            Word(Resolver.Resolve("ID"), 0xD0000);
            Word(0xD0010, 0);
            Word(Resolver.Resolve("ra3_1.12.game+8E6C58"), 0xE0000);
            // COPY 还会走选中对象头模板（unit+0x418）：不给它一个合法指针，就会先在选中对象链上拒，
            // 掩盖坐标/凭据故障 —— 那正是本条要找出来的假绿。
            Word(0x50418, 0xD0000);
        }
        private uint ReadWord(string symbol) => BitConverter.ToUInt32(_inner.ReadBytes(Resolver.Resolve(symbol), 4));
        public Win32MemoryRegion? Query(nint address) => address == FaultAt ? FaultRegion : _inner.Query(address);
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Writes++; LastAddress = address; LastBytes = bytes.ToArray();
            if (!IgnoreWrites) _inner.WriteBytes(address, bytes);
            // 模拟游戏侧消费“转换派发”（写到 ActionDispatch 的 0x0E）：清动作槽；除非注入
            // “静默丢弃完成凭据”，否则推进转换 epoch。夹具自身的写入不计入 Writes。
            if (!IgnoreWrites && bytes.Length == 1 && bytes[0] == RemoteStateLayout.CoordinateConversionDispatch &&
                address == Resolver.Resolve(RemoteStateLayout.ActionDispatch))
            {
                _inner.WriteBytes(address, [0]);
                if (!DropConversionCredential)
                    Word(Resolver.Resolve(RemoteStateLayout.ConversionEpoch), ReadWord(RemoteStateLayout.ConversionEpoch) + 1);
            }
        }
    }
}
