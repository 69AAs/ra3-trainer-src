namespace Ra3Trainer.Core.Memory;

public sealed class SequentialRemoteAllocator : IRemoteAllocator
{
    private nint _next;

    public SequentialRemoteAllocator(nint startAddress)
    {
        _next = startAddress;
    }

    public nint Allocate(int size)
    {
        // 基址为 0 是"没有有效分配器"的信号（TrainerSession 未传分配器时就是这个默认值）：
        // 原来照发 0 出去，等于把空页当成合法块地址写——静默失败。这里拒掉，在调用点炸出来。
        if (_next == 0)
        {
            throw new InvalidOperationException("远程块分配器没有有效基址（0），拒绝分配。");
        }

        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "分配大小必须为正。");
        }

        var address = _next;
        var next = (nint)((ulong)_next + (ulong)(uint)size);
        if (next == 0 || (ulong)next < (ulong)_next)
        {
            // 游标回绕等于把后面的块发到已用区域上，必须当场拒绝而不是继续发。
            throw new InvalidOperationException($"远程块分配游标溢出：0x{_next:X} + {size}。");
        }

        _next = next;
        return address;
    }
}
