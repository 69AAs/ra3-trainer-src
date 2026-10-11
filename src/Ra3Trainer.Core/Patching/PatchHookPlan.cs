namespace Ra3Trainer.Core.Patching;

public sealed record PatchHookPlan(
    string Address,
    string Target,
    int PatchLength,
    byte[] OriginalBytes)
{
    // 这个数组会先被 Install 写进游戏内存，再被 RestoreAll 原样写回来：
    // 只要有人拿到同一个引用改掉一个字节，恢复写回的就是被改过的字节。
    // 所以存取都过副本。注意**不做长度校验**：长度不一致要由 PatchEngine.Install 在
    // 「写入任何字节之前」拒绝（PatchEngineTests 钉着这条），校验若放在构造期就把那道兜底架空了。
    private readonly byte[] _originalBytes = (byte[])(OriginalBytes ?? throw new ArgumentNullException(nameof(OriginalBytes))).Clone();

    public byte[] OriginalBytes
    {
        get => (byte[])_originalBytes.Clone();
        init => _originalBytes = (byte[])(value ?? throw new ArgumentNullException(nameof(value))).Clone();
    }

    public string SectionTitle { get; init; } = string.Empty;
}
