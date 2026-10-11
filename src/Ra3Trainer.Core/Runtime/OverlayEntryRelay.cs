namespace Ra3Trainer.Core.Runtime;

/// <summary>Best-effort, session-scoped verbatim diagnostic relay; no installation authority.</summary>
public sealed class OverlayEntryRelay
{
    private long? _creation;
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _drawSeen = new(StringComparer.Ordinal);
    // 逐帧诊断（osd-draw-* / osd-panel-*）一次会话能刷上千行，把日志撑到几百 MB，
    // 而且每帧写文件本身会跟游戏抢 IO。头几条照发（要看第一帧），之后每 200 条留一条。
    private const int DrawVerboseHead = 5, DrawVerboseEvery = 200;

    public void Forward(string log, long creationTime, Action<string, string> sink)
    {
        var lines = log.Split('\n');
        var marker = $"osd-session {creationTime:X16}";
        var start = Array.FindLastIndex(lines, line => line.TrimEnd('\r') == marker);
        if (start < 0) return;
        // 只有确认到本会话的 osd-session 标记才提交会话号、清去重集合：否则一次标记缺失
        //（写了一半/被截断）就会先提交会话号并清 _sent，之后同名轮询不再复位。
        // 同时清 _drawSeen：复用本实例跨会话时若不清，新会话起始 seen 已过 DrawVerboseHead，
        // 首帧绘制诊断会被静默丢掉。
        if (_creation != creationTime) { _creation = creationTime; _sent.Clear(); _drawSeen.Clear(); }
        for (var i = start + 1; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("osd-session ", StringComparison.Ordinal)) break;
            var eventName = line.Split(' ', 2)[0];
            var isDraw = eventName is "osd-draw-step" or "osd-draw-desc" or "osd-draw-present"
                or "osd-draw-msaa" or "osd-draw-legacy-restrictions" or "osd-draw-failure-detail" or "osd-draw-failed"
                or "osd-draw-thread" or "osd-panel-step" or "osd-panel-desc"
                or "osd-panel-uploaded" or "osd-panel-drawn";
            if ((!line.StartsWith("osd-entry slot=", StringComparison.Ordinal) && !isDraw) || line.Length > 4096 ||
                line.Any(char.IsControl) || _sent.Contains(line)) continue;
            int? drawOrdinal = null;
            if (isDraw)
            {
                var seen = _drawSeen.TryGetValue(eventName, out var previous) ? previous + 1 : 1;
                if (seen > DrawVerboseHead && seen % DrawVerboseEvery != 0)
                {
                    // 节流跳过**也要**推进计数：不推进的话“每 200 条留一条”永远到不了 200，
                    // 头 5 条之后所有绘制诊断都会被永久丢掉。
                    _drawSeen[eventName] = seen;
                    continue;
                }
                drawOrdinal = seen;
            }
            try
            {
                sink(eventName, line);
                _sent.Add(line);
                // 只有**投递成功**才推进节流计数。失败的行不在 _sent 里、下一轮会原样重试，
                // 如果让失败的尝试也计入配额，seen 会爬到 >5 且非 200 的倍数，
                // 那条从未送达的行就被永久节流掉（静默丢行）。
                if (drawOrdinal is int ordinal) _drawSeen[eventName] = ordinal;
            }
            catch (Exception) { /* A failed sink must not affect feedback or hook behavior; retry next poll. */ }
        }
    }
}
