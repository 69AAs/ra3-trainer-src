using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class QueueStopTests
{
    public static ReinforcementQueueEntry[] Entries =>
        [new("第一批", "0x" + ReinforcementSettings.DefaultUnitId.ToString("X8"), "5", "2"),
         new("第二批", "0x" + ReinforcementSettings.DefaultUnitId.ToString("X8"), "5", "2")];

    [Theory]
    [InlineData("late-clear", false)]
    [InlineData("late-clear", true)]
    [InlineData("partial", false)]
    [InlineData("partial", true)]
    public async Task StopsBeforeSecondItemEvenWhenSlotIsFreeAndObserversFail(string mode, bool throwing)
    {
        var memory = new QueueMemory(mode);
        var controller = new FeatureController(memory, memory.Resolver);
        var evidenceCount = 0;
        controller.EvidenceSink = _ => { evidenceCount++; if (throwing) throw new IOException("日志故障"); };
        var reports = new List<ReinforcementQueueResult>();
        var validations = 0;
        var validationsAtBoundary = 0;
        var execution = ReinforcementQueueRunner.ExecuteAsync(Enumerate(), controller,
            CoordinateDispatchTests.Feature("We Need Back"), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1),
            validateBeforeDispatch: () => validations++, onResult: r =>
            { reports.Add(r); if (throwing) throw new IOException("面板/日志故障"); });
        var results = await execution;
        // 观察者（EvidenceSink / onResult）抛异常不许让整批执行失败：能正常跑完并拿到结果就是这条契约。
        Assert.Equal(TaskStatus.RanToCompletion, execution.Status);
        Assert.Equal(2, results.Count);
        Assert.Equal(Entries, results.Select(r => r.Entry));
        Assert.Equal(results, reports);
        Assert.All(results, r => Assert.Equal(mode == "partial" ? ReinforcementQueueItemStatus.Failed : ReinforcementQueueItemStatus.TimedOut, r.Status));
        Assert.All(results, r => Assert.Contains(mode == "partial" ? "队列已停止：上一批已派发后失败" : "队列已停止：上一批超时", r.Message));
        Assert.Contains("未派发", results[1].Message);
        Assert.Equal(1, memory.GenerationDispatches);
        Assert.Equal(1, memory.ConversionDispatches);
        Assert.Equal(5, memory.Writes.Count);
        Assert.Equal(1, evidenceCount);
        Assert.Equal(memory.ReadsAtBoundary, memory.ReadCount);
        Assert.Equal(memory.WritesAtBoundary, memory.Writes.Count);
        Assert.Equal(validationsAtBoundary, validations);

        IEnumerable<ReinforcementQueueEntry> Enumerate()
        {
            yield return Entries[0];
            // Deterministic late host completion, AFTER first result but BEFORE next item.
            // This is a fixture-side host write, never a controller/queue write.
            memory.CompleteLate();
            validationsAtBoundary = validations;
            yield return Entries[1];
        }
    }

    [Fact]
    public async Task InputAndRootRejectionsContinueWithOneResultPerEntry()
    {
        var memory = new QueueMemory("partial");
        memory.Word("ra3_1.12.game+8DAEFC", 0);
        var results = await ReinforcementQueueRunner.ExecuteAsync(
            [Entries[0] with { CountText = "51" }, .. Entries], new FeatureController(memory, memory.Resolver),
            CoordinateDispatchTests.Feature("We Need Back"));
        Assert.Equal(3, results.Count);
        Assert.Equal(ReinforcementQueueItemStatus.Skipped, results[0].Status);
        Assert.All(results.Skip(1), r => { Assert.Equal(ReinforcementQueueItemStatus.Failed, r.Status); Assert.Contains("根链", r.Message); Assert.DoesNotContain("队列已停止", r.Message); });
        Assert.Empty(memory.Writes);
    }

    // 【一】连发队列「已派发」判定时机：坐标动作把转换操作码写进动作槽之后、因缺完成凭据而失败时，
    // 这条必须算「已派发后失败」并让队列停下（用户定的语义：宁可多停一次），
    // 不能在「动作槽可能不干净」的状态下继续派发第二批。
    // 旧行为：dispatched 只在转换+生成两段都跑完后才置位 ⇒ 这里被当普通失败，第二批照发。
    [Fact]
    public async Task ConversionCredentialFailureStopsQueueBeforeNextDispatch()
    {
        var memory = new QueueMemory("no-credential");
        var controller = new FeatureController(memory, memory.Resolver);
        var results = await ReinforcementQueueRunner.ExecuteAsync(Enumerate(), controller,
            CoordinateDispatchTests.Feature("We Need Back"), TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(1));

        Assert.Equal(2, results.Count);
        Assert.Equal(ReinforcementQueueItemStatus.Failed, results[0].Status);
        // 失败理由本身：转换完成凭据缺失（写槽已发生）。
        Assert.Contains("完成凭据", results[0].Message);
        // 关键区分点：这条要按「已派发后失败」停下，而不是普通失败继续跑。
        Assert.Contains("队列已停止：上一批已派发后失败", results[0].Message);
        Assert.Contains("未派发", results[1].Message);
        // 只发生一次转换派发；第二批根本没被派发（旧行为会是 2 次）。
        Assert.Equal(1, memory.ConversionDispatches);
        Assert.Equal(0, memory.GenerationDispatches);

        IEnumerable<ReinforcementQueueEntry> Enumerate()
        {
            yield return Entries[0];
            yield return Entries[1];
        }
    }

    public sealed class QueueMemory : IProcessMemory, IProbeMemory
    {
        private readonly FakeProcessMemory inner = new();
        private readonly string mode;
        private uint conversionEpoch, dispatchEpoch;
        public AddressResolver Resolver { get; } = new(0x400000,
            new Dictionary<string, nint> { ["iEnable"] = 0x10000, ["ID"] = 0x11000, ["MustCode"] = 0x120000 });
        public List<string> Writes { get; } = [];
        public int GenerationDispatches, ConversionDispatches, ReadCount, ReadsAtBoundary, WritesAtBoundary;
        public QueueMemory(string mode)
        {
            this.mode = mode;
            SelectedObjectSafetyTests.Seed(inner, Resolver);
            Word("ra3_1.12.game+8DAEFC", 0xF0000); Word("ID", 0xD0000);
            Word("ra3_1.12.game+8E6C58", 0xE0000);
            Word(RemoteStateLayout.SelectedUnitCode, ReinforcementSettings.DefaultUnitId);
            inner.WriteBytes(0x50418, BitConverter.GetBytes(0xD0000));
        }
        public void Word(string field, uint value) => inner.WriteBytes(Resolver.Resolve(field), BitConverter.GetBytes(value));
        public void CompleteLate()
        {
            if (mode == "late-clear") { Word("iEnable+20", 0); Word("iEnable+48", ++dispatchEpoch); }
            ReadsAtBoundary = ReadCount; WritesAtBoundary = Writes.Count;
        }
        public Win32MemoryRegion? Query(nint address) => inner.Query(address);
        public byte[] ReadBytes(nint address, int count) { ReadCount++; return inner.ReadBytes(address, count); }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
        {
            Writes.Add($"{address:X}:{Convert.ToHexString(bytes)}");
            inner.WriteBytes(address, bytes);
            if (bytes.Length == 0) return; // 零长度写不能崩在 bytes[0]（会掩盖真正的生产断言）
            if (address != Resolver.Resolve("iEnable+20")) return;
            if (bytes[0] == 0x0E)
            {
                ConversionDispatches++;
                Word("MustCode+1080", BitConverter.SingleToUInt32Bits(4000));
                Word("MustCode+1084", BitConverter.SingleToUInt32Bits(4500));
                Word("MustCode+1088", BitConverter.SingleToUInt32Bits(200));
                // "no-credential"：转换被消费（动作槽归零）但**不写**完成凭据（iEnable+44），
                // 复现「转换操作码已写进动作槽、随后因缺完成凭据而失败」这条路径。
                if (mode != "no-credential") Word("iEnable+44", ++conversionEpoch);
                Word("iEnable+48", ++dispatchEpoch);
            }
            else if (bytes[0] == 0x0C)
            {
                GenerationDispatches++;
                if (mode == "late-clear") return;
                Word("iEnable+48", ++dispatchEpoch); Word("iEnable+4C", 1);
            }
            Word("iEnable+20", 0);
        }
    }
}
