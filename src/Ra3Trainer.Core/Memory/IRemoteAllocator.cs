namespace Ra3Trainer.Core.Memory;

/// <summary>
/// 在目标进程里分配一块远程内存。
/// <para>契约：size 必须为正；返回的地址必须是可用的非零地址（0 表示"没有有效基址"，实现应拒绝）。</para>
/// <para>本接口没有释放操作：实际的释放由持有 OS 内存的实现另外通过 IRemoteMemoryLifetime 承担；
/// 纯地址分配器（SequentialRemoteAllocator）不拥有内存，只能"退休"它的世代。</para>
/// <para>线程安全：Win32ProcessMemory 用锁保护，SequentialRemoteAllocator 依赖单线程调用。</para>
/// </summary>
public interface IRemoteAllocator
{
    nint Allocate(int size);
}
