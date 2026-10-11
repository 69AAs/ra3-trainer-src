using System.Text.Json;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ActionEvidenceTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("timeout-conversion")]
    [InlineData("timeout-generation")]
    [InlineData("stale")]
    [InlineData("partial")]
    [InlineData("busy")]
    [InlineData("short")]
    public async Task EvidenceAndThrowingSinkPreserveResultsAndEveryWrite(string mode)
    {
        var baseline = await Run(mode, false, false);
        var observed = await Run(mode, true, false);
        var failedSink = await Run(mode, true, true);
        Assert.Equal(baseline.Result, observed.Result);
        Assert.Equal(baseline.Result, failedSink.Result);
        // Run 会把异常编码进 Result：只证明三种运行"结果相同"识别不出"全体抛同一个异常"的派发回归，
        // 所以这里按模式钉死各自的确切结果——含 stale/partial 两条真实抛错路径与它们的原文。
        Assert.Equal(ExpectedResult(mode), baseline.Result);
        Assert.Equal(ExpectedResult(mode), observed.Result);
        Assert.Equal(ExpectedResult(mode), failedSink.Result);
        Assert.Equal(baseline.Memory.Writes, observed.Memory.Writes);
        Assert.Equal(baseline.Memory.Writes, failedSink.Memory.Writes);
        Assert.NotNull(observed.Evidence);
        Assert.Equal("fixture-" + mode, observed.Evidence.RequestId);
        Assert.NotNull(observed.Evidence.TerminalSnapshot);
        Assert.All(observed.Evidence.Phases, p => Assert.True(p.EndQpc >= p.StartQpc));
        Assert.Equal(1500, FeatureController.DefaultDispatchTimeout.TotalMilliseconds);
        if (mode != "success")
        {
            Assert.False(string.IsNullOrEmpty(observed.Evidence.Reason));
            var writes = observed.Memory.Writes.ToArray();
            Assert.NotNull(observed.Controller.CaptureTrailingEvidence());
            Assert.Equal(writes, observed.Memory.Writes);
        }
    }

    [Fact]
    public async Task SuccessRecordsExactParametersEpochsPhasesAndReadLengths()
    {
        var run = await Run("success", true, false);
        var e = run.Evidence!;
        Assert.Equal(new[] { "preflight", "conversion", "generation" }, e.Phases.Select(p => p.Phase));
        Assert.Equal(1u, e.Phases[1].ExpectedConversionEpoch);
        Assert.Equal(1u, e.Phases[1].ConversionEpochAfter);
        Assert.Equal(1u, e.Phases[2].DispatchEpochBefore);
        Assert.Equal(2u, e.Phases[2].DispatchEpochAfter);
        Assert.Contains(e.Phases[2].Writes, w => w.Field == "iEnable+28" && w.Value == 5 && w.Status == "written");
        Assert.Contains(e.Phases[2].Writes, w => w.Field == "iEnable+2C" && w.Value == 2);
        Assert.Equal(4, e.Phases[0].LastReads["iEnable+20"].ReadLength);
        Assert.Equal(0u, e.Phases[2].LastReads["iEnable+4C"].Bits);
        Assert.NotNull(e.ConversionMs); Assert.NotNull(e.GenerationMs);
        Assert.All(e.Phases.Skip(1), p => Assert.True(p.PollCount > 0));
        Assert.False(e.TerminalSnapshot!.Torn);
    }

    [Fact]
    public void SnapshotMarksTornAndRetainsShortAndFailedReadsWithoutZeroFill()
    {
        var memory = new EvidenceMemory("success") { TearEpoch = true, FailSnapshot = true };
        var snapshot = ActionEvidence.Capture(memory, memory.Resolver);
        Assert.True(snapshot.Torn);
        Assert.Contains(snapshot.Reads, r => r.Field == "MustCode+1100" && r.Bits is null && r.ReadLength is null && r.ReadStatus.StartsWith("read-failed"));
        memory = new EvidenceMemory("short");
        snapshot = ActionEvidence.Capture(memory, memory.Resolver);
        Assert.Contains(snapshot.Reads, r => r.Field == "iEnable+20" && r.ReadLength == 3 && r.Bits is null && r.ReadStatus == "short-read");
        Assert.Empty(memory.Writes);
    }

    [Fact]
    public async Task DiagnosticSnapshotFailureCannotReplaceConsumedResult()
    {
        var run = await Run("snapshot-fails", true, false);
        Assert.Equal("Consumed", run.Result);
        Assert.Contains(run.Evidence!.TerminalSnapshot!.Reads, r => r.Field == "MustCode+1100" && r.Bits is null);
    }

    [Fact]
    public async Task DefaultDeadlineRemains1500msAndNoAutomaticRetry()
    {
        var memory = new EvidenceMemory("timeout-conversion");
        ActionEvidence? evidence = null;
        var controller = new FeatureController(memory, memory.Resolver) { EvidenceSink = e => evidence = e };
        Assert.Equal(ActionDispatchResult.TimedOut, await controller.TriggerActionAndWaitForConsumptionAsync(Feature));
        Assert.Single(memory.Writes);
        Assert.Equal(1500, evidence!.TimeoutMs);
        Assert.Equal(50, evidence.PollIntervalMs);
        Assert.Null(evidence.GenerationMs);
        Assert.Contains("未继续派发", evidence.Reason);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("xUnit", "xUnit1013", Justification = "Explicit offline fixture CLI entry point, not a test")]
    public static async Task Demo(string directory)
    {
        Directory.CreateDirectory(directory);
        using var writer = new StreamWriter(Path.Combine(directory, "fixture-samples.jsonl"));
        foreach (var mode in new[] { "success", "timeout-conversion", "timeout-generation", "stale", "partial", "busy", "short", "snapshot-fails" })
        {
            var run = await Run(mode, true, false);
            writer.WriteLine(JsonSerializer.Serialize(new { source = "OFFLINE FIXTURE — 夹具不是真机", scenario = mode,
                result = run.Result, hostWrites = run.Memory.Writes, evidence = run.Evidence,
                trailing = run.Controller.CaptureTrailingEvidence() }));
        }
        var torn = new EvidenceMemory("success") { TearEpoch = true };
        writer.WriteLine(JsonSerializer.Serialize(new { source = "OFFLINE FIXTURE — 夹具不是真机", scenario = "torn", snapshot = ActionEvidence.Capture(torn, torn.Resolver) }));
    }

    /// <summary>每种夹具模式必须产生各自的确切结果（离线夹具的铁证，不是"三者相等"就算过）。</summary>
    private static string ExpectedResult(string mode) => mode switch
    {
        "success" => ActionDispatchResult.Consumed.ToString(),
        "timeout-conversion" or "timeout-generation" or "busy" or "short" => ActionDispatchResult.TimedOut.ToString(),
        "stale" => "InvalidOperationException: 本次转换未返回完成凭据，世界坐标新鲜性无法证明；已拒绝生成，未写入。",
        "partial" => "InvalidOperationException: 游戏未解析单位类型、生成函数返回空对象或新单位等级组件为空；可能已部分生成，无法回滚，请检查战场。",
        _ => throw new InvalidOperationException("未知的夹具模式：" + mode)
    };

    private static TrainerFeature Feature => CoordinateDispatchTests.Feature("We Need Back");
    private static async Task<(string Result, EvidenceMemory Memory, FeatureController Controller, ActionEvidence? Evidence)> Run(string mode, bool enabled, bool throwing)
    {
        var memory = new EvidenceMemory(mode); ActionEvidence? evidence = null;
        var controller = new FeatureController(memory, memory.Resolver) { AttachSessionId = "offline-fixture-session" };
        if (enabled) controller.EvidenceSink = e => { evidence = e; if (throwing) throw new IOException("fixture log failure"); };
        string result;
        try { result = (await controller.TriggerActionAndWaitForConsumptionAsync(Feature,
            new ReinforcementSettings(ReinforcementSettings.DefaultUnitId, 5, 2), TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(1), requestId: "fixture-" + mode)).ToString(); }
        catch (Exception ex) { result = ex.GetType().Name + ": " + ex.Message; }
        return (result, memory, controller, evidence);
    }

    private sealed class EvidenceMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory _inner = new();
        private readonly string _mode;
        private uint _reads;
        public bool TearEpoch, FailSnapshot;
        public List<string> Writes { get; } = new();
        public AddressResolver Resolver { get; } = new(0x400000,
            new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000, ["MustCode"] = 0x120000 });
        public EvidenceMemory(string mode)
        {
            _mode = mode; FailSnapshot = mode == "snapshot-fails";
            SelectedObjectSafetyTests.Seed(_inner, Resolver);
            Word("ra3_1.12.game+8DAEFC", 0xF0000); Word("ID", 0xD0000);
            Word("ra3_1.12.game+8E6C58", 0xE0000);
            Word(RemoteStateLayout.SelectedUnitCode, ReinforcementSettings.DefaultUnitId);
            _inner.WriteBytes(0x50418, BitConverter.GetBytes(0xD0000));
            if (mode == "busy") Word("iEnable+20", 0x0C);
        }
        private void Word(string field, uint value) => _inner.WriteBytes(Resolver.Resolve(field), BitConverter.GetBytes(value));
        public Win32MemoryRegion? Query(nint address) => _inner.Query(address);
        public byte[] ReadBytes(nint address, int count)
        {
            if (FailSnapshot && address == Resolver.Resolve("MustCode+1100")) throw new IOException("fixture unavailable");
            if (TearEpoch && address == Resolver.Resolve("iEnable+44")) return BitConverter.GetBytes(++_reads);
            // short 模式必须模拟"短读"：请求 4 字节只回 3 字节，但绝不能超过调用方请求的长度。
            return _inner.ReadBytes(address, _mode == "short" && address == Resolver.Resolve("iEnable+20") ? Math.Min(count, 3) : count);
        }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Writes.Add($"{address:X}:{Convert.ToHexString(bytes)}");
            _inner.WriteBytes(address, bytes);
            if (bytes.Length == 0) return; // 与 IProcessMemory 一致：零长度写不得崩在 bytes[0]。
            if (address != Resolver.Resolve("iEnable+20")) return;
            if (bytes[0] == 0x0E)
            {
                if (_mode == "timeout-conversion") return;
                Word("MustCode+1080", BitConverter.SingleToUInt32Bits(4000));
                Word("MustCode+1084", BitConverter.SingleToUInt32Bits(4500));
                Word("MustCode+1088", BitConverter.SingleToUInt32Bits(200));
                Word("iEnable+44", _mode == "stale" ? 0u : 1u); Word("iEnable+48", 1);
            }
            else
            {
                if (_mode == "timeout-generation") return;
                Word("iEnable+48", 2); Word("iEnable+4C", _mode == "partial" ? 1u : 0u);
            }
            Word("iEnable+20", 0);
        }
    }
}
