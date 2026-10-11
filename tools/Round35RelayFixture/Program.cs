using Ra3Trainer.Core.Runtime;
using System.Text.Json;
var root=Path.GetFullPath(args.FirstOrDefault()??".");
var nativeLog=Path.Combine(root,"artifacts/round35/thread-native.log");
if(!File.Exists(nativeLog)) throw new FileNotFoundException($"native thread evidence missing (run the thread-policy fixture first): {nativeLog}",nativeLog);
var entries=File.ReadLines(nativeLog).Where(l=>l.StartsWith("osd-draw-thread ")).Distinct().ToArray();
if(!entries.Any(l=>l.Contains("role=CreateDevice ")&&l.Contains("queryHr=00000000")) ||
   !entries.Any(l=>l.Contains("match=0 multithreaded=1 allowed=1")) ||
   !entries.Any(l=>l.Contains("match=0 multithreaded=0 allowed=0")) ||
   !entries.Any(l=>l.Contains("match=1 multithreaded=0 allowed=1")) ||
   !entries.Any(l=>l.Contains("match=1 multithreaded=1 allowed=1"))) throw new Exception("Native four-way evidence missing");
var directory=Path.Combine(root,"artifacts/round35/relay-fixture",Guid.NewGuid().ToString("N"));
using var log=TrainerSessionLog.Open(directory);
log.Write("fixture",new{qualification="Synthetic COM; actual product callback and Windows worker threads; NOT game/GPU evidence"});
var relay=new OverlayEntryRelay();
var input="osd-session 0000000000000001\n"+string.Join('\n',entries)+"\n";
relay.Forward(input,1,(kind,message)=>throw new IOException("injected sink failure"));
relay.Forward(input,1,(kind,message)=>log.Write(kind,new{message}));
relay.Forward(input,1,(kind,message)=>log.Write(kind,new{message}));
using var file=new FileStream(log.FilePath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
using var reader=new StreamReader(file);var text=reader.ReadToEnd();
var actual=text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l=>JsonDocument.Parse(l)).Where(j=>j.RootElement.GetProperty("event").GetString()=="osd-draw-thread").Select(j=>j.RootElement.GetProperty("data").GetProperty("message").GetString()).ToArray();
if(!actual.SequenceEqual(entries))throw new Exception("Thread relay altered fields or duplicated records");
int extra=0;
relay.Forward(input,2,(k,m)=>extra++);
relay.Forward("osd-session 0000000000000001\nosd-draw-thread role=Present tid=999",1,(k,m)=>extra++);
if(extra!=0)throw new Exception("Wrong session or incomplete record accepted");
var failure="osd-session 0000000000000001\nosd-installed\nosd-draw-failure-detail name=OwnerThread hr=8876086c reason=local-policy-needs-readings\nosd-draw-failed hr=8876086c disabled\n";
var feedback=OverlayFeedback.Parse(failure,1)??"";
if(!feedback.Contains("本地线程判定拒绝") || !feedback.Contains("不是 D3D") || !feedback.Contains("已停止绘制")) throw new Exception("Misleading thread error feedback");
Directory.CreateDirectory(Path.Combine(root,"docs/round35-evidence"));
File.WriteAllText(Path.Combine(root,"docs/round35-evidence/session-fixture.jsonl"),text);
Console.WriteLine($"PASS {entries.Length} real native thread records -> session verbatim; duplicate=0; throwing sink retry; stale session/incomplete rejected; Chinese local-policy reason. skipped=0. FIXTURE ONLY.");
