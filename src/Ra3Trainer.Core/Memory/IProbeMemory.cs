namespace Ra3Trainer.Core.Memory;

/// <summary>采样器唯一的远程能力；故意不继承任何可写接口。</summary>
public interface IProbeMemory
{
    byte[] ReadBytes(nint address, int count);
    Win32MemoryRegion? Query(nint address);
}
