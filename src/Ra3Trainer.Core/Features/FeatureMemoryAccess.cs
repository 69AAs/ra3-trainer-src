using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using System.Diagnostics;

namespace Ra3Trainer.Core.Features;

/// <summary>Stops controller memory access before the session restores hooks or closes its handle.</summary>
internal sealed class FeatureMemoryAccess(IProcessMemory memory) : IProcessMemory, IProbeMemory
{
    private readonly object _gate = new();
    private bool _stopped;

    public Win32MemoryRegion? Query(nint address)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            return memory is IProbeMemory probe ? probe.Query(address) :
                throw new InvalidOperationException("内存读取器不支持提交状态与页面保护查询，已拒绝执行。");
        }
    }

    public byte[] ReadBytes(nint address, int count)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (ActionEvidence.Current.Value is null) return memory.ReadBytes(address, count);
            var started = Stopwatch.GetTimestamp();
            byte[]? bytes = null;
            Exception? failure = null;
            try { return bytes = memory.ReadBytes(address, count); }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                try { ActionEvidence.Current.Value?.Read(address, count, bytes, failure, started, Stopwatch.GetTimestamp()); }
                catch { /* Evidence must not change reads or exceptions. */ }
            }
        }
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (ActionEvidence.Current.Value is null) { memory.WriteBytes(address, bytes); return; }
            var started = Stopwatch.GetTimestamp();
            Exception? failure = null;
            try { memory.WriteBytes(address, bytes); }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                try { ActionEvidence.Current.Value?.Write(address, bytes, failure, started, Stopwatch.GetTimestamp()); }
                catch { /* Evidence must not change writes or exceptions. */ }
            }
        }
    }

    public void Stop()
    {
        // Returning proves any current native access has finished and no later one can start.
        lock (_gate) _stopped = true;
    }

    /// <summary>
    /// 取证回调（ActionEvidence.Read/Write）是在**持有同一把 _gate** 的情况下调用的：
    /// 它是 stop/drain 屏障的一部分，所以实现必须不阻塞（写盘/等锁会拖住 Stop 的返回）。
    /// 理由：证据必须与原生访问同序，不能挪到锁外，那就只能约束回调本身。
    /// </summary>
    public void Drain()
    {
        // 约定：**先 Stop()（置 _stopped，之后不再放新操作进来）再 Drain()**，
        // FeatureController 正是这个顺序（Stop 在 64 行、Drain 在 74 行）。
        // 单独调 Drain 只证明「取锁那一刻没有原生调用正在进行」，不构成「之后也不会开始」的屏障。
        // Acquiring the same gate proves any native operation already started has returned.
        lock (_gate) { }
    }

    private void ThrowIfStopped()
    {
        if (_stopped) throw new OperationCanceledException("旧会话已停止内存访问。");
    }
}
