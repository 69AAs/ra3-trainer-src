using System.Diagnostics;
using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Runtime;

public sealed record EvidenceRead(string Field, int RequestedLength, int? ReadLength, string ReadStatus,
    string? Hex, uint? Bits, double? Value, long StartQpc, long EndQpc, DateTimeOffset Utc)
{
    public double ReadMs => Stopwatch.GetElapsedTime(StartQpc, EndQpc).TotalMilliseconds;
}
public sealed record EvidenceWrite(string Field, string Hex, uint? Value, string Status,
    long StartQpc, long EndQpc, DateTimeOffset Utc);
public sealed record ProtocolSnapshot(long StartQpc, long EndQpc, DateTimeOffset StartUtc,
    DateTimeOffset EndUtc, bool? Torn, string Consistency, IReadOnlyList<EvidenceRead> Reads);
public sealed class ActionPhaseEvidence
{
    public required string Phase { get; init; }
    public long StartQpc { get; init; } = Stopwatch.GetTimestamp();
    public DateTimeOffset StartUtc { get; init; } = DateTimeOffset.UtcNow;
    public long EndQpc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public long? WaitStartQpc { get; set; }
    public long? WaitEndQpc { get; set; }
    public int PollCount { get; set; }
    public double MaxPollGapMs { get; set; }
    public long? LastPollQpc { get; set; }
    public Dictionary<string, EvidenceRead> FirstReads { get; } = new();
    public Dictionary<string, EvidenceRead> LastReads { get; } = new();
    public List<EvidenceWrite> Writes { get; } = new();
    public uint? UnitId => WrittenValue(RemoteStateLayout.ReinforcementUnitId);
    public uint? Count => WrittenValue(RemoteStateLayout.ReinforcementCount);
    public uint? Rank => WrittenValue(RemoteStateLayout.ReinforcementRank);
    private uint? WrittenValue(string field) => Writes.LastOrDefault(w => w.Field == field && w.Status == "written")?.Value;
    public EvidenceWrite? DispatchWrite => Writes.LastOrDefault(w => w.Field == RemoteStateLayout.ActionDispatch);
    public EvidenceRead? CreationFailure => LastReads.GetValueOrDefault(RemoteStateLayout.CreationFailure);
    public uint? ExpectedConversionEpoch { get; set; }
    public uint? ExpectedDispatchEpoch { get; set; }
    public uint? ConversionEpochBefore { get; set; }
    public uint? ConversionEpochAfter { get; set; }
    public uint? DispatchEpochBefore { get; set; }
    public uint? DispatchEpochAfter { get; set; }
    public string Outcome { get; set; } = "not-completed";
    public string? Reason { get; set; }
    public double ElapsedMs => Stopwatch.GetElapsedTime(StartQpc, EndQpc).TotalMilliseconds;
}

/// <summary>Bounded observations, never a source of dispatch decisions. No sink calls in waits.</summary>
public sealed class ActionEvidence
{
    internal static readonly AsyncLocal<ActionEvidence?> Current = new();
    private readonly Dictionary<nint, string> _fields = new();
    public string RequestId { get; }
    public string? AttachSessionId { get; }
    public string Feature { get; }
    public long QpcFrequency => Stopwatch.Frequency;
    public long StartQpc { get; } = Stopwatch.GetTimestamp();
    public DateTimeOffset StartUtc { get; } = DateTimeOffset.UtcNow;
    public long EndQpc { get; private set; }
    public DateTimeOffset EndUtc { get; private set; }
    public double TimeoutMs { get; }
    public double PollIntervalMs { get; }
    public List<ActionPhaseEvidence> Phases { get; } = new();
    public ProtocolSnapshot? TerminalSnapshot { get; set; }
    public string Outcome { get; private set; } = "not-completed";
    public string? Reason { get; private set; }
    public double? ConversionMs => Phases.Find(p => p.Phase == "conversion")?.ElapsedMs;
    public double? GenerationMs => Phases.Find(p => p.Phase == "generation")?.ElapsedMs;
    public double TotalMs => Stopwatch.GetElapsedTime(StartQpc, EndQpc).TotalMilliseconds;
    public string CompletionQualification => "仅协议凭据；不证明实际新增数量。MustCode+1100仅辅助。";
    internal ActionPhaseEvidence Phase => Phases[^1];

    public ActionEvidence(AddressResolver resolver, string requestId, string? attachSessionId,
        string feature, double timeoutMs, double pollIntervalMs)
    {
        RequestId = requestId; AttachSessionId = attachSessionId; Feature = feature;
        TimeoutMs = timeoutMs; PollIntervalMs = pollIntervalMs;
        foreach (var field in ProtocolFields)
            try { _fields[resolver.Resolve(field)] = field; } catch { /* Observation only. */ }
        BeginPhase("preflight");
    }

    public static readonly string[] ProtocolFields = [RemoteStateLayout.ActionDispatch,
        RemoteStateLayout.DispatchLock, RemoteStateLayout.ConversionEpoch, RemoteStateLayout.DispatchEpoch,
        RemoteStateLayout.CreationFailure, RemoteStateLayout.WorldCoordinates, "MustCode+1084", "MustCode+1088",
        RemoteStateLayout.ReinforcementUnitId, RemoteStateLayout.ReinforcementCount,
        RemoteStateLayout.ReinforcementRank, "MustCode+1100"];

    internal void BeginPhase(string name)
    {
        if (Phases.Count > 0) EndPhase("passed", null);
        Phases.Add(new() { Phase = name });
    }
    internal void EndPhase(string outcome, string? reason)
    {
        Phase.EndQpc = Stopwatch.GetTimestamp(); Phase.EndUtc = DateTimeOffset.UtcNow;
        Phase.Outcome = outcome; Phase.Reason = reason;
    }
    internal void Finish(string outcome, string? reason)
    {
        EndPhase(outcome, reason); Outcome = outcome; Reason = reason;
        EndQpc = Phase.EndQpc; EndUtc = Phase.EndUtc;
    }
    internal void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        if (Phase.LastPollQpc is long last)
            Phase.MaxPollGapMs = Math.Max(Phase.MaxPollGapMs, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
        Phase.LastPollQpc = now; Phase.PollCount++;
    }
    internal void Read(nint address, int count, byte[]? bytes, Exception? error, long start, long end)
    {
        if (!_fields.TryGetValue(address, out var field)) return;
        var read = MakeRead(field, count, bytes, error, start, end);
        Phase.FirstReads.TryAdd(field, read); Phase.LastReads[field] = read;
        if (field == RemoteStateLayout.ConversionEpoch && Phase.Phase == "conversion") Phase.ConversionEpochAfter = read.Bits;
        if (field == RemoteStateLayout.DispatchEpoch && Phase.Phase == "generation") Phase.DispatchEpochAfter = read.Bits;
        // Existing guard may read the coordinate triple in a single 12-byte operation.
        if (field == RemoteStateLayout.WorldCoordinates && count == 12 && bytes?.Length == 12)
            for (var i = 1; i < 3; i++)
                Read(address + 4 * i, 4, bytes[(4 * i)..(4 * i + 4)], null, start, end);
    }
    internal void Write(nint address, ReadOnlySpan<byte> bytes, Exception? error, long start, long end)
    {
        if (!_fields.TryGetValue(address, out var field) || Phase.Writes.Count >= 32) return;
        Phase.Writes.Add(new(field, Convert.ToHexString(bytes), bytes.Length == 4 ? BitConverter.ToUInt32(bytes) :
            bytes.Length == 1 ? bytes[0] : null, error is null ? "written" : "write-failed: " + error.Message,
            start, end, DateTimeOffset.UtcNow));
    }
    private static EvidenceRead MakeRead(string field, int count, byte[]? bytes, Exception? error, long start, long end)
    {
        uint? bits = bytes?.Length == count && count >= 4 ? BitConverter.ToUInt32(bytes) : null;
        double? value = field.StartsWith("MustCode+108", StringComparison.Ordinal) && bits.HasValue
            ? BitConverter.UInt32BitsToSingle(bits.Value) : bits;
        return new(field, count, bytes?.Length, error is not null ? "read-failed: " + error.Message :
            bytes?.Length != count ? "short-read" : "readable-unvalidated", bytes is null ? null : Convert.ToHexString(bytes),
            bits, value is double v && double.IsFinite(v) ? v : null, start, end, DateTimeOffset.UtcNow);
    }
    public static ProtocolSnapshot Capture(IProbeMemory memory, AddressResolver resolver)
    {
        var start = Stopwatch.GetTimestamp(); var utc = DateTimeOffset.UtcNow;
        var reads = new List<EvidenceRead>();
        EvidenceRead Read(string field)
        {
            var qpc = Stopwatch.GetTimestamp(); byte[]? bytes = null; Exception? failure = null;
            try
            {
                var address = resolver.Resolve(field);
                if (!ReadOnlyProbe.Readable(memory.Query(address), address, 4))
                    throw new InvalidOperationException("字段页面不可读");
                bytes = memory.ReadBytes(address, 4);
            }
            catch (Exception ex) { failure = ex; }
            var result = MakeRead(field, 4, bytes, failure, qpc, Stopwatch.GetTimestamp());
            reads.Add(result); return result;
        }
        var c0 = Read(RemoteStateLayout.ConversionEpoch); var d0 = Read(RemoteStateLayout.DispatchEpoch);
        foreach (var field in ProtocolFields) Read(field);
        var c1 = Read(RemoteStateLayout.ConversionEpoch); var d1 = Read(RemoteStateLayout.DispatchEpoch);
        bool? torn = c0.Bits is null || c1.Bits is null || d0.Bits is null || d1.Bits is null ? null :
            c0.Bits != c1.Bits || d0.Bits != d1.Bits;
        return new(start, Stopwatch.GetTimestamp(), utc, DateTimeOffset.UtcNow, torn,
            torn is null ? "unknown-epoch-read-failed" : torn.Value ? "torn" : "epochs-stable-not-atomic", reads);
    }
    internal static void Observe(Action<ActionEvidence> action)
    {
        try { if (Current.Value is { } evidence) action(evidence); } catch { /* Never change control flow. */ }
    }
}
