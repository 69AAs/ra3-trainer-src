using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class MemoryRegionEnumerationTests
{
    /// <summary>
    /// 回归：结构体必须和原生 MEMORY_BASIC_INFORMATION 一样大。
    /// 曾经因为 x86 下多了 PartitionId/对齐字段（32 ≠ 28），VirtualQueryEx 直接失败，
    /// 结果"扫描游戏内存"读到 0 MB —— 这种 bug 只有真机才暴露，所以在这里钉死。
    /// </summary>
    [Fact]
    public void MemoryBasicInformationLayoutMatchesNative()
    {
        // x86 = 28（真机实测有效）；x64 = 48
        var expected = IntPtr.Size == 4 ? 28 : 48;
        Assert.Equal(expected, Marshal.SizeOf<Win32MemoryBasicInformation>());
    }

    [Fact]
    public void EnumerateReadableRegionsKeepsOnlyCommittedReadableRegions()
    {
        var api = new ScriptedApi(
        [
            // 未提交的保留区 → 跳过
            new Region(0x10000, 0x1000, 0x2000, 0x01),
            // 已提交但不可访问 → 跳过
            new Region(0x20000, 0x2000, 0x1000, 0x01),
            // 已提交但有 GUARD 页 → 跳过（读到会触发页错误/异常）
            new Region(0x30000, 0x3000, 0x1000, 0x104),
            // 正常的读写页 → 保留
            new Region(0x40000, 0x4000, 0x1000, 0x04),
        ]);

        using var memory = new Win32ProcessMemory(4242, api);
        var regions = memory.EnumerateReadableRegions();

        var region = Assert.Single(regions);
        Assert.Equal(0x40000, (long)region.BaseAddress);
        Assert.Equal(0x4000, region.Size);

        // 枚举必须按「上一区域 BaseAddress + RegionSize」前进。这个假 VirtualQueryEx 是按调用次序发区域的
        // （真机会返回「包含该地址」的区域），所以只有断言「问了哪些地址」才能钉住前进契约——
        // 否则原地打转、固定步长瞎走都能绿。序列从 0x10000 起，每一步 = 上一个区域的 Base + Size。
        Assert.Equal(
            new nint[] { 0x10000, 0x11000, 0x22000, 0x33000, 0x44000 },
            api.QueriedAddresses);
    }

    [Fact]
    public void EnumerateReadableRegionsStopsWhenQueryFails()
    {
        var api = new ScriptedApi([]);
        using var memory = new Win32ProcessMemory(4242, api);

        Assert.Empty(memory.EnumerateReadableRegions());
    }

    [Fact]
    public void EnumerateReadableRegionsStopsAfterAPartialListWhenQueryFails()
    {
        var api = new ScriptedApi(
        [
            new Region(0x10000, 0x1000, 0x1000, 0x04),
        ]);

        using var memory = new Win32ProcessMemory(4242, api);
        var region = Assert.Single(memory.EnumerateReadableRegions());

        Assert.Equal(0x10000, (long)region.BaseAddress);
        // 拿到一个有效区域后查询失败就必须终止（不是只在空缓冲上停，也不能继续瞎走）。
        Assert.Equal(new nint[] { 0x10000, 0x11000 }, api.QueriedAddresses);
    }

    [Fact]
    public void EnumerateReadableRegionsKeepsReadonlyAndExecutablePagesAndSetsTheExecutableFlag()
    {
        var api = new ScriptedApi(
        [
            new Region(0x10000, 0x1000, 0x1000, 0x02), // PAGE_READONLY
            new Region(0x11000, 0x1000, 0x1000, 0x20), // PAGE_EXECUTE_READ
            new Region(0x12000, 0x1000, 0x1000, 0x40), // PAGE_EXECUTE_READWRITE
            new Region(0x13000, 0x1000, 0x1000, 0x04), // PAGE_READWRITE
        ]);

        using var memory = new Win32ProcessMemory(4242, api);
        var regions = memory.EnumerateReadableRegions();

        Assert.Equal(4, regions.Count);
        // executable = (Protect & 0xF0) != 0：扫描要靠它排除代码页里"引用字符串"的假命中。
        Assert.False(regions[0].IsExecutable);
        Assert.True(regions[1].IsExecutable);
        Assert.True(regions[2].IsExecutable);
        Assert.False(regions[3].IsExecutable);
    }

    [Fact]
    public void EnumerateReadableRegionsClampsHugeRegionSizesToIntMaxValue()
    {
        var api = new ScriptedApi(
        [
            new Region(0x10000, (long)int.MaxValue + 0x1000, 0x1000, 0x04),
        ]);

        using var memory = new Win32ProcessMemory(4242, api);
        var region = Assert.Single(memory.EnumerateReadableRegions());

        // Size 是 int：超过 int.MaxValue 必须夹住，不能回绕成负数/小值。
        Assert.Equal(int.MaxValue, region.Size);
    }

    private readonly record struct Region(nint Address, long Size, uint State, uint Protect);

    private sealed class ScriptedApi : IWin32MemoryApi
    {
        private readonly Region[] _regions;
        private int _index;

        public ScriptedApi(Region[] regions) => _regions = regions;

        /// <summary>按调用次序收到过的地址（用来断言枚举的前进契约）。</summary>
        public List<nint> QueriedAddresses { get; } = [];

        public int GetLastWin32Error() => 0;

        public SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId) =>
            new(0x1234, ownsHandle: false);

        public bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesRead)
        {
            bytesRead = size;
            return true;
        }

        public bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesWritten)
        {
            bytesWritten = size;
            return true;
        }

        public nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect) => 0;

        public bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint newProtect, out uint oldProtect)
        {
            oldProtect = newProtect;
            return true;
        }

        public nuint VirtualQueryEx(SafeProcessHandle process, nint address, out Win32MemoryBasicInformation buffer, nuint length)
        {
            QueriedAddresses.Add(address);
            if (_index >= _regions.Length)
            {
                buffer = default;
                return 0;
            }

            var region = _regions[_index++];
            buffer = new Win32MemoryBasicInformation
            {
                BaseAddress = region.Address,
                AllocationBase = region.Address,
                AllocationProtect = region.Protect,
                RegionSize = (nuint)region.Size,
                State = region.State,
                Protect = region.Protect,
                Type = 0x20000,
            };

            return length;
        }

        public bool FlushInstructionCache(SafeProcessHandle process, nint baseAddress, nuint size) => true;
    }
}
