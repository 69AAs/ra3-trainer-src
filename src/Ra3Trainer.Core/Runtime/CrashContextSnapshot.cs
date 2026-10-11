using System.Text;
using System.Text.Json;

namespace Ra3Trainer.Core.Runtime;

public sealed record DiagnosticMemoryRange(string Name, ulong Address, ulong Size)
{
    // Only use a real absolute exception address obtained independently (e.g. from a dump).
    // 写成"先减后比"是有意的：Address+Size 可能溢出 ulong，换成 absoluteAddress < Address + Size 会静默判错。
    public bool Contains(ulong absoluteAddress) => absoluteAddress >= Address && absoluteAddress - Address < Size;
}
public sealed record SessionMemorySnapshot(
    IReadOnlyList<DiagnosticMemoryRange> InstalledHooks,
    IReadOnlyList<DiagnosticMemoryRange> RemoteAllocations,
    IReadOnlyList<RemoteAllocationSnapshot>? Blocks = null,
    Ra3Trainer.Core.Memory.RemoteAddressSpaceSnapshot? AddressSpace = null);

public sealed record RemoteAllocationSnapshot(string Name, ulong Address, ulong Size,
    DateTimeOffset AllocatedAt, DateTimeOffset? ReleasedAt, string State);

public sealed record CrashContextSnapshot(
    DateTimeOffset CapturedAt,
    string TargetIdentity,
    string ExitDescription,
    IReadOnlyList<string> EnabledFeatures,
    SessionMemorySnapshot Memory,
    IReadOnlyList<string> RecentActions)
{
    public string ToReadableText()
    {
        var text = new StringBuilder();
        text.AppendLine($"上下文采集时间：{CapturedAt:O}");
        text.AppendLine($"目标身份：{TargetIdentity}");
        text.AppendLine($"退出情况：{ExitDescription}");
        text.AppendLine("这是修改器记录的上下文快照，不是进程转储；不能证明修改器无责。地址不命中也不能排除较早的数据破坏。");
        text.AppendLine("异常地址与范围命中只表示相关性；必须结合异常线程、调用栈和内存转储确认原因。WER 可能延迟生成或属于旧会话。");
        text.AppendLine("启用功能：" + string.Join("、", EnabledFeatures));
        foreach (var range in Memory.InstalledHooks)
            text.AppendLine($"已安装 Hook：{range.Name} [0x{range.Address:X8}, 0x{range.Address + range.Size:X8})");
        foreach (var range in Memory.RemoteAllocations)
            text.AppendLine($"历史符号（元数据）：{range.Name} [0x{range.Address:X8}, 0x{range.Address + range.Size:X8})");
        foreach (var block in Memory.Blocks ?? [])
            text.AppendLine($"远程块：{block.Name} 0x{block.Address:X8} / 0x{block.Size:X}；{block.State}；申请 {block.AllocatedAt:O}；释放 {block.ReleasedAt:O}");
        if (Memory.AddressSpace is not null) text.AppendLine(Memory.AddressSpace.Summary);
        text.AppendLine("最近会话事件与动作（最多 128 条，日志时间戳）：");
        foreach (var action in RecentActions) text.AppendLine(action);
        return text.ToString();
    }
}

public sealed record LastTargetExit(DateTimeOffset Time, string Description, string EvidencePath);

/// <summary>Persists observation metadata only, never live hook or switch state.</summary>
public static class TargetExitHistory
{
    public static LastTargetExit? Load(string directory)
    {
        try { return JsonSerializer.Deserialize<LastTargetExit>(File.ReadAllText(Path.Combine(directory, "last-target-exit.json"))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(string directory, LastTargetExit exit)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "last-target-exit.json");
        // 临时名唯一：退出快照与错误路径可能并发写同一目录，固定的 .tmp 会被另一个线程
        // 在写入途中 Move/覆盖，抛 IOException 或留下半截文件。
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(exit));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
