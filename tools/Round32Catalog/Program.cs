using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
var root=Path.GetFullPath(args.FirstOrDefault()??".");
var dest=Path.Combine(root,"docs/round32-evidence");Directory.CreateDirectory(dest);
var opt=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
void Save(string n,object v)=>File.WriteAllText(Path.Combine(dest,n),JsonSerializer.Serialize(v,opt));
string Rel(string p)=>Path.GetRelativePath(root,p).Replace('\\','/');
var records=new List<Asset>(); var manifests=new List<object>();
foreach(var p in Directory.GetFiles(Path.Combine(root,"samples/CoronaResources"),"*.big")){
 var b=File.ReadAllBytes(p); if(Encoding.ASCII.GetString(b,0,4) is not "BIG4" and not "BIGF")throw new InvalidDataException("BIG signature");
 int BE(int at)=>checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at,4)));
 var count=BE(8);int pos=16;
 for(int i=0;i<count;i++){
  var off=BE(pos);var size=BE(pos+4);pos+=8;var end=Array.IndexOf(b,(byte)0,pos);if(end<0)throw new InvalidDataException();var entry=Encoding.UTF8.GetString(b,pos,end-pos);pos=end+1;
  if(off<0||size<0||(long)off+size>b.Length)throw new InvalidDataException("BIG bounds");
  if(!entry.EndsWith(".manifest"))continue;
  var m=b.AsSpan(off,size).ToArray();uint U(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at,4));
  if(m.Length<48||m[0]!=0||BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2,2))!=6)throw new InvalidDataException("Only LE manifest v6 supported");
  int n=checked((int)U(12));int names=checked(48+n*48+(int)U(32)+(int)U(36));int sources=checked(names+(int)U(40));
  if((long)sources+U(44)!=m.Length)throw new InvalidDataException("Manifest exact section coverage");
  string Z(int at,int stop){if(at<0||at>=stop)throw new InvalidDataException("String bounds");int z=Array.IndexOf(m,(byte)0,at,stop-at);if(z<at)throw new InvalidDataException("String terminator");return Encoding.UTF8.GetString(m,at,z-at);}
  manifests.Add(new{file=Rel(p),entry,offset=off,size,assets=n,nameTableOffset=off+names,sourceTableOffset=off+sources,exactCoverage=true});
  for(int j=0;j<n;j++) {int a=48+j*48;int no=checked(names+(int)U(a+24));int so=checked(sources+(int)U(a+28)); var name=Z(no,sources);var source=Z(so,m.Length);
   if(name.StartsWith("GameObject:",StringComparison.Ordinal))records.Add(new(name[11..],U(a+4).ToString("X8"),Rel(p),entry,off+a,off+a+4,off+no,source,U(a+32)));
  }
 }
}
Save("B-manifests.json",manifests);Save("B-object-records.json",records);
var transFile=Path.Combine(root,"samples/NewWorldBuilder/data/config/ObjWndTrans.json"); var trans=JsonNode.Parse(File.ReadAllText(transFile))!.AsObject();var transLines=File.ReadAllLines(transFile);
var legacy=new List<Legacy>();foreach(var file in new[]{"unit-catalog-full.txt","unit-catalog-props.txt"}) {var p=Path.Combine(root,"src/Ra3Trainer.Core/Assets",file);var ls=File.ReadAllLines(p);for(int i=0;i<ls.Length;i++){var a=ls[i].Split('\t');if(a.Length>=5&&!ls[i].StartsWith('#'))legacy.Add(new(a[0],a[4],a[2],a[1],Rel(p),i+1));}}
var locales=new List<Locale>();
foreach(var lang in new[]{"cn","en"}){
 var p=Path.Combine(root,$"samples/NewWorldBuilder/data/locale/{lang}/data/gamestrings.csf");var b=File.ReadAllBytes(p); using var r=new BinaryReader(new MemoryStream(b));
 string Tag()=>Encoding.ASCII.GetString(r.ReadBytes(4)); if(Tag()!=" FSC")throw new InvalidDataException("CSF signature");r.ReadUInt32();uint labels=r.ReadUInt32();r.ReadUInt32();r.ReadUInt32();r.ReadUInt32();
 for(uint i=0;i<labels;i++){var labelOffset=r.BaseStream.Position;if(Tag()!=" LBL")throw new InvalidDataException("CSF label");var n=r.ReadUInt32();var len=r.ReadInt32();if(len<0||len>65536)throw new InvalidDataException();var key=Encoding.ASCII.GetString(r.ReadBytes(len));for(int j=0;j<n;j++){var valueOffset=r.BaseStream.Position;var tag=Tag();if(tag is not " RTS" and not "WRTS")throw new InvalidDataException("CSF value");var chars=r.ReadInt32();if(chars<0||chars>1000000)throw new InvalidDataException();var raw=r.ReadBytes(checked(chars*2));if(raw.Length!=chars*2)throw new EndOfStreamException();for(int k=0;k<raw.Length;k++)raw[k]=(byte)~raw[k];var value=Encoding.Unicode.GetString(raw);if(tag=="WRTS"){int extra=r.ReadInt32();if(extra<0||extra>1000000)throw new InvalidDataException();r.ReadBytes(extra);}locales.Add(new(lang,key,value,Rel(p),labelOffset,valueOffset));}}
 if(r.BaseStream.Position!=b.Length)throw new InvalidDataException("CSF trailing bytes");
}
Save("B-csf-localization.json",locales);
// Exact key equality is a candidate only: the supplied manifests contain no GameObject payload/DisplayName field.
var rows=records.GroupBy(r=>r.Name,StringComparer.Ordinal).Select(g=>{
 var name=g.Key;var old=legacy.Where(l=>l.Name==name).ToArray();var chinese=trans[name]?.GetValue<string>();var line=chinese==null?(int?)null:Array.FindIndex(transLines,l=>l.Contains('"'+name+'"'))+1;
 var codes=g.Select(r=>r.Hex).Distinct().ToArray();var display=locales.Where(l=>l.Key.Equals("NAME:"+name,StringComparison.OrdinalIgnoreCase)).ToArray();
 return new{Name=name,Hex=codes.Length==1?codes[0]:null,ConflictingHex=codes.Length==1?Array.Empty<string>():codes,Chinese=chinese,ChineseSource=line==null?null:new{file=Rel(transFile),line},EnglishInternalName=name,EnglishDisplayName=(string?)null,DisplayNameCandidates=display,Legacy=old,Sources=g.ToArray(),status=old.Length==0?"additional-object-not-automatically-reinforceable":old.Any(l=>!codes.Contains(l.Hex))?"hex-conflict":chinese!=null&&old.Any(l=>l.Chinese!=chinese)?"editor-translation-difference":"hex-confirmed"};
}).OrderBy(r=>r.Name).ToArray();Save("B-catalog.json",rows);
var absent=legacy.Where(l=>!records.Any(r=>r.Name==l.Name)).ToArray();Save("B-diff.json",new{counts=new{manifestObjects=rows.Length,records=records.Count,legacyRows=legacy.Count,confirmed=rows.Count(r=>r.status=="hex-confirmed"),renamesToReview=rows.Count(r=>r.status=="editor-translation-difference"),conflicts=rows.Count(r=>r.status=="hex-conflict"),additional=rows.Count(r=>r.status.StartsWith("additional")),legacyAbsent=absent.Length},existing=rows.Where(r=>r.Legacy.Length>0),additional=rows.Where(r=>r.Legacy.Length==0),notObserved=absent,deletions="Not established; absent from supplied manifests does not prove removed from current mod.",productEdits=0});
Save("B-unified-catalog.json",new{qualification="Complete union of existing 630 catalog rows and 371 additional manifest objects; not a complete current-mod registry. Preserve every existing row, including conflicting variants.",existing=legacy.Select(l=>new{Legacy=l,IndependentSources=records.Where(r=>r.Name==l.Name&&r.Hex==l.Hex).ToArray(),EditorChinese=trans[l.Name]?.GetValue<string>(),EditorChineseSource=trans[l.Name]==null?null:new{file=Rel(transFile),line=Array.FindIndex(transLines,s=>s.Contains('\"'+l.Name+'\"'))+1}}),additional=rows.Where(r=>r.Legacy.Length==0)});
var md=new StringBuilder("# 结构化对象目录（只读）\n\nhex↔英文内部名来自 manifest 记录；英文显示名仍缺对象 DisplayName 引用，不把同名本地化键冒充绑定。中文列仅编辑器直接映射；旧库名字另列。每条完整六包出处见 B-catalog.json。\n\n|hex|英文内部名|编辑器中文|旧库中文|原始记录/名称偏移|\n|---|---|---|---|---|\n");
foreach(var r in rows){var s=r.Sources[0];md.AppendLine($"|{r.Hex??"冲突"}|{r.Name}|{r.Chinese??"未见"}|{string.Join(" / ",r.Legacy.Select(l=>l.Chinese))}|{s.File} → {s.Entry}: hex@{s.HexOffset}, name@{s.NameOffset}"+(r.ChineseSource==null?"":$"; {r.ChineseSource.file}:{r.ChineseSource.line}")+"|");}File.WriteAllText(Path.Combine(dest,"B-catalog.md"),md.ToString());
Console.WriteLine(JsonSerializer.Serialize(new{manifests=manifests.Count,records=records.Count,objects=rows.Length,editorChinese=rows.Count(r=>r.Chinese!=null),legacyMatched=rows.Count(r=>r.Legacy.Length>0),legacyAbsent=absent.Length,conflicts=rows.Count(r=>r.status=="hex-conflict"),locales=locales.Count,exactKeyCandidates=rows.Count(r=>r.DisplayNameCandidates.Length>0),objectPayloads=records.Count(r=>r.BinarySize>0)},opt));
record Asset(string Name,string Hex,string File,string Entry,int RecordOffset,int HexOffset,int NameOffset,string OriginalSource,uint BinarySize);
record Legacy(string Hex,string Name,string Chinese,string Category,string File,int Line);
record Locale(string Language,string Key,string Value,string File,long LabelOffset,long ValueOffset);
