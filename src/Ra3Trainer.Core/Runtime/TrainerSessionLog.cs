using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// Append-only jsonl record of everything this tool does to the target process: attach attempts,
/// hook site checks, feature toggles, restores and target exits. Kept next to the executable so a
/// crash report can be assembled from it later.
/// Deliberately mod-agnostic: it records the target process and the modules it had loaded as raw
/// data, never a hard-coded mod name, so switching to another mod keeps working unchanged.
/// </summary>
public sealed class TrainerSessionLog : IDisposable
{
    public const string FileNamePrefix = "session-";

    /// <summary>Numbers/plus signs and non-ASCII text stay readable in the file, so the log can be
    /// read by a human when explaining what happened.</summary>
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly StreamWriter? _writer;
    private bool _disposed;
    private readonly Queue<string> _recent = new();

    public IReadOnlyList<string> RecentEntries
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    private TrainerSessionLog(string filePath, Func<DateTimeOffset> clock, StreamWriter? writer)
    {
        FilePath = filePath;
        _clock = clock;
        _writer = writer;
    }

    /// <summary>File this instance appends to (even when logging is unavailable).</summary>
    public string FilePath { get; }

    /// <summary>False when the directory could not be written; all calls then become no-ops.</summary>
    public bool IsAvailable => _writer is not null;

    public static string DefaultDirectory(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "logs");
    }

    /// <summary>
    /// Opens (or appends to) today's log file and prunes older ones. Logging must never break the
    /// tool, so an unwritable directory yields a no-op instance instead of an exception.
    /// </summary>
    public static TrainerSessionLog Open(
        string directory,
        Func<DateTimeOffset>? clock = null,
        int retentionDays = 14)
    {
        var nowProvider = clock ?? (() => DateTimeOffset.Now);
        var path = Path.Combine(directory, $"{FileNamePrefix}{nowProvider():yyyyMMdd}.jsonl");
        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory, nowProvider(), retentionDays);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream) { AutoFlush = true };
            return new TrainerSessionLog(path, nowProvider, writer);
        }
        catch (IOException)
        {
            return new TrainerSessionLog(path, nowProvider, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new TrainerSessionLog(path, nowProvider, null);
        }
    }

    public void Write(string eventName, object? data = null)
    {
        if (_disposed)
        {
            return;
        }

        string line;
        try
        {
            line = JsonSerializer.Serialize(new
            {
                ts = _clock().ToString("O"),
                @event = eventName,
                data
            }, LineOptions);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
        {
            // Cycles and non-finite numbers must not make a successful game operation fail.
            // Do not call the original object's ToString/getters again in this fallback.
            line = JsonSerializer.Serialize(new
            {
                ts = _clock().ToString("O"),
                @event = eventName,
                data = $"日志数据无法序列化：{ex.Message}"
            }, LineOptions);
        }

        lock (_gate)
        {
            // 锁内重新取：Dispose 是"先置 _disposed、再进锁里释放 writer"，
            // 锁外拿到的快照可能已经释放 ⇒ 往已释放的 writer 写会抛 ObjectDisposedException，
            // 而下面只吞 IOException，异常会一路冒到正在执行游戏动作的调用方。
            if (_disposed)
            {
                return;
            }

            var writer = _writer;
            _recent.Enqueue(line);
            while (_recent.Count > 128) _recent.Dequeue();
            try
            {
                writer?.WriteLine(line);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Prune(string directory, DateTimeOffset now, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = now.Date.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(directory, $"{FileNamePrefix}*.jsonl"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[FileNamePrefix.Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) &&
                date < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
