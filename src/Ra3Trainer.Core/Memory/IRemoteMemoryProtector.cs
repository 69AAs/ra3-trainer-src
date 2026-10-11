namespace Ra3Trainer.Core.Memory;

/// <summary>
/// 把一段远程内存改成可执行可写（装跳转 / 写回原始字节都需要）。
/// <para>契约：本接口**不负责恢复**原有保护属性，也不负责在结束后翻回只读 ——
/// 这些区域会被登记进"已改保护"清单，由释放路径统一处理（见 Win32ProcessMemory 的
/// _executeReadWriteRanges 与 Free）。调用方不要假设它会还原。</para>
/// </summary>
public interface IRemoteMemoryProtector
{
    void ProtectExecuteReadWrite(nint address, int size);
}
