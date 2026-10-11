using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
var root=Path.GetFullPath(args.Length>0?args[0]:".");
var dest=Path.Combine(root,"docs/round31-evidence"); Directory.CreateDirectory(dest);
var opt=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
string Rel(string p)=>Path.GetRelativePath(root,p).Replace('\\','/');
void Save(string n,object o)=>File.WriteAllText(Path.Combine(dest,n),JsonSerializer.Serialize(o,opt));
var hashes=new List<object>();
var files=Directory.GetFiles(Path.Combine(root,"samples"),"*",SearchOption.AllDirectories);
foreach(var p in files.Where(p=>p.Contains("NewWorldBuilder")||p.Contains("CoronaResources"))) { using var f=File.OpenRead(p); hashes.Add(new {file=Rel(p),bytes=f.Length,sha256=Convert.ToHexString(SHA256.HashData(f))}); }
Save("source-hashes.json",hashes);
var rows=new Dictionary<string,Unit>(StringComparer.OrdinalIgnoreCase);
Unit Get(string n) { if(!rows.TryGetValue(n,out var u)) rows[n]=u=new Unit{Name=n}; return u; }
var trans=Path.Combine(root,"samples/NewWorldBuilder/data/config/ObjWndTrans.json");
var transLines=File.ReadAllLines(trans);
using(var j=JsonDocument.Parse(File.ReadAllText(trans))) foreach(var p in j.RootElement.EnumerateObject()) {
 var u=Get(p.Name); u.Chinese=p.Value.GetString(); u.Sources.Add(new(Rel(trans),Array.FindIndex(transLines,l=>l.Contains('"'+p.Name+'"'))+1,"translation-key (may include UI categories)"));
}
var cat=Path.Combine(root,"samples/NewWorldBuilder/data/config/ObjectCategory.json"); var catLines=File.ReadAllLines(cat);
using(var j=JsonDocument.Parse(File.ReadAllText(cat))) foreach(var c in j.RootElement.EnumerateArray()) foreach(var n in c.GetProperty("subObjects").EnumerateArray()) {
 var name=n.GetString()!; var u=Get(name); u.Categories.Add(c.GetProperty("englishName").GetString()+" / "+c.GetProperty("chineseName").GetString()); u.Sources.Add(new(Rel(cat),Array.FindIndex(catLines,l=>l.Contains('"'+name+'"'))+1,"editor-category"));
}
var limits=new List<object>(); var xmlErrors=new List<object>(); var scans=new List<object>();
foreach(var p in files.Where(p=>p.Contains("NewWorldBuilder")&&(p.EndsWith(".xml")||p.EndsWith(".xsd")))) {
 var lines=File.ReadAllLines(p); scans.Add(new{file=Rel(p),lines=lines.Length});
 for(int i=0;i<lines.Length;i++) if(Regex.IsMatch(lines[i],@"(?i)(max.?objects|max.?units|unit.?limit|object.?limit|build.?limit|batch.?limit|placement.?limit|maxOccurs|数量上限|批量|最大数量)")) limits.Add(new{file=Rel(p),line=i+1,text=lines[i].Trim(),meaning="candidate syntax only; XML cardinality/script conditions are not engine population limits"});
 try {
 using var reader=XmlReader.Create(p,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
 var x=XDocument.Load(reader,LoadOptions.SetLineInfo);
 foreach(var e in x.Descendants().Where(e=>e.Name.LocalName=="ObjectType"||e.Name.LocalName.StartsWith("ObjectType_"))) {
 var a=e.Attribute("Object")??e.Attribute("value"); if(a==null||string.IsNullOrWhiteSpace(a.Value)) continue;
 Get(a.Value).Sources.Add(new(Rel(p),((IXmlLineInfo)a).LineNumber,"XML object reference; object-list expressions may not be unit types"));
 }
 } catch(Exception e) { xmlErrors.Add(new{file=Rel(p),error=e.Message}); }
}
Save("limit-candidates.json",limits); Save("xml-scan.json",new{scanned=scans,errors=xmlErrors});
var legacy=new List<object>();
foreach(var file in new[]{"unit-catalog-full.txt","unit-catalog-props.txt"}) {
 var p=Path.Combine(root,"src/Ra3Trainer.Core/Assets",file); var ls=File.ReadAllLines(p);
 for(int i=0;i<ls.Length;i++) { var parts=ls[i].Split('\t'); if(parts.Length<5||ls[i].StartsWith('#')) continue; var u=Get(parts[4]);
 var external=u.Sources.Count>0; u.LegacyCode=parts[0]; u.LegacyChinese=parts[2]; u.LegacyCategory=parts[1]; u.Sources.Add(new(Rel(p),i+1,"legacy mapping only; hex not independently verified by editor"));
 legacy.Add(new{code=parts[0],name=parts[4],status=!external?"not-observed-not-deleted":u.Chinese!=null&&u.Chinese!=parts[2]?"translation-diff-review":"observed",oldChinese=parts[2],sourceChinese=u.Chinese,source=Rel(p),line=i+1});
 }
}
Save("catalog.json",rows.Values.OrderBy(u=>u.Name));
Save("catalog-diff.json",new{existing=legacy,additional=rows.Values.Where(u=>u.LegacyCode==null).Select(u=>new{u.Name,u.Chinese,u.Sources}),deletions="No deletion proven: editor references are not an exhaustive mod type registry.",hexWarning="Existing hex values remain legacy evidence; no hash algorithm guessed. English display names unknown; Name is internal identifier/reference."});
var md=new StringBuilder("# 本地可读对象目录\n\n内部名/引用不是英文显示名；旧库hex不是独立验证；空缺写未见。XML列表表达式及UI分类词也保留并按出处标记，不能全部当可增援单位。完整逐出处列表见 catalog.json。\n\n|内部名/引用|hex（旧库未独立验证）|中文（编辑器）|旧库中文/阵营类别|编辑器类别|出处|\n|---|---|---|---|---|---|\n");
string Cell(string? s)=>string.IsNullOrEmpty(s)?"未见":s.Replace("|","\\|").Replace("\n"," ").Replace("\r","");
foreach(var u in rows.Values.OrderBy(u=>u.Name)) md.AppendLine($"|{Cell(u.Name)}|{Cell(u.LegacyCode)}|{Cell(u.Chinese)}|{Cell(u.LegacyChinese)} / {Cell(u.LegacyCategory)}|{Cell(string.Join(";",u.Categories.Distinct()))}|{string.Join("; ",u.Sources.Take(3).Select(s=>$"{s.File}:{s.Line} ({s.Kind})"))}; 全部出处见JSON|");
File.WriteAllText(Path.Combine(dest,"catalog.md"),md.ToString());
var big=new List<object>(); var strings=new List<object>();
foreach(var p in files.Where(p=>p.EndsWith(".big",StringComparison.OrdinalIgnoreCase))) {
 var b=File.ReadAllBytes(p); if(b.Length<16) continue; var magic=Encoding.ASCII.GetString(b,0,4); var entries=new List<object>();
 if(magic is "BIG4" or "BIGF") { uint U(int i)=>BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(i,4)); var count=U(8); var pos=16;
 if(count>100000) throw new InvalidDataException("Unbounded BIG count");
 for(int i=0;i<count;i++) { var index=pos; if(pos+8>b.Length) throw new InvalidDataException($"BIG index overrun: entry {i} at offset {pos} of {b.Length}"); var offset=U(pos); var size=U(pos+4); pos+=8; var end=Array.IndexOf(b,(byte)0,pos); if(end<pos) throw new InvalidDataException(); var name=Encoding.UTF8.GetString(b,pos,end-pos); pos=end+1; bool valid=(ulong)offset+size<=(ulong)b.Length; var prefix=valid?Convert.ToHexString(b.AsSpan((int)offset,(int)Math.Min(size,16))):"out-of-bounds";
 if(valid && name.EndsWith(".str",StringComparison.OrdinalIgnoreCase)) {
 var text=Encoding.UTF8.GetString(b,(int)offset,(int)size);
 // Text keys are localization identifiers; no unsupported link to object types is invented.
 foreach(Match m in Regex.Matches(text,@"(?m)^([^\r\n\s][^\r\n]*)\r?\n[\t ]+""([^""\r\n]*)""\r?\nEnd\b")) {
 var byteOffset=offset+Encoding.UTF8.GetByteCount(text.AsSpan(0,m.Index));
 strings.Add(new{file=Rel(p),entry=name,key=m.Groups[1].Value,value=m.Groups[2].Value,absoluteByteOffset=byteOffset,entryByteOffset=byteOffset-offset,kind="localization-key-not-proven-object-link"});
 }
}entries.Add(new{indexOffset=index,name,payloadOffset=offset,payloadBytes=size,valid,prefix,sha256=valid?Convert.ToHexString(SHA256.HashData(b.AsSpan((int)offset,(int)size))):null});
 }
 }
 big.Add(new{file=Rel(p),magic,entries});
}
Save("big-index.json",big); Save("big-localization.json",strings);
var templates=new List<object>();
foreach(var p in files.Where(p=>p.EndsWith(".object.bin",StringComparison.OrdinalIgnoreCase))) {
 var b=File.ReadAllBytes(p); var text=Encoding.Latin1.GetString(b);
 var candidates=Regex.Matches(text,@"[A-Za-z_][A-Za-z0-9_:. /\\-]{4,}").Cast<Match>().Select(m=>new{offset=m.Index,text=m.Value,kind="raw ASCII run, not a decoded object record"}).ToArray();
 templates.Add(new{file=Rel(p),bytes=b.Length,strings=candidates,limitMatches=candidates.Where(c=>Regex.IsMatch(c.text,@"(?i)(limit|max.?unit|max.?object|batch|placement)"))});
}
Save("template-string-candidates.json",templates);
int newActionRows=0; var evidence=new List<object>(); var parameterRows=new List<(string File,int Line,string Type,uint? N)>();
foreach(var p in files.Where(p=>p.EndsWith(".jsonl")&&(p.Contains("probe-20261001")||p.Contains("session-20261001")))) {
 var counts=new Dictionary<string,int>(); int line=0,malformed=0,identity=0; string? first=null,last=null;
 foreach(var s in File.ReadLines(p)) { line++; try { using var j=JsonDocument.Parse(s); var r=j.RootElement;
 string? t=r.TryGetProperty("ts",out var ts)?ts.GetString():r.TryGetProperty("Timestamp",out ts)?ts.GetString():null; first??=t; last=t;
 if(r.TryGetProperty("event",out var ev) && ev.ValueKind==JsonValueKind.String) {var key=ev.GetString()!; counts[key]=counts.GetValueOrDefault(key)+1; if(key=="action-evidence") newActionRows++;}
 if(r.TryGetProperty("Fields",out var fields)) {
 uint? Bits(string key)=>fields.TryGetProperty(key,out var field)&&field.TryGetProperty("Bits",out var v)&&v.ValueKind==JsonValueKind.Number?v.GetUInt32():null;
 var type=Bits("reinforcement.type"); var n=Bits("reinforcement.count");
 if(type.HasValue) parameterRows.Add((Rel(p),line,$"0x{type:X8}",n));
}if(r.TryGetProperty("AttachSessionId",out var id)&&id.ValueKind!=JsonValueKind.Null) identity++;
 } catch(JsonException) {malformed++;} }
 evidence.Add(new{file=Rel(p),bytes=new FileInfo(p).Length,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))),lines=line,first,last,counts,identityRows=identity,malformed});
}
Save("available-action-evidence.json",evidence);
if(newActionRows>0) throw new InvalidDataException("New action-evidence is present: this inventory tool cannot replace a request/session join; do not reuse the old insufficient-evidence table.");
Save("quantity-evidence.json",parameterRows.GroupBy(r=>(r.Type,r.N)).Select(g=>new{type=g.Key.Type,parameterN=g.Key.N,parameterSnapshotRows=g.Count(),firstSource=new{file=g.First().File,line=g.First().Line},lastSource=new{file=g.Last().File,line=g.Last().Line},linkedRequests=0,verifiedCreatedN=(int?)null,strength="insufficient: available inputs have no action-evidence or request/session IDs; parameter snapshots are not requests or results",observedNoStall="unverified"}));
Console.WriteLine($"source files hashed={hashes.Count}; catalog references={rows.Count}; limits candidates={limits.Count}; XML errors={xmlErrors.Count}; BIG files={big.Count}");
class Unit { public string Name{get;set;}=""; public string? Chinese{get;set;} public string? LegacyCode{get;set;} public string? LegacyChinese{get;set;} public string? LegacyCategory{get;set;} public List<string> Categories{get;set;}=new(); public List<Source> Sources{get;set;}=new(); }
record Source(string File,int Line,string Kind);
