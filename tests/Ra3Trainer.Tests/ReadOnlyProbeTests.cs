using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ReadOnlyProbeTests
{
    private static readonly AddressResolver Resolver = new(0x400000, new Dictionary<string, nint>
    { ["MustCode"] = 0x710000, ["iEnable"] = 0x700100, ["ID"] = 0x700000 });

    [Fact]
    public void NativeSamplingHasOnlyReadAndQueryCallsAndReadOnlyAccessRights()
    {
        var api = new ProbeApi();
        using var memory = new Win32ProbeMemory(999, api);
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        Assert.Equal(0x410u, api.Access);
        Assert.Equal(1, api.Opens); // 只允许打开一次只读句柄
        Assert.True(api.Reads > 0);
        Assert.True(api.Queries > 0);
        // 断言禁止项"一次都没被调用过"，而不是只靠它们会抛异常。
        Assert.Equal(0, api.Writes);
        Assert.Equal(0, api.Allocations);
        Assert.Equal(0, api.Frees);
        Assert.Equal(0, api.Protections);
        Assert.Equal(0, api.CacheFlushes);
        Assert.False(sample.SelectionStable);
        Assert.DoesNotContain(typeof(IProbeMemory).GetMethods(), method => method.Name.Contains("Write"));
        // Every forbidden API below throws. This executes the actual production adapter.
    }

    [Fact]
    public void NativeShortReadsAreRejected()
    {
        using var memory = new Win32ProbeMemory(999, new ProbeApi { ShortRead = true });
        Assert.Throws<System.ComponentModel.Win32Exception>(() => memory.ReadBytes(0x10000, 4));
    }

    [Fact]
    public void NativeHardReadFailureSurfacesTheWin32Error()
    {
        // 返回值就是 false 的硬失败（不是短读）：必须抛 Win32Exception 并带上原生错误码。
        using var memory = new Win32ProbeMemory(999, new ProbeApi { HardFailure = true, LastError = 5 });
        var error = Assert.Throws<System.ComponentModel.Win32Exception>(() => memory.ReadBytes(0x10000, 4));
        Assert.Equal(5, error.NativeErrorCode);
    }

    [Fact]
    public void SamplesKnownChainAndComponentsWithoutFollowingLinkedList()
    {
        var memory = Fixture();
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        Assert.True(sample.SelectionStable);
        Assert.Equal(100, sample.Fields["head.health.4"].Value);
        Assert.Equal(25, sample.Fields["head.movement.first.8"].Value);
        Assert.Equal(3, sample.Fields["flags.14"].Value);
        Assert.Equal(123.5, sample.Fields["head.position.38"].Value);
        Assert.Equal(300, sample.Fields["mouse.source.3C"].Value);
        Assert.Single(memory.ReadAddresses, address => address == (nint)0x61004);
        Assert.DoesNotContain((nint)0x30000, memory.ReadAddresses); // never read node.next
        Assert.All(memory.ReadAddresses, address => Assert.True(address >= 0x10000));
        Assert.Null(sample.Fields["zoom.current"].Value);
        Assert.Contains("在线采样未启用", sample.Fields["zoom.current"].Status);
        Assert.Contains("陈旧", sample.Qualification);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(4u)]
    [InlineData(0x20001u)]
    [InlineData(0xFFFFFFFFu)]
    public void InvalidComponentIsRecordedAndNeverDereferenced(uint pointer)
    {
        var memory = Fixture();
        memory.Put(0x5033C, pointer);
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        Assert.Null(sample.Fields["head.health.4"].Value);
        Assert.DoesNotContain(unchecked((nint)pointer) + 4, memory.ReadAddresses);
    }

    [Theory]
    [InlineData(0x7FC00000u)]
    [InlineData(0x7F800000u)]
    [InlineData(0xFF800000u)]
    public void NonfiniteSamplesKeepRawBitsAndSerializeAsNull(uint bits)
    {
        var memory = Fixture();
        memory.Put(0x50038, bits);
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        var value = sample.Fields["head.position.38"];
        Assert.Null(value.Value);
        Assert.Equal(bits, value.Bits);
        Assert.Equal("非有限值", value.Status);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sample));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("Fields").GetProperty("head.position.38").GetProperty("Value").ValueKind);
    }

    [Fact]
    public void ChangedSelectionIsMarkedUnstable()
    {
        var memory = Fixture();
        memory.ChangeRootAfterFirstRead = true;
        Assert.False(new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now).SelectionStable);
    }

    [Fact]
    public void UninstalledCopiesAndFlagsAreNotRead()
    {
        var memory = Fixture();
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(false, DateTimeOffset.Now);
        Assert.Null(sample.Fields["coordinate.copy.1080"].Value);
        Assert.DoesNotContain(memory.ReadAddresses, address => address >= 0x700000 && address < 0x720000);
        Assert.Equal(300, sample.Fields["mouse.source.3C"].Value);
    }

    [Theory]
    [InlineData(0x101u, 0x1000u, false)]
    [InlineData(0x01u, 0x1000u, false)]
    [InlineData(0x04u, 0x2000u, false)]
    [InlineData(0x10u, 0x1000u, false)]
    [InlineData(0x02u, 0x1000u, true)]
    [InlineData(0x20u, 0x1000u, true)]
    public void QueryRejectsGuardUncommittedAndExecuteOnlyPages(uint protection, uint state, bool expected)
    {
        var region = new Win32MemoryRegion(0x10000, 0x1000, protection, state, protection, 0);
        Assert.Equal(expected, ReadOnlyProbe.Readable(region, 0x10000, 4));
        Assert.False(ReadOnlyProbe.Readable(region, 0x10FFF, 4));
    }

    [Fact]
    public void SummaryAndBothExportsContainExactSampleCountAndDistributions()
    {
        var root = Path.Combine(Path.GetTempPath(), "ra3-probe-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(root, "probe");
            var log = new ProbeLog(directory);
            foreach (var value in new double?[] { 0, 12, null, -4 })
                log.Append(new(DateTimeOffset.Now, "fake", false, true,
                    new Dictionary<string, ProbeField> { ["x"] = new(value, value is null ? "失败" : "可读") }));
            Assert.Equal(4, log.SampleCount);
            Assert.Equal(4, File.ReadAllLines(log.FilePath).Length);
            using var summary = JsonDocument.Parse(File.ReadAllText(log.SummaryPath));
            var field = summary.RootElement.GetProperty("Fields").GetProperty("x");
            Assert.Equal(-4, field.GetProperty("Min").GetDouble());
            Assert.Equal(12, field.GetProperty("Max").GetDouble());
            Assert.Equal(.75, field.GetProperty("NonNullRate").GetDouble());
            Assert.Equal(.5, field.GetProperty("NonzeroRate").GetDouble());
            using (var archive = ZipFile.OpenRead(ProbeLog.Export(directory, root)))
                Assert.Equal(2, archive.Entries.Count);
            var collector = new CrashEvidenceCollector(werRoot: Path.Combine(root, "no-wer"), windowsEventLogReader: () => null);
            var evidence = collector.Collect(root, new("fake.game", null, null, null, "test", [], ProbeDirectory: directory), [], []);
            using var zip = ZipFile.OpenRead(evidence.ArchivePath);
            Assert.Equal(2, zip.Entries.Count(entry => entry.FullName.StartsWith("trainer/probe/")));
            Assert.Equal(0, new ProbeLog(directory).SampleCount);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptyExportIsAnExplicitChineseError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProbeLog.Export(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), Path.GetTempPath()));
        Assert.Contains("尚无采样", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableProbeDirectoryWarnsWithoutLosingOtherEvidence(bool denied)
    {
        var root = Path.Combine(Path.GetTempPath(), "ra3-probe-evidence-" + Guid.NewGuid().ToString("N"));
        try
        {
            var collector = new CrashEvidenceCollector(werRoot: Path.Combine(root, "no-wer"), windowsEventLogReader: () => null,
                probeFiles: _ => throw (denied ? new UnauthorizedAccessException("fake denied") : new IOException("fake I/O")));
            var result = collector.Collect(root, new("fake.game", null, null, null, "test", [], ProbeDirectory: "fake-probe"), [], []);
            Assert.Contains(result.Warnings, warning => warning.Contains("读取采样目录失败，其余取证继续"));
            using var archive = ZipFile.OpenRead(result.ArchivePath);
            Assert.Contains(archive.Entries, entry => entry.FullName == "trainer/hooks.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "trainer/modules.json");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RemovedMouseCopiesAreAbsentAndNeverRead()
    {
        var memory = Fixture();
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        foreach (var suffix in new[] { "1110", "1114", "111C" })
        {
            Assert.DoesNotContain("coordinate.copy." + suffix, sample.Fields.Keys);
            Assert.DoesNotContain((nint)(0x710000 + Convert.ToInt32(suffix, 16)), memory.ReadAddresses);
        }
        Assert.Equal(123.5, sample.Fields["head.position.38"].Value);
    }

    [Fact]
    public void SummarySeparatesStableSelectedSamplesFromEmptyAndChangedSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "ra3-selected-probe-" + Guid.NewGuid());
        try
        {
            var log = new ProbeLog(root);
            foreach (var (count, stable, value) in new (double, bool, double?)[]
                     { (0, true, null), (1, true, 100), (1, true, null), (1, false, 200) })
                log.Append(new(DateTimeOffset.Now, "fake", false, stable, new Dictionary<string, ProbeField>
                { ["selection.count"] = new(count, "test"), ["head.health.4"] = new(value, "test") }));
            using var document = JsonDocument.Parse(File.ReadAllText(log.SummaryPath));
            var health = document.RootElement.GetProperty("Fields").GetProperty("head.health.4");
            Assert.Equal(4, health.GetProperty("Samples").GetInt32());
            Assert.Equal(2, health.GetProperty("SelectedSamples").GetInt32());
            Assert.Equal(1, health.GetProperty("SelectedAvailable").GetInt32());
            Assert.Equal(.5, health.GetProperty("SelectedNonNullRate").GetDouble());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void EmptySelectionSkipsSentinelAndDeepReads()
    {
        var memory = Fixture();
        memory.Put(0x2005C, 0);
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(false, DateTimeOffset.Now);
        Assert.Equal("未选中", sample.Fields["head.health.4"].Status);
        Assert.Equal("未选中", sample.Fields["selection.state"].Status);
        Assert.DoesNotContain((nint)0x30008, memory.ReadAddresses);
        Assert.DoesNotContain((nint)0x20050, memory.ReadAddresses);
    }

    [Theory]
    [InlineData(0x80010000u)]
    [InlineData(0x96C5D080u)]
    [InlineData(0xB7160000u)]
    public void ReadableHighX86WrapperDoesNotBreakSelectedChain(uint wrapper)
    {
        var memory = Fixture();
        memory.Put(0x30008, wrapper);
        memory.Put(unchecked((nint)wrapper) + 0x138, 0x50000);
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(false, DateTimeOffset.Now);
        Assert.True(sample.SelectionStable);
        Assert.Equal(100, sample.Fields["head.health.4"].Value);
        Assert.Equal("已选中", sample.Fields["selection.state"].Status);
    }

    [Fact]
    public void UnreadableQueryIsReportedInsteadOfReadingThroughIt()
    {
        // 指针落点所在页查不到（VirtualQueryEx 返回 0 → Query 为 null）时必须记为"指针落点不可读"并不再深入。
        var memory = Fixture();
        memory.UnqueriedPage = 0x40000; // head.wrapper 的落点
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        Assert.Equal("指针落点不可读", sample.Fields["head.wrapper"].Status);
        Assert.Null(sample.Fields["head.health.4"].Value);
        Assert.DoesNotContain((nint)0x61004, memory.ReadAddresses);
    }

    [Fact]
    public void UnreadableFieldPageIsReportedAsNotReadable()
    {
        // 字段自身所在页不可查询时记"字段不可读"，也绝不能拿零填充当读数。
        var memory = Fixture();
        memory.UnqueriedPage = 0x700000; // iEnable 页：flags.14 读在 0x700114
        var sample = new ReadOnlyProbe(memory, Resolver, "fake").Capture(true, DateTimeOffset.Now);
        Assert.Equal("字段不可读", sample.Fields["flags.14"].Status);
        Assert.Null(sample.Fields["flags.14"].Value);
        Assert.DoesNotContain((nint)0x700114, memory.ReadAddresses);
        // 只是这一页查不到，别的页照常可读：health 链不受影响。
        Assert.Equal(100, sample.Fields["head.health.4"].Value);
    }

    private static ProbeMemory Fixture()
    {
        var memory = new ProbeMemory();
        foreach (var (address, value) in new (int, uint)[]
        {
            (0xCDB73C, 0x20000), (0x2005C, 1), (0x20050, 0x30000), (0x20054, 0x30000),
            (0x30008, 0x40000), (0x40138, 0x50000), (0x50004, 0x60000), (0x5033C, 0x61000),
            (0x50374, 0x62000), (0x62200, 0x63000), (0x63000, 0x64000), (0x63004, 0x65000),
            (0x503CC, 0x66000), (0x50418, 0x67000), (0x700000, 0x67000), (0x700114, 3),
            (0xCDAEFC, 0x68000)
        }) memory.Put(address, value);
        memory.Put(0x61004, BitConverter.SingleToUInt32Bits(100));
        memory.Put(0x64008, BitConverter.SingleToUInt32Bits(25));
        memory.Put(0x50038, BitConverter.SingleToUInt32Bits(123.5f));
        memory.Put(0x6803C, 300);
        return memory;
    }

    private sealed class ProbeMemory : IProbeMemory
    {
        private readonly Dictionary<nint, uint> _values = new();
        public List<nint> ReadAddresses { get; } = [];
        public bool ChangeRootAfterFirstRead { get; set; }
        /// <summary>该地址所在页的 Query 返回 null，用来覆盖"字段不可读 / 指针落点不可读"分支。</summary>
        public nint? UnqueriedPage { get; set; }
        public void Put(nint address, uint value) => _values[address] = value;
        public Win32MemoryRegion? Query(nint address) =>
            UnqueriedPage is nint missing && (address & ~0xFFF) == (missing & ~0xFFF)
                ? null
                : new(address & ~0xFFF, 0x1000, 4, 0x1000, 4, 0x20000);
        public byte[] ReadBytes(nint address, int count)
        {
            var changed = ChangeRootAfterFirstRead && address == 0xCDB73C && ReadAddresses.Contains(address);
            ReadAddresses.Add(address);
            return BitConverter.GetBytes(changed ? 0u : _values.GetValueOrDefault(address))[..count];
        }
    }

    private sealed class ProbeApi : IWin32MemoryApi
    {
        public uint Access;
        public int Opens, Reads, Queries, Writes, Allocations, Frees, Protections, CacheFlushes;
        public bool ShortRead, HardFailure;
        public int LastError = 299;
        public int GetLastWin32Error() => LastError;
        public SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId)
        { Opens++; Access = desiredAccess; return new(1234, ownsHandle: false); }
        public bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, int size, out int bytesRead)
        {
            Reads++;
            if (HardFailure) { bytesRead = 0; return false; }
            bytesRead = ShortRead ? 0 : size;
            return true;
        }
        public nuint VirtualQueryEx(SafeProcessHandle process, nint address, out Win32MemoryBasicInformation buffer, nuint length)
        { Queries++; buffer = new() { BaseAddress = address & ~0xFFF, RegionSize = 0x1000, State = 0x1000, Protect = 4 }; return length; }
        // 先计数再抛：即使异常被吞掉，调用次数也留得下痕迹。
        public bool WriteProcessMemory(SafeProcessHandle process, nint address, byte[] buffer, int size, out int written)
        { Writes++; throw new Exception("FORBIDDEN Write"); }
        public nint VirtualAllocEx(SafeProcessHandle process, nint address, nuint size, uint allocationType, uint protect)
        { Allocations++; throw new Exception("FORBIDDEN Allocate"); }
        public bool VirtualFreeEx(SafeProcessHandle process, nint address, nuint size, uint freeType)
        { Frees++; throw new Exception("FORBIDDEN Free"); }
        public bool VirtualProtectEx(SafeProcessHandle process, nint address, nuint size, uint protect, out uint oldProtect)
        { Protections++; throw new Exception("FORBIDDEN Protect"); }
        public bool FlushInstructionCache(SafeProcessHandle process, nint address, nuint size)
        { CacheFlushes++; throw new Exception("FORBIDDEN Execute"); }
    }
}
