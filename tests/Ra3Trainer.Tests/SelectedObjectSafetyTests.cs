using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class SelectedObjectSafetyTests
{
    public static IEnumerable<object[]> Features => new[]
    {
        "Select Unit Level UP", "Select Unit Super Speed", "Select Unit Slow Speed", "Select Unit Freeze",
        "Restore Select Unit Speed", "Select Unit HP MAX", "Select Unit HP MIN", "Restore Select Unit Normal HP",
        "Select Unit Ammo MAX", "Select Unit Ammo MAX 2", "Select Unit Change ID", "Destory Select Unit",
        "Restore Select Ore Mine"
    }.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Features))]
    public void DefaultPolicyRejectsEverySelectedFeatureAndMarksRisk(string name)
    {
        var feature = Feature(name);
        Assert.True(FeatureSafetyPolicy.RequiresWorldPositionOptIn(feature));
        var error = Assert.Throws<InvalidOperationException>(() => FeatureSafetyPolicy.Validate(feature, true));
        Assert.Contains("暂受限制", error.Message);
        Assert.Contains("「诊断」页", error.Message);
        FeatureSafetyPolicy.Validate(feature, false);
    }

    [Theory]
    [MemberData(nameof(Features))]
    public void OptInCannotBypassNullSelectionPointer(string name)
    {
        var memory = new ControlledMemory();
        memory.Inner.WriteBytes(memory.Resolver.Resolve("ra3_1.12.game+8DB73C"), new byte[4]);
        var feature = Feature(name);
        FeatureSafetyPolicy.Validate(feature, true, true);
        var error = Assert.Throws<InvalidOperationException>(() => Execute(memory, feature));
        Assert.Contains("校验未通过", error.Message);
        Assert.Equal(0, memory.Writes);
    }

    [Theory]
    [InlineData("out-of-range")]
    [InlineData("unaligned")]
    [InlineData("unreadable")]
    [InlineData("short-read")]
    [InlineData("dead")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("empty")]
    [InlineData("multiple")]
    [InlineData("huge-count")]
    [InlineData("changed")]
    [InlineData("type")]
    public void InvalidObjectRefusesBeforeAnyWrite(string fault)
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        switch (fault)
        {
            case "out-of-range": memory.Set(0x20050, 0xFFFFFFFC); break;
            case "unaligned": memory.Set(0x20050, 0x30001); break;
            case "unreadable": memory.FailAt = 0x40000; break;
            case "short-read": memory.ShortAt = 0x5033C; break;
            case "dead": memory.Set(0x70004, 0); break;
            case "nan": memory.Set(0x70004, 0x7FC00000); break;
            case "infinity": memory.Set(0x70004, 0x7F800000); break;
            case "empty": memory.Set(0x2005C, 0); break;
            case "multiple": memory.Set(0x2005C, 2); memory.Set(0x20050, 0); break;
            case "huge-count": memory.Set(0x2005C, uint.MaxValue); memory.Set(0x30008, 0); break;
            case "changed": memory.ChangeRoot = true; break;
            case "type": memory.Set(0x50004, 0); break;
        }
        Assert.Throws<InvalidOperationException>(() => Execute(memory, Feature("Select Unit HP MAX")));
        Assert.Equal(0, memory.Writes);
        Assert.InRange(memory.Reads, 1, 40);
        if (fault == "changed")
            // 只有真的在校验之后又读了根，才走得到“根在校验与派发之间变化”的 TOCTOU 检测。
            // 钉住这一点，夹具的触发次数语义一旦漂移，这条用例不会静默失效。
            Assert.True(memory.RootReads >= 2, $"ChangeRoot 未生效：根只被读了 {memory.RootReads} 次");
    }

    [Theory]
    [InlineData("Select Unit Ammo MAX")]
    [InlineData("Select Unit Ammo MAX 2")]
    [InlineData("Restore Select Ore Mine")]
    public void UnverifiableHookTargetRefusesEvenWithReadableSelectedObject(string name)
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        var error = Assert.Throws<InvalidOperationException>(() => Execute(memory, Feature(name)));
        // 契约：拒绝执行 + 一个字都没写进游戏（原来断言的是旧提示语里的"担责开启也不派发"）
        Assert.Contains("根链校验未通过", error.Message);
        Assert.Contains("已拒绝执行", error.Message);
        Assert.Contains("未写入", error.Message);
        Assert.Equal(0, memory.Writes);
    }

    [Theory]
    [InlineData("Select Unit Ammo MAX")]
    [InlineData("Select Unit Ammo MAX 2")]
    public void AmmoDisableRemainsAvailableWithoutSelection(string name)
    {
        var memory = new ControlledMemory();
        var feature = Feature(name) with { ValueHint = null };
        memory.Inner.WriteBytes(memory.Resolver.Resolve("iEnable+12"), [1, 91, 92]);
        new FeatureController(memory, memory.Resolver).SetToggle(feature, false);
        Assert.Equal(new byte[] { 0, 91, 92 }, memory.Inner.ReadBytes(memory.Resolver.Resolve("iEnable+12"), 3));
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public void ReadableSingleObjectDispatchesOnlyOneByteOnce()
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        Execute(memory, Feature("Select Unit HP MAX"));
        Assert.Equal(1, memory.Writes);
        Assert.Equal(1, memory.LastWriteWidth);
        Assert.Equal(memory.Resolver.Resolve("iEnable+20"), memory.LastWriteAddress);
        Assert.Equal(5, memory.Inner.ReadByte(memory.LastWriteAddress));
    }

    [Theory]
    [InlineData("Select Unit Level UP")]
    [InlineData("Select Unit Super Speed")]
    [InlineData("Select Unit Slow Speed")]
    [InlineData("Select Unit Freeze")]
    [InlineData("Restore Select Unit Speed")]
    [InlineData("Select Unit HP MIN")]
    [InlineData("Restore Select Unit Normal HP")]
    [InlineData("Select Unit Change ID")]
    [InlineData("Destory Select Unit")]
    public void ValidSingleObjectUsesOriginalActionByteWithoutObjectWrites(string name)
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Set(0x50374, 0x80000);
        memory.Set(0x80200, 0x90000);
        memory.Set(0x90000, 0xA0000);
        memory.Set(0x90004, 0xB0000);
        memory.Set(0x503CC, 0xC0000);
        memory.Set(0x50418, 0xD0000);
        memory.Set(memory.Resolver.Resolve("ID"), 0xD0000);
        var feature = Feature(name);
        Execute(memory, feature);
        Assert.Equal(1, memory.Writes);
        Assert.Equal(1, memory.LastWriteWidth);
        Assert.Equal(memory.Resolver.Resolve("iEnable+20"), memory.LastWriteAddress);
        Assert.Equal(Convert.ToByte(feature.ValueHint![2..], 16), memory.Inner.ReadByte(memory.LastWriteAddress));
        Assert.InRange(memory.Reads, 1, 80);
    }

    [Fact]
    public void DestroyUsesHeadAndNeverReadsUnreadableTailOrNext()
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Set(0x2005C, 1806);
        memory.ForbiddenReads.UnionWith([0x20054, 0x30000]);
        Execute(memory, Feature("Destory Select Unit"));
        Assert.Equal(1, memory.Writes);
        Assert.DoesNotContain(memory.ReadAddresses, memory.ForbiddenReads.Contains);
    }

    [Theory]
    [InlineData("Select Unit HP MAX")]
    [InlineData("Select Unit HP MIN")]
    [InlineData("Restore Select Unit Normal HP")]
    [InlineData("Select Unit Level UP")]
    public void LargeSelectionChecksOnlyHeadPayload(string name)
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Set(0x2005C, 1806);
        memory.Set(0x503CC, 0xC0000);
        memory.ForbiddenReads.UnionWith([0x20054, 0x30000]);
        Execute(memory, Feature(name));
        Assert.Equal(1, memory.Writes);
        Assert.DoesNotContain(memory.ReadAddresses, memory.ForbiddenReads.Contains);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(123u)]
    public void UnknownHeadTemplateCodeRejectsLargeSelectionBeforeWrites(uint type)
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Set(0x2005C, 1806);
        memory.Set(0x60008, type);
        memory.ForbiddenReads.UnionWith([0x20054, 0x30000]);
        var error = Assert.Throws<InvalidOperationException>(() => Execute(memory, Feature("Select Unit HP MAX")));
        Assert.Contains("单位目录", error.Message);
        // 拒绝文案必须带上读到的类型码：没有它，现场只剩"被拒了"，分不清"目录外的新 mod 单位/建筑"和"读歪了"。
        Assert.Contains($"type=0x{type:X8}", error.Message);
        Assert.Equal(0, memory.Writes);
        Assert.DoesNotContain(memory.ReadAddresses, memory.ForbiddenReads.Contains);
    }

    [Fact]
    public void BuildingSizedHealthIsAcceptedBecauseHealthHasNoGameEmpiricalCap()
    {
        // 用户 2026-10-08 的原话：「那你只看这一个建筑的血量上限吗？我们不可能针对每一个建筑都来测吧」——
        // 对。所以**血量不再有"游戏经验上界"**（原来 100000），只留数值理智线 1e9。
        // 这条钉住"建筑量级的血量合法"：旧代码判「生命值超出经验范围或对象已死亡（读到 125000，边界 [1, 100000]…）」⇒ 必红。
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Set(0x70004, BitConverter.SingleToUInt32Bits(125000f));
        memory.Set(0x7000C, BitConverter.SingleToUInt32Bits(125000f));
        memory.Set(0x70010, BitConverter.SingleToUInt32Bits(125000f));
        Execute(memory, Feature("Select Unit HP MAX"));
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public void UnknownTypeNoLongerBlocksTheOwnershipChangeButStillBlocksCatalogFeatures()
    {
        // 目录外的类型码（建筑/别的 mod 单位）不该挡住「改归属」——引擎侧只比它自己的 6 型黑名单；
        // 但同一份数据下，需要目录的功能（HP MAX）仍必须被挡。
        const uint unknown = 0x7CA957C7;   // 真机日志里被拒的那个建筑类型码
        var ownership = new ControlledMemory();
        Seed(ownership.Inner, ownership.Resolver);
        ownership.Set(0x60008, unknown);
        var ownershipError = Record.Exception(() => Execute(ownership, Feature("Select Unit Change ID")));
        Assert.True(ownershipError is null || !ownershipError.Message.Contains("单位目录", StringComparison.Ordinal),
            "改归属不该因为类型不在增援目录里被拒: " + ownershipError?.Message);

        var catalog = new ControlledMemory();
        Seed(catalog.Inner, catalog.Resolver);
        catalog.Set(0x60008, unknown);
        var catalogError = Assert.Throws<InvalidOperationException>(() => Execute(catalog, Feature("Select Unit HP MAX")));
        Assert.Contains("单位目录", catalogError.Message);
        Assert.Contains($"type=0x{unknown:X8}", catalogError.Message);
    }

    [Fact]
    public void OutOfRangeHealthMessageCarriesTheReadingAndTheBounds()
    {
        // 经验边界"太紧"的表现和"功能坏了"一模一样（真踩过：坐标上界定在 8000、他地图能到 9908）。
        // 所以越界拒了的时候，文案里必须有**读到的值**和**边界** —— 否则每一轮都只能猜是收界还是放宽。
        // 注：探针日志（logs/probe/*.jsonl，14091 行）里实测过 123750 这种血量，比当前上界 100000 还大，
        // 那条观测是"上界可能还需要按新采样重算"的证据，但要等这台机器上那个对象自己复现读数再定，不先猜。
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        const float huge = 1_000_000_000f;
        memory.Set(0x70004, BitConverter.SingleToUInt32Bits(huge));
        var error = Assert.Throws<InvalidOperationException>(() => Execute(memory, Feature("Select Unit HP MAX")));
        Assert.Contains("生命值", error.Message);
        Assert.Contains("读到", error.Message);
        Assert.Contains("1000000000", error.Message);   // 理智线本身要印出来，不然下次还是得猜
        Assert.Contains("理智线", error.Message);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public async Task AsyncEntryAlsoRefusesBeforeDispatchCallback()
    {
        var memory = new ControlledMemory();
        var dispatched = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FeatureController(memory, memory.Resolver).TriggerActionAndWaitForConsumptionAsync(
                Feature("Select Unit HP MAX"), onDispatched: () => dispatched = true));
        Assert.False(dispatched);
        Assert.Equal(0, memory.Writes);
    }

    [Fact]
    public async Task ThrowingCallerValidationBlocksDispatchAndOnDispatched()
    {
        // 真实 UI 入口（FeatureItemViewModel）会把 FeatureSafetyPolicy.Validate(...) 作为 validateBeforeDispatch 传进来。
        // 上一个用例只证明内部 SelectedObjectGuard 会拦，这个回调却是在 Guard 之前跑的 —— 必须单独覆盖：
        // 传一个抛异常的 validateBeforeDispatch，断言 onDispatched 仍为 false 且一个字节都没写。
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        var dispatched = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FeatureController(memory, memory.Resolver).TriggerActionAndWaitForConsumptionAsync(
                Feature("Select Unit HP MAX"),
                validateBeforeDispatch: () => throw new InvalidOperationException("调用方校验拒绝"),
                onDispatched: () => dispatched = true));
        Assert.Equal("调用方校验拒绝", error.Message);
        Assert.False(dispatched);
        Assert.Equal(0, memory.Writes);
    }

    private static TrainerFeature Feature(string name)
    {
        var feature = TestAssets.LoadManifest().Features.Single(f => f.RawName == name);
        // The original MAX 2 is a clearing action. Exercise attempted enabling through that alias too.
        return name == "Select Unit Ammo MAX 2" ? feature with { ValueHint = "0x01" } : feature;
    }

    [Fact]
    public void OriginalAmmoMax2ClearsFlagEvenWithoutObjectOrOptIn()
    {
        var memory = new ControlledMemory();
        var feature = TestAssets.LoadManifest().Features.Single(f => f.RawName == "Select Unit Ammo MAX 2");
        Assert.Equal("0x00", feature.ValueHint);
        memory.Inner.WriteBytes(memory.Resolver.Resolve("iEnable+12"), [1, 91]);
        FeatureSafetyPolicy.Validate(feature, true, false);
        Execute(memory, feature);
        Assert.Equal(new byte[] { 0, 91 }, memory.Inner.ReadBytes(memory.Resolver.Resolve("iEnable+12"), 2));
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public void CorruptDispatchHighBytesRefuseWithoutWriting()
    {
        var memory = new ControlledMemory();
        Seed(memory.Inner, memory.Resolver);
        memory.Inner.WriteBytes(memory.Resolver.Resolve("iEnable+20"), [0, 1, 0, 0]);
        Assert.Throws<InvalidOperationException>(() => Execute(memory, Feature("Select Unit HP MAX")));
        Assert.Equal(0, memory.Writes);
    }
    private static void Execute(ControlledMemory memory, TrainerFeature feature)
    {
        var controller = new FeatureController(memory, memory.Resolver);
        if (FeatureController.IsToggleFeature(feature)) controller.SetToggle(feature, true);
        else controller.TriggerAction(feature);
    }

    // Explicit object fixture for the pre-existing dispatch protocol tests; their assertions stay intact.
    internal static void Seed(IProcessMemory memory, AddressResolver resolver)
    {
        void Word(nint address, uint value) => memory.WriteBytes(address, BitConverter.GetBytes(value));
        Word(resolver.Resolve("ra3_1.12.game+8DB73C"), 0x20000);
        Word(0x2005C, 1);
        Word(0x20050, 0x30000);
        Word(0x20054, 0x30000);
        Word(0x30008, 0x40000);
        Word(0x40138, 0x50000);
        Word(0x50004, 0x60000);
        Word(0x60008, ReinforcementSettings.DefaultUnitId);
        Word(0x5033C, 0x70000);
        Word(0x70004, 0x42C80000);
        Word(0x7000C, 0x42C80000);
        Word(0x70010, 0x42C80000);
    }

    private sealed class ControlledMemory : IProcessMemory, IProbeMemory
    {
        public Win32MemoryRegion? Query(nint address) => Inner.Query(address);
        public FakeProcessMemory Inner { get; } = new();
        public AddressResolver Resolver { get; } = new(0x400000, new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000 });
        public int Writes, Reads, LastWriteWidth;
        public HashSet<nint> ForbiddenReads { get; } = [];
        public List<nint> ReadAddresses { get; } = [];
        public nint LastWriteAddress;
        public nint? FailAt, ShortAt;
        public bool ChangeRoot;
        private int _rootReads;
        /// <summary>校验与派发之间“根变化”的场景只有在根被读 ≥2 次时才走得到；暴露出来给用例断言。</summary>
        public int RootReads => _rootReads;
        public void Set(nint address, uint value) => Inner.WriteBytes(address, BitConverter.GetBytes(value));
        public byte[] ReadBytes(nint address, int count)
        {
            Reads++;
            ReadAddresses.Add(address);
            if (ForbiddenReads.Contains(address)) throw new IOException("Forbidden next/tail read");
            if (address == FailAt) throw new IOException("桩：页面不可读");
            // 短读要像真实的页边界短读那样：只要请求区间 [address, address+count) **相交** ShortAt 就触发，
            // 而不是仅在起始地址恰好相等时 —— 否则“跨过故障地址的读”会静默返回全长，
            // 产品一旦回退成重叠/分页读就抓不到。
            if (ShortAt is nint shortAt && address <= shortAt && shortAt < address + count)
                return new byte[Math.Max(0, (int)(shortAt - address))];
            if (ChangeRoot && address == Resolver.Resolve("ra3_1.12.game+8DB73C") && ++_rootReads > 1)
                return BitConverter.GetBytes(0x90000u);
            return Inner.ReadBytes(address, count);
        }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Writes++;
            LastWriteAddress = address;
            LastWriteWidth = bytes.Length;
            Inner.WriteBytes(address, bytes);
        }
    }
}
