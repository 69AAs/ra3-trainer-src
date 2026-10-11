using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Runtime;
using Ra3Trainer.Core.Codegen;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class TrainerSessionTests
{
    [Fact]
    public void CombinedAllocationPreservesSymbolOffsetsAndFreesOnlyBaseOnce()
    {
        var allocator = new LifetimeAllocator();
        var session = NewLifetimeSession(allocator);
        Assert.True(session.Attach().Success);
        Assert.Equal(new[] { 0x4200 }, allocator.Requests);
        Assert.Equal((nint)0x700000, session.Symbols["ID"]);
        Assert.Equal((nint)0x700100, session.Symbols["iEnable"]);
        Assert.Equal((nint)0x700200, session.Symbols["MustCode"]);
        Assert.Equal((nint)0x703200, session.Symbols["MustCode2"]);
        var before = session.GetDiagnosticSnapshot();
        // 记录证据里的**真实地址/尺寸**再对比：GetDiagnosticSnapshot 返回的是同一批记录实例，
        // 直接比 before.RemoteAllocations == after.RemoteAllocations 只能发现条数变化，
        // 地址被 Dispose 改坏/重算过也照样过。
        var beforeRanges = before.RemoteAllocations.Select(range => (range.Name, range.Address, range.Size)).ToArray();
        session.Dispose(); session.Dispose();
        Assert.Equal(new nint[] { 0x700000 }, allocator.Freed);
        var after = session.GetDiagnosticSnapshot();
        Assert.Equal(beforeRanges, after.RemoteAllocations.Select(range => (range.Name, range.Address, range.Size)).ToArray());
        Assert.Null(Assert.Single(before.Blocks!).ReleasedAt);
        Assert.NotNull(Assert.Single(after.Blocks!).ReleasedAt);
        Assert.Equal("已释放", after.Blocks![0].State);
        session.InstallPatches();
        Assert.Equal(new[] { 0x4200, 0x4200 }, allocator.Requests);
        Assert.NotEqual((nint)0x700000, session.Symbols["ID"]);
        var secondBlock = session.Symbols["ID"];
        // 重装路径又申请了第二个组合块：收尾时必须把它也释放，而且只释放一次 —— 否则这里的泄漏没人发现。
        session.Dispose();
        Assert.Equal(new nint[] { 0x700000, secondBlock }, allocator.Freed);
    }

    [Fact]
    public void ExhaustedAddressSpaceRejectsAttachWithoutAllocatingOrThrowing()
    {
        var allocator = new LifetimeAllocator { Space = new(1234, 0, 0, true, DateTimeOffset.Now) };
        var session = NewLifetimeSession(allocator);
        var result = session.Attach();
        Assert.False(result.Success);
        Assert.Contains("游戏 32 位地址空间已满，请重启游戏后再试", result.Message);
        Assert.Empty(allocator.Requests);
        Assert.False(session.CanUseFeatures);
        Assert.Empty(session.Symbols);
    }

    [Fact]
    public void IncompletePreflightDoesNotMisreportExhaustion()
    {
        var allocator = new LifetimeAllocator { Space = new(0, 0, 0, false, DateTimeOffset.Now, "拒绝访问") };
        Assert.True(NewLifetimeSession(allocator).Attach().Success);
        Assert.Single(allocator.Requests);
    }

    [Fact]
    public void FailedFreeRetainsOwnershipAndEvidenceForRetry()
    {
        var allocator = new LifetimeAllocator { FailFree = true };
        var session = NewLifetimeSession(allocator);
        session.Attach();
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        Assert.Null(Assert.Single(session.GetDiagnosticSnapshot().Blocks!).ReleasedAt);
        allocator.FailFree = false;
        session.Dispose();
        Assert.Single(allocator.Freed);
    }

    [Fact]
    public void PendingFeatureRestoreDefersFreeUntilRetry()
    {
        var allocator = new LifetimeAllocator();
        var session = NewLifetimeSession(allocator);
        session.Attach();
        session.Restore(releaseAllocations: false);
        Assert.Empty(allocator.Freed);
        session.Restore();
        Assert.Single(allocator.Freed);
    }

    [Fact]
    public void ExitedProcessReclaimsBlockWithoutVirtualFreeAndKeepsMetadata()
    {
        var allocator = new LifetimeAllocator();
        var session = NewLifetimeSession(allocator);
        session.Attach(); session.ForgetExitedProcess(); session.Dispose();
        Assert.Empty(allocator.Freed);
        Assert.Equal("进程退出，系统回收", Assert.Single(session.GetDiagnosticSnapshot().Blocks!).State);
        Assert.Equal(4, session.GetDiagnosticSnapshot().RemoteAllocations.Count);
    }

    private static TrainerSession NewLifetimeSession(LifetimeAllocator allocator) => new(
        MinimalManifest(), new FakeProcessMemory(), new TrainerTarget("ra3_1.12.game", 0x400000, true, true), allocator,
        targetProcessState: new FixedTargetProcessState(true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PublishedCodeMustPassQuiescenceBeforeFreeAndAlwaysResume(bool referenced)
    {
        var allocator = new LifetimeAllocator();
        var guard = new GuardSuspender { Referenced = referenced };
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x401000, [1, 2, 3, 4, 5]);
        var session = new TrainerSession(MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test",
            ["jmp MustCode"], "MustCode", "_Back", [], ["db 01 02 03 04 05"])), memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: 1234), allocator,
            processSuspender: guard, targetProcessState: new FixedTargetProcessState(true));
        session.Attach(); session.InstallPatches();
        guard.Resumed = false;
        if (referenced)
        {
            Assert.Throws<InvalidOperationException>(() => session.Restore());
            Assert.Empty(allocator.Freed);
            Assert.Null(Assert.Single(session.GetDiagnosticSnapshot().Blocks!).ReleasedAt);
        }
        else
            session.Restore();
        Assert.True(guard.Checked);
        Assert.True(guard.Resumed);
        Assert.Equal(0, session.InstalledHookCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, memory.ReadBytes(0x401000, 5));
        guard.Referenced = false;
        session.Restore();
        Assert.Single(allocator.Freed);
    }

    private sealed class GuardSuspender : IProcessSuspender, IDisposable, IRemoteCodeQuiescence
    {
        public bool Referenced { get; set; }
        public bool Checked { get; private set; }
        public bool Resumed { get; set; }
        public IDisposable Suspend(int pid) { Resumed = false; return this; }
        public void EnsureUnreferenced(IProcessMemory memory, ulong address, ulong size)
        {
            Assert.False(Resumed);
            Assert.Equal(0x700000UL, address);
            Assert.Equal(0x4200UL, size);
            Checked = true;
            if (Referenced) throw new InvalidOperationException("线程仍引用远程块");
        }
        public void Dispose() => Resumed = true;
    }

    private sealed class LifetimeAllocator : IRemoteAllocator, IRemoteMemoryLifetime, IRemoteAddressSpaceSource
    {
        public List<int> Requests { get; } = [];
        public List<nint> Freed { get; } = [];
        public bool FailFree { get; set; }
        public RemoteAddressSpaceSnapshot Space { get; init; } = new(0x100000, 0x100000, 1, true, DateTimeOffset.Now);
        public RemoteAddressSpaceSnapshot InspectAddressSpace() => Space;
        public nint Allocate(int size) { Requests.Add(size); return 0x700000 + (Requests.Count - 1) * 0x10000; }
        public void Free(nint address)
        {
            if (FailFree) throw new InvalidOperationException("释放故障注入");
            Freed.Add(address);
        }
    }

    [Fact]
    public void FailedHookPublicationAlsoRetiresItsCodeBeforeRetry()
    {
        var memory = new RejectHookOnceMemory();
        var session = new TrainerSession(
            MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test", ["jmp MustCode"],
                "MustCode", "_Back", [], ["db 01 02 03 04 05"])),
            memory, new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000), new FakeBootstrapCodeBuilder());
        session.Attach();
        var oldCode = session.Symbols["MustCode"];
        Assert.Throws<PatchInstallException>(() => session.InstallPatches());
        Assert.Equal(0, session.InstalledHookCount);
        session.InstallPatches();
        Assert.NotEqual(oldCode, session.Symbols["MustCode"]);
        Assert.Equal(new byte[] { 0xCC }, memory.ReadBytes(oldCode, 1));
        Assert.Equal(8, session.GetDiagnosticSnapshot().RemoteAllocations.Count);
    }

    [Fact]
    public void ConfirmedExitForgetsActiveStateWithoutWritingOrSuspending()
    {
        var memory = new RecordingProcessMemory();
        memory.WriteBytes(0x401000, [1, 2, 3, 4, 5]);
        var session = new TrainerSession(
            MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test", ["jmp MustCode"],
                "MustCode", "_Back", [], ["db 01 02 03 04 05"])),
            memory, new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: 1234),
            new SequentialRemoteAllocator(0x700000), processSuspender: new RecordingProcessSuspender(memory.Events));
        session.Attach();
        session.InstallPatches();
        memory.Events.Clear();
        session.ForgetExitedProcess();
        session.Dispose();
        session.Dispose();
        Assert.Empty(memory.Events);
        Assert.False(session.CanUseFeatures);
        Assert.Equal(0, session.InstalledHookCount);
        Assert.Empty(session.Symbols);
        Assert.Equal(4, session.GetDiagnosticSnapshot().RemoteAllocations.Count);
    }

    private sealed class RejectHookOnceMemory : IProcessMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private bool _reject = true;
        public RejectHookOnceMemory() => _inner.WriteBytes(0x401000, [1, 2, 3, 4, 5]);
        public byte[] ReadBytes(nint address, int count) => _inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            if (address == 0x401000 && _reject)
            {
                _reject = false;
                throw new InvalidOperationException("故障注入：发布失败");
            }
            _inner.WriteBytes(address, bytes);
        }
    }

    [Fact]
    public void ReinstallRetiresPublishedCodeAndPreservesAllocationEvidence()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x401000, [1, 2, 3, 4, 5]);
        var session = new TrainerSession(
            MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test", ["jmp MustCode"],
                "MustCode", "_Back", [], ["db 01 02 03 04 05"])),
            memory, new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000), new FakeBootstrapCodeBuilder());
        session.Attach();
        session.InstallPatches();
        var oldCode = session.Symbols["MustCode"];
        Assert.Throws<InvalidOperationException>(() => session.InstallPatches());
        session.Dispose();
        session.InstallPatches();
        Assert.NotEqual(oldCode, session.Symbols["MustCode"]);
        Assert.Equal(new byte[] { 0xCC }, memory.ReadBytes(oldCode, 1));
        Assert.Equal(8, session.GetDiagnosticSnapshot().RemoteAllocations.Count);
        Assert.Single(session.GetDiagnosticSnapshot().InstalledHooks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidBootstrapWritesNothing(bool oversizedCode)
    {
        var memory = new RecordingProcessMemory();
        var session = new TrainerSession(MinimalManifest(), memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000), new InvalidBootstrapBuilder(oversizedCode));
        session.Attach();
        Assert.Throws<InvalidOperationException>(() => session.InstallPatches());
        Assert.Empty(memory.Events);
        Assert.Equal(0, session.InstalledHookCount);
    }

    private sealed class InvalidBootstrapBuilder(bool oversizedCode) : BootstrapCodeBuilder
    {
        public override BootstrapCode Build(IReadOnlyList<string> lines, BootstrapBuildContext context) =>
            oversizedCode ? new(new byte[0x3001], [], new Dictionary<string, byte[]>()) :
            new([], [], new Dictionary<string, byte[]> { ["iEnable+FF"] = [1, 2] });
    }

    [Fact]
    public void RepeatedAttachKeepsSymbolsAndInstalledHooks()
    {
        var memory = new FakeProcessMemory();
        byte[] original = [1, 2, 3, 4, 5];
        memory.WriteBytes(0x401000, original);
        var session = new TrainerSession(
            MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test", ["jmp MustCode"],
                "MustCode", "_Back", [], ["db 01 02 03 04 05"])),
            memory, new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000));
        session.Attach();
        session.InstallPatches();
        var symbols = session.Symbols;

        Assert.True(session.Attach().Success);
        Assert.Same(symbols, session.Symbols);
        Assert.Equal(1, session.InstalledHookCount);
        session.Dispose();
        Assert.Equal(original, memory.ReadBytes(0x401000, 5));
    }

    [Fact]
    public void DisposeSuspendsTargetUntilHooksAreRestoredAndDoesNothingOnSecondDispose()
    {
        var memory = new RecordingProcessMemory();
        memory.WriteBytes(0x401000, [1, 2, 3, 4, 5]);
        var session = new TrainerSession(
            MinimalManifest(new PatchHook("ra3_1.12.game+1000", "test", ["jmp MustCode"],
                "MustCode", "_Back", [], ["db 01 02 03 04 05"])),
            memory, new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: 1234),
            new SequentialRemoteAllocator(0x700000),
            processSuspender: new RecordingProcessSuspender(memory.Events));
        session.Attach();
        session.InstallPatches();
        memory.Events.Clear();

        session.Dispose();
        session.Dispose();

        Assert.Equal(["suspend 1234", "write 0x401000", "resume 1234"], memory.Events);
    }

    [Fact]
    public void AttachRejectsUnsupportedTarget()
    {
        var manifest = MinimalManifest();
        var target = new TrainerTarget(
            ProcessName: "ra3_1.12.game",
            ModuleBase: 0x400000,
            Is32Bit: true,
            VersionSupported: false);

        var session = new TrainerSession(manifest, new FakeProcessMemory(), target);

        var result = session.Attach();

        Assert.False(result.Success);
        Assert.Contains("版本不支持", result.Message);
        Assert.False(session.CanUseFeatures);
    }

    [Fact]
    public void AttachAllocatesExpectedRemoteSymbols()
    {
        var session = new TrainerSession(
            MinimalManifest(),
            new FakeProcessMemory(),
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000));

        var result = session.Attach();

        Assert.True(result.Success);
        Assert.Equal((nint)0x700000, session.Symbols["ID"]);
        Assert.Equal((nint)0x700100, session.Symbols["iEnable"]);
        Assert.Equal((nint)0x700200, session.Symbols["MustCode"]);
        Assert.Equal((nint)0x703200, session.Symbols["MustCode2"]);
        Assert.Contains("iEnable=0x700100", session.RemoteSymbolSummary);
    }

    [Fact]
    public void AttachAcceptsProcessNameWithoutGameExtension()
    {
        var session = new TrainerSession(
            MinimalManifest(),
            new FakeProcessMemory(),
            new TrainerTarget("ra3_1.12", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000));

        var result = session.Attach();

        Assert.True(result.Success);
    }

    [Fact]
    public void InstallPatchesWritesBootstrapCodeAndHooks()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x401000, new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 });
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder());

        session.Attach();
        session.InstallPatches();

        Assert.Equal(1, session.InstalledHookCount);
        Assert.Equal(new byte[] { 0xCC }, memory.ReadBytes(0x700200, 1));
        Assert.Equal(new byte[] { 0x90 }, memory.ReadBytes(0x703200, 1));
        Assert.Equal(new byte[] { 0xE9, 0xFB, 0xF1, 0x2F, 0x00, 0x90 }, memory.ReadBytes(0x401000, 6));
    }

    [Fact]
    public void InstallPatchesSuspendsTargetProcessWhileWritingRemoteCodeAndHooks()
    {
        var memory = new RecordingProcessMemory();
        memory.WriteBytes(0x401000, new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 });
        memory.Events.Clear();
        var suspender = new RecordingProcessSuspender(memory.Events);
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: 1234),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder(),
            processSuspender: suspender);

        session.Attach();
        session.InstallPatches();

        Assert.Equal(
            [
                "suspend 1234",
                "write 0x700124",
                "write 0x700200",
                "write 0x703200",
                "write 0x401000",
                "resume 1234"
            ],
            memory.Events);
    }

    [Fact]
    public void InstallPatchesDoesNotMakeWholeImageExecuteReadWrite()
    {
        var memory = new RecordingProtectedProcessMemory();
        memory.WriteBytes(0x401000, new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 });
        memory.Events.Clear();
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder());

        session.Attach();
        session.InstallPatches();

        Assert.DoesNotContain(memory.Events, item => item.StartsWith("protect ", StringComparison.Ordinal));
    }

    [Fact]
    public void InstallPatchesCanLimitInstalledHookCountForDiagnostics()
    {
        var memory = new FakeProcessMemory();
        var firstOriginal = new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 };
        var secondOriginal = new byte[] { 0x8B, 0x40, 0x04, 0x8B, 0x8E, 0xB0, 0x03, 0x00, 0x00 };
        memory.WriteBytes(0x401000, firstOriginal);
        memory.WriteBytes(0x401100, secondOriginal);
        var manifest = MinimalManifest(
            new PatchHook(
                "ra3_1.12.game+1000",
                "First Hook",
                ["jmp MustCode"],
                "MustCode",
                "_BackFirst",
                [],
                ["mov edx,[eax+28]", "mov eax,[edx+20]"]),
            new PatchHook(
                "ra3_1.12.game+1100",
                "Second Hook",
                ["jmp MustCode2"],
                "MustCode2",
                "_BackSecond",
                [],
                ["mov eax,[eax+04]", "mov ecx,[esi+000003b0]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder());

        session.Attach();
        session.InstallPatches(maxHookCount: 1);

        Assert.Equal(1, session.InstalledHookCount);
        Assert.NotEqual(firstOriginal, memory.ReadBytes(0x401000, firstOriginal.Length));
        Assert.Equal(secondOriginal, memory.ReadBytes(0x401100, secondOriginal.Length));
    }

    [Fact]
    public void DisposeRestoresInstalledHooks()
    {
        var memory = new FakeProcessMemory();
        var originalBytes = new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 };
        memory.WriteBytes(0x401000, originalBytes);
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder());

        session.Attach();
        session.InstallPatches();
        session.Dispose();

        Assert.Equal(originalBytes, memory.ReadBytes(0x401000, originalBytes.Length));
    }

    [Fact]
    public void DisposeDoesNotThrowWhenExitedTargetMakesRestoreMemoryUnavailable()
    {
        var originalBytes = new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 };
        var memory = new ExitAfterInstallMemory();
        memory.Seed(0x401000, originalBytes);
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: null),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder(),
            targetProcessState: new FixedTargetProcessState(isRunning: false));

        session.Attach();
        session.InstallPatches();

        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
        Assert.Equal(0, session.InstalledHookCount);
        Assert.False(session.CanUseFeatures);
        Assert.Empty(session.Symbols);
        Assert.Null(session.Resolver);
        Assert.Equal(4, session.GetDiagnosticSnapshot().RemoteAllocations.Count);
    }

    [Fact]
    public void DisposeStillThrowsRestoreFailureWhenTargetIsRunning()
    {
        var originalBytes = new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 };
        var memory = new ExitAfterInstallMemory();
        memory.Seed(0x401000, originalBytes);
        var manifest = MinimalManifest(new PatchHook(
            "ra3_1.12.game+1000",
            "Test Hook",
            ["jmp MustCode"],
            "MustCode",
            "_Back",
            [],
            ["mov edx,[eax+28]", "mov eax,[edx+20]"]));
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true, ProcessId: 1234),
            new SequentialRemoteAllocator(0x700000),
            new FakeBootstrapCodeBuilder(),
            processSuspender: NoopProcessSuspender.Instance,
            targetProcessState: new FixedTargetProcessState(isRunning: true));

        session.Attach();
        session.InstallPatches();

        var exception = Assert.Throws<InvalidOperationException>(session.Dispose);

        Assert.Contains("memory unavailable", exception.Message);
    }

    [Fact]
    public void AttachNamesTheDetectedVersionOfANonStandardBuild()
    {
        var session = new TrainerSession(
            MinimalManifest(),
            new FakeProcessMemory(),
            new TrainerTarget(
                "ra3_1.12.game",
                0x400000,
                true,
                true,
                ModulePath: @"D:\Games\RA3\Data\ra3_1.12.game",
                FileVersion: "1.12.5000.12345"),
            new SequentialRemoteAllocator(0x700000));

        var result = session.Attach();

        Assert.True(result.Success);
        Assert.Contains("1.12.5000.12345", result.Message);
        Assert.Contains("兼容模式", result.Message);
    }

    [Fact]
    public void AttachNamesTheDetectedVersionWhenRejectingUnsupportedTarget()
    {
        var session = new TrainerSession(
            MinimalManifest(),
            new FakeProcessMemory(),
            new TrainerTarget(
                "ra3_1.12.game",
                0x400000,
                true,
                false,
                FileVersion: "1.13.0.1"));

        var result = session.Attach();

        Assert.False(result.Success);
        Assert.Contains("1.13.0.1", result.Message);
        Assert.Contains("1.12.3444.25830", result.Message);
    }

    private static TrainerManifest MinimalManifest(params PatchHook[] hooks)
    {
        return new TrainerManifest(
            "ra3_1.12.game",
            [],
            new PatchManifest(hooks),
            []);
    }

    private sealed class FakeBootstrapCodeBuilder : BootstrapCodeBuilder
    {
        public override BootstrapCode Build(IReadOnlyList<string> autoAssemblerLines)
        {
            return BuildFake();
        }

        public override BootstrapCode Build(IReadOnlyList<string> autoAssemblerLines, BootstrapBuildContext context)
        {
            return BuildFake();
        }

        private static BootstrapCode BuildFake()
        {
            return new BootstrapCode([0xCC], [0x90], new Dictionary<string, byte[]>
            {
                ["iEnable+24"] = [0xA0, 0xA5, 0x86, 0x65]
            });
        }
    }

    private sealed class RecordingProcessMemory : IProcessMemory
    {
        private readonly FakeProcessMemory _inner = new();

        public List<string> Events { get; } = [];

        public byte[] ReadBytes(nint address, int count)
        {
            return _inner.ReadBytes(address, count);
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            _inner.WriteBytes(address, bytes);
            Events.Add($"write 0x{address:X}");
        }
    }

    private sealed class RecordingProtectedProcessMemory : IProcessMemory, IRemoteMemoryProtector
    {
        private readonly FakeProcessMemory _inner = new();

        public List<string> Events { get; } = [];

        public byte[] ReadBytes(nint address, int count)
        {
            return _inner.ReadBytes(address, count);
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            _inner.WriteBytes(address, bytes);
            Events.Add($"write 0x{address:X}");
        }

        public void ProtectExecuteReadWrite(nint address, int size)
        {
            Events.Add($"protect 0x{address:X} 0x{size:X}");
        }
    }

    private sealed class RecordingProcessSuspender : IProcessSuspender
    {
        private readonly List<string> _events;

        public RecordingProcessSuspender(List<string> events)
        {
            _events = events;
        }

        public IDisposable Suspend(int processId)
        {
            _events.Add($"suspend {processId}");
            return new ResumeScope(_events, processId);
        }

        private sealed class ResumeScope : IDisposable
        {
            private readonly List<string> _events;
            private readonly int _processId;

            public ResumeScope(List<string> events, int processId)
            {
                _events = events;
                _processId = processId;
            }

            public void Dispose()
            {
                _events.Add($"resume {_processId}");
            }
        }
    }

    private sealed class ExitAfterInstallMemory : IProcessMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private int _writes;

        public byte[] ReadBytes(nint address, int count)
        {
            return _inner.ReadBytes(address, count);
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            _writes++;
            if (_writes > 4)
            {
                throw new InvalidOperationException("simulated process memory unavailable");
            }

            _inner.WriteBytes(address, bytes);
        }

        public void Seed(nint address, ReadOnlySpan<byte> bytes)
        {
            _inner.WriteBytes(address, bytes);
        }
    }

    private sealed class FixedTargetProcessState : ITargetProcessState
    {
        private readonly bool _isRunning;

        public FixedTargetProcessState(bool isRunning)
        {
            _isRunning = isRunning;
        }

        public bool IsRunning(int? processId)
        {
            return _isRunning;
        }
    }

    private sealed class NoopProcessSuspender : IProcessSuspender
    {
        public static NoopProcessSuspender Instance { get; } = new();

        public IDisposable Suspend(int processId)
        {
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
