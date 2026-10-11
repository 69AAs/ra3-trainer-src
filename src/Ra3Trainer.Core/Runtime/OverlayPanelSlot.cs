using System.IO.MemoryMappedFiles;

namespace Ra3Trainer.Core.Runtime;

/// <summary>Pagefile-backed, host-only writer. No disk file and no game memory offsets.</summary>
public sealed class OverlayPanelSlot : IDisposable
{
    public const uint Magic = 0x374C4E50, Abi = 1, Pbgra32 = 1;
    /// <summary>reserved[0]: panel mode (0/1) shared with the native reader.</summary>
    public const int ModeOffset = 88;
    /// <summary>M2a: forwarded to the native header; never changes host input behaviour.</summary>
    public bool PanelMode { get; set; }
    public const int HeaderSize = 128, MaxWidth = 512, MaxHeight = 512;
    public const int PixelCapacity = MaxWidth * MaxHeight * 4;
    public const int EventReserve = 4096, Size = HeaderSize + PixelCapacity + EventReserve;
    // M3: the game side writes its event block here. Layout is mirrored from
    // tools/Dx9OverlayHarness/panel-input.h and both sides ship in one package.
    public const int EventOffset = HeaderSize + PixelCapacity;
    public const uint EventAbi = 2;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _gate = new();
    private byte[] _last = [];
    private uint _sequence, _generation;

    public readonly record struct PanelEvent(uint Sequence, uint Mode, int X, int Y, uint Left, uint Wheel,
                                             uint Clicks, int ClickX, int ClickY, uint Abi, uint KeyCount, uint LastKey);

    /// <summary>
    /// Reads the game side's event block with a sequence guard and an ABI check.
    /// A torn read, a mid-write block or an unknown layout returns false: never guess.
    /// </summary>
    public bool TryReadEvent(out PanelEvent value)
    {
        value = default;
        if (_view is null) return false;
        // 与写入侧共用 _gate，并且先看 _disposed：Dispose 之后 _view 已经释放，
        // 原来的 ReadUInt32 会抛 ObjectDisposedException —— 而本方法的契约是"读不到就返回 false，绝不猜"。
        lock (_gate)
        {
        if (_disposed) return false;
        const int seq = 0, mode = 4, x = 8, y = 12, left = 16, wheel = 20, clicks = 24, clickX = 28, clickY = 32,
                  abi = 36, keyCount = 40, lastKey = 44;
        var first = _view.ReadUInt32(EventOffset + seq);
        if ((first & 1u) != 0) return false;                       // the game is mid-write
        var layout = _view.ReadUInt32(EventOffset + abi);
        if (layout != EventAbi) return false;                      // unknown layout: ignore
        var candidate = new PanelEvent(first, _view.ReadUInt32(EventOffset + mode),
            (int)_view.ReadUInt32(EventOffset + x), (int)_view.ReadUInt32(EventOffset + y),
            _view.ReadUInt32(EventOffset + left), _view.ReadUInt32(EventOffset + wheel),
            _view.ReadUInt32(EventOffset + clicks),
            (int)_view.ReadUInt32(EventOffset + clickX), (int)_view.ReadUInt32(EventOffset + clickY), layout,
            _view.ReadUInt32(EventOffset + keyCount), _view.ReadUInt32(EventOffset + lastKey));
        Thread.MemoryBarrier();
        if (_view.ReadUInt32(EventOffset + seq) != first) return false;  // torn snapshot
        value = candidate;
        return true;
        }
    }
    private int _width, _height, _x, _y;
    private bool _disposed;
    public uint Generation => _generation;
    public static string Name(int pid, long created) => $@"Local\Ra3Trainer.Panel.v1.{pid}.{created:X16}";

    public OverlayPanelSlot(int targetPid, long created)
    {
        // CreateNew rejects a second writer; reconnect retries after DLL closes its read view.
        _mapping = MemoryMappedFile.CreateNew(Name(targetPid, created), Size, MemoryMappedFileAccess.ReadWrite);
        try
        {
            _view = _mapping.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            _view.Write(0, Magic); _view.Write(4, Size); _view.Write(8, Abi);
            _view.Write(16, targetPid); _view.Write(20, Environment.ProcessId);
            _view.Write(60, HeaderSize); _view.Write(64, HeaderSize + PixelCapacity);
            _view.Write(68, EventReserve); // Event ring / ACK reserved, never consumed in M1.
            _view.Write(80, BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()));
        }
        catch { _view?.Dispose(); _mapping.Dispose(); throw; }
    }

    public static uint Checksum(ReadOnlySpan<byte> pixels)
    {
        uint hash = 2166136261;
        foreach (byte value in pixels) hash = unchecked((hash ^ value) * 16777619);
        return hash;
    }

    /// <summary>
    /// 只刷新面板头里的心跳（offset 24）。不进 Begin/End 的序列握手：心跳只是"修改器还活着"的
    /// 证明，跟像素代次无关，写它不该让原生侧丢一帧。
    /// </summary>
    public void Touch()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _view.Write(24, unchecked((uint)Environment.TickCount));
        }
    }

    public bool Publish(byte[] pixels, int width, int height, int x, int y)
    {
        if (width is < 1 or > MaxWidth || height is < 1 or > MaxHeight ||
            pixels.Length != width * height * 4 || x is < 0 or > 8192 || y is < 0 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(pixels), "面板尺寸或位置越界，已停止绘制。");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool changed = width != _width || height != _height || x != _x || y != _y || !pixels.AsSpan().SequenceEqual(_last);
            Begin();
            if (changed)
            {
                _view.WriteArray(HeaderSize, pixels, 0, pixels.Length);
                _view.Write(32, width); _view.Write(36, height); _view.Write(40, width * 4);
                _view.Write(44, Pbgra32); _view.Write(48, ++_generation);
                _view.Write(52, x); _view.Write(56, y);
                _view.Write(72, pixels.Length); _view.Write(76, Checksum(pixels));
                _last = (byte[])pixels.Clone(); _width = width; _height = height; _x = x; _y = y;
            }
            var mode = PanelMode ? 1u : 0u;
            // 这里**不能**再开一对 Begin/End：外层第 104 行的 Begin 已经打开（序列为奇数 = 正在写）。
            // 内层再 Begin 会让序列变回偶数，读者会以为拿到稳定快照，读到只写了一半的字段。
            if (_view.ReadUInt32(ModeOffset) != mode) _view.Write(ModeOffset, mode);
            _view.Write(24, unchecked((uint)Environment.TickCount)); _view.Write(28, 1u);
            End();
            return changed;
        }
    }

    public void Disable()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Begin(); _view.Write(28, 0u); End();
        }
    }
    private void Begin() { _view.Write(12, ++_sequence); Thread.MemoryBarrier(); }
    private void End() { Thread.MemoryBarrier(); _view.Write(12, ++_sequence); }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Disable(); }
            finally { _disposed = true; _view.Dispose(); _mapping.Dispose(); }
        }
    }
}
