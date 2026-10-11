using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Ra3Trainer.Core.Memory;

public sealed class Win32ProcessMemory : IProcessMemory, IProbeMemory, IRemoteAllocator, IRemoteMemoryProtector, IMemoryRegionSource, IRemoteMemoryLifetime, IRemoteAddressSpaceSource, IDisposable
{
    private const uint MemCommitState = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const long UserSpaceEnd = 0x7FFF0000;

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint RequiredProcessAccess =
        ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation;
    private const uint MemCommitReserve = 0x3000;
    private const uint PageExecuteReadWrite = 0x40;

    private readonly IWin32MemoryApi _api;
    private readonly SafeProcessHandle _handle;
    private readonly bool _readOnly;
    private readonly HashSet<nint> _ownedAllocations = new();
    // 这两个集合会被 Allocate/Free/WriteBytes 从不同线程碰：原来只有 _retiredAddresses 有锁。
    private readonly object _allocationGate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<ulong>> RetiredByProcess = new();
    private readonly HashSet<ulong> _retiredAddresses;
    private readonly List<(nint Address, nint End)> _executeReadWriteRanges = new();

    public Win32ProcessMemory(int processId, bool readOnly = false)
        : this(processId, Kernel32MemoryApi.Instance, readOnly)
    {
    }

    internal Win32ProcessMemory(int processId, IWin32MemoryApi api, bool readOnly = false)
    {
        _api = api;
        _readOnly = readOnly;
        _retiredAddresses = ReferenceEquals(api, Kernel32MemoryApi.Instance)
            ? RetiredByProcess.GetOrAdd(processId, _ => new HashSet<ulong>()) : new();
        _handle = _api.OpenProcess(readOnly ? ProcessVmRead | ProcessQueryInformation : RequiredProcessAccess, false, processId);
        if (_handle.IsInvalid)
        {
            throw CreateLastWin32Exception("OpenProcess failed.");
        }
    }

    /// <summary>
    /// 列出所有已提交、可读的内存区域（跳过 NOACCESS / GUARD）。只读扫描用，不会改内存。
    /// </summary>
    public IReadOnlyList<MemoryRegion> EnumerateReadableRegions()
    {
        var regions = new List<MemoryRegion>();
        var address = (nint)0x10000;
        var infoSize = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Win32MemoryBasicInformation>();

        while ((ulong)(long)address < UserSpaceEnd)
        {
            if (_api.VirtualQueryEx(_handle, address, out var info, infoSize) == 0 || info.RegionSize == 0)
            {
                break;
            }

            if (info.State == MemCommitState
                && (info.Protect & PageNoAccess) == 0
                && (info.Protect & PageGuard) == 0)
            {
                var size = (long)info.RegionSize;
                if (size > int.MaxValue)
                {
                    size = int.MaxValue;
                }

                // 0x10/0x20/0x40/0x80 都是可执行页；识别出来才能在扫描时排除"代码里引用字符串"的假命中
                var executable = (info.Protect & 0xF0) != 0;
                regions.Add(new MemoryRegion(info.BaseAddress, (int)size, executable));
            }

            address = info.BaseAddress + (nint)info.RegionSize;
        }

        return regions;
    }

    public byte[] ReadBytes(nint address, int count)
    {
        var buffer = new byte[count];
        if (!_api.ReadProcessMemory(_handle, address, buffer, buffer.Length, out var read) || read != count)
        {
            throw CreateLastWin32Exception($"ReadProcessMemory failed at 0x{address:X}.");
        }
        return buffer;
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        ThrowIfReadOnly();
        ValidateWriteRange(address, bytes.Length);
        // VirtualProtectEx reports only the first page's old protection. Restore each
        // page separately so a write crossing differently protected pages preserves both.
        var offset = 0;
        while (offset < bytes.Length)
        {
            var current = address + offset;
            var remainingInPage = Environment.SystemPageSize -
                (int)(unchecked((nuint)current) % (nuint)Environment.SystemPageSize);
            var length = Math.Min(remainingInPage, bytes.Length - offset);
            WritePage(current, bytes.Slice(offset, length));
            offset += length;
        }
    }

    // Validate the entire range before changing any page. Target mapping changes
    // can still race this check; callers retain rollback records for write failures.
    private void ValidateWriteRange(nint address, int count)
    {
        var start = (ulong)(nuint)address;
        var end = start + (ulong)count;
        if (start < 0x10000 || end > uint.MaxValue || end < start)
            throw new ArgumentOutOfRangeException(nameof(address), "写入范围不属于有效的 x86 用户地址空间。");
        var cursor = start;
        while (cursor < end)
        {
            var region = Query(unchecked((nint)(nuint)cursor));
            if (region is null || region.State != MemCommitState ||
                (region.Protect & (PageNoAccess | PageGuard)) != 0 ||
                (region.Protect & 0xEE) == 0)
                throw new InvalidOperationException("写入目标未提交、不可读或受到保护，已拒绝写入。");
            var regionStart = (ulong)(nuint)region.BaseAddress;
            var regionEnd = regionStart + (ulong)region.RegionSize;
            if (regionStart > cursor || regionEnd <= cursor || regionEnd > (1UL << 32))
                throw new InvalidOperationException("目标内存区域范围无效，已拒绝写入。");
            cursor = Math.Min(end, regionEnd);
        }
    }

    private void WritePage(nint address, ReadOnlySpan<byte> bytes)
    {
        var buffer = bytes.ToArray();
        if (buffer.Length == 0)
        {
            return;
        }

        if (IsInExecuteReadWriteRange(address, buffer.Length))
        {
            WriteBytesWithoutProtect(address, buffer);
            return;
        }

        if (!_api.VirtualProtectEx(_handle, address, (nuint)buffer.Length, PageExecuteReadWrite, out var oldProtect))
        {
            throw CreateLastWin32Exception($"VirtualProtectEx failed at 0x{address:X}.");
        }

        Exception? writeFailure = null;
        try
        {
            if (!_api.WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written) || written != buffer.Length)
            {
                throw CreateLastWin32Exception($"WriteProcessMemory failed at 0x{address:X}.");
            }

            if (!_api.FlushInstructionCache(_handle, address, (nuint)buffer.Length))
            {
                throw CreateLastWin32Exception($"FlushInstructionCache failed at 0x{address:X}.");
            }
        }
        catch (Exception ex)
        {
            writeFailure = ex;
            throw;
        }
        finally
        {
            if (!_api.VirtualProtectEx(_handle, address, (nuint)buffer.Length, oldProtect, out _))
            {
                var restoreFailure = CreateLastWin32Exception($"VirtualProtectEx restore failed at 0x{address:X}.");
                if (writeFailure is not null)
                {
                    throw new AggregateException(writeFailure, restoreFailure);
                }
                throw restoreFailure;
            }
        }
    }

    public void ProtectExecuteReadWrite(nint address, int size)
    {
        ThrowIfReadOnly();
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive.");
        }

        ValidateWriteRange(address, size);

        if (!_api.VirtualProtectEx(_handle, address, (nuint)size, PageExecuteReadWrite, out _))
        {
            throw CreateLastWin32Exception($"VirtualProtectEx failed at 0x{address:X} for 0x{size:X} bytes.");
        }

        lock (_allocationGate) _executeReadWriteRanges.Add((address, address + size));
    }

    public Win32MemoryRegion? Query(nint address)
    {
        var size = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Win32MemoryBasicInformation>();
        var result = _api.VirtualQueryEx(_handle, address, out var info, size);
        if (result == 0)
        {
            return null;
        }

        return new Win32MemoryRegion(
            info.BaseAddress,
            info.RegionSize,
            info.AllocationProtect,
            info.State,
            info.Protect,
            info.Type);
    }

    public nint Allocate(int size)
    {
        ThrowIfReadOnly();
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size), "分配大小必须为正数。");
        nint address;
        lock (_retiredAddresses)
        {
            // Once a generation is retired, never publish new code at its old base, even
            // when Windows would immediately return that base again after MEM_RELEASE.
            var preferred = _retiredAddresses.Count == 0 ? 0 : FindFreshAddress(size);
            address = _api.VirtualAllocEx(_handle, preferred, (nuint)size, MemCommitReserve, PageExecuteReadWrite);
        }
        if (address == 0)
        {
            var error = _api.GetLastWin32Error(); // Capture before the diagnostic query.
            var space = InspectAddressSpace();
            var message = error == 8 ? space.ExhaustedMessage : "远程内存申请失败。" + space.Summary;
            throw new Win32Exception(error, $"{message}（VirtualAllocEx failed. Win32 error {error}: {new Win32Exception(error).Message}）");
        }
        lock (_allocationGate) _ownedAllocations.Add(address);
        return address;
    }

    public void Free(nint baseAddress)
    {
        ThrowIfReadOnly();
        lock (_allocationGate)
        {
        if (!_ownedAllocations.Contains(baseAddress))
            throw new InvalidOperationException("拒绝释放非本会话申请的远程块。");
        if (!_api.VirtualFreeEx(_handle, baseAddress, 0, 0x8000))
            throw CreateLastWin32Exception("VirtualFreeEx failed.");
        _ownedAllocations.Remove(baseAddress);
        lock (_retiredAddresses) _retiredAddresses.Add((ulong)(nuint)baseAddress);
        _executeReadWriteRanges.RemoveAll(range => range.Address == baseAddress);
        }
    }

    public RemoteAddressSpaceSnapshot InspectAddressSpace()
    {
        ulong cursor = 0x10000, free = 0, largest = 0;
        var count = 0;
        var size = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Win32MemoryBasicInformation>();
        // Production is x86. Do not silently use a cross-bitness diagnostic as preflight evidence.
        if (Environment.Is64BitProcess && ReferenceEquals(_api, Kernel32MemoryApi.Instance))
            return new(0, 0, 0, false, DateTimeOffset.Now, "请使用 x86 修改器进行同位数探测");
        while (cursor < 0x100000000UL)
        {
            var result = _api.VirtualQueryEx(_handle, unchecked((nint)(nuint)cursor), out var info, size);
            if (result == 0)
            {
                var error = _api.GetLastWin32Error();
                // ERROR_INVALID_PARAMETER marks the target's upper address limit (2/3/4GB).
                return new(free, largest, count, error == 87 && cursor >= 0x7FFF0000UL,
                    DateTimeOffset.Now, $"VirtualQueryEx Win32 error {error} at 0x{cursor:X}");
            }
            var start = (ulong)(nuint)info.BaseAddress;
            var end = start + (ulong)info.RegionSize;
            if (result != size || start > cursor || end <= cursor || end > 0x100000000UL)
                return new(free, largest, count, false, DateTimeOffset.Now, "VirtualQueryEx 返回了无效区域或结构大小");
            if (info.State == 0x10000) // MEM_FREE; reservations are not available.
            {
                var aligned = (cursor + 0xFFFFUL) & ~0xFFFFUL;
                var usable = end > aligned ? (end - aligned) & ~0xFFFFUL : 0;
                free += end - cursor;
                if (usable != 0) { count++; largest = Math.Max(largest, usable); }
            }
            cursor = end;
        }
        return new(free, largest, count, true, DateTimeOffset.Now);
    }

    private nint FindFreshAddress(int requestedSize)
    {
        ulong cursor = 0x10000;
        var size = (nuint)System.Runtime.InteropServices.Marshal.SizeOf<Win32MemoryBasicInformation>();
        while (cursor < 0x100000000UL)
        {
            if (_api.VirtualQueryEx(_handle, unchecked((nint)(nuint)cursor), out var info, size) != size) break;
            var start = (ulong)(nuint)info.BaseAddress;
            var end = start + (ulong)info.RegionSize;
            if (start > cursor || end <= cursor || end > 0x100000000UL) break;
            if (info.State == 0x10000)
                for (var candidate = (cursor + 0xFFFFUL) & ~0xFFFFUL;
                     candidate + (ulong)requestedSize <= end; candidate += 0x10000)
                    if (!_retiredAddresses.Contains(candidate)) return unchecked((nint)(nuint)candidate);
            cursor = end;
        }
        throw new InvalidOperationException("游戏 32 位地址空间没有新的可用区域，请重启游戏后再试。已退休的地址不会复用于新代码。" + InspectAddressSpace().Summary);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    private void ThrowIfReadOnly()
    {
        if (_readOnly) throw new InvalidOperationException("只读内存会话不能写入、分配或修改页面保护。");
    }

    private bool IsInExecuteReadWriteRange(nint address, int size)
    {
        var end = address + size;
        lock (_allocationGate)
        {
            return _executeReadWriteRanges.Any(range => address >= range.Address && end <= range.End);
        }
    }

    private void WriteBytesWithoutProtect(nint address, byte[] buffer)
    {
        if (!_api.WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written) || written != buffer.Length)
        {
            throw CreateLastWin32Exception($"WriteProcessMemory failed at 0x{address:X}.");
        }

        if (!_api.FlushInstructionCache(_handle, address, (nuint)buffer.Length))
        {
            throw CreateLastWin32Exception($"FlushInstructionCache failed at 0x{address:X}.");
        }
    }

    private Win32Exception CreateLastWin32Exception(string message)
    {
        var error = _api.GetLastWin32Error();
        var region = QueryContext(message);
        return new Win32Exception(error, $"{message} Win32 error {error}: {new Win32Exception(error).Message}{region}");
    }

    private string QueryContext(string message)
    {
        var marker = message.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return string.Empty;
        }

        var end = marker + 2;
        while (end < message.Length && Uri.IsHexDigit(message[end]))
        {
            end++;
        }

        if (!nint.TryParse(message[(marker + 2)..end], System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            return string.Empty;
        }

        var region = Query(address);
        return region is null ? " Memory region: unavailable." : $" Memory region: {region}.";
    }
}
