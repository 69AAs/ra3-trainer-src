using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var output = Path.Combine(root, "docs/round28-evidence"); Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, options));
var rows = new List<Row>(); var inventory = new List<object>(); var schemas = new Dictionary<string, HashSet<string>>();
var selectedFiles = Directory.GetFiles(Path.Combine(root, "samples/probe"), "*.jsonl").Where(p => Path.GetFileName(p).StartsWith("probe-20260930-") || Path.GetFileName(p).StartsWith("probe-20261001-")).Order().ToArray();
foreach (var file in selectedFiles)
{
    int line = 0; var start = rows.Count; var keys = new HashSet<string>();
    foreach (var json in File.ReadLines(file))
    {
        line++; using var doc = JsonDocument.Parse(json); var r = doc.RootElement; var f = r.GetProperty("Fields");
        foreach (var field in f.EnumerateObject()) keys.Add(field.Name);
        uint? Bits(string key) => f.TryGetProperty(key, out var v) && v.TryGetProperty("Bits", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetUInt32() : null;
        double? Val(string key) => f.TryGetProperty(key, out var v) && v.TryGetProperty("Value", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : null;
        rows.Add(new Row(Path.GetFileName(file), line, r.GetProperty("Timestamp").GetDateTimeOffset(), Bits("flags.20"), Bits("reinforcement.type"), Bits("reinforcement.count"), Bits("reinforcement.rank"), Bits("selection.count"), Bits("selected.typeCode"), r.GetProperty("SelectionStable").GetBoolean(), Val("coordinate.copy.1080"), Val("coordinate.copy.1084"), Val("coordinate.copy.1088")));
    }
    var part = rows.Skip(start).ToArray(); var gaps = part.Zip(part.Skip(1), (a,b) => (b.Time-a.Time).TotalMilliseconds).Order().ToArray();
    inventory.Add(new { file = Path.GetFileName(file), bytes = new FileInfo(file).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), lines = line, first = part.First().Time, last = part.Last().Time, gapMs = new { min = gaps.First(), median = gaps[gaps.Length/2], p95 = gaps[(int)(gaps.Length*.95)], max = gaps.Last() }, nonzeroSlots = part.Where(r => r.Slot is not null and not 0).GroupBy(r => r.Slot).Select(g => new { slot=g.Key, count=g.Count() }), missingSlot = part.Count(r=>r.Slot is null), maxSelection = part.Max(r=>r.Selection) });
    schemas[Path.GetFileName(file)] = keys;
    Console.WriteLine($"Parsed {Path.GetFileName(file)}: {line} rows");
}
Save("inventory.json", inventory); Save("schema.json", schemas);
Save("parameter-groups.json", rows.GroupBy(r => (r.Type,r.N)).OrderBy(g=>g.Key.Type).ThenBy(g=>g.Key.N).Select(g => new { type = g.Key.Type.HasValue ? $"0x{g.Key.Type:X8}" : "missing", parameterN=g.Key.N, rows=g.Count(), first=g.First(), last=g.Last(), maxSelection=g.Max(r=>r.Selection), nonzeroSlotRows=g.Count(r=>r.Slot is not null and not 0) }));
Save("nonzero-slots.json", rows.Where(r=>r.Slot is not null and not 0));
var events = new List<Event>();
foreach (var file in Directory.GetFiles(Path.Combine(root,"samples/sessions"),"session-2026*.jsonl").Where(p=>p.Contains("20260930")||p.Contains("20261001")).Order())
{
    int line=0;
    foreach(var json in File.ReadLines(file)) { line++; using var doc=JsonDocument.Parse(json); var r=doc.RootElement; events.Add(new Event(Path.GetFileName(file),line,r.GetProperty("ts").GetDateTimeOffset(),r.GetProperty("event").GetString()!,r.GetProperty("data").Clone())); }
}
Save("session-event-counts.json",events.GroupBy(e=>e.Name).Select(g=>new { name=g.Key,count=g.Count() }));
var actions=events.Where(e=>e.Name=="feature" && e.Data.TryGetProperty("name",out var name) && name.GetString()=="We Need Back").ToArray();
Save("reinforcement-events.json", actions.Select(e => new { session=e, prior = rows.LastOrDefault(r=>r.Time<=e.Time), next=rows.FirstOrDefault(r=>r.Time>e.Time), warning="Nearest parameter snapshots are not batch IDs, receipts, actual unit counts or dispatch duration." }));
foreach(var target in new[]{"00:38:01.5823403","00:52:50.9584921"})
{
    var t=DateTimeOffset.Parse("2026-10-01T"+target+"+08:00");
    Save("timeout-"+target[..8].Replace(":", "")+".json",new { target=t, events=events.Where(e=>Math.Abs((e.Time-t).TotalSeconds)<15), probe=rows.Where(r=>Math.Abs((r.Time-t).TotalSeconds)<8) });
}
Save("session-relevant.json",events.Where(e=>e.Name.Contains("reinforce")||e.Name.Contains("coordinate")||e.Name.Contains("dispatch")||e.Name.Contains("install")||e.Name.Contains("probe")||e.Name.Contains("attach")));
var batches = new List<object>();
Event? pending = null;
foreach (var e in actions)
{
    var action = e.Data.GetProperty("action").GetString();
    if (action == "request") { pending=e; continue; }
    if (action != "execute") continue;
    var start = pending;
    pending = null;
    if (start is null || start.File != e.File) throw new InvalidDataException($"Unpaired terminal event {e.File}:{e.Line}");
    // A previous/next sample from another attachment or minutes away is not attributable.
    var before = rows.LastOrDefault(r=>r.Time<=start.Time && (start.Time-r.Time).TotalSeconds<1);
    var after = rows.FirstOrDefault(r=>r.Time>e.Time && (r.Time-e.Time).TotalSeconds<1);
    var during = rows.Where(r=>r.Time>=start.Time && r.Time<=e.Time).ToArray();
    batches.Add(new { id=$"{e.File}:{start.Line}-{e.Line}", request=start, terminal=e,
        requestToTerminalMs=(e.Time-start.Time).TotalMilliseconds,
        requestedN=(int?)null, completedLoopCount=(int?)null, dispatchDurationMs=(double?)null, actualCreated=(int?)null, completionEpoch=(uint?)null, frameTimeMs=(double?)null,
        before, after, parameterSnapshots=during.Select(r=>new {r.File,r.Line,r.Time,r.Type,r.N,r.Selection,r.SelectedType}),
        nonzeroSlots=during.Where(r=>r.Slot!=0),
        warning="Request-to-terminal includes UI scheduling/conversion/generation; parameter snapshots are candidates, not a per-dispatch N or completed loop count. No frame clock/object census/receipt recorded." });
}
Save("batches.json", batches);
var episodes=new List<object>();
foreach (var fileGroup in rows.GroupBy(r=>r.File))
{
    var list=fileGroup.ToArray();
    for(int i=0;i<list.Length;i++)
    {
        if(list[i].Slot is null or 0) continue;
        int first=i; while(i+1<list.Length && list[i+1].Slot==list[first].Slot) i++;
        episodes.Add(new { first=list[first], last=list[i], preceding=first>0?list[first-1]:null, following=i+1<list.Length?list[i+1]:null,
            observedSpanMs=(list[i].Time-list[first].Time).TotalMilliseconds,
            warning="Repeated same slot at polling instants; not proof of continuous busy, identical request, lock ownership or completed generation." });
    }
}
Save("slot-episodes.json",episodes);
var catalog=File.ReadLines(Path.Combine(root,"src/Ra3Trainer.Core/Assets/unit-catalog-full.txt")).Where(l=>!l.StartsWith('#')).Select(l=>l.Split('\t')).Where(p=>p.Length>=3).GroupBy(p=>p[0],StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>string.Join(" / ",g.Select(p=>p[2]).Distinct()),StringComparer.OrdinalIgnoreCase);
var table=new StringBuilder("# 按类型的数量结论\n\n全部时间为 +08:00；行号从 1 起。仅列7份指定 probe 的参数类型，不将选中类型当生成类型。显示名来自当前本地目录，mod映射未独立核实。\n\n所有类型的**已验证一次创建 N 区间均为：证据不足（1–100 未获完整证明）**。未见引擎上限证据；没有可与实测范围取较小值的引擎限制。下表参数 N 是观测值，不是安全容量；不能由离散 8/10/20/50 推导 1–50。\n\n|类型代码 / 本地名称|观测参数 N|首条依据（文件 / 行 / 时间）|末条依据|已验证 N 区间 / 强度|\n|---|---|---|---|---|\n");
foreach(var g in rows.GroupBy(r=>r.Type).OrderBy(g=>g.Key))
{
    var first=g.First(); var last=g.Last(); var code=$"{g.Key:X8}";
    table.AppendLine($"|0x{code} {catalog.GetValueOrDefault(code,"目录无匹配")}|{string.Join('/',g.Select(r=>r.N).Distinct().Order())}|{first.File}:{first.Line}<br>{first.Time:O}|{last.File}:{last.Line}<br>{last.Time:O}|证据不足；参数观测强，完成数量/耗时/帧表现缺失|");
}
table.AppendLine("\n未出现的类型同样证据不足。操作策略：保留旧有 1–50 参数设置范围和段内硬钳制（不是已验证范围），51–100 仍拒绝；不新增任何经实测认证的快捷范围。详见 batches.json 的逐请求未知字段。\n");
File.WriteAllText(Path.Combine(output,"quantity-table.md"),table.ToString());
var sb=new StringBuilder("file,line,timestamp,slot,type,parameterN,rank,selectedCount,selectedType,selectionStable,x,y,z\n");
foreach(var r in rows) sb.AppendLine(FormattableString.Invariant($"{r.File},{r.Line},{r.Time:O},{r.Slot},0x{r.Type:X8},{r.N},{r.Rank},{r.Selection},0x{r.SelectedType:X8},{r.Stable},{r.X},{r.Y},{r.Z}"));
File.WriteAllText(Path.Combine(output,"probe-extract.csv"),sb.ToString());
Console.WriteLine($"Total {rows.Count} probe rows; {events.Count} session events; {actions.Length} reinforcement events");
Console.WriteLine($"Paired {batches.Count} request/terminal records; {rows.Select(r=>r.Type).Distinct().Count()} parameter types. No actual-created, frame-time or epoch fields.");
record Row(string File,int Line,DateTimeOffset Time,uint? Slot,uint? Type,uint? N,uint? Rank,uint? Selection,uint? SelectedType,bool Stable,double? X,double? Y,double? Z);
record Event(string File,int Line,DateTimeOffset Time,string Name,JsonElement Data);
