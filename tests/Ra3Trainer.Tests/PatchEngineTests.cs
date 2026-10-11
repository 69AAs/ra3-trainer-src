using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PatchEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SilentShortWriteIsDetectedDuringInstallAndRestore(bool restoring)
    {
        var memory = new SilentShortMemory();
        byte[] original = [1, 2, 3, 4, 5];
        memory.Inner.WriteBytes(0x1000, original);
        var engine = new PatchEngine(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 }));
        PatchHookPlan[] plans = [new("ra3_1.12.game+1000", "MustCode", 5, original)];
        if (restoring)
        {
            engine.Install(plans);
            memory.ShortenNextWrite = true;
            Assert.Throws<InvalidOperationException>(engine.RestoreAll);
            Assert.Equal(1, engine.InstalledHookCount);
            engine.RestoreAll();
        }
        else
        {
            memory.ShortenNextWrite = true;
            Assert.Throws<PatchInstallException>(() => engine.Install(plans));
        }
        Assert.Equal(original, memory.ReadBytes(0x1000, 5));
        Assert.Equal(0, engine.InstalledHookCount);
    }

    private sealed class SilentShortMemory : IProcessMemory
    {
        public FakeProcessMemory Inner { get; } = new();
        public bool ShortenNextWrite { get; set; }
        public byte[] ReadBytes(nint address, int count) => Inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Inner.WriteBytes(address, ShortenNextWrite ? bytes[..1] : bytes);
            ShortenNextWrite = false;
        }
    }

    [Fact]
    public void InstallRejectsMismatchedRestoreLengthBeforeWritingFirstHook()
    {
        var memory = new FakeProcessMemory();
        byte[] original = [1, 2, 3, 4, 5];
        memory.WriteBytes(0x1000, original);
        var engine = new PatchEngine(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 }));

        Assert.Throws<PatchInstallException>(() => engine.Install([
            new PatchHookPlan("ra3_1.12.game+1000", "MustCode", 5, original),
            new PatchHookPlan("ra3_1.12.game+1100", "MustCode", 6, original)]));

        Assert.Equal(original, memory.ReadBytes(0x1000, 5));
        Assert.Empty(engine.InstalledRanges);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void InstallRollsBackCurrentHookWhenWriteChangesMemoryThenThrows(int bytesWritten)
    {
        var memory = new FaultingMemory();
        byte[] original = [1, 2, 3, 4, 5];
        memory.Inner.WriteBytes(0x1000, original);
        memory.FailureAddress = 0x1000;
        memory.BytesBeforeFailure = bytesWritten;
        var engine = new PatchEngine(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 }));

        Assert.Throws<PatchInstallException>(() => engine.Install(
            [new PatchHookPlan("ra3_1.12.game+1000", "MustCode", 5, original)]));

        Assert.Equal(original, memory.ReadBytes(0x1000, 5));
        Assert.Equal(0, engine.InstalledHookCount);
    }

    [Fact]
    public void RestoreAttemptsOtherHooksAndRetainsOnlyFailedHooksForRetry()
    {
        var memory = new FaultingMemory();
        byte[] original = [1, 2, 3, 4, 5];
        memory.Inner.WriteBytes(0x1000, original);
        memory.Inner.WriteBytes(0x1100, original);
        var engine = new PatchEngine(memory,
            new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 }));
        engine.Install([
            new PatchHookPlan("ra3_1.12.game+1000", "MustCode", 5, original),
            new PatchHookPlan("ra3_1.12.game+1100", "MustCode", 5, original)]);
        memory.FailureAddress = 0x1100;

        Assert.Throws<InvalidOperationException>(engine.RestoreAll);

        Assert.Equal(original, memory.ReadBytes(0x1000, 5));
        Assert.Equal(1, engine.InstalledHookCount);
        engine.RestoreAll();
        Assert.Equal(original, memory.ReadBytes(0x1100, 5));
        Assert.Equal(0, engine.InstalledHookCount);
    }

    private sealed class FaultingMemory : IProcessMemory
    {
        public FakeProcessMemory Inner { get; } = new();
        public nint? FailureAddress { get; set; }
        public int BytesBeforeFailure { get; set; }

        public byte[] ReadBytes(nint address, int count) => Inner.ReadBytes(address, count);

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            if (FailureAddress == address)
            {
                FailureAddress = null;
                Inner.WriteBytes(address, bytes[..Math.Min(BytesBeforeFailure, bytes.Length)]);
                throw new InvalidOperationException("simulated partial write or cache flush failure");
            }
            Inner.WriteBytes(address, bytes);
        }
    }

    [Fact]
    public void PlannerCreatesPlansForAllParsedHooks()
    {
        var manifest = TestAssets.LoadManifest();

        var plans = PatchHookPlanner.CreatePlans(manifest.PatchManifest);

        Assert.Equal(22, plans.Count);
        Assert.All(plans, plan =>
        {
            Assert.True(plan.PatchLength >= 5);
            // >= 5 是构造保证的（PatchLength=Max(5, len)），真正要钉的是与原始字节等长：
            // Install 会拒绝 OriginalBytes.Length != PatchLength 的计划。
            Assert.Equal(plan.OriginalBytes.Length, plan.PatchLength);
            Assert.NotEmpty(plan.OriginalBytes);
            Assert.Contains(plan.OriginalBytes, value => value != 0);
        });
    }

    [Fact]
    public void InstallWritesPatchesAndRestoreWritesOriginalBytes()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x1000, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var hook = new PatchHookPlan(
            Address: "ra3_1.12.game+1000",
            Target: "MustCode+20",
            PatchLength: 5,
            OriginalBytes: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 });
        var engine = new PatchEngine(memory, resolver);

        engine.Install([hook]);

        Assert.Equal(new byte[] { 0xE9, 0x1B, 0x10, 0x00, 0x00 }, memory.ReadBytes(0x1000, 5));

        engine.RestoreAll();

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, memory.ReadBytes(0x1000, 5));
    }

    [Fact]
    public void InstallTreatsAlreadyInstalledPatchAsIdempotent()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x1000, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var hook = new PatchHookPlan(
            Address: "ra3_1.12.game+1000",
            Target: "MustCode+20",
            PatchLength: 5,
            OriginalBytes: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 });
        var engine = new PatchEngine(memory, resolver);

        engine.Install([hook]);
        engine.Install([hook]);

        Assert.Equal(1, engine.InstalledHookCount);

        engine.RestoreAll();

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, memory.ReadBytes(0x1000, 5));
        Assert.Equal(0, engine.InstalledHookCount);
    }

    [Fact]
    public void InstallRollsBackAlreadyWrittenHooksWhenLaterHookFails()
    {
        var memory = new WriteRecordingMemory();
        memory.Inner.WriteBytes(0x1000, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        memory.Inner.WriteBytes(0x1100, new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0xBB });
        var resolver = new AddressResolver(0, new Dictionary<string, nint> { ["MustCode"] = 0x2000 });
        var engine = new PatchEngine(memory, resolver);

        var hooks = new[]
        {
            new PatchHookPlan("ra3_1.12.game+1000", "MustCode+20", 5, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }),
            new PatchHookPlan("ra3_1.12.game+1100", "MustCode+40", 5, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE })
        };

        Assert.Throws<PatchInstallException>(() => engine.Install(hooks));

        // 关键：hook1 必须**先被真的写成跳转、再被回滚**。只断言 0x1000 仍等于原始字节，
        // “写了再回滚”与“根本没写”无法区分 —— 那样这条用例其实没覆盖到回滚路径。
        Assert.Contains(memory.Log, entry => entry.Address == 0x1000 && entry.Bytes.Length == 5 && entry.Bytes[0] == 0xE9);
        // hook2 的原始字节对不上（内存里是 FF EE DD CC BB），在写入前就该被拒 —— 0x1100 一个字节都不许动。
        Assert.DoesNotContain(memory.Log, entry => entry.Address == 0x1100);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, memory.ReadBytes(0x1000, 5));
        Assert.Equal(new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0xBB }, memory.ReadBytes(0x1100, 5));
        Assert.Equal(0, engine.InstalledHookCount);
    }

    /// <summary>记录每一次经 IProcessMemory 的写入（种子写入走 Inner，不进日志）。</summary>
    private sealed class WriteRecordingMemory : IProcessMemory
    {
        public FakeProcessMemory Inner { get; } = new();
        public List<(nint Address, byte[] Bytes)> Log { get; } = [];
        public byte[] ReadBytes(nint address, int count) => Inner.ReadBytes(address, count);
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Log.Add((address, bytes.ToArray()));
            Inner.WriteBytes(address, bytes);
        }
    }

    [Fact]
    public void InstallReportsExpectedAndActualBytesWhenOriginalBytesMismatch()
    {
        var memory = new FakeProcessMemory();
        memory.WriteBytes(0x70F530, new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 });
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint> { ["MustCode"] = 0x10000000 });
        var engine = new PatchEngine(memory, resolver);
        var hook = new PatchHookPlan(
            Address: "ra3_1.12.game+30F530",
            Target: "MustCode+3d0",
            PatchLength: 5,
            OriginalBytes: new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 });

        var exception = Assert.Throws<PatchInstallException>(() => engine.Install([hook]));

        Assert.Equal(1, exception.HookIndex);
        Assert.Equal(1, exception.HookCount);
        Assert.Equal((nint)0x70F530, exception.AbsoluteAddress);
        Assert.Contains("ra3_1.12.game+30F530", exception.Message);
        Assert.Contains("absolute 0x70F530", exception.Message);
        Assert.Contains("expected 03 50 04 3B D7", exception.Message);
        Assert.Contains("actual E9 11 22 33 44", exception.Message);
    }

    [Fact]
    public void InstallReportsHookContextWhenPatchWriteFails()
    {
        var originalBytes = new byte[] { 0x8B, 0x89, 0x3C, 0x03, 0x00, 0x00 };
        var memory = new WriteFailingProcessMemory(0x6E24E3);
        memory.Seed(0x6E24E3, originalBytes);
        var resolver = new AddressResolver(0x400000, new Dictionary<string, nint> { ["MustCode"] = 0x10000000 });
        var engine = new PatchEngine(memory, resolver);
        var hook = new PatchHookPlan(
            Address: "ra3_1.12.game+2E24E3",
            Target: "MustCode+506",
            PatchLength: 6,
            OriginalBytes: originalBytes)
        {
            SectionTitle = "Player One Kill It Mode Code"
        };

        var exception = Assert.Throws<PatchInstallException>(() => engine.Install([hook]));

        Assert.Equal(1, exception.HookIndex);
        Assert.Equal(1, exception.HookCount);
        Assert.Equal((nint)0x6E24E3, exception.AbsoluteAddress);
        Assert.Contains("hook 1/1", exception.Message);
        Assert.Contains("ra3_1.12.game+2E24E3", exception.Message);
        Assert.Contains("absolute 0x6E24E3", exception.Message);
        Assert.Contains("Player One Kill It Mode Code", exception.Message);
        Assert.Contains("simulated write failure", exception.Message);
    }

    private sealed class WriteFailingProcessMemory : IProcessMemory
    {
        private readonly Dictionary<nint, byte> _bytes = new();
        private readonly nint _failAddress;

        public WriteFailingProcessMemory(nint failAddress)
        {
            _failAddress = failAddress;
        }

        public byte[] ReadBytes(nint address, int count)
        {
            var bytes = new byte[count];
            for (var index = 0; index < count; index++)
            {
                bytes[index] = _bytes.GetValueOrDefault(address + index);
            }

            return bytes;
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            if (address == _failAddress)
            {
                throw new InvalidOperationException($"simulated write failure at 0x{address:X}");
            }

            Seed(address, bytes.ToArray());
        }

        public void Seed(nint address, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                _bytes[address + index] = bytes[index];
            }
        }
    }
}
