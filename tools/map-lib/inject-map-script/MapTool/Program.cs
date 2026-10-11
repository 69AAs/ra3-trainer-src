// MapTool — 用「地编伴侣」同款解析器（Dreamness.RA3.Map.Parser）打开/另存 RA3 日冕 .map。
//
// 为什么需要它：MapCoreLib（x86 / .NET Framework，编辑器 Lua 导入插件用的那个库）写出来的
// .map，它自己的解析器读不回来（无限刷 "unknown assetName: " 并卡死）。但地编伴侣实际用的
// 解析器 Dreamness.RA3.Map.Parser 能把它完整读出来。所以：
//   InjectMapScript.exe (MapCoreLib) 负责"改动脚本结构"；MapTool 负责"验证 + 最终落盘"。
//
// 用法：
//   MapTool info   <map>                          # 打开并列出资产与脚本组；坏图会抛异常
//   MapTool resave <in.map> <out.map> <0|1>       # 另存（1=压缩，0=不压缩）
using System;
using System.Collections;
using System.IO;
using System.Linq;
using Dreamness.Ra3.Map.Parser.Core.Map;
using Dreamness.Ra3.Map.Parser.Asset.Impl.Script;
using Dreamness.Ra3.Map.Parser.Asset.Base;

class Program
{
    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try
        {
            if (args.Length == 0) { Usage(); return 2; }
            switch (args[0])
            {
                case "info": return Info(args[1]);
                case "resave": return Resave(args[1], args[2], args.Length < 4 || args[3] != "0");
                case "grouplua": return GroupLua(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 80);
                case "newmap": return NewMap(args);
                case "slinfo": return SlInfo(args[1]);
                case "reflect": return Reflect(args[1]);
                default: Usage(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static void Usage()
    {
        Console.Error.WriteLine("MapTool info   <map>");
        Console.Error.WriteLine("MapTool resave <in.map> <out.map> [0|1 compress]");
        Console.Error.WriteLine("MapTool grouplua <map> <groupIndex> [headChars]");
        Console.Error.WriteLine("MapTool newmap <out.map> [playableW playableH border startWaypoints] [0|1 compress]");
    }

    // 新建一张空白遭遇战图（含 N 个玩家出生点航点 = 基地车起点）。
    // 引擎来自地编伴侣自己的门面 Ra3MapFacade.NewMap(...)。
    static int NewMap(string[] a)
    {
        string outPath = a[1];
        int pw = a.Length > 2 ? int.Parse(a[2]) : 200;
        int ph = a.Length > 3 ? int.Parse(a[3]) : 200;
        int border = a.Length > 4 ? int.Parse(a[4]) : 20;
        int starts = a.Length > 5 ? int.Parse(a[5]) : 4;
        bool compress = a.Length > 6 ? a[6] != "0" : true;

        var path = System.IO.Path.GetFullPath(outPath);
        var dir = System.IO.Path.GetDirectoryName(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        System.IO.Directory.CreateDirectory(dir);

        Dreamness.Ra3.Map.Facade.Core.Ra3MapFacade facade;
        try
        {
            facade = Dreamness.Ra3.Map.Facade.Core.Ra3MapFacade.NewMap(pw, ph, border, starts, "Grass");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("新建地图失败: " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine("说明：Ra3MapFacade.NewMap 需要一个已注册的贴图表（RegisterSupportedTexture），");
            Console.Error.WriteLine("      这张表由地编伴侣在它自己的进程里初始化；独立进程里没有，所以会 KeyNotFoundException。");
            Console.Error.WriteLine("      要一张带基地车起点的测试图，请走『克隆现成遭遇战图』路线：");
            Console.Error.WriteLine("        ./inject-map-script.sh <有出生点的图.map> <输出.map> <脚本.lua>");
            return 4;
        }
        facade.SaveAs(dir, name, compress);
        string produced = System.IO.Path.Combine(dir, name, name + ".map");
        Console.WriteLine("新建地图: " + produced + "  (" + (System.IO.File.Exists(produced) ? new System.IO.FileInfo(produced).Length.ToString() : "缺失") + " bytes)");
        Console.WriteLine("  可玩区=" + pw + "x" + ph + " border=" + border + " 出生点数=" + starts + " compress=" + compress);
        // 验证
        var m2 = Ra3Map.Open(produced);
        var wp = m2.Context.AssetDict;
        Console.WriteLine("  复读: Errored=" + m2.Errored);
        foreach (var w in facade.GetWaypoints()) Console.WriteLine("  waypoint: " + w);
        return 0;
    }


    // 调试：打印 PlayerScriptsList -> ScriptLists 的结构（每个 ScriptList 的组数/顶层脚本数）

    static int Reflect(string name)
    {
        var asm = typeof(Ra3Map).Assembly;
        foreach (var t in asm.GetTypes().OrderBy(x => x.FullName))
        {
            if (t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine("=== " + t.FullName + " (base=" + (t.BaseType==null?"-":t.BaseType.Name) + ")");
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.DeclaredOnly))
                Console.WriteLine("   F " + (f.IsStatic?"static ":"") + f.FieldType.Name + " " + f.Name);
            foreach (var pp in t.GetProperties(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.DeclaredOnly))
                Console.WriteLine("   P " + pp.PropertyType.Name + " " + pp.Name);
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.DeclaredOnly).Where(m=>!m.IsSpecialName))
                Console.WriteLine("   M " + (m.IsStatic?"static ":"") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name)) + ")");
        }
        return 0;
    }

    static int SlInfo(string path)
    {
        var map = Ra3Map.Open(path);
        Console.WriteLine("文件=" + path + " Errored=" + map.Errored);
        var d = (IDictionary)map.Context.AssetDict;
        foreach (DictionaryEntry kv in d)
        {
            if (kv.Key.ToString() != "PlayerScriptsList") continue;
            var psl = kv.Value;
            var listsProp = psl.GetType().GetProperty("ScriptLists");
            var lists = (System.Collections.IEnumerable)listsProp.GetValue(psl);
            int li = 0;
            foreach (var sl in lists)
            {
                var gProp = sl.GetType().GetProperty("ScriptGroups");
                var sProp = sl.GetType().GetProperty("Scripts");
                object g = gProp == null ? null : gProp.GetValue(sl);
                object scr = sProp == null ? null : sProp.GetValue(sl);
                int gc = Count(g), sc = Count(scr);
                Console.WriteLine("  ScriptList[" + (li++) + "] " + sl.GetType().Name + " groups=" + gc + " topScripts=" + sc);
                var ga = g as System.Collections.IEnumerable;
                if (ga != null)
                {
                    int gi = 0;
                    foreach (var grp in ga)
                    {
                        var nProp = grp.GetType().GetProperty("Name");
                        Console.WriteLine("      group[" + (gi++) + "] Name=" + (nProp == null ? "?" : "" + nProp.GetValue(grp)) + " dataSize=" + DSize(grp));
                    }
                }
            }
        }
        return 0;
    }

    static int Count(object o)
    {
        var c = o as System.Collections.ICollection;
        if (c != null) return c.Count;
        var e = o as System.Collections.IEnumerable;
        if (e == null) return -1;
        int n = 0; foreach (var x in e) n++; return n;
    }

    static string DSize(object o)
    {
        var p = o == null ? null : o.GetType().GetProperty("DataSize");
        return p == null ? "?" : "" + p.GetValue(o);
    }

    static ScriptList FirstNonEmptyScriptList(Ra3Map map)
    {
        var d = (IDictionary)map.Context.AssetDict;
        foreach (DictionaryEntry kv in d)
            if (kv.Key.ToString() == "PlayerScriptsList")
            {
                var psl = (PlayerScriptsList)kv.Value;
                foreach (var sl in psl.ScriptLists.GetAssets())
                    if (sl.ScriptGroups.Count > 0) return sl;
            }
        return null;
    }

    static int Info(string path)
    {
        var map = Ra3Map.Open(path);
        Console.WriteLine("打开成功: Errored=" + map.Errored + "  文件=" + path + "  (" + new FileInfo(path).Length + " bytes)");
        var d = (IDictionary)map.Context.AssetDict;
        Console.WriteLine("资产数=" + d.Count);
        foreach (DictionaryEntry kv in d)
        {
            var ba = (BaseAsset)kv.Value;
            Console.WriteLine("  [" + kv.Key + "] " + ba.GetType().Name + " ver=" + ba.Version
                              + " dataSize=" + ba.DataSize + " parsed=" + ba.Parsed + " errored=" + ba.Errored);
        }
        var sl = FirstNonEmptyScriptList(map);
        if (sl == null) { Console.WriteLine("脚本表为空"); return 3; }
        Console.WriteLine("脚本组数=" + sl.ScriptGroups.Count);
        int i = 0;
        foreach (var g in sl.ScriptGroups.GetAssets())
            Console.WriteLine("  group[" + (i++) + "] dataSize=" + g.DataSize);
        return 0;
    }

    static int Resave(string inPath, string outPath, bool compress)
    {
        var map = Ra3Map.Open(inPath);
        Console.WriteLine("打开: Errored=" + map.Errored + "  源文件 " + new FileInfo(inPath).Length + " bytes");
        if (File.Exists(outPath)) File.Delete(outPath);
        map.SaveAs(outPath, compress);
        long len = new FileInfo(outPath).Length;
        Console.WriteLine("另存: " + outPath + "  " + len + " bytes  compress=" + compress);
        // 复读确认
        var m2 = Ra3Map.Open(outPath);
        var sl = FirstNonEmptyScriptList(m2);
        Console.WriteLine("复读: Errored=" + m2.Errored + " 资产=" + ((IDictionary)m2.Context.AssetDict).Count
                          + " 脚本组=" + (sl == null ? -1 : sl.ScriptGroups.Count));
        return 0;
    }

    // 把某个脚本组的原始字节 dump 到 stdout 的 head（调试用；脚本内容在未压缩图里是明文）
    static int GroupLua(string path, int idx, int head)
    {
        var map = Ra3Map.Open(path);
        var sl = FirstNonEmptyScriptList(map);
        var g = sl.ScriptGroups.GetAssets()[idx];
        byte[] data = g.Data;
        Console.WriteLine("group[" + idx + "] dataSize=" + g.DataSize + " dataLen=" + (data == null ? -1 : data.Length));
        if (data != null)
        {
            string s = System.Text.Encoding.GetEncoding("gbk").GetString(data);
            Console.WriteLine(s.Length > head ? s.Substring(0, head) : s);
        }
        return 0;
    }
}
