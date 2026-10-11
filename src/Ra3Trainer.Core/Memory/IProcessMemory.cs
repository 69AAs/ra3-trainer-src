namespace Ra3Trainer.Core.Memory;

/// <summary>
/// 目标进程内存读写。
/// <para>失败契约：不可读/不可写时抛（Win32Exception/InvalidOperationException），不要吞成空数组；
/// <see cref="ReadBytes"/> 允许返回长度小于 count 的数组（尾部跨到未提交页），调用方必须校验长度。</para>
/// <para>线程安全：实现本身不保证；并发访问由调用方串行化（见 FeatureMemoryAccess 的锁与停止/排空契约）。</para>
/// <para><see cref="WriteBytes"/> 契约：实现**不得**保留传入的 bytes（它是只读 span，
/// 可能指向调用方的栈内存，返回后即失效）。若无法完整写入，必须抛异常（Win32Exception/InvalidOperationException），
/// 不得静默只写一部分、也不得吞掉访问拒绝/进程退出等失败。</para>
/// </summary>
public interface IProcessMemory
{
    byte[] ReadBytes(nint address, int count);

    void WriteBytes(nint address, ReadOnlySpan<byte> bytes);
}
