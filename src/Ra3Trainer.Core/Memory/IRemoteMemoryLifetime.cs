namespace Ra3Trainer.Core.Memory;

public interface IRemoteMemoryLifetime
{
    void Free(nint baseAddress);
}

public interface IRemoteAddressSpaceSource
{
    RemoteAddressSpaceSnapshot InspectAddressSpace();
}

public sealed record RemoteAddressSpaceSnapshot(
    ulong FreeBytes, ulong LargestAlignedBlock, int AlignedRegionCount,
    bool Complete, DateTimeOffset CapturedAt, string? Error = null)
{
    // Complete=false 时 Error 契约上应非空；万一为空，至少给一个非空原因，别让用户看到空的原因。
    private const string UnknownErrorReason = "未知原因";

    public string Summary => Complete
        ? $"可用地址空间 {FreeBytes:N0} 字节；64KB 对齐最大块 {LargestAlignedBlock:N0} 字节；可用区 {AlignedRegionCount} 个（{CapturedAt:HH:mm:ss}）"
        : $"地址空间探测不完整：{Error ?? UnknownErrorReason}（已观察空闲 {FreeBytes:N0} 字节；不能据此判定已满）";

    public string ExhaustedMessage => "游戏 32 位地址空间已满，请重启游戏后再试。" +
        "长时间游戏、大地图或多 mod 可能耗尽地址空间。" + Summary;
}
