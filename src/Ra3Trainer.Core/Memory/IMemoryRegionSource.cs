namespace Ra3Trainer.Core.Memory;

// 契约说明：
// - EnumerateReadableRegions 返回的是**快照**，不是活视图：调用方要拿它循环读写时必须自行重取。
// - 允许**部分失败**：Win32 实现在 VirtualQueryEx 失败处 break，可能返回不完整列表；
//   RegionSize 大于 int.MaxValue 时会被截断（见 Win32ProcessMemory 的扫描上限）。
// - 返回的区域可能只覆盖"可读"页；是否可执行由 MemoryRegion.IsExecutable 单独标注。

/// <summary>一段可读内存区域（用于只读扫描，如找内部名字符串）。</summary>
public readonly record struct MemoryRegion(nint BaseAddress, int Size, bool IsExecutable = false)
{
    public nint EndAddress => BaseAddress + Size;
}

/// <summary>能列出可读内存区域的来源（Win32ProcessMemory 实现它）。</summary>
public interface IMemoryRegionSource
{
    IReadOnlyList<MemoryRegion> EnumerateReadableRegions();
}
