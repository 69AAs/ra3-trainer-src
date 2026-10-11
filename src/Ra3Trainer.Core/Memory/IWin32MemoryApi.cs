using Microsoft.Win32.SafeHandles;

namespace Ra3Trainer.Core.Memory;

/// <summary>
/// 内核内存 API 适配层（生产实现是 Kernel32MemoryApi）。
/// <para>句柄归属：所有方法的 <c>process</c> 都是**借用的**句柄 —— 由调用方（Win32ProcessMemory）拥有，
/// 并负责保证整个调用期间它保持存活。实现**绝不能**释放、关闭它或取得其所有权，
/// 否则会与 Win32ProcessMemory 自己的 Dispose 双重释放、或关闭一个仍被使用的句柄。</para>
/// <para><see cref="VirtualFreeEx"/> 刻意保留一个默认抛 <see cref="NotSupportedException"/> 的占位：
/// 纯读/测试替身可以只实现自己用到的成员，只有真正承担远程释放责任的实现才覆盖它。</para>
/// </summary>
internal interface IWin32MemoryApi
{
    int GetLastWin32Error();

    SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesRead);

    bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, byte[] buffer, int size, out int bytesWritten);

    nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect);

    bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint freeType) =>
        throw new NotSupportedException("未实现远程释放。");

    bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint newProtect, out uint oldProtect);

    nuint VirtualQueryEx(SafeProcessHandle process, nint address, out Win32MemoryBasicInformation buffer, nuint length);

    bool FlushInstructionCache(SafeProcessHandle process, nint baseAddress, nuint size);
}

/// <summary>
/// 必须和原生 MEMORY_BASIC_INFORMATION 逐字节一致，否则 VirtualQueryEx 会失败
/// （踩过：x86 下多塞了 PartitionId/对齐字段，长度 32 ≠ 28，枚举区域回来是空的）。
/// </summary>
internal struct Win32MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
#if WIN64
    public ushort PartitionId;
    public ushort Padding;
#endif
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}
