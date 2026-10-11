using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class Win32ProcessMemoryTests
{
    [Fact]
    public void ExplicitProtectionRejectsGuardPageBeforeMutation()
    {
        var page = (nint)(Environment.SystemPageSize * 16);
        var api = new FakeWin32MemoryApi();
        api.PageProtections[page] = 0x120;
        using var memory = new Win32ProcessMemory(1234, api);
        Assert.Throws<InvalidOperationException>(() => memory.ProtectExecuteReadWrite(page, 16));
        Assert.Single(api.Calls);
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x01)]
    [InlineData(0x10)]
    public void WriteBytesRejectsUnreadableLaterPageBeforeAnyMutation(uint protection)
    {
        var page = (nint)(Environment.SystemPageSize * 16);
        var api = new FakeWin32MemoryApi();
        api.PageProtections[page] = 0x20;
        api.PageProtections[page + Environment.SystemPageSize] = protection;
        using var memory = new Win32ProcessMemory(1234, api);

        Assert.Throws<InvalidOperationException>(() => memory.WriteBytes(page + Environment.SystemPageSize - 1, [1, 2]));

        Assert.Single(api.Calls);
        Assert.Equal(0x20u, api.PageProtections[page]);
    }

    [Fact]
    public void WriteBytesRejectsAddressWrapBeforeAnyMutation()
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api);
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.WriteBytes(unchecked((nint)(nuint)uint.MaxValue), [1, 2]));
        Assert.Single(api.Calls);
    }

    [Fact]
    public void ReadOnlyHandleRequestsOnlyReadAndQueryAndRejectsMutations()
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api, readOnly: true);
        Assert.Equal(new byte[2], memory.ReadBytes(0x5000, 2));
        // 只读会话必须仍能“读 + 查询 + 枚举区域”，否则只读诊断根本用不了。
        Assert.NotNull(memory.Query(0x5000));
        Assert.NotEmpty(memory.EnumerateReadableRegions());
        Assert.Throws<InvalidOperationException>(() => memory.WriteBytes(0x5000, [1]));
        Assert.Throws<InvalidOperationException>(() => memory.Allocate(128));
        Assert.Throws<InvalidOperationException>(() => memory.ProtectExecuteReadWrite(0x5000, 128));
        // 释放也是“变更型”入口，只读句柄下必须被 ThrowIfReadOnly 拦下。
        Assert.Throws<InvalidOperationException>(() => memory.Free(0x20000000));
        Assert.Equal(new[] { "OpenProcess 1234 0x410" }, api.Calls);
    }

    [Fact]
    public void ZeroLengthWriteIsASilentNoOp()
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api);
        // 空写入：ValidateWriteRange 的循环体一次都不跑，WriteBytes 的分页循环也不跑 ⇒ 什么都不做。
        // 钉住这个（有意的）契约：既不改保护也不报错。
        memory.WriteBytes(0x6E24E3, []);
        Assert.Equal(new[] { "OpenProcess 1234 0x438" }, api.Calls);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x5000)]
    [InlineData(0xFFFF)]
    public void WriteBytesRejectsLowAddressesBeforeAnyMutation(int address)
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api);
        // 低于 0x10000 的地址不属于 x86 用户地址空间：写前就必须拒，且不碰任何页面。
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.WriteBytes((nint)address, [1, 2]));
        Assert.Single(api.Calls);
    }

    [Fact]
    public void ReadBytesReportsReadProcessMemoryFailure()
    {
        var api = new FakeWin32MemoryApi { FailRead = true };
        using var memory = new Win32ProcessMemory(1234, api);
        var error = Assert.Throws<Win32Exception>(() => memory.ReadBytes(0x6E24E3, 4));
        Assert.Contains("ReadProcessMemory failed", error.Message);
    }

    [Fact]
    public void WriteBytesReportsFlushInstructionCacheFailure()
    {
        var api = new FakeWin32MemoryApi { FailFlush = true };
        using var memory = new Win32ProcessMemory(1234, api);
        // 写入成功但 FlushInstructionCache 失败 ⇒ 必须报出来（原来夹具永远返回 true，这条分支从没测过）。
        var error = Assert.Throws<Win32Exception>(() => memory.WriteBytes(0x6E24E3, [0xE9]));
        Assert.Contains("FlushInstructionCache failed", error.Message);
    }

    [Fact]
    public void CrossPageWriteToUnregisteredPageUsesRealisticOldProtection()
    {
        var pageSize = Environment.SystemPageSize;
        var firstPage = (nint)(pageSize * 16);
        var api = new FakeWin32MemoryApi();
        api.PageProtections[firstPage] = 0x20; // 只登记第一页，第二页故意不登记
        using var memory = new Win32ProcessMemory(1234, api);

        // 真实 VirtualProtectEx 不会因为“这页没登记”就失败：对未登记页必须给一个现实的老保护值，
        // 而不是抛 KeyNotFoundException。
        memory.WriteBytes(firstPage + pageSize - 1, new byte[] { 1, 2 });

        Assert.Equal(0x20u, api.PageProtections[firstPage]);                        // 恢复了本页老保护
        Assert.Equal(0x20u, api.PageProtections[firstPage + pageSize]);             // 未登记页按默认 0x20 记录并恢复
    }

    [Fact]
    public void CrossPageWriteHonorsRestoreFailureWithPageStateModelling()
    {
        var pageSize = Environment.SystemPageSize;
        var firstPage = (nint)(pageSize * 16);
        var api = new FakeWin32MemoryApi { FailRestore = true };
        api.PageProtections[firstPage] = 0x20;
        api.PageProtections[firstPage + pageSize] = 0x02;
        using var memory = new Win32ProcessMemory(1234, api);

        // 页状态分支必须同样遵守 FailRestore：设了 PageProtections 也不能让恢复失败被无条件 return true 吞掉。
        var error = Assert.Throws<Win32Exception>(
            () => memory.WriteBytes(firstPage + pageSize - 1, new byte[] { 1, 2 }));
        Assert.Contains("VirtualProtectEx restore failed", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossPageWriteRestoresEachPagesOwnProtection(bool failSecondPageWrite)
    {
        var pageSize = Environment.SystemPageSize;
        var firstPage = (nint)(pageSize * 16);
        var secondPage = firstPage + pageSize;
        var api = new FakeWin32MemoryApi { FailWriteAt = failSecondPageWrite ? secondPage : null };
        api.PageProtections[firstPage] = 0x20;
        api.PageProtections[secondPage] = 0x02;
        using var memory = new Win32ProcessMemory(1234, api);

        if (failSecondPageWrite)
        {
            Assert.Throws<Win32Exception>(() => memory.WriteBytes(secondPage - 1, new byte[] { 1, 2 }));
        }
        else
        {
            memory.WriteBytes(secondPage - 1, new byte[] { 1, 2 });
        }

        Assert.Equal(0x20u, api.PageProtections[firstPage]);
        Assert.Equal(0x02u, api.PageProtections[secondPage]);
        Assert.Contains($"Write 0x{secondPage - 1:X} 01", api.Calls);
        Assert.Contains($"Write 0x{secondPage:X} 02", api.Calls);
    }

    [Fact]
    public void WriteBytesReportsProtectionRestoreFailure()
    {
        var api = new FakeWin32MemoryApi { FailRestore = true };
        using var memory = new Win32ProcessMemory(1234, api);

        var exception = Assert.Throws<Win32Exception>(() => memory.WriteBytes(0x6E24E3, new byte[] { 0xE9 }));

        Assert.Contains("VirtualProtectEx restore failed", exception.Message);
    }

    [Fact]
    public void WriteBytesPreservesBothWriteAndProtectionRestoreFailures()
    {
        var api = new FakeWin32MemoryApi { FailRestore = true, FailWrite = true };
        using var memory = new Win32ProcessMemory(1234, api);

        var exception = Assert.Throws<AggregateException>(() => memory.WriteBytes(0x6E24E3, new byte[] { 0xE9 }));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.Contains("WriteProcessMemory failed", exception.InnerExceptions[0].Message);
        Assert.Contains("VirtualProtectEx restore failed", exception.InnerExceptions[1].Message);
        // 只比消息子串分不清"顺序被换过"：把两个内层异常的类型也钉住。
        Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerExceptions[0]);
        Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerExceptions[1]);
    }

    [Theory]
    [InlineData("OpenProcessNative", "OpenProcess")]
    [InlineData("ReadProcessMemoryNative", "ReadProcessMemory")]
    [InlineData("WriteProcessMemoryNative", "WriteProcessMemory")]
    [InlineData("VirtualAllocExNative", "VirtualAllocEx")]
    [InlineData("VirtualFreeExNative", "VirtualFreeEx")]
    [InlineData("VirtualProtectExNative", "VirtualProtectEx")]
    [InlineData("VirtualQueryExNative", "VirtualQueryEx")]
    [InlineData("FlushInstructionCacheNative", "FlushInstructionCache")]
    public void Kernel32NativeMethodsBindToExportedEntryPoints(string methodName, string expectedEntryPoint)
    {
        var method = typeof(Kernel32MemoryApi).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var dllImport = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(dllImport);
        Assert.Equal("kernel32.dll", dllImport.Value);
        Assert.Equal(expectedEntryPoint, dllImport.EntryPoint);
    }

    [Fact]
    public void WriteBytesTemporarilyEnablesExecuteReadWriteAndRestoresProtection()
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api);

        memory.WriteBytes(0x6E24E3, new byte[] { 0xE9, 0x01, 0x02, 0x03, 0x04 });

        Assert.Equal(
            [
                "OpenProcess 1234 0x438",
                "Protect 0x6E24E3 5 0x40",
                "Write 0x6E24E3 E9-01-02-03-04",
                "Flush 0x6E24E3 5",
                "Protect 0x6E24E3 5 0x20"
            ],
            api.Calls);
    }

    [Fact]
    public void WriteBytesReportsNativeErrorCodeWhenVirtualProtectFails()
    {
        var api = new FakeWin32MemoryApi { FailVirtualProtect = true };
        using var memory = new Win32ProcessMemory(1234, api);

        var exception = Assert.Throws<Win32Exception>(
            () => memory.WriteBytes(0x6E24E3, new byte[] { 0xE9 }));

        Assert.Equal(5, exception.NativeErrorCode);
        Assert.Contains("VirtualProtectEx failed at 0x6E24E3", exception.Message);
        Assert.Contains("Win32 error 5", exception.Message);
        Assert.Contains("Memory region: base=0x6E2000", exception.Message);
    }

    [Fact]
    public void ProtectExecuteReadWriteRecordsRangeAndSkipsPerWriteVirtualProtect()
    {
        var api = new FakeWin32MemoryApi();
        using var memory = new Win32ProcessMemory(1234, api);

        memory.ProtectExecuteReadWrite(0x401000, 0x7C6000);
        memory.WriteBytes(0x6E24E3, new byte[] { 0xE9, 0x01, 0x02, 0x03, 0x04 });

        Assert.Equal(
            [
                "OpenProcess 1234 0x438",
                "Protect 0x401000 8151040 0x40",
                "Write 0x6E24E3 E9-01-02-03-04",
                "Flush 0x6E24E3 5"
            ],
            api.Calls);
    }

    private sealed class FakeWin32MemoryApi : IWin32MemoryApi
    {
        // 未登记页的默认保护 = 已提交可读页（真实进程里 PAGE_READWRITE 一类）。
        private const uint DefaultPageProtection = 0x20u;
        private int _protectCallCount;

        public List<string> Calls { get; } = [];

        public bool FailVirtualProtect { get; init; }
        public bool FailRestore { get; init; }
        public bool FailWrite { get; init; }
        public bool FailRead { get; init; }
        public bool FailFlush { get; init; }
        public nint? FailWriteAt { get; init; }
        public Dictionary<nint, uint> PageProtections { get; } = new();

        public int GetLastWin32Error() => 5;

        public SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId)
        {
            Calls.Add($"OpenProcess {processId} 0x{desiredAccess:X}");
            return new SafeProcessHandle(0x1234, ownsHandle: false);
        }

        public bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesRead)
        {
            bytesRead = FailRead ? 0 : size;
            return !FailRead;
        }

        public bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesWritten)
        {
            var failed = FailWrite || FailWriteAt == baseAddress;
            bytesWritten = failed ? 0 : size;
            Calls.Add($"Write 0x{baseAddress:X} {BitConverter.ToString(buffer)}");
            return !failed;
        }

        public nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect) => 0x20000000;

        // 老保护一律按“页的真实状态”取，而不是按第几次调用猜：同一页第二次写、或换一个
        // protect/restore 顺序时，按调用序号会报出错误的老保护 —— 那样测的是夹具，不是产品。
        // 失败开关在这里同样生效，否则把 FailVirtualProtect/FailRestore 与页状态一起用会被静默吞掉。
        public bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint newProtect, out uint oldProtect)
        {
            Calls.Add($"Protect 0x{address:X} {size} 0x{newProtect:X}");
            var call = _protectCallCount++;
            var page = address - (nint)(unchecked((nuint)address) % (nuint)Environment.SystemPageSize);
            oldProtect = PageProtections.GetValueOrDefault(page, DefaultPageProtection);
            PageProtections[page] = newProtect;
            return !FailVirtualProtect && !(FailRestore && call > 0);
        }

        public nuint VirtualQueryEx(SafeProcessHandle process, nint address, out Win32MemoryBasicInformation buffer, nuint length)
        {
            buffer = new Win32MemoryBasicInformation
            {
                BaseAddress = address - (nint)((nuint)address % (nuint)Environment.SystemPageSize),
                RegionSize = 0x1000,
                AllocationProtect = 0x20,
                State = 0x1000,
                Protect = PageProtections.GetValueOrDefault(address - (nint)((nuint)address % (nuint)Environment.SystemPageSize), DefaultPageProtection),
                Type = 0x1000000
            };
            return length;
        }

        public bool FlushInstructionCache(SafeProcessHandle process, nint baseAddress, nuint size)
        {
            Calls.Add($"Flush 0x{baseAddress:X} {size}");
            return !FailFlush;
        }
    }
}
