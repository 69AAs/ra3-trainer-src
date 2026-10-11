using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.IO.Compression;
var root=Path.GetFullPath(args.FirstOrDefault()??".");
var dest=Path.Combine(root,"docs/round32-evidence"); Directory.CreateDirectory(dest);
var options=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
void Save(string name,object value)=>File.WriteAllText(Path.Combine(dest,name),JsonSerializer.Serialize(value,options));
string S(JsonNode? n)=>n?.ToString()??"";
DateTimeOffset T(JsonNode n)=>DateTimeOffset.Parse(S(n["ts"]??n["Timestamp"]));
var events=File.ReadLines(Path.Combine(root,"samples/evening/session-20261001.jsonl")).Select((s,i)=>new Row("session-20261001.jsonl",i+1,JsonNode.Parse(s)!)).ToArray();
var primaryProbeFiles=Directory.GetFiles(Path.Combine(root,"samples/evening"),"probe*.jsonl");
var probeList=primaryProbeFiles.SelectMany(p=>File.ReadLines(p).Select((s,i)=>new Row(Path.GetFileName(p),i+1,JsonNode.Parse(s)!))).ToList();
var seenProbeNames=primaryProbeFiles.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach(var zipPath in Directory.GetFiles(Path.Combine(root,"samples/evening"),"*.zip").Order()){
 using var zip=ZipFile.OpenRead(zipPath);
 foreach(var entry in zip.Entries.Where(e=>Path.GetFileName(e.FullName).StartsWith("probe-20261001")&&e.FullName.EndsWith(".jsonl"))){
  if(!seenProbeNames.Add(Path.GetFileName(entry.FullName)))continue;
  using var reader=new StreamReader(entry.Open());int line=0;string? text;
  while((text=reader.ReadLine())!=null)probeList.Add(new Row(Path.GetFileName(zipPath)+"!/"+entry.FullName,++line,JsonNode.Parse(text)!));
 }
}
var probes=probeList.OrderBy(r=>T(r.Node)).ToArray();
object Ref(Row r)=>new {file="samples/evening/"+r.File,line=r.Line,time=T(r.Node)};
uint? Bits(Row? r,string key)=>r?.Node["Fields"]?[key]?["Bits"]?.GetValue<uint>();
var inventory=Directory.GetFiles(Path.Combine(root,"samples/evening")).Select(p=>new {file=Path.GetFileName(p),bytes=new FileInfo(p).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray(); Save("A-inputs.json",inventory);
var feature=events.Where(r=>S(r.Node["event"])=="feature").ToArray();
Save("A-feature-counts.json",feature.GroupBy(r=>S(r.Node["data"]?["display"])).Select(g=>new{display=g.Key,rows=g.Count(),actions=g.GroupBy(r=>S(r.Node["data"]?["action"])).Select(a=>new{action=a.Key,count=a.Count()}),statuses=g.GroupBy(r=>S(r.Node["data"]?["status"])).Select(a=>new{status=a.Key,count=a.Count()})}));
var evidence=events.Where(r=>S(r.Node["event"])=="action-evidence").ToArray();
var joins=evidence.Select(r=>{
 var d=r.Node["data"]!; var id=S(d["RequestId"]); var session=S(d["AttachSessionId"]); var matches=probes.Where(p=>S(p.Node["AttachSessionId"])==session&&S(p.Node["LastActionRequestId"])==id).ToArray();
 return new {source=Ref(r),request=id,session,feature=S(d["Feature"]),outcome=S(d["Outcome"]),reason=S(d["Reason"]),totalMs=d["TotalMs"],phases=d["Phases"],snapshot=d["TerminalSnapshot"],probeMatches=matches.Length,firstProbe=matches.Length==0?null:Ref(matches[0]),lastProbe=matches.Length==0?null:Ref(matches[^1]),selectionRange=matches.Select(p=>Bits(p,"selection.count")).Where(v=>v.HasValue).Distinct().Order().ToArray()};
}).ToArray(); Save("A-request-joins.json",joins);
var noonTimeouts=feature.Where(r=>S(r.Node["data"]?["status"])=="超时").Select(r=>{
 var time=T(r.Node); var local=probes.Where(p=>Math.Abs((T(p.Node)-time).TotalSeconds)<=5).ToArray();
 var before=probes.LastOrDefault(p=>T(p.Node)<=time); var after=probes.FirstOrDefault(p=>T(p.Node)>time);
 object? Snap(Row? p)=>p==null?null:new{source=Ref(p),deltaSeconds=(T(p.Node)-time).TotalSeconds,session=p.Node["AttachSessionId"],request=p.Node["LastActionRequestId"],type=Bits(p,"reinforcement.type")?.ToString("X8"),count=Bits(p,"reinforcement.count"),selection=Bits(p,"selection.count"),rank=Bits(p,"reinforcement.rank"),fields=p.Node["Fields"],enabled=p.Node["EnabledFeatures"]};
 return new{source=Ref(r),data=r.Node["data"],before=Snap(before),after=Snap(after),nearby=local.Select(p=>Snap(p)),recentEvents=events.Where(e=>T(e.Node)<=time&&(time-T(e.Node)).TotalSeconds<=12).Select(e=>new{source=Ref(e),record=e.Node})};
}).ToArray(); Save("A-timeout-context.json",noonTimeouts);
var evening=probes.Where(p=>p.File.Contains("224519")).ToArray();
Save("A-minute-trends.json",evening.GroupBy(p=>T(p.Node).ToString("HH:mm")).Select(g=>{var a=g.ToArray(); var counts=a.Select(p=>Bits(p,"selection.count")).Where(v=>v.HasValue).Select(v=>v!.Value).ToArray();return new{minute=g.Key,samples=a.Length,first=Ref(a[0]),last=Ref(a[^1]),selectionMin=counts.Min(),selectionMax=counts.Max(),maxSampleGapSeconds=a.Zip(a.Skip(1),(x,y)=>(T(y.Node)-T(x.Node)).TotalSeconds).DefaultIfEmpty().Max(),unitPopulation=(int?)null,fps=(double?)null};}));
var copies=feature.Where(r=>S(r.Node["data"]?["display"]).StartsWith("复制")&&S(r.Node["data"]?["action"])=="execute").ToArray();
var runs=new List<List<Row>>(); foreach(var r in copies.Where(r=>T(r.Node).Hour>=22)) {if(runs.Count==0||(T(r.Node)-T(runs[^1][^1].Node)).TotalSeconds>5) runs.Add(new()); runs[^1].Add(r);}
Save("A-copy-bursts.json",runs.Select(run=>new{definition="successive copy execute records separated by <=5 seconds; not guaranteed no intervening action",n=run.Count,first=Ref(run[0]),last=Ref(run[^1]),durationSeconds=(T(run[^1].Node)-T(run[0].Node)).TotalSeconds,requests=run.Select(r=>new{source=Ref(r),id=r.Node["data"]?["requestId"],status=r.Node["data"]?["status"]})}));
Save("A-timeline.json",events.Where(r=>T(r.Node).Hour>=22).Select(r=>new{source=Ref(r),record=r.Node}));
var fields=probes.SelectMany(r=>r.Node["Fields"]!.AsObject().Select(k=>k.Key)).Distinct().Order().ToArray(); Save("A-probe-fields.json",fields);
string Fingerprint(Row r)=>r.Node["Fields"]!.ToJsonString();
var stableRuns=new List<List<Row>>();foreach(var r in evening){if(stableRuns.Count==0||Fingerprint(stableRuns[^1][^1])!=Fingerprint(r))stableRuns.Add(new());stableRuns[^1].Add(r);}
Save("A-stable-snapshots.json",stableRuns.OrderByDescending(r=>r.Count).Take(10).Select(r=>new{samples=r.Count,first=Ref(r[0]),last=Ref(r[^1]),durationSeconds=(T(r[^1].Node)-T(r[0].Node)).TotalSeconds,selection=Bits(r[0],"selection.count"),lastRequest=r[0].Node["LastActionRequestId"],fields=r[0].Node["Fields"]}));
Save("A-copy-measures.json",runs.Select(run=>{
 var first=T(run[0].Node);var last=T(run[^1].Node);var before=evening.LastOrDefault(p=>T(p.Node)<first);var after=evening.FirstOrDefault(p=>T(p.Node)>last);
 var ids=run.Select(r=>S(r.Node["data"]?["requestId"])).ToHashSet();var actions=joins.Where(j=>ids.Contains(j.request)).ToArray();
 var inside=evening.Where(p=>T(p.Node)>=first&&T(p.Node)<=last).ToArray();
 return new {n=run.Count,first=Ref(run[0]),last=Ref(run[^1]),before=before==null?null:new{source=Ref(before),selection=Bits(before,"selection.count")},after=after==null?null:new{source=Ref(after),selection=Bits(after,"selection.count")},selectionValues=inside.Select(p=>Bits(p,"selection.count")).Distinct().Order().ToArray(),actionMinMs=actions.Min(a=>a.totalMs!.GetValue<double>()),actionMaxMs=actions.Max(a=>a.totalMs!.GetValue<double>()),firstActionMs=actions.First().totalMs,lastActionMs=actions.Last().totalMs,otherFeatureExecutions=feature.Count(f=>T(f.Node)>=first&&T(f.Node)<=last&&S(f.Node["data"]?["action"])=="execute"&&!ids.Contains(S(f.Node["data"]?["requestId"]))),populationDelta=(int?)null};
}));
var quantities=joins.SelectMany(j=>j.phases!.AsArray().Where(p=>p?["UnitId"]!=null||p?["Count"]!=null).Select(p=>new{j.source,j.request,j.session,j.feature,j.outcome,j.totalMs,phase=p})).ToArray(); Save("A-quantity-requests.json",quantities);
Save("A-legacy-quantity-context.json",feature.Where(r=>S(r.Node["data"]?["name"])=="We Need Back"&&S(r.Node["data"]?["action"])=="execute"&&r.Node["data"]?["requestId"]==null).Select(r=>{
 var request=feature.LastOrDefault(e=>e.Line<r.Line&&S(e.Node["data"]?["name"])=="We Need Back"&&S(e.Node["data"]?["action"])=="request");
 var time=T(r.Node);var p=probes.LastOrDefault(p=>T(p.Node)<=time&&(time-T(p.Node)).TotalSeconds<=2);
 return new{source=Ref(r),status=S(r.Node["data"]?["status"]),request=request==null?null:Ref(request),nearby=p==null?null:new{source=Ref(p),type=Bits(p,"reinforcement.type")?.ToString("X8"),count=Bits(p,"reinforcement.count"),selection=Bits(p,"selection.count"),flag=Bits(p,"flags.20")},qualification="temporal parameter snapshot only; no per-request parameter/epoch evidence"};
}));
Console.WriteLine(JsonSerializer.Serialize(new{files=inventory.Length,bytes=inventory.Sum(f=>f.bytes),events=events.Length,probes=probes.Length,evidence=evidence.Length,joined=joins.Count(j=>j.probeMatches>0),copyExecute=copies.Length,eveningCopies=copies.Count(r=>T(r.Node).Hour>=22),runs=runs.Select(r=>r.Count),timeouts=noonTimeouts.Length,quantityPhases=quantities.Length},options));
record Row(string File,int Line,JsonNode Node);
