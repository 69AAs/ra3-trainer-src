using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Ra3Trainer.Core.Runtime;

public sealed class ProbeFieldSummary
{
    public long Samples { get; set; }
    public long Available { get; set; }
    public long Nonzero { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double NonNullRate => Samples == 0 ? 0 : (double)Available / Samples;
    public double NonzeroRate => Samples == 0 ? 0 : (double)Nonzero / Samples;
    public long Readable { get; set; }
    public double ReadableRate => Samples == 0 ? 0 : (double)Readable / Samples;
    public long SelectedReadable { get; set; }
    public double SelectedReadableRate => SelectedSamples == 0 ? 0 : (double)SelectedReadable / SelectedSamples;
    public Dictionary<string, long> Statuses { get; } = new();
    public long SelectedSamples { get; set; }
    public long SelectedAvailable { get; set; }
    public double SelectedNonNullRate => SelectedSamples == 0 ? 0 : (double)SelectedAvailable / SelectedSamples;
}

/// <summary>每次开启一个独立文件；汇总只对应这个文件，重开不混入旧会话。</summary>
public sealed class ProbeLog
{
    private readonly Dictionary<string, ProbeFieldSummary> _summary = new();
    public string FilePath { get; }
    public string SummaryPath => Path.ChangeExtension(FilePath, ".summary.json");
    public long SampleCount { get; private set; }
    public long StableSelectionSamples { get; private set; }
    public long HooksInstalledSamples { get; private set; }

    public ProbeLog(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"probe-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
    }

    public void Append(ProbeSample sample)
    {
        // File.AppendAllText 默认 FileShare.Read：取证时另开的读者会写这个 .jsonl，
        // 于是这里共享冲突并沿着 OnProbeSample 冒出去（探针是"只观察"的，绝不能因此改行为）。
        using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(sample));
        }
        SampleCount++;
        if (sample.SelectionStable) StableSelectionSamples++;
        if (sample.HooksInstalled) HooksInstalledSamples++;
        var selected = sample.SelectionStable && sample.Fields.TryGetValue("selection.count", out var count) && count.Value > 0;
        foreach (var (key, field) in sample.Fields)
        {
            if (!_summary.TryGetValue(key, out var summary)) _summary[key] = summary = new();
            summary.Samples++;
            if (selected) summary.SelectedSamples++;
            summary.Statuses[field.Status] = summary.Statuses.GetValueOrDefault(field.Status) + 1;
            // Null/invalid pointers can still have numeric Value; count only successful reads
            // or successful destination checks, never interpret this as object/type validity.
            if (field.Value is double finite && double.IsFinite(finite) &&
                (field.Status.StartsWith("可读", StringComparison.Ordinal) ||
                 field.Status.StartsWith("落点可读", StringComparison.Ordinal)))
            {
                summary.Readable++;
                if (selected) summary.SelectedReadable++;
            }
            if (field.Value is double value)
            {
                summary.Available++;
                if (selected) summary.SelectedAvailable++;
                if (value != 0) summary.Nonzero++;
                summary.Min = summary.Min is double min ? Math.Min(min, value) : value;
                summary.Max = summary.Max is double max ? Math.Max(max, value) : value;
            }
        }
        // Durable after every sample, including an unexpected trainer exit.
        // 磁盘契约提示：下面的匿名对象/ProbeFieldSummary 的属性名（Source/SampleCount/Fields、
        // Samples/Available/SelectedReadableRate…）就是 .summary.json 的字段名，消费者按名字取值
        // （ProbeHitRateTests、ReadOnlyProbeTests 直接 GetProperty("Fields")）。改名不会编译报错，
        // 只会静默读到 null —— 要改必须同步改消费者与这批测试。
        var temporary = SummaryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            Source = Path.GetFileName(FilePath), SampleCount, StableSelectionSamples, HooksInstalledSamples,
            Note = "min/max 为经验观测边界，不是引擎不变量；NonNullRate 仅表示有数值，可能包含无效指针。ReadableRate 为字段读取或指针落点检查通过率，SelectedReadableRate 仅用稳定且已选中的样本作分母；二者均不代表对象类型或生命周期安全。Statuses 保留失败分布。",
            Fields = _summary
        }), new UTF8Encoding(false));
        File.Move(temporary, SummaryPath, overwrite: true);
    }

    public static string Export(string directory, string outputDirectory)
    {
        var files = Files(directory);
        if (files.Count == 0) throw new InvalidOperationException("尚无采样，请先连接游戏并开启安全采样。");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"probe-export-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) zip.CreateEntryFromFile(file, "probe/" + Path.GetFileName(file));
        return path;
    }

    public static IReadOnlyList<string> Files(string directory) => !Directory.Exists(directory) ? [] :
        Directory.EnumerateFiles(directory, "probe-*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".summary.json", StringComparison.OrdinalIgnoreCase)).Order().ToArray();
}
