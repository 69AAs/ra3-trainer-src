using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Tests;

internal sealed class FakeProcessMemory : IProcessMemory, IProbeMemory
{
    /// <summary>设成某地址时，该地址所在页的 Query 返回 null —— 覆盖"查询失败/不可读"分支。</summary>
    public nint? UnqueriedPage { get; set; }

    // 返回被查询地址所在的那一页（与 ReadOnlyProbeTests 的本地夹具同款）：
    // 旧实现固定返回 [0x10000, +0xFFFE0000) 的"万用可读区"，任何越界/未提交地址都会被当成可读。
    public Win32MemoryRegion? Query(nint address) =>
        UnqueriedPage is nint missing && (address & ~0xFFF) == (missing & ~0xFFF)
            ? null
            : new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0x20000);
    private readonly Dictionary<nint, byte> _bytes = new();
    public int WriteCount { get; private set; }

    public byte[] ReadBytes(nint address, int count)
    {
        var bytes = new byte[count];
        for (var index = 0; index < count; index++)
        {
            bytes[index] = _bytes.GetValueOrDefault(address + index);
        }
        return bytes;
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        WriteCount++;
        for (var index = 0; index < bytes.Length; index++)
        {
            _bytes[address + index] = bytes[index];
        }
    }

    public byte ReadByte(nint address) => _bytes.GetValueOrDefault(address);
}
