using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var all = new Dictionary<string,List<double>>();
var reports = new List<object>();
var types = new Dictionary<string,Dictionary<uint,int>>();
var transitions = new List<object>();
var eventWindow = new List<object>();
foreach (var path in Directory.GetFiles(Path.Combine(root,"docs/round19-samples"),"*.jsonl").Order()) {
 var fields = new Dictionary<string,List<double>>();
 string? first=null,last=null; int count=0,stable=0,zero=0,partial=0,over=0,changes=0; uint[]? previous=null;
 foreach(var line in File.ReadLines(path)) {
  using var doc=JsonDocument.Parse(line); var row=doc.RootElement; var ts=row.GetProperty("Timestamp").GetString()!; first ??=ts; last=ts; count++;
  if(row.GetProperty("SelectionStable").GetBoolean()) stable++;
  var fs=row.GetProperty("Fields");
  foreach(var p in fs.EnumerateObject()) {
   if(!(p.Name.StartsWith("coordinate.copy.") || p.Name.Contains("health.") || p.Name.Contains("position.") || p.Name.EndsWith("first.8") || p.Name is "selection.count" or "reinforcement.count" or "reinforcement.rank" or "selected.typeCode" or "reinforcement.type")) continue;
   if(p.Value.GetProperty("Value").ValueKind != JsonValueKind.Number) continue;
   bool integral=p.Name is "selection.count" or "reinforcement.count" or "reinforcement.rank" or "selected.typeCode" or "reinforcement.type";
   var bits=p.Value.GetProperty("Bits");
   double val=integral && bits.ValueKind==JsonValueKind.Number ? bits.GetUInt32() : p.Value.GetProperty("Value").GetDouble();
   if(!fields.TryGetValue(p.Name,out var list)) fields[p.Name]=list=new(); list.Add(val);
   if(!all.TryGetValue(p.Name,out var aggregate)) all[p.Name]=aggregate=new(); aggregate.Add(val);
   if(p.Name is "selected.typeCode" or "reinforcement.type") { if(!types.TryGetValue(p.Name,out var set)) types[p.Name]=set=new(); var v=(uint)val;set[v]=set.GetValueOrDefault(v)+1; }
  }
  var xyz=new[]{"1080","1084","1088"}.Select(s=>fs.GetProperty("coordinate.copy."+s).GetProperty("Bits").GetUInt32()).ToArray();
  var values=xyz.Select(BitConverter.UInt32BitsToSingle).ToArray();
  if(ts.StartsWith("2026-09-30T19:47:") || ts.StartsWith("2026-09-30T19:48:"))
    eventWindow.Add(new {Timestamp=ts,CoordinateBits=xyz.Select(x=>$"{x:X8}").ToArray(),Coordinates=values.Select(x=>(double)x).ToArray(),Screen=new[]{"3C","40"}.Select(s=>fs.GetProperty("mouse.source."+s).GetProperty("Bits").GetUInt32()).ToArray(),Action=fs.GetProperty("flags.20").GetProperty("Bits").GetUInt32(),Selection=fs.GetProperty("selection.count").GetProperty("Bits").GetUInt32(),HeadPosition=new[]{"38","3C","40"}.Select(s=>fs.GetProperty("head.position."+s).GetProperty("Value").ValueKind==JsonValueKind.Number ? (double?)fs.GetProperty("head.position."+s).GetProperty("Value").GetDouble():null).ToArray()});
  if(values.All(x=>x==0)) zero++; else if(values.Any(x=>x==0)) partial++;
  if(values.Any(x=>x>8000 || x< -1)) over++;
  if(previous==null || !previous.SequenceEqual(xyz)) {changes++;transitions.Add(new {File=Path.GetFileName(path),Timestamp=ts,Bits=xyz.Select(x=>$"{x:X8}").ToArray(),Values=values,Screen=new[]{"3C","40"}.Select(s=>fs.GetProperty("mouse.source."+s).GetProperty("Bits").GetUInt32()).ToArray(),Flags=fs.GetProperty("flags.20").GetProperty("Bits").GetUInt32()});} previous=xyz;
 }
 reports.Add(new {File=Path.GetFileName(path),Bytes=new FileInfo(path).Length,Sha256=HashFile(path),Count=count,Stable=stable,First=first,Last=last,AllZeroTriples=zero,PartialZeroTriples=partial,OutsideOldBounds=over,CoordinateRuns=changes,Fields=Summarize(fields)});
}
string HashFile(string path){ using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
object Summarize(Dictionary<string,List<double>> source) => source.OrderBy(p=>p.Key).ToDictionary(p=>p.Key,p=> {var a=p.Value.Order().ToArray();var n=a.Where(v=>v!=0 && double.IsFinite(v)).ToArray();return (object)new {Available=a.Length,Zero=a.Count(v=>v==0),Nonfinite=a.Count(v=>!double.IsFinite(v)),Min=a[0],Max=a[^1],Nonzero=n.Length,NonzeroMin=n.Length>0?(double?)n[0]:null,NonzeroP01=Q(n,.01),NonzeroP50=Q(n,.5),NonzeroP95=Q(n,.95),NonzeroP99=Q(n,.99),NonzeroMax=n.Length>0?(double?)n[^1]:null,Distinct=a.Distinct().Count()};});
double? Q(double[] a,double q)=>a.Length==0?null:a[(int)Math.Floor((a.Length-1)*q)];
var options=new JsonSerializerOptions {WriteIndented=true};
var output=Path.Combine(root,"docs/round19-audit");Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output,"statistics.json"),JsonSerializer.Serialize(new {Method="Raw JSONL; integer Bits; finite nonzero quantiles nearest lower index floor((n-1)*q); rows are observations, not independent conversions; no on-map label present",Files=reports,Aggregate=Summarize(all)},options));
File.WriteAllText(Path.Combine(output,"coordinate-transitions.json"),JsonSerializer.Serialize(transitions,options));
File.WriteAllText(Path.Combine(output,"event-window.json"),JsonSerializer.Serialize(eventWindow,options));
File.WriteAllText(Path.Combine(output,"type-codes.json"),JsonSerializer.Serialize(types.ToDictionary(p=>p.Key,p=>p.Value.OrderBy(x=>x.Key).ToDictionary(x=>$"{x.Key:X8}",x=>x.Value)),options));
var selectionSamples = all.TryGetValue("selection.count",out var selectionList)?selectionList.Count:0;
Console.WriteLine($"Analyzed {reports.Count} files, {selectionSamples} samples; {transitions.Count} coordinate runs.");
