namespace Ra3Trainer.Core.Patching;

public static class X86PatchEncoder
{
    public static byte[] EncodeNearJumpWithNops(nint source, nint target, int totalLength)
    {
        if (totalLength < 5)
        {
            throw new ArgumentOutOfRangeException(nameof(totalLength), "x86 near jumps require at least 5 bytes.");
        }

        // 0 地址 = 没解析出来的地址（解析失败会被上层兜，但 IntPtr.Zero 那种"空"值不会）：
        // 这里写进游戏内存的是跳转指令，编出一个跳向低地址的跳转等于让游戏当场崩。
        if (source == 0)
        {
            throw new InvalidOperationException("补丁源地址未解析（0），拒绝编码跳转。");
        }

        if (target == 0)
        {
            throw new InvalidOperationException("补丁目标地址未解析（0），拒绝编码跳转。");
        }

        var bytes = Enumerable.Repeat((byte)0x90, totalLength).ToArray();
        bytes[0] = 0xE9;
        // 超 ±2GB 时 checked 抛的是 OverflowException，而调用方只 catch InvalidOperationException：
        // 换成同一类型并带上距离，别让"跳太远"变成一次未处理异常。
        var distance = (long)target - ((long)source + 5);
        if (distance < int.MinValue || distance > int.MaxValue)
        {
            throw new InvalidOperationException($"跳转距离 {distance} 超出 rel32 范围（±2GB）。");
        }

        var relative = (int)distance;
        BitConverter.GetBytes(relative).CopyTo(bytes, 1);
        return bytes;
    }
}
