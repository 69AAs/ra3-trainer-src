using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// “选中单位归属候选字段”的**只读**探针 —— 心灵控制（把单位变成自己的）取证第一步。
///
/// 只读保证：本类只依赖 <see cref="IProbeMemory"/>（该接口**故意不继承任何可写接口**，
/// 只有 ReadBytes/Query，见 Memory/IProbeMemory.cs:4）。它没有 WriteBytes、没有远程执行、
/// 不遍历选择列表（只取头节点，不读 node.next），地址无效一律记成不可读而不抛。
///
/// 对象根链出处（既有代码/文档，均已核实）：
///   S = [ra3_1.12.game+8DB73C]                  ReadOnlyProbe.cs:26 / SelectedObjectGuard.cs:100
///   N = [S+0x5C]（选中数量）                     ReadOnlyProbe.cs:27 / SelectedObjectGuard.cs:101
///   L = [S+0x50]（头节点）                       ReadOnlyProbe.cs:33 / SelectedObjectGuard.cs:106
///   W = [L+8]（包装对象）                        ReadOnlyProbe.cs:36 / SelectedObjectGuard.cs:107
///   U = [W+0x138]（单位对象本体）                ReadOnlyProbe.cs:37 / SelectedObjectGuard.cs:108 / REPORT.md:738
///   T = [U+4]（模板）; type = [T+8]              SelectedObjectGuard.cs:109-112 / docs/round19-findings.md:48
///   owner 候选 = [U+0x418]                       ReadOnlyProbe.cs:51 / REPORT.md:752 / docs/round14-guards-and-probe.md:100
///   本机玩家指针 [ID]（修改器分配块，仅附加后有效）SelectedObjectGuard.cs:139 / round14:63
///
/// 引擎自己是怎么“心灵控制”的（bootstrap 原文，Assets/00_bootstrap.aa:966-997，动作码 0x09）：
///   mov eax,[ID]          ; 本机玩家指针
///   mov [ebx+418],eax     ; 把选中单位 U 的归属指针改成本机玩家
/// 命中的六个类型码（U+4→+8 的 type）会被引擎直接跳过、不转让。
/// </summary>
public sealed class SelectedUnitOwnershipProbe(IProbeMemory memory, AddressResolver resolver)
{
    public const string SelectionRootExpression = "ra3_1.12.game+8DB73C";
    public const string LocalPlayerExpression = "ID";
    public const int SelectionCountOffset = 0x5C;
    public const int SelectionHeadOffset = 0x50;
    public const int NodeWrapperOffset = 0x8;
    public const int WrapperUnitOffset = 0x138;
    public const int UnitTemplateOffset = 0x4;
    public const int TemplateTypeOffset = 0x8;
    public const int UnitOwnerOffset = 0x418;      // 归属/玩家指针候选
    /// <summary>
    /// 2026-10-08 真机确认：`[U+0x414]` 是"账本条目"（等价于 `[本机玩家+0xAC]`）。
    /// 只写 owner 时建筑会卡住、不再出单位；把条目也写成用户的 ⇒ **建筑继续出单位、产出归用户**。
    /// 三次独立真机验证：玩家自己那栋楼 `[U+0x414] == [它的 owner + 0xAC]`。
    /// </summary>
    public const int UnitEntryOffset = 0x414;
    /// <summary>目标条目 = `[本机玩家 + 0xAC]`（<see cref="UnitEntryOffset"/> 的说明）。</summary>
    public const int PlayerEntryOffset = 0xAC;

    /// <summary>引擎转让黑名单：命中这些类型码时 [U+0x418] 不会被写（bootstrap:981-992）。</summary>
    public static readonly uint[] TransferBlacklistTypes =
        [0x95D6E965, 0xFD87E82A, 0x856C9DD6, 0xD20552E1, 0x1AFC9A6E, 0xD6D22475];

    /// <summary>只读窗口默认范围（对象表面一层的候选字段），十六进制含端点。</summary>
    public const int WindowStart = 0x3E0;   // 含 0x414（条目）
    public const int WindowEnd = 0x440;   // 含 0x418

    public SelectedUnitOwnershipSnapshot Probe(bool hooksInstalled)
    {
        var timestamp = DateTimeOffset.Now;
        nint? selectionRoot = null, node = null, wrapper = null, unit = null, template = null, ownerAddr = null;
        uint? typeCode = null, ownerValue = null, localPlayer = null;
        uint count = 0;
        var state = "未选中";
        var window = new List<OwnershipCandidateField>();

        try { selectionRoot = TryResolvePointer(SelectionRootExpression, out _); }
        catch (Exception) { selectionRoot = null; }

        if (selectionRoot is nint root)
        {
            var countField = Read(root + SelectionCountOffset);
            if (countField.Bits is uint bits)
            {
                count = bits;
                if (count == 0) { state = "未选中（count=0）"; }
                else
                {
                    state = "已选中";
                    node = TryResolvePointerAt(root + SelectionHeadOffset, out _);
                    if (node is nint n)
                    {
                        wrapper = TryResolvePointerAt(n + NodeWrapperOffset, out _);
                        if (wrapper is nint w)
                        {
                            unit = TryResolvePointerAt(w + WrapperUnitOffset, out _);
                            if (unit is nint u)
                            {
                                template = TryResolvePointerAt(u + UnitTemplateOffset, out _);
                                if (template is nint t) typeCode = Read(t + TemplateTypeOffset).Bits;
                                ownerAddr = u + UnitOwnerOffset;
                                ownerValue = Read(ownerAddr.Value).Bits;
                                window = ReadWindow(u, WindowStart, WindowEnd);
                            }
                        }
                    }
                }
            }
            else
            {
                state = "选择链断裂（count 不可读）";
            }
        }
        else
        {
            state = "选择根不可用";
        }

        // 只有修改器已附加、ID 块已分配时才有本机玩家指针；未附加时老实报“未定位”。
        // [ID] 这个 dword 本身就是引擎写进去的本机玩家指针（见 bootstrap:993 `mov eax,[ID]`）。
        if (hooksInstalled)
        {
            try
            {
                var value = Read(resolver.Resolve(LocalPlayerExpression)).Bits;
                localPlayer = value == 0 ? null : value;   // [ID] 未写入时是 0，按“未定位”处理，不当成玩家 0
            }
            catch (Exception) { localPlayer = null; }
        }

        var blacklisted = typeCode is uint tc && TransferBlacklistTypes.Contains(tc);
        var ownerIsLocal = ownerValue is uint ov && localPlayer is uint lp && ov == lp;

        var summary = BuildSummary(state, count, unit, typeCode, blacklisted, ownerValue, localPlayer, ownerIsLocal, hooksInstalled);
        return new SelectedUnitOwnershipSnapshot(timestamp, state, count, selectionRoot, node, wrapper, unit,
            template, typeCode, blacklisted, ownerAddr, ownerValue, localPlayer, ownerIsLocal, window, summary);
    }

    /// <summary>
    /// 只读窗口：对象表面一层候选字段（十六进制含端点，步长 4）。给出原始值供人对照，
    /// 不声称除 U+0x418 以外的任何偏移有含义。
    /// </summary>
    private List<OwnershipCandidateField> ReadWindow(nint unit, int start, int end)
    {
        var list = new List<OwnershipCandidateField>();
        for (var off = start; off <= end; off += 4)
        {
            var field = Read(unit + off);
            var name = off == UnitOwnerOffset ? "owner 候选(U+0x418)"
                : off == UnitEntryOffset ? "账本条目(U+0x414)" : "unit 窗口";
            list.Add(new OwnershipCandidateField(name, $"+0x{off:X}", unit + off, field.Status, field.Bits));
        }
        return list;
    }

    private static string BuildSummary(string state, uint count, nint? unit, uint? typeCode, bool blacklisted,
        uint? owner, uint? localPlayer, bool ownerIsLocal, bool hooksInstalled)
    {
        if (unit is null) return $"未取到选中单位对象：{state}（count={count}）。只读取证，未写内存。";
        var sb = new System.Text.StringBuilder();
        sb.Append($"对象 0x{unit.Value:X}，类型 0x{(typeCode is uint t ? t.ToString("X8") : "--------")}（{state}，count={count}）。");
        sb.Append(" 取到 owner 候选 [U+0x418]");
        sb.Append(owner is uint o ? $" = 0x{o:X8}" : " 不可读");
        if (!hooksInstalled) sb.Append("；本机玩家指针 [ID] 未定位（修改器未附加），无法判等");
        else if (localPlayer is uint lp)
        {
            sb.Append($"；本机玩家 [ID] = 0x{lp:X8} → {(ownerIsLocal ? "等于本机玩家（已归属你）" : "不等于本机玩家（非你所有）")}");
        }
        else sb.Append("；本机玩家指针 [ID] 不可读");
        if (blacklisted) sb.Append("；⚠ 类型码命中引擎转让黑名单，引擎会跳过不转让");
        sb.Append("。本原型只读，未写内存。");
        return sb.ToString();
    }

    private ProbeRead Read(nint address)
    {
        try
        {
            var region = memory.Query(address);
            if (!Readable(region, address, 4)) return new ProbeRead(null, "字段不可读");
            var bytes = memory.ReadBytes(address, 4);
            if (bytes.Length != 4) return new ProbeRead(null, "短读");
            return new ProbeRead(BitConverter.ToUInt32(bytes), "可读（未认证合法）");
        }
        catch (Exception ex)
        {
            return new ProbeRead(null, "读取失败：" + ex.Message);
        }
    }

    /// <summary>读一个 dword 并当成指针；越界/未对齐/落点不可读一律返回 null（不猜）。</summary>
    private nint? TryResolvePointer(string expression, out string status)
    {
        status = "未解析";
        nint address;
        try { address = resolver.Resolve(expression); }
        catch (Exception ex) { status = "地址表达式无法解析：" + ex.Message; return null; }
        return TryResolvePointerAt(address, out status);
    }

    private nint? TryResolvePointerAt(nint address, out string status)
    {
        var value = Read(address);
        if (value.Bits is not uint bits) { status = "上游读取失败：" + value.Status; return null; }
        if (bits == 0) { status = "空指针"; return null; }
        if (bits < EmpiricalSafetyBounds.MinimumPointer || bits > EmpiricalSafetyBounds.MaximumPointer || (bits & 3) != 0)
        { status = "指针越界或未对齐"; return null; }
        var landing = (nint)bits;
        var region = memory.Query(landing);
        if (!Readable(region, landing, 4)) { status = "落点不可读"; return null; }
        status = "落点可读（类型未证明）";
        return landing;
    }

    /// <summary>与 ReadOnlyProbe.Readable 同款：必须在已提交、非保护页、范围内。</summary>
    internal static bool Readable(Win32MemoryRegion? region, nint address, int size)
    {
        var start = (ulong)(nuint)address;
        if (region is null || size <= 0 || start < 0x10000 || start + (uint)size > 0xFFFF0000UL ||
            region.State != 0x1000 || (region.Protect & 0x101) != 0 || (region.Protect & 0xEE) == 0) return false;
        var baseAddress = (ulong)(nuint)region.BaseAddress;
        return start >= baseAddress && start - baseAddress <= (ulong)region.RegionSize &&
            (ulong)size <= (ulong)region.RegionSize - (start - baseAddress);
    }

    private readonly record struct ProbeRead(uint? Bits, string Status);
}

/// <summary>窗口里的一个候选字段（只读）。</summary>
public sealed record OwnershipCandidateField(string Name, string Offset, nint Address, string Status, uint? Value)
{
    public string Display => Value is uint v ? $"{Offset} {Name} = 0x{v:X8}" : $"{Offset} {Name} = <{Status}>";
}

/// <summary>一次只读取证的完整结果（含时间戳与对象地址，可直接落盘留档）。</summary>
public sealed record SelectedUnitOwnershipSnapshot(
    DateTimeOffset Timestamp,
    string SelectionState,
    uint SelectionCount,
    nint? SelectionRoot,
    nint? NodeAddress,
    nint? WrapperAddress,
    nint? UnitAddress,
    nint? TemplateAddress,
    uint? TypeCode,
    bool TypeInTransferBlacklist,
    nint? OwnerCandidateAddress,
    uint? OwnerCandidate,
    uint? LocalPlayerPointer,
    bool OwnerEqualsLocalPlayer,
    IReadOnlyList<OwnershipCandidateField> Window,
    string Summary);
