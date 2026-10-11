using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Ra3Trainer.Core.Runtime;

/// <summary>Separate host-owned mapping, never part of trainer executable/enable blocks.</summary>
public sealed class OverlayStatusSlot : IDisposable
{
    public const int Size = 356;
    public const uint Magic = 0x3544534f;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly int _targetPid;
    private uint _sequence;
    private readonly object _gate = new();
    private bool _disposed;

    public OverlayStatusSlot(int targetPid, long processCreationTime = 0)
    {
        _targetPid = targetPid;
        _mapping = MemoryMappedFile.CreateOrOpen($@"Local\Ra3Trainer.Overlay.v1.{targetPid}.{processCreationTime:X16}", Size, MemoryMappedFileAccess.ReadWrite);
        try
        {
            _view = _mapping.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            _sequence = _view.ReadUInt32(8) & ~1u;
        }
        catch { _mapping.Dispose(); throw; }
    }

    /// <summary>
    /// 只刷新心跳字段（宿主存活证明）。原生侧用"现在 − 心跳 ≥ 5000"判定修改器掉线并释放面板，
    /// 所以这一笔必须按固定节奏写、且与像素/代次的握手无关（4 字节对齐写在 x86 上本身就是原子的）。
    /// </summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _view.Write(20, unchecked((uint)Environment.TickCount));
        }
    }

    public static byte[] Encode(int hostPid, int targetPid, uint heartbeat, bool enabled,
        bool connected, int count, string version, string action)
    {
        var bytes = new byte[Size];
        Put(0, Magic); Put(4, Size); Put(12, checked((uint)hostPid)); Put(16, checked((uint)targetPid));
        Put(20, heartbeat); Put(24, enabled ? 1u : 0); Put(28, connected ? 1u : 0);
        Put(32, checked((uint)Math.Max(0, count)));
        Text(36, 32, version); Text(100, 128, action);
        return bytes;
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void Text(int offset, int capacity, string value)
        {
            var length = Math.Min(value.Length, capacity - 1);
            if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
            Encoding.Unicode.GetBytes(value.AsSpan(0, length), bytes.AsSpan(offset, length * 2));
        }
    }

    public void Publish(bool enabled, bool connected, int count, string version, string action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var bytes = Encode(Environment.ProcessId, _targetPid, unchecked((uint)Environment.TickCount), enabled, connected, count, version, action);
            // Aligned sequence is published last. DLL rejects torn/odd/expired snapshots.
            _view.Write(8, unchecked(++_sequence | 1u));
            Thread.MemoryBarrier();
            _view.WriteArray(0, bytes, 0, 8);
            _view.WriteArray(12, bytes, 12, Size - 12);
            Thread.MemoryBarrier();
            _sequence = unchecked((_sequence | 1u) + 1);
            _view.Write(8, _sequence);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Publish(false, false, 0, string.Empty, "主机断开"); }
            catch (Exception)
            {
                // 最佳努力信号：映射已关或写失败时不要从 Dispose 抛出（using 会在栈展开途中替换掉原异常）。
                // 原生侧靠 5s 心跳超时自行释放面板，吞掉这次失败的写不影响正确性。
            }
            finally { _disposed = true; _view.Dispose(); _mapping.Dispose(); }
        }
    }
}
