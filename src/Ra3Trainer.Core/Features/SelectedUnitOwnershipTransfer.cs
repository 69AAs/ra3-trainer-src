using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 【心灵控制】把**当前选中单位**变成自己的 —— 逻辑层（只读判定，不含写入）。
///
/// 走的是「首选路线」：**复用引擎自己那段已验证的代码**（<c>Assets/00_bootstrap.aa:966-997</c>，
/// 动作码 0x09 `Select Unit Change ID`），而不是我们猜偏移裸写指针。引擎那段原文：
/// <code>
/// MustCode2+800:                                  ; 只对当前选中列表的头节点
///   ebx = [[[[game+8DB73C]+0x50]+8]+0x138]        ; U = 单位对象本体
///   eax = [ebx+4] ; if [eax+8] in {6 个黑名单类型} → 直接返回（不转让）
///   eax = [ID]                                    ; 本机玩家指针
///   [ebx+0x418] = eax                             ; ← 归属 = 本机玩家
/// </code>
/// 本类据此判定：能不能转（CanTransfer）、黑名单跳过、已属本机时不动，并给出「旧 owner → 新 owner」。
///
/// 只读保证：本类只依赖 <see cref="IProbeMemory"/>（该接口**故意不继承任何可写接口**，
/// 只有 ReadBytes/Query）。它没有 WriteBytes、没有远程执行、不遍历选择列表（只取头节点，不读 node.next）。
///
/// 字段/偏移**全部引用** <see cref="SelectedUnitOwnershipProbe"/> 的公开常量，不另立一套，避免漂移。
/// 黑名单 6 型与引擎原文逐字一致（bootstrap:981-992）。
/// </summary>
public sealed class SelectedUnitOwnershipTransfer(IProbeMemory memory, AddressResolver resolver)
{
    /// <summary>把当前选中单位判定成一个可执行/不可执行的转让计划（只读）。</summary>
    public OwnershipTransferPlan Plan(bool hooksInstalled)
    {
        nint? root = TryResolvePointer(SelectedUnitOwnershipProbe.SelectionRootExpression);
        if (root is not nint r)
            return OwnershipTransferPlan.Blocked("选择根不可用",
                "选择链根 [ra3_1.12.game+8DB73C] 不可读；请先在游戏里选中一个单位。");

        var countRead = Read(r + SelectedUnitOwnershipProbe.SelectionCountOffset);
        if (countRead.Bits is not uint count)
            return OwnershipTransferPlan.Blocked("选择链断裂", "选中数量 [S+0x5C] 不可读，拒绝转让。");
        if (count == 0)
            return OwnershipTransferPlan.Blocked("未选中",
                "当前没有选中单位（count=0）；先在游戏里选中一个单位再点。", count);

        var node = TryResolvePointerAt(r + SelectedUnitOwnershipProbe.SelectionHeadOffset);
        var wrapper = node is nint n ? TryResolvePointerAt(n + SelectedUnitOwnershipProbe.NodeWrapperOffset) : null;
        var unit = wrapper is nint w ? TryResolvePointerAt(w + SelectedUnitOwnershipProbe.WrapperUnitOffset) : null;
        if (unit is not nint u)
            return OwnershipTransferPlan.Blocked("对象链断裂", "选中对象链在 包装/单位 一步断了，拒绝转让。", count);

        var template = TryResolvePointerAt(u + SelectedUnitOwnershipProbe.UnitTemplateOffset);
        var typeCode = template is nint t ? Read(t + SelectedUnitOwnershipProbe.TemplateTypeOffset).Bits : null;
        var blacklisted = typeCode is uint tc && SelectedUnitOwnershipProbe.TransferBlacklistTypes.Contains(tc);

        var ownerAddress = u + SelectedUnitOwnershipProbe.UnitOwnerOffset;
        var oldOwner = Read(ownerAddress).Bits;

        // 引擎原文先查黑名单再写；黑名单命中一律跳过，不转让（无论 owner 是谁）。
        if (blacklisted)
            return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, true, ownerAddress, oldOwner,
                null, false, false, "当前单位类型命中引擎转让黑名单（6 型之一），引擎会跳过不转让。");

        if (!hooksInstalled)
            return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, false, ownerAddress, oldOwner,
                null, false, false, "本机玩家指针 [ID] 未定位（修改器未附加或本机玩家尚未建立）；引擎路线的 [ID] 取不到，拒绝转让。");

        uint? newOwner = null;
        try
        {
            var idRead = Read(resolver.Resolve(SelectedUnitOwnershipProbe.LocalPlayerExpression));
            if (idRead.Bits is uint id && id != 0) newOwner = id;
        }
        catch (Exception) { newOwner = null; }

        if (newOwner is null)
            return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, false, ownerAddress, oldOwner,
                null, false, false, "本机玩家指针 [ID] 读不到或仍为 0（引擎还没写进去），拒绝转让。");

        if (oldOwner is not uint currentOwner)
            return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, false, ownerAddress, null,
                newOwner, false, false, "归属字段 [U+0x418] 不可读，无法证明改前值，拒绝转让。");

        if (currentOwner == newOwner.Value)
            return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, false, ownerAddress, currentOwner,
                newOwner, true, false, "该单位已归属你（[U+0x418] == [ID]），无需转让，不会写入。");

        // ③ 账本条目：目标 = [本机玩家 + 0xAC]（真机三次验证）。只写 owner 时建筑会卡住、不再出单位；
        //    owner 与条目都写成用户的之后，建筑继续出单位且产出归用户（2026-10-08 真机确认）。
        var entryAddress = u + SelectedUnitOwnershipProbe.UnitEntryOffset;
        var oldEntry = Read(entryAddress).Bits;
        nint? entryTarget = null;
        try { entryTarget = TryResolvePointerAt((nint)newOwner.Value + SelectedUnitOwnershipProbe.PlayerEntryOffset); }
        catch (Exception) { entryTarget = null; }
        // 要写进 [U+0x414] 的就是**条目指针本身**（= `[本机玩家+0xAC]` 的值），
        // 不是它内部的首个 dword —— 这里曾经写成 `Read(ep).Bits`，那就等于往游戏里塞了个野指针。
        var newEntry = entryTarget is nint ep ? (uint)ep : (uint?)null;

        return OwnershipTransferPlan.FromSelection("已选中", count, u, typeCode, false, ownerAddress, currentOwner,
            newOwner, false, true,
            $"可转让：把 [U+0x418] 从 0x{currentOwner:X8} 改成本机玩家 [ID] = 0x{newOwner.Value:X8}（引擎动作 0x09）；"
            + $"并把账本条目 [U+0x414] 从 {(oldEntry is uint oe ? $"0x{oe:X8}" : "不可读")} 补成 "
            + (newEntry is uint ne ? $"0x{ne:X8}" : "（本局条目未取到，将只写 owner）") + "。",
            entryAddress, oldEntry, newEntry);
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

    private nint? TryResolvePointer(string expression)
    {
        nint address;
        try { address = resolver.Resolve(expression); }
        catch (Exception) { return null; }
        return TryResolvePointerAt(address);
    }

    private nint? TryResolvePointerAt(nint address)
    {
        var value = Read(address);
        if (value.Bits is not uint bits) return null;
        if (bits == 0) return null;
        if (bits < EmpiricalSafetyBounds.MinimumPointer || bits > EmpiricalSafetyBounds.MaximumPointer || (bits & 3) != 0)
            return null;
        var landing = (nint)bits;
        var region = memory.Query(landing);
        if (!Readable(region, landing, 4)) return null;
        return landing;
    }

    /// <summary>与 ReadOnlyProbe/SelectedUnitOwnershipProbe 同款：必须在已提交、非保护页、范围内。</summary>
    internal static bool Readable(Win32MemoryRegion? region, nint address, int size)
    {
        var start = (ulong)(nuint)address;
        if (region is null || size <= 0 || start < 0x10000 || start + (uint)size > 0xFFFF0000UL ||
            region.State != 0x1000 || (region.Protect & 0x101) != 0 || (region.Protect & 0xEE) == 0) return false;
        var baseAddress = (ulong)(nuint)region.BaseAddress;
        return start >= baseAddress && start - baseAddress <= (ulong)region.RegionSize &&
            (ulong)size <= (ulong)region.RegionSize - (start - baseAddress);
    }

    /// <summary>
    /// 只读回读一个 owner 字段（延迟确认用）。`[U+0x418]` 才是地面真相：
    /// 引擎只在"资金更新"那条钩子上轮询动作槽，消费可能晚于修改器的等待窗口，
    /// 所以写后必须用**回读归属**来确认，而不是拿"动作槽超时"当失败。不可读返回 null，绝不猜。
    /// </summary>
    public uint? ReadOwner(nint ownerAddress) => Read(ownerAddress).Bits;

    private readonly record struct ProbeRead(uint? Bits, string Status);
}

/// <summary>一次转让判定（只读）：能不能转、转什么、为什么不能转。</summary>
public sealed record OwnershipTransferPlan(
    string SelectionState,
    uint SelectionCount,
    nint? UnitAddress,
    uint? TypeCode,
    bool TypeInTransferBlacklist,
    nint? OwnerAddress,
    uint? OldOwner,
    uint? NewOwner,
    bool AlreadyOurs,
    bool CanTransfer,
    string Reason,
    nint? EntryAddress = null,
    uint? OldEntry = null,
    uint? NewEntry = null)
{
    internal static OwnershipTransferPlan Blocked(string state, string reason, uint count = 0) =>
        new(state, count, null, null, false, null, null, null, false, false, reason);

    internal static OwnershipTransferPlan FromSelection(string state, uint count, nint unit, uint? type, bool blacklisted,
        nint ownerAddress, uint? oldOwner, uint? newOwner, bool alreadyOurs, bool canTransfer, string reason,
        nint? entryAddress = null, uint? oldEntry = null, uint? newEntry = null) =>
        new(state, count, unit, type, blacklisted, ownerAddress, oldOwner, newOwner, alreadyOurs, canTransfer, reason,
            entryAddress, oldEntry, newEntry);
}

/// <summary>「解除心灵控制」的结果：写入前后回读到的 owner 值，以及是否真的写了。</summary>
public sealed record OwnershipRestoreOutcome(nint OwnerAddress, uint Before, uint After, bool Wrote, string? Note);

/// <summary>
/// 心灵控制写入后"到底成没成"的判定（**纯函数，可单测**）。
///
/// 核心口径：**回读 owner 才是地面真相**，动作槽协议超时**不算失败**。
/// 依据是真机实测（2026-10-07 22:21，队友的帝国采矿车）：修改器等 1500ms 报 `TimedOut`、
/// 回读 owner 仍是旧值，于是界面报"失败"；但动作槽随后才被游戏消费，**归属确实改成了本机玩家**，
/// 单位也真的能被指挥。原因是动作槽只在游戏"资金更新"那条钩子上被轮询，消费时刻不受修改器控制。
/// </summary>
public enum OwnershipWriteVerdict
{
    /// <summary>回读到的 owner 已等于目标值 ⇒ 成功（无论动作槽协议当时报的是消费还是超时）。</summary>
    Confirmed,

    /// <summary>owner 未变、但动作槽仍被占用（还是那枚 0x09）⇒ 游戏还没轮到，**待确认，不是失败**。</summary>
    PendingGamePolling,

    /// <summary>owner 未变、动作槽已归零 ⇒ 引擎消费了却没改（类型/对象已变），才是真没生效。</summary>
    ConsumedButUnchanged
}

/// <summary>把「owner 回读结果 + 动作槽现值」判成一个明确结论。</summary>
public static class OwnershipWriteConfirmation
{
    public static OwnershipWriteVerdict Classify(bool ownerEqualsTarget, uint slotAfterDispatch) =>
        ownerEqualsTarget ? OwnershipWriteVerdict.Confirmed
        : slotAfterDispatch != 0 ? OwnershipWriteVerdict.PendingGamePolling
        : OwnershipWriteVerdict.ConsumedButUnchanged;
}
