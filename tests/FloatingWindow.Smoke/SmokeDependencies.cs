using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

// These doubles have no native/process fallback. An unexpected call fails closed.
internal sealed class SmokeLauncher : ILauncher
{
    public const string Failure = "请先在上面选一个启动方式（或点「浏览…」指定程序）。";
    public List<string> Calls { get; } = new();
    public Process Start(string launcherPath, string arguments = "")
    {
        Calls.Add(launcherPath);
        throw new InvalidOperationException(Failure);
    }
}

internal sealed class SmokeLocator : IProcessLocator
{
    public bool AllowNonStandardVersion { get; set; }
    public int Finds { get; private set; }
    public int Snapshots { get; private set; }
    public TrainerTarget? Find(string processNameOrPath) { Finds++; return null; }
    public IReadOnlyList<TrainerProcessCandidate> Snapshot() { Snapshots++; return []; }
}

internal sealed class SmokeMemory : IProcessMemory, IRemoteAllocator
{
    public int Calls { get; private set; }
    public byte[] ReadBytes(nint address, int count) { Calls++; throw new InvalidOperationException("Unexpected memory read"); }
    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) { Calls++; throw new InvalidOperationException("Unexpected memory write"); }
    public nint Allocate(int size) { Calls++; throw new InvalidOperationException("Unexpected allocation"); }
}

/// <summary>
/// 能**回答读取**的坐标夹具。为什么必须有它：产品的坐标动作入口（MainViewModel.PrepareCoordinateActionAsync）
/// 会先调 controller.HasValidCurrentCoordinates(...) **预检**，只有预检判定"缓存坐标无效"才进 3 秒倒计时。
/// 预检本身就要读内存 ⇒ "任何读都抛"的 SmokeMemory 永远走不到倒计时分支，只能测出"读不了就拒绝"
/// （夹具走不到被测分支 = 断言测的是空气）。
/// 这个夹具给出一条合法的世界根/坐标系，并用 SetCoordinates 控制缓存坐标有效/无效，
/// 从而把 **立即派发** 与 **倒计时** 两条分支都真走一遍；写入 ActionDispatch 记进 Dispatches（派发证据）。
/// </summary>
internal sealed class SmokeCoordinateMemory : IProcessMemory, IProbeMemory
{
    private readonly Dictionary<nint, byte> _bytes = new();
    public List<byte> Dispatches { get; } = new();
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    /// <summary>倒计时结束后"这次转换拿到"的坐标：0 = 无效（会被拒），200 = 有效。</summary>
    public float ConversionCoordinate { get; set; } = 200;
    public AddressResolver Resolver { get; } = new(0x400000, new Dictionary<string, nint>
    {
        ["iEnable"] = 0x10000,
        ["ID"] = 0x11000,
        ["MustCode"] = 0x120000
    });

    public SmokeCoordinateMemory()
    {
        Word(Resolver.Resolve("ra3_1.12.game+8DAEFC"), 0xF0000);
        Word(Resolver.Resolve("ID"), 0xD0000);
        Word(Resolver.Resolve("ra3_1.12.game+8E6C58"), 0xE0000);
        SetCoordinates(0);   // 默认"缓存坐标无效" ⇒ 点击后进倒计时分支
    }

    private void Word(nint address, uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        for (var i = 0; i < 4; i++) _bytes[address + i] = bytes[i];
    }

    public void SetCoordinates(float x)
    {
        foreach (var offset in new[] { 0, 4, 8 })
            Word(Resolver.Resolve(RemoteStateLayout.WorldCoordinates) + offset, BitConverter.SingleToUInt32Bits(x));
    }

    public Win32MemoryRegion? Query(nint address) => new(0x10000, 0xFFFE0000, 4, 0x1000, 4, 0x20000);

    public byte[] ReadBytes(nint address, int count)
    {
        Reads++;
        return Enumerable.Range(0, count).Select(offset => _bytes.GetValueOrDefault(address + offset)).ToArray();
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> data)
    {
        Writes++;
        for (var i = 0; i < data.Length; i++) _bytes[address + i] = data[i];
        if (address != Resolver.Resolve(RemoteStateLayout.ActionDispatch)) return;
        Dispatches.Add(data[0]);
        if (data[0] == 14)
        {
            // 派发第一段之后，游戏侧会刷新一份新坐标 —— 用 ConversionCoordinate 决定它有效还是无效。
            SetCoordinates(ConversionCoordinate);
            Increment(RemoteStateLayout.ConversionEpoch);
        }
        Increment(RemoteStateLayout.DispatchEpoch);
        Word(address, 0);
    }

    private void Increment(string name)
    {
        var address = Resolver.Resolve(name);
        Word(address, BitConverter.ToUInt32(ReadBytes(address, 4)) + 1);
    }
}

internal sealed class SmokeProbeMemory : IProbeMemory, IDisposable
{
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }
    public byte[] ReadBytes(nint address, int count)
    {
        if (Disposed) throw new InvalidOperationException("Read after probe disposal");
        Reads++;
        return new byte[count];
    }
    public Win32MemoryRegion? Query(nint address)
    {
        // 真 Win32ProcessMemory 处置后连 Query 都会失败（句柄已释放）：假件必须同样失败，
        // 否则"停表后还有排队 tick 去查询"这种生产 bug 会被假件放过去。
        if (Disposed) throw new InvalidOperationException("Query after probe disposal");
        return new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0);
    }
    public void Dispose() => Disposed = true;
}

internal sealed class SmokeHighProbeMemory : IProbeMemory
{
    public byte[] ReadBytes(nint address, int count)
    {
        uint value = unchecked((uint)address) switch
        {
            0xCDB73C => 0x20000, 0x2005C => 1,
            0x20050 or 0x20054 => 0x30000, 0x30008 => 0x96C5D080,
            0x96C5D1B8 => 0x50000, 0x5033C => 0xB0000000,
            0xB0000004 => 0x42C80000, _ => 0
        };
        // 真 ReadBytes 返回的永远是调用方请求的 count 字节；只 Take(count) 会在 count>4 时短一截，
        // 让"调用方误处理短读"这类 bug 漏过去。这里按请求长度补齐（不足补 0）。
        var bytes = BitConverter.GetBytes(value);
        if (count <= bytes.Length) return bytes.Take(Math.Max(0, count)).ToArray();
        var padded = new byte[count];
        bytes.CopyTo(padded, 0);
        return padded;
    }
    public Win32MemoryRegion? Query(nint address) => new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0);
}

internal sealed class SmokeSuspender : IProcessSuspender
{
    public List<string> Events { get; } = new();
    public IDisposable Suspend(int processId) { Events.Add("suspend"); return new Scope(Events); }
    private sealed class Scope(List<string> events) : IDisposable, IRemoteCodeQuiescence
    {
        public void EnsureUnreferenced(IProcessMemory memory, ulong address, ulong size)
        {
            events.Add("validate");
            if (size != TrainerSession.RemoteBlockSize) throw new InvalidOperationException("Wrong combined block size");
        }
        public void Dispose() => events.Add("resume");
    }
}

internal sealed class SmokeSessionResources(List<string> events) :
    IProcessMemory, IRemoteAllocator, IRemoteMemoryLifetime, ITargetProcessState
{
    private readonly Dictionary<nint, byte> _bytes = new();
    public nint BaseAddress { get; } = (nint)0x700000;
    public int Allocations { get; private set; }
    public int Frees { get; private set; }
    public nint Allocate(int size)
    {
        if (size != TrainerSession.RemoteBlockSize) throw new InvalidOperationException("Wrong allocation size");
        Allocations++;
        return BaseAddress;
    }
    public void Free(nint address)
    {
        if (address != BaseAddress || events.LastOrDefault() != "validate")
            throw new InvalidOperationException("Free must follow validation while suspended");
        Frees++;
        events.Add("free");
    }
    public byte[] ReadBytes(nint address, int count) =>
        Enumerable.Range(0, count).Select(offset => _bytes.GetValueOrDefault(address + offset)).ToArray();
    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++) _bytes[address + i] = bytes[i];
    }
    public bool IsRunning(int? processId) => true;
}

// Exercise the real Win32ProcessMemory wrapper with a synthetic API, never an OS handle.
internal sealed class SmokeMemoryApi : IWin32MemoryApi
{
    private bool _allocated;
    public (nint Address, nuint Size, uint Type)? Freed { get; private set; }
    public int Opens { get; private set; }
    public int GetLastWin32Error() => 87;
    public SafeProcessHandle OpenProcess(uint access, bool inherit, int pid)
    { Opens++; return new SafeProcessHandle((nint)1234, ownsHandle: false); }
    public nint VirtualAllocEx(SafeProcessHandle p, nint address, nuint size, uint type, uint protect)
    { _allocated = true; return (nint)0x10000; }
    public bool VirtualFreeEx(SafeProcessHandle p, nint address, nuint size, uint type)
    { Freed = (address, size, type); _allocated = false; return true; }
    public nuint VirtualQueryEx(SafeProcessHandle p, nint address, out Win32MemoryBasicInformation info, nuint length)
    {
        info = new() { BaseAddress = (nint)0x10000, RegionSize = 0x7FFE0000,
            State = _allocated ? 0x1000u : 0x10000u, Protect = 0x40 };
        return (ulong)(nuint)address < 0x7FFF0000 ? length : 0;
    }
    public bool ReadProcessMemory(SafeProcessHandle p, nint a, byte[] b, int s, out int read)
    { read = 0; throw new InvalidOperationException("Unexpected native read"); }
    public bool WriteProcessMemory(SafeProcessHandle p, nint a, byte[] b, int s, out int written)
    { written = 0; throw new InvalidOperationException("Unexpected native write"); }
    public bool VirtualProtectEx(SafeProcessHandle p, nint a, nuint s, uint n, out uint old)
    { old = 0; throw new InvalidOperationException("Unexpected native protection change"); }
    public bool FlushInstructionCache(SafeProcessHandle p, nint a, nuint s) =>
        throw new InvalidOperationException("Unexpected native cache flush");
}
