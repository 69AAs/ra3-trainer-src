using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 心灵控制取证探针的只读测试。夹具与 ReadOnlyProbeTests 同款（同一条对象根链的原始地址）。
/// 这里**不存在**任何写接口：IProbeMemory 只有 ReadBytes/Query，所以"不写内存"是类型层面的保证，
/// 测试只验"读对了没 / 判等对不对 / 该拒绝的有没有拒绝"。
/// </summary>
public sealed class SelectedUnitOwnershipProbeTests
{
    // 与 ReadOnlyProbeTests 一致：moduleBase=0x400000，选择根 [0xCDB73C]。
    private static readonly AddressResolver Resolver = new(0x400000, new Dictionary<string, nint>
    { ["MustCode"] = 0x710000, ["iEnable"] = 0x700100, ["ID"] = 0x700000 });

    private const uint LocalPlayer = 0x00067000;

    private static ProbeMemory Fixture(uint typeCode, uint ownerPointer, uint count = 1)
    {
        var memory = new ProbeMemory();
        foreach (var (address, value) in new (int, uint)[]
        {
            (0xCDB73C, 0x20000),        // [game+8DB73C] = selection root S
            (0x2005C, count),           // [S+0x5C] = count
            (0x20050, 0x30000),         // [S+0x50] = node L
            (0x30008, 0x40000),         // [L+8]    = wrapper W
            (0x40138, 0x50000),         // [W+0x138]= unit U
            (0x50004, 0x60000),         // [U+4]    = template T
            (0x60008, typeCode),        // [T+8]    = type code
            (0x50418, ownerPointer),    // [U+0x418]= owner 候选
            (0x700000, LocalPlayer),    // [ID]     = 本机玩家指针
        }) memory.Put(address, value);
        return memory;
    }

    [Fact]
    public void OwnerEqualToLocalPlayerIsReportedAsMine()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: true);

        Assert.Equal("已选中", snapshot.SelectionState);
        Assert.Equal(1u, snapshot.SelectionCount);
        Assert.Equal((nint)0x50000, snapshot.UnitAddress);
        Assert.Equal(0x12345678u, snapshot.TypeCode);
        Assert.Equal(LocalPlayer, snapshot.OwnerCandidate);
        Assert.Equal(LocalPlayer, snapshot.LocalPlayerPointer);
        Assert.True(snapshot.OwnerEqualsLocalPlayer);
        Assert.False(snapshot.TypeInTransferBlacklist);
        Assert.Contains("已归属你", snapshot.Summary);
        Assert.Contains("未写内存", snapshot.Summary);
        // 时间戳/对象地址齐备，可直接落盘留档。
        Assert.Equal((nint)0x50418, snapshot.OwnerCandidateAddress);
        Assert.NotEqual(default, snapshot.Timestamp);
    }

    [Fact]
    public void OwnerDifferentFromLocalPlayerIsReportedAsNotMine()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000); // 别人的玩家对象
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: true);

        Assert.Equal(0x00050000u, snapshot.OwnerCandidate);
        Assert.False(snapshot.OwnerEqualsLocalPlayer);
        Assert.Contains("不等于本机玩家", snapshot.Summary);
    }

    [Theory]
    [InlineData(0x95D6E965u)]
    [InlineData(0xFD87E82Au)]
    [InlineData(0x856C9DD6u)]
    [InlineData(0xD20552E1u)]
    [InlineData(0x1AFC9A6Eu)]
    [InlineData(0xD6D22475u)]
    public void EngineTransferBlacklistTypesAreReported(uint typeCode)
    {
        var memory = Fixture(typeCode: typeCode, ownerPointer: 0x00050000);
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: true);

        Assert.True(snapshot.TypeInTransferBlacklist);
        Assert.Contains("黑名单", snapshot.Summary);
    }

    [Fact]
    public void EmptySelectionReportsNoSelectionAndNeverReadsDeeper()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer, count: 0);
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: true);

        Assert.Null(snapshot.UnitAddress);
        Assert.Contains("未选中", snapshot.Summary);
        // 空选择时不得把包装/单位那一层读出来（上游指针 0x30008/0x40138 都不该碰）。
        Assert.DoesNotContain((nint)0x30008, memory.ReadAddresses);
        Assert.DoesNotContain((nint)0x40138, memory.ReadAddresses);
    }

    [Fact]
    public void NotAttachedLeavesLocalPlayerUnreadAndSaysSoInsteadOfGuessing()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: false);

        Assert.Null(snapshot.LocalPlayerPointer);
        Assert.False(snapshot.OwnerEqualsLocalPlayer);
        Assert.Contains("未定位", snapshot.Summary);
        // 未附加时不许去读 ID 块（那地址在没附加时根本不存在）。
        Assert.DoesNotContain((nint)0x700000, memory.ReadAddresses);
    }

    [Fact]
    public void WindowIncludesTheOwnerOffsetAndOnlyReadsAlignedUserAddresses()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        var snapshot = new SelectedUnitOwnershipProbe(memory, Resolver).Probe(hooksInstalled: true);

        Assert.Contains(snapshot.Window, field => field.Offset == "+0x418" && field.Value == LocalPlayer);
        Assert.Contains(snapshot.Window, field => field.Name.Contains("owner 候选"));
        Assert.All(snapshot.Window, field => Assert.True((ulong)(nuint)field.Address >= 0x10000));
        Assert.All(memory.ReadAddresses, address => Assert.True(address >= 0x10000));
        Assert.NotEmpty(snapshot.Window);
    }

    [Fact]
    public void ProbeSurfaceHasNoWriteCapability()
    {
        // 与 ReadOnlyProbeTests 同款：把"不写"钉在接口层，而不是靠人记得别写。
        Assert.DoesNotContain(typeof(IProbeMemory).GetMethods(), method => method.Name.Contains("Write"));
        Assert.DoesNotContain(typeof(SelectedUnitOwnershipProbe).GetMethods(),
            method => method.Name.Contains("Write") || method.Name.Contains("Restore") || method.Name.Contains("Dispatch"));
    }

    private sealed class ProbeMemory : IProbeMemory
    {
        private readonly Dictionary<nint, uint> _values = new();
        public List<nint> ReadAddresses { get; } = [];
        public nint? UnqueriedPage { get; set; }
        public void Put(nint address, uint value) => _values[address] = value;
        public Win32MemoryRegion? Query(nint address) =>
            UnqueriedPage is nint missing && (address & ~0xFFF) == (missing & ~0xFFF)
                ? null
                : new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0x20000);
        public byte[] ReadBytes(nint address, int count)
        {
            ReadAddresses.Add(address);
            return BitConverter.GetBytes(_values.GetValueOrDefault(address))[..count];
        }
    }
}
