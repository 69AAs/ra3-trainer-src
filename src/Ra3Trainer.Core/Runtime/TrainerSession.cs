using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Codegen;

namespace Ra3Trainer.Core.Runtime;

public sealed class TrainerSession : IDisposable
{
    private readonly TrainerManifest _manifest;
    private readonly IProcessMemory _memory;
    private readonly TrainerTarget _target;
    private readonly IRemoteAllocator _allocator;
    private readonly BootstrapCodeBuilder _codeBuilder;
    private readonly IReadOnlyList<string> _bootstrapLines;
    private readonly IProcessSuspender _processSuspender;
    private readonly ITargetProcessState _targetProcessState;
    private PatchEngine? _patchEngine;
    private readonly List<DiagnosticMemoryRange> _allocationHistory = new();
    private bool _codePublished;
    public const int RemoteBlockSize = 0x4200;
    private nint? _activeBlock;
    private readonly List<RemoteAllocationSnapshot> _blocks = new();
    public RemoteAddressSpaceSnapshot? AddressSpace { get; private set; }

    public TrainerSession(
        TrainerManifest manifest,
        IProcessMemory memory,
        TrainerTarget target,
        IRemoteAllocator? allocator = null,
        BootstrapCodeBuilder? codeBuilder = null,
        IReadOnlyList<string>? bootstrapLines = null,
        IProcessSuspender? processSuspender = null,
        ITargetProcessState? targetProcessState = null)
    {
        _manifest = manifest;
        _memory = memory;
        _target = target;
        _allocator = allocator ?? new SequentialRemoteAllocator(0);
        _codeBuilder = codeBuilder ?? new BootstrapCodeBuilder();
        _bootstrapLines = bootstrapLines ?? Array.Empty<string>();
        _processSuspender = processSuspender ?? Win32ProcessSuspender.Instance;
        _targetProcessState = targetProcessState ?? TargetProcessState.Instance;
    }

    public bool CanUseFeatures { get; private set; }

    public IReadOnlyDictionary<string, nint> Symbols { get; private set; } =
        new Dictionary<string, nint>();

    public AddressResolver? Resolver { get; private set; }

    public int InstalledHookCount => _patchEngine?.InstalledHookCount ?? 0;

    public string RemoteSymbolSummary =>
        Symbols.Count == 0
            ? "远程符号未分配。"
            : string.Join(", ", Symbols.Select(symbol => $"{symbol.Key}=0x{symbol.Value:X}"));

    public AttachResult Attach()
    {
        if (CanUseFeatures)
        {
            return new AttachResult(true, $"已连接 RA3 {DetectedVersionText}。{CompatibilityNotice}");
        }

        if (!TrainerProcessName.Matches(_target.ProcessName, _manifest.TargetProcess))
        {
            return Reject("目标进程不匹配。");
        }

        if (!_target.Is32Bit)
        {
            return Reject("目标进程不是 32 位。");
        }

        if (!_target.VersionSupported)
        {
            return Reject(
                $"版本不支持（检测到 {DetectedVersionText}），仅支持 RA3 {TrainerVersionPolicy.ExpectedVersionText}。");
        }

        if (_target.ModuleBase == 0)
        {
            // 基址 0 = 模块根本没解析出来（TrainerTarget 只是个 record，谁都能造一个默认值实例）。
            // 放过去的话后面 Resolve(...) 就是往 0 附近写代码，先在这里拒。
            return Reject("目标模块基址未解析（0），已拒绝附加。");
        }

        AddressSpace = (_allocator as IRemoteAddressSpaceSource)?.InspectAddressSpace();
        if (AddressSpace is { Complete: true, LargestAlignedBlock: < 0x10000 })
            return Reject(AddressSpace.ExhaustedMessage);
        try { AllocateFreshBlocks(preflight: false); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RefreshAddressSpace();
            return Reject(ex.Message);
        }
        CanUseFeatures = true;
        return new AttachResult(true, $"已连接 RA3 {DetectedVersionText}。{CompatibilityNotice}");
    }

    public void InstallPatches(int? maxHookCount = null)
    {
        if (!CanUseFeatures || Resolver is null || _patchEngine is null)
        {
            throw new InvalidOperationException("Attach must succeed before installing patches.");
        }

        if (InstalledHookCount > 0)
            throw new InvalidOperationException("仍有 Hook 或恢复记录，必须先一键全撤后再装载。");
        // Restore entry points and establish thread quiescence before freeing old code.
        // Neither restoring hooks nor freeing a block alone makes its address safe to reuse.
        if (_codePublished)
            Restore();
        if (_activeBlock is null) AllocateFreshBlocks();

        var suspension = _target.ProcessId is int processId
            ? _processSuspender.Suspend(processId)
            : null;

        try
        {
            var plans = PatchHookPlanner.CreatePlans(_manifest.PatchManifest);
            if (maxHookCount is < 0 || maxHookCount > plans.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxHookCount),
                    $"Hook count must be between 0 and {plans.Count}.");
            }

            var bootstrapCode = _codeBuilder.Build(_bootstrapLines, CreateBootstrapContext(plans));
            ValidateBootstrap(bootstrapCode);
            // 注释本来就写着"失败/部分发布也要退休这些块"，但只有放进 finally 才真做到：
            // 半写完（例如 MustCode 写成功、MustCode2 抛）时 _codePublished 仍是 false，
            // 清理路径会跳过这个块 —— 既不复用也不释放，留下的半截代码留在目标进程里。
            try
            {
                foreach (var initializer in bootstrapCode.Initializers)
                {
                    _memory.WriteBytes(Resolver.Resolve(initializer.Key), initializer.Value);
                }
                if (bootstrapCode.MustCode.Length > 0)
                {
                    _memory.WriteBytes(Resolver.Resolve("MustCode"), bootstrapCode.MustCode);
                }
                if (bootstrapCode.MustCode2.Length > 0)
                {
                    _memory.WriteBytes(Resolver.Resolve("MustCode2"), bootstrapCode.MustCode2);
                }
            }
            finally
            {
                _codePublished = true; // Even a failed/partial publication retires these blocks.
            }
            _patchEngine.Install(maxHookCount is int count ? plans.Take(count) : plans);
        }
        finally
        {
            suspension?.Dispose();
        }
    }

    /// <summary>
    /// Pre-flight check of every hook site against the patch manifest. The manifest's recorded
    /// original bytes are the only source of truth, so this check stays valid no matter which mod
    /// or patch the user has installed on top of the game.
    /// </summary>
    public HookSiteReport InspectHookSites()
    {
        if (Resolver is null)
        {
            throw new InvalidOperationException("Attach must succeed before inspecting hook sites.");
        }

        var plans = PatchHookPlanner.CreatePlans(_manifest.PatchManifest);
        var sites = new List<HookSiteSnapshot>(plans.Count);
        foreach (var plan in plans)
        {
            var address = Resolver.Resolve(plan.Address);
            var expected = plan.OriginalBytes;
            var actual = _memory.ReadBytes(address, expected.Length);

            byte[]? patched = null;
            try
            {
                patched = X86PatchEncoder.EncodeNearJumpWithNops(
                    address,
                    Resolver.Resolve(plan.Target),
                    plan.PatchLength);
            }
            catch (InvalidOperationException)
            {
                // Un-encodable plan: differing bytes can then only be reported as unexpected.
            }

            var state = actual.SequenceEqual(expected)
                ? HookSiteState.Original
                : patched is not null && actual.SequenceEqual(patched)
                    ? HookSiteState.PatchedByUs
                    : HookSiteState.Unexpected;

            sites.Add(new HookSiteSnapshot(plan.Address, plan.SectionTitle, address, state, expected, actual));
        }

        return new HookSiteReport(sites);
    }

    public void Dispose() => Restore();

    public void Restore(bool releaseAllocations = true)
    {
        try
        {
            using var suspension = (InstalledHookCount > 0 || (_activeBlock is not null && _codePublished)) && _target.ProcessId is int processId
                ? _processSuspender.Suspend(processId)
                : null;
            _patchEngine?.RestoreAll();
            if (releaseAllocations && _activeBlock is nint block && _allocator is IRemoteMemoryLifetime lifetime)
            {
                if (_codePublished && _target.ProcessId is not null)
                {
                    if (suspension is not IRemoteCodeQuiescence quiescence)
                        throw new InvalidOperationException("线程静默尚未验证，暂不释放远程块，请重试一键全撤。");
                    quiescence.EnsureUnreferenced(_memory, (ulong)(nuint)block, RemoteBlockSize);
                }
                lifetime.Free(block);
                // FindLastIndex 没有匹配时返回 -1，直接索引会抛 ArgumentOutOfRangeException，
                // 把真实状态（都释放过了）掩盖成"下标越界"。
                var index = _blocks.FindLastIndex(item => item.ReleasedAt is null);
                if (index >= 0)
                {
                    _blocks[index] = _blocks[index] with { ReleasedAt = DateTimeOffset.Now, State = "已释放" };
                }
                _activeBlock = null;
                _codePublished = false;
            }
            else if (releaseAllocations && _activeBlock is not null && _allocator is not IRemoteMemoryLifetime)
            {
                // Pure address allocators own no OS memory; retire their generation as before.
                _activeBlock = null;
            }
        }
        catch when (!_targetProcessState.IsRunning(_target.ProcessId))
        {
            // The game has already exited, so its address space is gone and hooks cannot be restored.
            ForgetExitedProcess();
        }
        catch (Exception ex)
        {
            // 标记"待释放"要标到**当前活动块**那一条：原来标的是列表最后一条，
            // 里面可能躺着早已释放的块或被 ForgetExitedProcess 退休的块 ⇒ 状态挂到了错误的快照上。
            var activeIndex = _activeBlock is nint failed
                ? _blocks.FindLastIndex(item => item.Address == (ulong)(nuint)failed && item.ReleasedAt is null)
                : -1;
            if (activeIndex >= 0)
            {
                _blocks[activeIndex] = _blocks[activeIndex] with { State = "待释放：" + ex.Message };
            }
            throw;
        }
    }

    /// <summary>Call only after the owned Process handle confirms exit; never writes to a dead/reused PID.</summary>
    public void ForgetExitedProcess()
    {
        for (var i = 0; i < _blocks.Count; i++)
            if (_blocks[i].ReleasedAt is null)
                _blocks[i] = _blocks[i] with { ReleasedAt = DateTimeOffset.Now, State = "进程退出，系统回收" };
        _activeBlock = null;
        _codePublished = false;
        _patchEngine = null;
        Symbols = new Dictionary<string, nint>();
        Resolver = null;
        CanUseFeatures = false;
    }

    public SessionMemorySnapshot GetDiagnosticSnapshot() => new(
        _patchEngine?.InstalledRanges.Select(range => new DiagnosticMemoryRange(
            "Hook", unchecked((ulong)(nuint)range.Address), (ulong)range.Size)).ToArray()
            ?? Array.Empty<DiagnosticMemoryRange>(),
        _allocationHistory.ToArray(), _blocks.ToArray(), AddressSpace);

    public void RefreshAddressSpace() =>
        AddressSpace = (_allocator as IRemoteAddressSpaceSource)?.InspectAddressSpace();

    private void AllocateFreshBlocks(bool preflight = true)
    {
        if (preflight)
        {
            RefreshAddressSpace();
            if (AddressSpace is { Complete: true, LargestAlignedBlock: < 0x10000 })
                throw new InvalidOperationException(AddressSpace.ExhaustedMessage);
        }
        var symbols = new Dictionary<string, nint>();
        var baseAddress = _allocator.Allocate(RemoteBlockSize);
        // 先登记快照再置 _activeBlock：反过来的话，两条语句之间任何异常都会留下
        // "已激活但没登记"的块 —— 释放路径看不到它，等于泄漏。
        _blocks.Add(new RemoteAllocationSnapshot("ID / iEnable / MustCode / MustCode2",
            (ulong)(nuint)baseAddress, RemoteBlockSize, DateTimeOffset.Now, null, "已申请"));
        _activeBlock = baseAddress;
        var offset = 0;
        foreach (var (name, size) in new[] { ("ID", 0x100), ("iEnable", 0x100), ("MustCode", 0x3000), ("MustCode2", 0x1000) })
        {
            var address = baseAddress + offset;
            offset += size;
            // Evidence is metadata only; physical ownership belongs to the single combined block.
            _allocationHistory.Add(new DiagnosticMemoryRange(name, unchecked((ulong)(nuint)address), (ulong)size));
            symbols.Add(name, address);
        }
        Symbols = symbols;
        Resolver = new AddressResolver(_target.ModuleBase, symbols);
        _patchEngine = new PatchEngine(_memory, Resolver);
        _codePublished = false;
    }

    private void ValidateBootstrap(BootstrapCode code)
    {
        if (code.MustCode.Length > 0x3000 || code.MustCode2.Length > 0x1000)
            throw new InvalidOperationException("远程代码超出分配范围，已拒绝装载。");
        foreach (var initializer in code.Initializers)
        {
            var address = unchecked((ulong)(nuint)Resolver!.Resolve(initializer.Key));
            if (!Symbols.Any(symbol =>
            {
                var start = unchecked((ulong)(nuint)symbol.Value);
                var size = symbol.Key switch { "MustCode" => 0x3000UL, "MustCode2" => 0x1000UL, _ => 0x100UL };
                return address >= start && address - start <= size &&
                    (ulong)initializer.Value.Length <= size - (address - start);
            }))
                throw new InvalidOperationException($"初始化写入超出分配范围：{initializer.Key}。");
        }
    }

    /// <summary>Version actually reported by the target module; falls back to the known build when
    /// the module exposes no version resource (stripped images, unit-test doubles).</summary>
    private string DetectedVersionText =>
        string.IsNullOrWhiteSpace(_target.FileVersion)
            ? TrainerVersionPolicy.ExpectedVersionText
            : _target.FileVersion.Trim();

    /// <summary>Empty unless this session attached to a build other than the one the hook
    /// addresses were derived from, so the UI can flag the risk.</summary>
    private string CompatibilityNotice =>
        TrainerVersionPolicy.IsExact(_target.FileVersion)
            ? string.Empty
            : $"（兼容模式：hook 地址按 {TrainerVersionPolicy.ExpectedVersionText} 生成，非此版本可能不稳定）";

    private AttachResult Reject(string message)
    {
        CanUseFeatures = false;
        return new AttachResult(false, message);
    }

    private BootstrapBuildContext CreateBootstrapContext(IReadOnlyList<PatchHookPlan> plans)
    {
        var labels = new Dictionary<string, nint>(StringComparer.OrdinalIgnoreCase);
        // 原来拿 hooks 的下标去索引 plans：planner 会过滤/去重（plans.Count 可能小于 Hooks.Count），
        // 一旦不等就会 IndexOutOfRange，或者把标签挂到错误的计划上。改成按地址配对。
        var planByAddress = new Dictionary<string, PatchHookPlan>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            planByAddress[plan.Address] = plan;
        }

        foreach (var hook in _manifest.PatchManifest.Hooks)
        {
            if (hook.ReturnLabel is null || !planByAddress.TryGetValue(hook.Address, out var plan))
            {
                continue;
            }

            labels[hook.ReturnLabel] = Resolver!.Resolve(plan.Address) + plan.PatchLength;
        }

        return new BootstrapBuildContext(expression => Resolver!.Resolve(expression), labels);
    }
}
