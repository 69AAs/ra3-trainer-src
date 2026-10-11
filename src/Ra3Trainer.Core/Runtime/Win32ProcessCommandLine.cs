using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// 读另一个进程的**命令行**（只读，不改目标）。
///
/// <para>为什么要它：本机实测（2026-10-07，游戏在跑）里，原版与所有 mod 的游戏进程名都是
/// <c>ra3_1.12.game</c> —— 进程名认不出 mod；但命令行 <c>-modConfig &lt;skudef&gt;</c> 直接点名当前 mod，
/// 是最硬的信号。命令行在 PEB 里，Windows 没有「读别人命令行」的托管 API，所以走
/// <c>NtQueryInformationProcess</c> + <c>ReadProcessMemory</c>。</para>
///
/// <para><b>口径</b>：目标恒为 32 位（RA3 是 32 位，修改器也发布 x86），所以用 **WOW64 的 32 位 PEB**
/// 与 x86 偏移（PEB+0x10 → ProcessParameters，+0x40 → CommandLine 的 UNICODE_STRING）。
/// 本机用同样偏移从 64 位进程考古验证过：读出的正是
/// <c>... -modConfig D:\Games\CoronaLauncher\CoronaData\Corona\corona_3.258.skudef ...</c>。</para>
///
/// <para>任何一步失败都返回 null（调用方落到其它信号）—— 读命令行是**加分项**，不许把识别搞崩。</para>
/// </summary>
public static class Win32ProcessCommandLine
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessWow64Information = 26;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // x86 RTL_USER_PROCESS_PARAMETERS 里 CommandLine 的偏移；x86 PEB 里 ProcessParameters 的偏移。
    private const int PebProcessParameters32 = 0x10;
    private const int ProcessParametersCommandLine32 = 0x40;

    /// <summary>读不到就返回 null（不抛）。</summary>
    public static string? TryRead(int processId)
    {
        try
        {
            return Read(processId);
        }
        catch
        {
            return null;
        }
    }

    private static string? Read(int processId)
    {
        using var handle = Kernel32MemoryApi.Instance.OpenProcess(
            ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        // 32 位目标：优先要 WOW64 的 32 位 PEB（64/32 位调用方都能拿）；拿不到再退回常规 PEB。
        var peb = QueryWow64Peb(handle);
        if (peb == 0)
        {
            peb = QueryBasicPeb(handle);
        }

        return peb == 0 ? null : ReadCommandLineFromPeb32(handle, peb);
    }

    private static nint QueryWow64Peb(SafeProcessHandle handle)
    {
        // ULONG_PTR（64 位调用方 8 字节 / 32 位调用方 4 字节）。
        var buffer = new byte[IntPtr.Size];
        var status = NtQueryInformationProcess(handle, ProcessWow64Information, buffer, buffer.Length, out _);
        if (status != 0)
        {
            return 0;
        }

        var value = IntPtr.Size == 8
            ? (nint)(long)BitConverter.ToUInt64(buffer, 0)
            : (nint)BitConverter.ToUInt32(buffer, 0);
        return value;
    }

    private static nint QueryBasicPeb(SafeProcessHandle handle)
    {
        var size = Marshal.SizeOf<ProcessBasicInformation32>();
        var info = default(ProcessBasicInformation32);
        var status = NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, size, out _);
        return status == 0 ? info.PebBaseAddress : 0;
    }

    private static string? ReadCommandLineFromPeb32(SafeProcessHandle handle, nint peb)
    {
        if (!TryReadUInt32(handle, peb + PebProcessParameters32, out var parameters) || parameters == 0)
        {
            return null;
        }

        // UNICODE_STRING{x86}: Length(2) MaximumLength(2) Buffer(4)
        var header = new byte[8];
        if (!ReadExact(handle, unchecked((nint)(parameters + ProcessParametersCommandLine32)), header))
        {
            return null;
        }

        var length = BitConverter.ToUInt16(header, 0);
        var buffer = BitConverter.ToUInt32(header, 4);
        if (length == 0 || length > 0x8000 || (length & 1) != 0 || buffer == 0)
        {
            return null;
        }

        var text = new byte[length];
        return ReadExact(handle, unchecked((nint)buffer), text)
            ? System.Text.Encoding.Unicode.GetString(text)
            : null;
    }

    private static bool TryReadUInt32(SafeProcessHandle handle, nint address, out uint value)
    {
        var buffer = new byte[4];
        if (!ReadExact(handle, address, buffer))
        {
            value = 0;
            return false;
        }

        value = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    private static bool ReadExact(SafeProcessHandle handle, nint address, byte[] buffer) =>
        Kernel32MemoryApi.Instance.ReadProcessMemory(handle, address, buffer, buffer.Length, out var read) &&
        read == buffer.Length;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation32
    {
        public int ExitStatus;
        public nint PebBaseAddress;         // IntPtr：x86 4 字节 / x64 8 字节，结构自适配
        public nint AffinityMask;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int informationClass,
        byte[] information,
        int informationLength,
        out int returnLength);

    [DllImport("ntdll.dll", SetLastError = false)]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle process,
        int informationClass,
        ref ProcessBasicInformation32 information,
        int informationLength,
        out int returnLength);
}
