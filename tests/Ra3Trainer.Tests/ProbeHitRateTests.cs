using System.IO.Compression;
using System.Text.Json;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ProbeHitRateTests
{
    [Fact]
    public void ExportReportsReadableRateWithoutCountingNullOrInvalidPointersAsHits()
    {
        var root = Path.Combine(Path.GetTempPath(), "round16-hits-" + Guid.NewGuid());
        try
        {
            var log = new ProbeLog(Path.Combine(root, "probe"));
            foreach (var (status, value, count, stable) in new (string, double?, double, bool)[]
            {
                ("空指针", 0, 0, true),
                ("指针越界或未对齐", 123, 1, true),
                ("落点可读（类型待确认）", 65536, 1, true),
                ("可读（未认证合法）", 100, 1, false),
                ("上游不可用", null, 1, true)
            })
                log.Append(new(DateTimeOffset.UnixEpoch, "fake", false, stable,
                    new Dictionary<string, ProbeField>
                    {
                        ["selection.count"] = new(count, "可读"),
                        ["target"] = new(value, status)
                    }));

            using var zip = ZipFile.OpenRead(ProbeLog.Export(Path.Combine(root, "probe"), root));
            var entry = Assert.Single(zip.Entries, e => e.FullName.EndsWith(".summary.json"));
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var field = json.RootElement.GetProperty("Fields").GetProperty("target");
            Assert.Equal(5, field.GetProperty("Samples").GetInt32());
            // Available 只表示"有数值"：空指针的 0 与"指针越界或未对齐"的 123 都算进来。
            Assert.Equal(4, field.GetProperty("Available").GetInt32());
            Assert.Equal(.8, field.GetProperty("NonNullRate").GetDouble());
            Assert.Equal(3, field.GetProperty("Nonzero").GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("Fields").GetProperty("target")
                .GetProperty("Statuses").GetProperty("空指针").GetInt32());
            Assert.Equal(2, field.GetProperty("Readable").GetInt32());
            Assert.Equal(.4, field.GetProperty("ReadableRate").GetDouble());
            Assert.Equal(3, field.GetProperty("SelectedSamples").GetInt32());
            Assert.Equal(1, field.GetProperty("SelectedReadable").GetInt32());
            Assert.Equal(1d / 3, field.GetProperty("SelectedReadableRate").GetDouble());
            Assert.Contains("不是引擎不变量", json.RootElement.GetProperty("Note").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EmptySummaryHasZeroRates()
    {
        var summary = new ProbeFieldSummary();
        Assert.Equal(0, summary.ReadableRate);
        Assert.Equal(0, summary.SelectedReadableRate);
        // 空汇总下每条比率都必须走 0 守卫，不能给出 NaN/∞。
        Assert.Equal(0, summary.NonNullRate);
        Assert.Equal(0, summary.NonzeroRate);
        Assert.Equal(0, summary.SelectedNonNullRate);
        Assert.False(double.IsNaN(summary.SelectedReadableRate));
    }

    [Fact]
    public void RatesStayFiniteWhenSamplesExistButNothingWasSelected()
    {
        // Samples>0 而 SelectedSamples==0：选中口径的分母守卫必须按"选中样本"算，不能借用全部样本。
        var summary = new ProbeFieldSummary { Samples = 4, Available = 2, Nonzero = 1, Readable = 1 };
        Assert.Equal(.5, summary.NonNullRate);
        Assert.Equal(.25, summary.NonzeroRate);
        Assert.Equal(.25, summary.ReadableRate);
        Assert.Equal(0, summary.SelectedReadableRate);
        Assert.Equal(0, summary.SelectedNonNullRate);

        // 反向：只有选中样本时（Samples==0）SelectedReadableRate 的分母仍是 SelectedSamples。
        var selectedOnly = new ProbeFieldSummary { SelectedSamples = 2, SelectedReadable = 1, SelectedAvailable = 1 };
        Assert.Equal(.5, selectedOnly.SelectedReadableRate);
        Assert.Equal(.5, selectedOnly.SelectedNonNullRate);
        Assert.Equal(0, selectedOnly.ReadableRate);
    }
}
