using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class RemoteAddressSpaceTests
{
    [Fact]
    public void EnumerationUsesUnsignedUpperHalfAndAlignedFreeRegions()
    {
        var api = new Api();
        api.Regions.Add(new() { BaseAddress = 0x10000, RegionSize = 0x80001000, State = 0x1000 });
        api.Regions.Add(new() { BaseAddress = unchecked((nint)(nuint)0x80011000u), RegionSize = 0x2F000, State = 0x10000 });
        api.Regions.Add(new() { BaseAddress = unchecked((nint)(nuint)0x80040000u), RegionSize = 0x7FFC0000, State = 0x2000 });
        using var memory = new Win32ProcessMemory(1234, api);
        var result = memory.InspectAddressSpace();
        Assert.True(result.Complete);
        Assert.Equal(0x2F000UL, result.FreeBytes);
        Assert.Equal(0x20000UL, result.LargestAlignedBlock);
        Assert.Equal(1, result.AlignedRegionCount);
    }

    [Fact]
    public void GapsBetweenRegionsAreWalkedThroughAsFreeSpace()
    {
        var api = new Api();
        // 已提交 → 空洞（MEM_FREE，真机会把它作为区域返回而不是返回 0）→ 已提交 → 空到 4GB。
        api.Regions.Add(new() { BaseAddress = 0x10000, RegionSize = 0x10000, State = 0x1000 });
        api.Regions.Add(new() { BaseAddress = 0x20000, RegionSize = 0x10000, State = 0x10000 });
        api.Regions.Add(new() { BaseAddress = 0x30000, RegionSize = 0x10000, State = 0x1000 });
        api.Regions.Add(new() { BaseAddress = (nint)0x40000, RegionSize = (nuint)(0x100000000UL - 0x40000UL), State = 0x10000 });
        using var memory = new Win32ProcessMemory(1234, api);

        var result = memory.InspectAddressSpace();

        // 空洞不能把整次探测判成"不完整"：游标必须跨过空洞一路走到第二块已提交区的起点。
        Assert.True(result.Complete);
        Assert.Contains((nint)0x30000, api.QueriedAddresses);
        Assert.True(result.FreeBytes >= 0x10000UL); // 空洞本身也要计入空闲字节
    }

    [Theory]
    [InlineData(0xF000, 0)]
    [InlineData(0x10000, 0x10000)]
    [InlineData(0x30000, 0x30000)]
    public void FragmentedFreeSpaceIsNotMistakenForAlignedCapacity(int length, int expected)
    {
        var api = new Api();
        api.Regions.Add(new() { BaseAddress = 0x10000, RegionSize = (nuint)length, State = 0x10000 });
        api.Regions.Add(new() { BaseAddress = 0x10000 + length, RegionSize = (nuint)(0x100000000UL - 0x10000UL - (uint)length), State = 0x1000 });
        using var memory = new Win32ProcessMemory(1234, api);
        Assert.Equal((ulong)expected, memory.InspectAddressSpace().LargestAlignedBlock);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidQueryResultIsIncompleteRatherThanZeroFree(bool shortStructure)
    {
        var api = new Api { ShortStructure = shortStructure };
        api.Regions.Add(new() { BaseAddress = 0x10000, RegionSize = shortStructure ? 0x10000u : 0u, State = 0x10000 });
        using var memory = new Win32ProcessMemory(1234, api);
        Assert.False(memory.InspectAddressSpace().Complete);
        Assert.Contains("探测不完整", memory.InspectAddressSpace().Summary);
    }

    [Fact]
    public void AllocationErrorEightKeepsNativeCodeAndChineseRecoveryMessage()
    {
        var api = new Api { AllocationFails = true };
        using var memory = new Win32ProcessMemory(1234, api);
        var error = Assert.Throws<Win32Exception>(() => memory.Allocate(0x4200));
        Assert.Equal(8, error.NativeErrorCode);
        Assert.Contains("游戏 32 位地址空间已满，请重启游戏后再试", error.Message);
        Assert.Contains("Win32 error 8", error.Message);
        Assert.Contains("探测不完整", error.Message);
    }

    [Fact]
    public void FreeUsesOriginalBaseZeroSizeAndMemReleaseAndRejectsForeignOrDoubleFree()
    {
        var api = new Api();
        using var memory = new Win32ProcessMemory(1234, api);
        var address = memory.Allocate(0x4200);
        Assert.Throws<InvalidOperationException>(() => memory.Free(address + 0x100));
        memory.Free(address);
        Assert.Equal((address, (nuint)0, 0x8000u), Assert.Single(api.Frees));
        Assert.Throws<InvalidOperationException>(() => memory.Free(address));
    }

    [Fact]
    public void FailedFreeCanRetryWithoutLosingOwnership()
    {
        var api = new Api { FreeFails = true };
        using var memory = new Win32ProcessMemory(1234, api);
        var address = memory.Allocate(0x4200);
        Assert.Throws<Win32Exception>(() => memory.Free(address));
        api.FreeFails = false;
        memory.Free(address);
        Assert.Equal(2, api.Frees.Count);
    }

    [Fact]
    public void NewGenerationSkipsRetiredAddressWithoutKeepingItReserved()
    {
        var api = new Api();
        api.Regions.Add(new() { BaseAddress = 0x10000, RegionSize = 0x30000, State = 0x10000 });
        using var memory = new Win32ProcessMemory(1234, api);
        var first = memory.Allocate(0x4200);
        memory.Free(first);
        Assert.Equal((nint)0x20000, memory.Allocate(0x4200));
        Assert.Single(api.Frees);
    }

    private sealed class Api : IWin32MemoryApi
    {
        public List<Win32MemoryBasicInformation> Regions { get; } = [];
        public List<(nint, nuint, uint)> Frees { get; } = [];
        public List<nint> QueriedAddresses { get; } = [];
        public bool AllocationFails { get; init; }
        public bool ShortStructure { get; init; }
        public bool FreeFails { get; set; }
        private int _error = 87;
        public int GetLastWin32Error() => _error;
        public SafeProcessHandle OpenProcess(uint access, bool inherit, int id) => new(1, false);
        public nint VirtualAllocEx(SafeProcessHandle p, nint a, nuint s, uint t, uint protection)
        {
            // 只有真的失败才写 last-error：成功路径也写的话，后续断言到的错误码就是脏值。
            if (AllocationFails) { _error = 8; return 0; }
            return a == 0 ? 0x10000 : a;
        }
        public bool VirtualFreeEx(SafeProcessHandle p, nint a, nuint s, uint t)
        { Frees.Add((a, s, t)); if (FreeFails) { _error = 5; return false; } return true; }
        public nuint VirtualQueryEx(SafeProcessHandle p, nint a, out Win32MemoryBasicInformation buffer, nuint length)
        {
            var address = (ulong)(nuint)a;
            QueriedAddresses.Add(a);
            // 真 VirtualQueryEx 返回的是**包含**该地址的区域（不是只匹配基址）。按包含关系找，
            // 才不会把"区域之间有空洞"的真实布局误判成探测失败。
            buffer = Regions.FirstOrDefault(r =>
                address >= (ulong)(nuint)r.BaseAddress &&
                address < (ulong)(nuint)r.BaseAddress + r.RegionSize);
            if (buffer.BaseAddress == 0) { _error = 87; return 0; }
            return ShortStructure ? length - 4 : length;
        }
        public bool ReadProcessMemory(SafeProcessHandle p, nint a, byte[] b, int s, out int read) { read = 0; return false; }
        public bool WriteProcessMemory(SafeProcessHandle p, nint a, byte[] b, int s, out int written) { written = 0; return false; }
        public bool VirtualProtectEx(SafeProcessHandle p, nint a, nuint s, uint n, out uint o) { o = 0; return false; }
        public bool FlushInstructionCache(SafeProcessHandle p, nint a, nuint s) => true;
    }
}
