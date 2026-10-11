using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 【心灵控制】逻辑层测试：黑名单跳过 / 已属本机时不动 / 未选中 / [ID] 未定位 / 快照→还原往返 / 写失败提示 /
/// 与引擎原文（Assets/00_bootstrap.aa 的 MustCode2+800 段）逐项对齐。
///
/// 夹具与 SelectedUnitOwnershipProbeTests 同款（同一条对象根链的原始地址），只是多了一份可写内存桩，
/// 以便验「还原」这一条真正的对象级回写（写入本身走引擎 0x09 动作，不在本类里）。
/// </summary>
public sealed class SelectedUnitOwnershipTransferTests
{
    private static readonly AddressResolver Resolver = new(0x400000, new Dictionary<string, nint>
        { ["ID"] = 0x700000, ["MustCode"] = 0x710000, ["iEnable"] = 0x700100 });

    private const uint LocalPlayer = 0x00067000;
    private const nint Unit = 0x50000;
    private const nint OwnerAddress = 0x50418;   // U + 0x418

    private static TransferMemory Fixture(uint typeCode, uint ownerPointer, uint count = 1)
    {
        var memory = new TransferMemory();
        memory.Set(0xCDB73C, 0x20000);      // [game+8DB73C] = selection root S
        memory.Set(0x2005C, count);         // [S+0x5C] = count
        memory.Set(0x20050, 0x30000);       // [S+0x50] = node L
        memory.Set(0x30008, 0x40000);       // [L+8]    = wrapper W
        memory.Set(0x40138, (uint)Unit);    // [W+0x138]= unit U
        memory.Set(0x50004, 0x60000);       // [U+4]    = template T
        memory.Set(0x60008, typeCode);      // [T+8]    = type code
        memory.Set((nint)OwnerAddress, ownerPointer); // [U+0x418]= owner
        memory.Set(0x700000, LocalPlayer);  // [ID]     = 本机玩家指针
        memory.ResetWrites();
        return memory;
    }

    [Theory]
    [InlineData(0x95D6E965u)]
    [InlineData(0xFD87E82Au)]
    [InlineData(0x856C9DD6u)]
    [InlineData(0xD20552E1u)]
    [InlineData(0x1AFC9A6Eu)]
    [InlineData(0xD6D22475u)]
    public void BlacklistedTypeIsNeverTransferable(uint typeCode)
    {
        var memory = Fixture(typeCode, 0x00050000);   // 别人的单位，但类型命中黑名单
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);

        Assert.True(plan.TypeInTransferBlacklist);
        Assert.False(plan.CanTransfer);
        Assert.False(plan.AlreadyOurs);
        Assert.Null(plan.NewOwner);
        Assert.Contains("黑名单", plan.Reason);
        Assert.Equal(0, memory.WriteCount);            // 只读判定，一个字节都没写
    }

    [Fact]
    public void AlreadyOwnedUnitIsNotTouched()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);

        Assert.True(plan.AlreadyOurs);
        Assert.False(plan.CanTransfer);
        Assert.Equal(LocalPlayer, plan.OldOwner);
        Assert.Equal(LocalPlayer, plan.NewOwner);
        Assert.Contains("已归属你", plan.Reason);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void DifferentOwnerIsTransferableToLocalPlayer()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);

        Assert.True(plan.CanTransfer);
        Assert.False(plan.AlreadyOurs);
        Assert.Equal(0x00050000u, plan.OldOwner);
        Assert.Equal(LocalPlayer, plan.NewOwner);
        Assert.Equal((nint)Unit, plan.UnitAddress);
        Assert.Equal((nint)OwnerAddress, plan.OwnerAddress!.Value);
        Assert.Equal("已选中", plan.SelectionState);
        Assert.Contains("可转让", plan.Reason);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void NotSelectedIsBlockedAndNeverReadsDeeper()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer, count: 0);
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);

        Assert.False(plan.CanTransfer);
        Assert.Null(plan.UnitAddress);
        Assert.Equal("未选中", plan.SelectionState);
        Assert.Contains("没有选中单位", plan.Reason);
        Assert.DoesNotContain((nint)0x30008, memory.ReadAddresses);
        Assert.DoesNotContain((nint)0x40138, memory.ReadAddresses);
    }

    [Fact]
    public void UnattachedLeavesLocalPlayerUnreadAndBlocksTransfer()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: false);

        Assert.False(plan.CanTransfer);
        Assert.Null(plan.NewOwner);
        Assert.Contains("未定位", plan.Reason);
        // 未附加时不许去读 ID 块（那地址在没附加时根本不存在）。
        Assert.DoesNotContain((nint)0x700000, memory.ReadAddresses);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void TransferSurfaceHasNoWriteCapability()
    {
        // 「不裸写指针」钉在类型层：判定类里没有任何写/派发方法（还原是 FeatureController 的受控接口）。
        Assert.DoesNotContain(typeof(SelectedUnitOwnershipTransfer).GetMethods(),
            method => method.Name.Contains("Write") || method.Name.Contains("Restore") || method.Name.Contains("Dispatch"));
        Assert.DoesNotContain(typeof(IProbeMemory).GetMethods(), method => method.Name.Contains("Write"));
    }

    // ---- 账本条目 [U+0x414]（2026-10-08 真机确认：只写 owner 时被心灵控制的建筑会卡住、不再出单位）----

    private const nint EntryAddress = 0x50414;   // U + 0x414

    /// <summary>
    /// 条目必须解析成"`[本机玩家+0xAC]` 指向的那个条目指针本身"，写进 [U+0x414] 的也是它。
    /// （实现里曾经错写成"读条目内部首个 dword"，那等于往游戏里塞野指针 —— 这条用例把它钉住。）
    /// </summary>
    [Fact]
    public void PlanResolvesAccountEntryPointerFromLocalPlayer()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        memory.Set((nint)EntryAddress, 0xAABBCCDD);   // [U+0x414] = 现在挂在原主名下的条目
        memory.Set(0x670AC, 0x00080000);              // [本机玩家+0xAC] = 用户的条目（指针）
        memory.Set(0x80000, 0x11111111);              // 条目对象内部首个 dword（**不能**被当成条目值）
        memory.ResetWrites();

        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);

        Assert.True(plan.CanTransfer);
        Assert.Equal((nint)EntryAddress, plan.EntryAddress);
        Assert.Equal(0xAABBCCDDu, plan.OldEntry);
        Assert.Equal(0x00080000u, plan.NewEntry);      // ← 条目指针本身，不是 0x11111111
        Assert.Contains("账本条目", plan.Reason);
        Assert.Equal(0, memory.WriteCount);            // 判定阶段仍然只读
    }

    /// <summary>快照要带上条目；**旧快照（没有这三个字段）必须向后兼容**：解析出 null，还原时只回写 owner。</summary>
    [Fact]
    public void SnapshotCarriesEntryAndToleratesLegacySnapshots()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        memory.Set((nint)EntryAddress, 0xAABBCCDD);
        memory.Set(0x670AC, 0x00080000);
        memory.Set(0x80000, 0);
        memory.ResetWrites();

        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);
        var snapshot = OwnershipApplySnapshot.FromPlan(plan, DateTimeOffset.Now);

        Assert.Equal(OwnershipApplySnapshot.FormatX86(EntryAddress), snapshot.EntryAddress);
        Assert.Equal("0xAABBCCDD", snapshot.OldEntry);
        Assert.Equal("0x00080000", snapshot.NewEntry);

        var entry = snapshot.ParseEntryTargets();
        Assert.NotNull(entry);
        Assert.Equal((nint)EntryAddress, entry!.Value.EntryAddress);
        Assert.Equal(0xAABBCCDDu, entry.Value.EntryOld);
        Assert.Equal(0x00080000u, entry.Value.EntryNew);
        // 还原语义："期望当前值"=我们写进去的新条目，"要写回的"=旧条目。
        Assert.Equal(0x00080000u, snapshot.ToRestoreTarget().EntryExpectedCurrent);
        Assert.Equal(0xAABBCCDDu, snapshot.ToRestoreTarget().EntryRestoreValue);

        // 旧快照（历史 JSON 没有 entryAddress/oldEntry/newEntry 三个字段）⇒ 解析出 null，不抛。
        var legacy = snapshot with { EntryAddress = null, OldEntry = null, NewEntry = null };
        Assert.Null(legacy.ParseEntryTargets());
        Assert.Null(legacy.ToRestoreTarget().EntryAddress);
        Assert.Equal(plan.OldOwner, legacy.ToRestoreTarget().RestoreValue);   // owner 那条照旧可用
    }

    // ---- 快照 → 还原 往返 ----

    [Fact]
    public void SnapshotRoundTripsThroughDiskAndRestoreWritesTheOldOwnerBack()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        var plan = new SelectedUnitOwnershipTransfer(memory, Resolver).Plan(hooksInstalled: true);
        Assert.True(plan.CanTransfer);

        var snapshot = OwnershipApplySnapshot.FromPlan(plan, DateTimeOffset.Now);
        var directory = Path.Combine(Path.GetTempPath(), "ra3-duoshe-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = OwnershipApplySnapshotStore.Write(directory, snapshot);
            Assert.StartsWith(OwnershipApplySnapshotStore.FilePrefix, Path.GetFileName(path));
            Assert.EndsWith(OwnershipApplySnapshotStore.FileSuffix, path);
            Assert.True(File.Exists(path));

            // JSON 里必须能读到「对象地址 U / 旧 owner / 新值 / 时间戳」。
            var json = File.ReadAllText(path);
            Assert.Contains(snapshot.Timestamp, json);
            Assert.Contains(snapshot.Unit, json);
            Assert.Contains(snapshot.OldOwner, json);
            Assert.Contains(snapshot.NewOwner, json);

            var read = OwnershipApplySnapshotStore.Read(path);
            Assert.Equal(snapshot, read);

            // 模拟「心灵控制已写成功」：owner 现在是新值（本机玩家）。
            memory.Set((nint)OwnerAddress, LocalPlayer);
            memory.ResetWrites();

            var (ownerAddress, newOwner, oldOwner) = read.ParseRestoreTargets();
            Assert.Equal(OwnerAddress, ownerAddress);

            var outcome = new FeatureController(memory, Resolver)
                .RestoreSelectedUnitOwnership(ownerAddress, newOwner, oldOwner);

            Assert.True(outcome.Wrote);
            Assert.Equal(LocalPlayer, outcome.Before);
            Assert.Equal(0x00050000u, outcome.After);
            Assert.Equal(0x00050000u, memory.Get((nint)OwnerAddress));  // 内存里真的写回了旧 owner
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RestoreRefusesWhenTheTransferNeverTookEffect()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        // 快照记录：new = 本机玩家，old = 0x50000；但此刻字段还是 0x50000（说明心灵控制其实没生效）。
        // Restore(addr, expected=本机玩家, restore=0x50000) 会因为「当前值 != 期望新值」而拒绝。
        var error = Assert.Throws<InvalidOperationException>(() =>
            new FeatureController(memory, Resolver).RestoreSelectedUnitOwnership(OwnerAddress, LocalPlayer, 0x00050000));
        Assert.Contains("不一致", error.Message);
        Assert.Contains("拒绝还原", error.Message);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void RestoreIsANoOpWhenTheFieldAlreadyHoldsTheRecordedOldValue()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        memory.ResetWrites();

        // new == old（旧值本来就是本机玩家）⇒ 无需写，明确回报，且一个字节都不写。
        var outcome = new FeatureController(memory, Resolver)
            .RestoreSelectedUnitOwnership(OwnerAddress, expectedCurrent: LocalPlayer, restoreValue: LocalPlayer);

        Assert.False(outcome.Wrote);
        Assert.NotNull(outcome.Note);
        Assert.Equal(LocalPlayer, outcome.Before);
        Assert.Equal(LocalPlayer, outcome.After);
        Assert.Equal(0, memory.WriteCount);
    }

    [Fact]
    public void RestoreRefusesWhenTheCurrentValueIsNeitherOldNorNew()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        memory.Set((nint)OwnerAddress, 0x00099999); // 单位被换代/重新选中 ⇒ 值既不是旧也不是新
        memory.ResetWrites();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new FeatureController(memory, Resolver).RestoreSelectedUnitOwnership(OwnerAddress, LocalPlayer, 0x00050000));
        Assert.Contains("拒绝还原", error.Message);
        Assert.Equal(0, memory.WriteCount);
        Assert.Equal(0x00099999u, memory.Get((nint)OwnerAddress));
    }

    [Fact]
    public void RestoreSurfacesTheWriteFailureMessageAndRollsBack()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: LocalPlayer);
        memory.FailNextWrite = true;

        var error = Assert.Throws<InvalidOperationException>(() =>
            new FeatureController(memory, Resolver).RestoreSelectedUnitOwnership(OwnerAddress, LocalPlayer, 0x00050000));
        Assert.Contains("模拟写入失败", error.Message);
        // 写失败后被 WriteTransaction 回滚回原值（本机玩家）。
        Assert.Equal(LocalPlayer, memory.Get((nint)OwnerAddress));
    }

    [Fact]
    public void SnapshotRequiresACompletePlan()
    {
        var blocked = OwnershipTransferPlan.Blocked("未选中", "没有选中单位");
        Assert.Throws<InvalidOperationException>(() => OwnershipApplySnapshot.FromPlan(blocked, DateTimeOffset.Now));
    }

    [Fact]
    public void SnapshotRejectsMalformedFieldsInsteadOfGuessing()
    {
        var snapshot = new OwnershipApplySnapshot("t", OwnershipApplySnapshot.ApplyKind,
            "0x1", "0x2", "not-an-address", "0x50000", "0x67000", "0x67000", "engine");
        Assert.Throws<InvalidOperationException>(() => snapshot.ParseRestoreTargets());
    }

    // ---- 与引擎原文对齐（防止偏移/黑名单漂移）----

    [Fact]
    public void TheEngineChangeIdFeatureDispatchesTheSameActionByteTheBootstrapBlockHandles()
    {
        var feature = TestAssets.LoadManifest().Features.Single(f => f.RawName == "Select Unit Change ID");
        Assert.Equal("0x09", feature.ValueHint);                 // 动作码 0x09
        Assert.Equal("MustCode2+800", feature.DispatchTarget);    // 引擎侧 ChangeSelectUnitID 块
    }

    [Fact]
    public void BootstrapChangeIdBlockMatchesTheTransferRulesWeReuse()
    {
        var lines = TestAssets.ReadBootstrapLines();
        var start = Array.FindIndex(lines, line => line.Trim() == "//ChangeSelectUnitID Code");
        Assert.True(start >= 0, "找不到 //ChangeSelectUnitID Code 段");
        var end = Array.FindIndex(lines, start, line => line.Trim() == "//DestroySelectUnit Code");
        Assert.True(end > start, "找不到 //DestroySelectUnit Code 结束标记");
        var block = lines.Skip(start).Take(end - start).ToArray();

        // 引擎走的完全就是我们要走的链与字段。
        Assert.Contains(block, line => line.Contains("ra3_1.12.game+8DB73C"));      // 选择根
        Assert.Contains(block, line => line.Contains("[esi+5c]"));                  // count
        Assert.Contains(block, line => line.Contains("[esi+50]"));                  // 头节点
        Assert.Contains(block, line => line.Contains("[edi+8]"));
        Assert.Contains(block, line => line.Contains("[ebx+138]"));                 // U = [W+0x138]
        Assert.Contains(block, line => line.Contains("mov eax,[ebx+4]"));           // T = [U+4]
        Assert.Contains(block, line => line.Contains("cmp [eax+8],"));              // type = [T+8]
        Assert.Contains(block, line => line.Contains("mov eax,[ID]"));              // 本机玩家
        Assert.Contains(block, line => line.Contains("mov [ebx+418],eax"));         // [U+0x418] = [ID]

        // 6 个黑名单类型码逐字一致（我们引用的那份 == 引擎原文那份）。
        foreach (var code in SelectedUnitOwnershipProbe.TransferBlacklistTypes)
            Assert.Contains(block, line => line.Contains(code.ToString("X8")));
        Assert.Equal(6, SelectedUnitOwnershipProbe.TransferBlacklistTypes.Length);
        Assert.Equal(SelectedUnitOwnershipProbe.UnitOwnerOffset, 0x418);
    }

    // ---- 写后"到底成没成"：owner 回读为准，动作槽协议超时 ≠ 失败 ----

    [Fact]
    public void ReadOwnerReturnsTheLiveFieldValueSoDelayedConfirmationCanTrustIt()
    {
        var memory = Fixture(typeCode: 0x12345678, ownerPointer: 0x00050000);
        var transfer = new SelectedUnitOwnershipTransfer(memory, Resolver);

        Assert.Equal(0x00050000u, transfer.ReadOwner(OwnerAddress));

        // 模拟"引擎稍后才执行"：字段被写成本机玩家 ⇒ 延迟确认必须能看到这个变化（不是缓存值）。
        memory.Set((nint)OwnerAddress, LocalPlayer);
        Assert.Equal(LocalPlayer, transfer.ReadOwner(OwnerAddress));
    }

    [Theory]
    [InlineData(true, 0x09u, OwnershipWriteVerdict.Confirmed)]              // 归属已变 ⇒ 成功，slot 还占着也算成功
    [InlineData(true, 0x00u, OwnershipWriteVerdict.Confirmed)]
    [InlineData(false, 0x09u, OwnershipWriteVerdict.PendingGamePolling)]    // ← 真机实测的那一格：超时不是失败
    [InlineData(false, 0x00u, OwnershipWriteVerdict.ConsumedButUnchanged)]
    public void WriteConfirmationPutsTheOwnerReadbackAboveTheActionSlotProtocol(
        bool ownerMatches, uint slotAfterDispatch, OwnershipWriteVerdict expected)
    {
        Assert.Equal(expected, OwnershipWriteConfirmation.Classify(ownerMatches, slotAfterDispatch));
    }

    /// <summary>可读可写内存桩：记录读过的地址、计写入次数、可注入「下一次写失败」。</summary>
    private sealed class TransferMemory : IProcessMemory, IProbeMemory
    {
        private readonly Dictionary<nint, byte> _bytes = new();
        public int WriteCount { get; private set; }
        public bool FailNextWrite { get; set; }
        public List<nint> ReadAddresses { get; } = [];

        public void Set(nint address, uint value) => WriteRaw(address, BitConverter.GetBytes(value));
        public uint Get(nint address) => BitConverter.ToUInt32(ReadBytes(address, 4));
        public void ResetWrites() => WriteCount = 0;

        public Win32MemoryRegion? Query(nint address) => new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0x20000);

        public byte[] ReadBytes(nint address, int count)
        {
            ReadAddresses.Add(address);
            var bytes = new byte[count];
            for (var index = 0; index < count; index++) bytes[index] = _bytes.GetValueOrDefault(address + index);
            return bytes;
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            WriteCount++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new InvalidOperationException("模拟写入失败");
            }
            WriteRaw(address, bytes);
        }

        private void WriteRaw(nint address, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++) _bytes[address + index] = bytes[index];
        }
    }
}
