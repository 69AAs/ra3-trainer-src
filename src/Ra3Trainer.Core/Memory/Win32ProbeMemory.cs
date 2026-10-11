using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ra3Trainer.Core.Memory;

/// <summary>独立只读句柄。生命周期只包含 OpenProcess / RPM / VQE / CloseHandle。</summary>
public sealed class Win32ProbeMemory : IProbeMemory, IDisposable
{
    private readonly IWin32MemoryApi _api;
    private readonly SafeProcessHandle _handle;

    public Win32ProbeMemory(int processId) : this(processId, Kernel32MemoryApi.Instance) { }

    internal Win32ProbeMemory(int processId, IWin32MemoryApi api)
    {
        _api = api;
        _handle = api.OpenProcess(0x0410, false, processId); // QUERY_INFORMATION | VM_READ only
        if (_handle.IsInvalid)
        {
            var error = api.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error, "无法打开只读采样句柄。");
        }
    }

    public byte[] ReadBytes(nint address, int count)
    {
        if (count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = new byte[count];
        // 调用失败和“读到了但短读”要分开：后者时 GetLastWin32Error 是上一次残留值，
        // 拿来报错会给出与本次无关的错误码、掩盖真实原因（跨页只有半页可读时就会短读）。
        if (!_api.ReadProcessMemory(_handle, address, bytes, bytes.Length, out var read))
            throw new Win32Exception(_api.GetLastWin32Error(), $"采样字段读取失败 at 0x{address:X}。");
        if (read != count)
            throw new Win32Exception($"采样字段读取不完整：需要 {count} 字节、实际 {read} 字节 at 0x{address:X}。");
        return bytes;
    }

    public Win32MemoryRegion? Query(nint address)
    {
        if (_api.VirtualQueryEx(_handle, address, out var info,
                (nuint)Marshal.SizeOf<Win32MemoryBasicInformation>()) == 0) return null;
        return new(info.BaseAddress, info.RegionSize, info.AllocationProtect, info.State, info.Protect, info.Type);
    }

    public void Dispose() => _handle.Dispose();
}
