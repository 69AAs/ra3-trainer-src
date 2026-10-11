using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 【心灵控制】写入前的落盘快照（<c>duoshe-apply-&lt;时间戳&gt;.json</c>）：对象地址 U、旧 owner、新值、时间戳。
/// 写入之后「解除心灵控制」就用它把旧值写回去（<see cref="Features.FeatureController.RestoreSelectedUnitOwnership"/>）。
/// </summary>
/// <summary>单条快照 → 统一形状的还原目标（与批量快照同一个消费端）。</summary>
public static class OwnershipApplySnapshotExtensions
{
    public static OwnershipRestoreTarget ToRestoreTarget(this OwnershipApplySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (address, newOwner, oldOwner) = snapshot.ParseRestoreTargets();
        var entry = snapshot.ParseEntryTargets();
        return entry is (nint ea, uint en, uint eo)
            ? new OwnershipRestoreTarget(address, newOwner, oldOwner, ea, en, eo)
            : new OwnershipRestoreTarget(address, newOwner, oldOwner);
    }
}

/// <summary>「解除心灵控制」要回写的一项：期望当前值（我们写进去的新值）+ 要写回的旧值。</summary>
public sealed record OwnershipRestoreTarget(nint OwnerAddress, uint ExpectedCurrent, uint RestoreValue,
    nint? EntryAddress = null, uint? EntryExpectedCurrent = null, uint? EntryRestoreValue = null);

public sealed record OwnershipApplySnapshot(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("typeCode")] string TypeCode,
    [property: JsonPropertyName("ownerAddress")] string OwnerAddress,
    [property: JsonPropertyName("oldOwner")] string OldOwner,
    [property: JsonPropertyName("newOwner")] string NewOwner,
    [property: JsonPropertyName("localPlayer")] string LocalPlayer,
    [property: JsonPropertyName("engine")] string Engine,
    /// <summary>账本条目字段 `[U+0x414]`（2026-10-08 起一起写）。旧快照没有这三个字段 ⇒ 反序列化为 null，
    /// `ParseEntryTargets` 返回 null，还原时只回写 owner（向后兼容，不抛）。</summary>
    [property: JsonPropertyName("entryAddress")] string? EntryAddress = null,
    [property: JsonPropertyName("oldEntry")] string? OldEntry = null,
    [property: JsonPropertyName("newEntry")] string? NewEntry = null)
{
    public const string ApplyKind = "duoshe-apply";

    public static OwnershipApplySnapshot FromPlan(OwnershipTransferPlan plan, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OwnerAddress is not nint ownerAddress || plan.OldOwner is not uint oldOwner || plan.NewOwner is not uint newOwner)
            throw new InvalidOperationException("转让计划缺少完整的 owner 地址/旧值/新值，不能落盘快照。");
        return new OwnershipApplySnapshot(
            timestamp.ToString("o"),
            ApplyKind,
            plan.UnitAddress is nint unit ? FormatX86(unit) : string.Empty,
            plan.TypeCode is uint type ? $"0x{type:X8}" : string.Empty,
            FormatX86(ownerAddress),
            $"0x{oldOwner:X8}",
            $"0x{newOwner:X8}",
            $"0x{newOwner:X8}",
            "action 0x09 (ChangeSelectUnitID) → MustCode2+800: [U+0x418] = [ID]"
            + "；宿主侧再补写账本条目 [U+0x414] = [[ID]+0xAC]（只写 owner 时建筑会卡住不出单位）",
            plan.EntryAddress is nint ea ? FormatX86(ea) : null,
            plan.OldEntry is uint oe ? $"0x{oe:X8}" : null,
            plan.NewEntry is uint ne ? $"0x{ne:X8}" : null);
    }

    /// <summary>从快照文本解析出可用于回写的地址/新值/旧值；缺字段或非法格式一律抛（不猜）。</summary>
    public (nint OwnerAddress, uint NewOwner, uint OldOwner) ParseRestoreTargets()
    {
        return (ParsePointer(OwnerAddress, "ownerAddress"), ParseUInt32(NewOwner, "newOwner"), ParseUInt32(OldOwner, "oldOwner"));
    }

    /// <summary>
    /// 解析"账本条目"那一组（2026-10-08 起写入）。**旧快照没有这组字段 ⇒ 返回 null（不抛）**，
    /// 还原时只回写 owner，保持与历史快照兼容。三个字段缺一个就整体当缺失（不半套）。
    /// </summary>
    public (nint EntryAddress, uint EntryNew, uint EntryOld)? ParseEntryTargets()
    {
        if (string.IsNullOrWhiteSpace(EntryAddress) || string.IsNullOrWhiteSpace(NewEntry) || string.IsNullOrWhiteSpace(OldEntry))
            return null;
        return (ParsePointer(EntryAddress!, "entryAddress"), ParseUInt32(NewEntry!, "newEntry"), ParseUInt32(OldEntry!, "oldEntry"));
    }

    /// <summary>
    /// 按 x86 语义印地址：`nint` 在 32 位进程里存的是带符号的值，直接 `(ulong)` 转换会把 0x98B07A10
    /// 印成 0xFFFFFFFF98B07A10（2026-10-07 真机快照/日志里见过）。这里只取低 32 位、固定 8 位宽。
    /// </summary>
    public static string FormatX86(nint value) => $"0x{(uint)(nuint)value:X8}";

    internal static nint ParsePointer(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value == 0 || value > uint.MaxValue)
            throw new InvalidOperationException($"快照字段 {field} 不是有效的 x86 地址：'{text}'。");
        return (nint)(nuint)value;
    }

    internal static uint ParseUInt32(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"快照字段 {field} 不是有效的 32 位十六进制值：'{text}'。");
        return value;
    }
}

/// <summary>心灵控制快照的落盘/读回（纯文件 IO，无内存访问，可单测）。</summary>
public static class OwnershipApplySnapshotStore
{
    public const string FilePrefix = "duoshe-apply-";
    public const string FileSuffix = ".json";

    /// <summary>写快照到 <paramref name="directory"/>（不存在就建），返回完整路径。目录创建失败/写失败一律抛。</summary>
    public static string Write(string directory, OwnershipApplySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(directory);
        // 文件名里的时间戳只保留到秒 + 毫秒，避免非法字符；真正的 ISO 时间在 ts 字段里。
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(directory, $"{FilePrefix}{stamp}{FileSuffix}");
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SnapshotJsonOptions));
        return path;
    }

    /// <summary>落盘用的人读友好选项：不转义 '+' 等字符（快照是给人看/复核的）。</summary>
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>读回快照（还原时用）。文件不存在或 JSON 非法一律抛。</summary>
    public static OwnershipApplySnapshot Read(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<OwnershipApplySnapshot>(text)
            ?? throw new InvalidOperationException($"快照文件内容为空，无法解析：{path}");
    }
}
