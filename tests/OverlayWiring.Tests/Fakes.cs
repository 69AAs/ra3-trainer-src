using System.Diagnostics;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

internal sealed class SmokeLauncher : ILauncher
{
    /// <summary>启动尝试次数：异常本身证明"不可能真启动"，但计数让测试仍能断言"根本没被调用过"。</summary>
    public int Starts { get; private set; }
    public Process Start(string path, string arguments = "")
    {
        Starts++;
        throw new InvalidOperationException("No real process launch allowed");
    }
}
internal sealed class SmokeLocator : IProcessLocator
{
    public bool AllowNonStandardVersion { get; set; }
    public TrainerTarget? Find(string name) => null;
    public IReadOnlyList<TrainerProcessCandidate> Snapshot() => [];
}
internal sealed class SmokeSuspender : IProcessSuspender
{
    public List<string> Events { get; } = [];
    public IDisposable Suspend(int pid) { Events.Add("suspend"); return new Scope(Events); }
    private sealed class Scope(List<string> events) : IDisposable, IRemoteCodeQuiescence
    {
        public void EnsureUnreferenced(IProcessMemory memory, ulong address, ulong size)
        {
            events.Add("validate");
            // 生产端 TrainerSession.cs:218 释放的是整个合并块：尺寸写错必须在这里炸，而不是悄悄通过。
            if (size != TrainerSession.RemoteBlockSize) throw new InvalidOperationException("Wrong combined block size");
        }
        public void Dispose() => events.Add("resume");
    }
}
internal sealed class SmokeSessionResources(List<string> events) : IProcessMemory, IRemoteAllocator, IRemoteMemoryLifetime, ITargetProcessState
{
    private readonly Dictionary<nint, byte> _bytes = [];
    private nint? _allocated;
    public int Frees { get; private set; }
    public nint Allocate(int size) => (_allocated = (nint)0x700000).Value;
    public void Free(nint address)
    {
        // 按 Allocate 真正返回的地址校验（而不是硬编码字面量）：分配地址一变就会在这里暴露。
        if (address != _allocated || events.LastOrDefault() != "validate") throw new InvalidOperationException("Unproved fake release");
        _allocated = null;
        Frees++; events.Add("free");
    }
    public byte[] ReadBytes(nint address, int count) => Enumerable.Range(0, count).Select(i => _bytes.GetValueOrDefault(address + i)).ToArray();
    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) { for (int i = 0; i < bytes.Length; i++) _bytes[address + i] = bytes[i]; }
    public bool IsRunning(int? pid) => true;
}
