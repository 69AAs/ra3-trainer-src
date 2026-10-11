// InjectMapScript.exe — 把一段 Lua 脚本"导入进" RA3 日冕 mod 的 .map 文件。
//
// 与编辑器插件 RA3MapUtil_LuaImporter 同源（见
//   D:\Games\CoronaLauncher\CoronaResources\NewWorldBuilder\data\scripts\RA3MapUtil_LuaImporter\Source\Main.cs
// ），但**关键区别**：插件用 `new Script()/new ScriptGroup()` 从零造对象再 registerSelf，
// 写出来的图在红警3里脚本整组不生效（实测）。本工具改走"**原地替换 / 深拷贝**"两条安全路线：
//
//   replace : 找到已存在的脚本组（按组名）→ 找到组里已存在的 Script（按脚本名）→
//             **只把该 Script 承载脚本内容的那个 ScriptAction 参数 stringValue 换掉**，
//             组 / 脚本 / 条件对象以及它们的**所有字段与开关一字不动**。
//             不新建组、不新建 Script 对象。
//
//   add     : 深拷贝一个已存在的脚本组（组/脚本/条件对象和所有字段一起拷），
//             只改组名和（目标）脚本名，再把目标脚本的内容换成我们的 lua，插到同一 ScriptList。
//             适用于"真实地图本身带脚本组，我要在不破坏它们原脚本的前提下新增自己的组"。
//
// 用法：
//   InjectMapScript.exe dump    <map> [groupNameSubstring] [--fields]
//   InjectMapScript.exe fields  <map> <groupName> [--out FILE]         # 逐字段 dump（用于对照表）
//   InjectMapScript.exe replace <src.map> <out.map> <script.lua> [groupName] [scriptName] [opts]
//   InjectMapScript.exe add     <src.map> <out.map> <script.lua> <srcGroupName> [newGroupName] [newScriptName] [opts]
//   InjectMapScript.exe roundtrip <src.map> <out.map> [save|raw|decompress|dosave|modify]
//   opts: --redundancy N --gbk --raw --wb <编辑器目录> [--src-script <名>]
//
// 依赖：MapCoreLib.dll（x86 / .NET Framework 4.x）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MapCoreLib.Core;
using MapCoreLib.Core.Asset;

class InjectMapScript
{
    const string LUA_PREFIX = "#!ra3luabridge\r\n";
    const string PAD_LINE = "-- end of script, please ignore this line";

    static int Main(string[] argv)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        try
        {
            if (argv.Length == 0) { Usage(); return 2; }
            var rest = argv.Skip(1).ToArray();
            switch (argv[0])
            {
                case "dump": return DoDump(rest);
                case "fields": return DoFields(rest);
                case "replace": return DoReplace(rest);
                case "add": return DoAdd(rest);
                case "inject": Console.Error.WriteLine("[note] `inject` 已改名为 `replace`（默认原地替换，不再新建脚本组）"); return DoReplace(rest);
                case "roundtrip": return DoRoundtrip(rest);
                default: Usage(); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    static void Usage()
    {
        Console.Error.WriteLine("InjectMapScript.exe dump    <map> [groupNameSubstring] [--fields]");
        Console.Error.WriteLine("InjectMapScript.exe fields  <map> <groupName> [--out FILE]");
        Console.Error.WriteLine("InjectMapScript.exe replace <src.map> <out.map> <lua> [groupName] [scriptName] [--redundancy N] [--gbk] [--raw] [--dump-fields FILE] [--wb dir]");
        Console.Error.WriteLine("InjectMapScript.exe add     <src.map> <out.map> <lua> <srcGroupName> [newGroupName] [newScriptName] [--template-map MAP] [--src-script NAME] [--keep-other-scripts] [--dump-fields FILE] [--redundancy N] [--gbk] [--raw] [--wb dir]");
        Console.Error.WriteLine("InjectMapScript.exe roundtrip <src.map> <out.map> [save|raw|decompress|dosave|modify]");
    }

    // ------------------------------------------------------------------ load

    // MapCoreLib 是为编辑器进程写的：它假定宿主已经初始化了日志（LogUtil.init）和
    // PrefUtil.record 这两个静态状态；独立进程里必须先补上，否则 parse() 直接 NPE。
    static bool libInited = false;
    static void InitLib()
    {
        if (libInited) return;
        libInited = true;
        try
        {
            string logDir = Path.Combine(Path.GetTempPath(), "inject-map-script-logs");
            Directory.CreateDirectory(Path.Combine(logDir, "data", "logs"));
            MapCoreLib.Util.LogUtil.init(logDir);
            // 关键：解析失败时 MapCoreLib 会无限刷日志（实测刷出 4.6GB）。把写日志的
            // StreamWriter 换成 Stream.Null / 限量 writer，既是防爆盘，也让出错时进程能被立刻杀掉。
            var lf = typeof(MapCoreLib.Util.LogUtil).GetField("sw", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (lf != null)
            {
                string keepLog = Environment.GetEnvironmentVariable("MAPINJ_LOG");
                CappedWriter cw;
                if (string.IsNullOrEmpty(keepLog)) cw = new CappedWriter(TextWriter.Null, 0);
                else cw = new CappedWriter(new StreamWriter(new FileStream(keepLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)), 400);
                lf.SetValue(null, cw);
            }
        }
        catch (Exception e) { Console.Error.WriteLine("[warn] LogUtil.init: " + e.Message); }
        try
        {
            var pf = typeof(Ra3Map).Assembly.GetType("MapCoreLib.Log.PrefUtil");
            var f = pf.GetField("record", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null && f.GetValue(null) == null)
                f.SetValue(null, Activator.CreateInstance(f.FieldType));
        }
        catch (Exception e) { Console.Error.WriteLine("[warn] PrefUtil.record: " + e.Message); }
        try
        {
            var ss = typeof(Ra3Map).Assembly.GetType("RMGlib.Core.Utility.ScriptSpec");
            ss.GetMethod("initScriptSpec", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
        }
        catch (Exception e) { Console.Error.WriteLine("[warn] initScriptSpec: " + e.Message); }
    }

    // 兜底：如果 initScriptSpec() 因为工作目录不对而没加载出规格表，就直接从编辑器目录读 JSON。
    // （ScriptAction.of / ScriptCondition.of 依赖 actionsSpec/conditionsSpec，否则报
    //  "unknown commandWord DEBUG_MESSAGE_BOX"。）
    static void EnsureScriptSpec(string wbDir)
    {
        var ss = typeof(Ra3Map).Assembly.GetType("RMGlib.Core.Utility.ScriptSpec");
        var cf = ss.GetField("conditionsSpec", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var af = ss.GetField("actionsSpec", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        bool needC = (cf.GetValue(null) as IDictionary) == null || ((IDictionary)cf.GetValue(null)).Count == 0;
        bool needA = (af.GetValue(null) as IDictionary) == null || ((IDictionary)af.GetValue(null)).Count == 0;
        if (!needC && !needA) return;
        if (wbDir == null) throw new Exception("ScriptSpec 未加载，且没提供 --wb <编辑器目录> 兜底读取 data/config/*.json");
        LoadSpecInto(cf, Path.Combine(wbDir, "data", "config", "ScriptConditonNew.json"));
        LoadSpecInto(af, Path.Combine(wbDir, "data", "config", "ScriptActionNew.json"));
    }

    static void LoadSpecInto(FieldInfo f, string jsonPath)
    {
        if (!File.Exists(jsonPath)) throw new FileNotFoundException("找不到脚本规格 json: " + jsonPath);
        Type dictType = f.FieldType;
        Type[] ga = dictType.GetGenericArguments();
        Type listType = typeof(List<>).MakeGenericType(ga[1]);
        object list = Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(jsonPath), listType);
        var dict = (IDictionary)Activator.CreateInstance(dictType);
        var keyProp = ga[1].GetProperty("commandWord");
        foreach (object item in (IEnumerable)list)
            dict[keyProp.GetValue(item, null)] = item;
        f.SetValue(null, dict);
        Console.Error.WriteLine("[info] 兜底载入 " + Path.GetFileName(jsonPath) + " -> " + dict.Count + " 条");
    }

    static Ra3Map LoadMap(string path)
    {
        InitLib();
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var map = new Ra3Map(path);
        map.parse();
        return map;
    }

    static ScriptList FirstScriptList(Ra3Map map)
    {
        var psl = map.getAsset<PlayerScriptsList>(Ra3MapConst.ASSET_PlayerScriptsList);
        if (psl == null) throw new Exception("地图里没有 PlayerScriptsList 资产");
        if (psl.scriptLists == null || psl.scriptLists.Count == 0)
            throw new Exception("PlayerScriptsList.scriptLists 是空的");
        return psl.scriptLists[0];
    }

    // ------------------------------------------------------------------ dump

    static int DoDump(string[] a)
    {
        if (a.Length < 1) { Usage(); return 2; }
        string mapPath = a[0];
        string filter = a.Length > 1 && !a[1].StartsWith("--") ? a[1] : null;

        var map = LoadMap(mapPath);
        var sl = FirstScriptList(map);
        Console.WriteLine("### MAP: " + mapPath);
        Console.WriteLine("### PlayerScriptsList.scriptLists[0]: groups=" + (sl.scriptGroups == null ? -1 : sl.scriptGroups.Count)
                          + " topScripts=" + (sl.scripts == null ? -1 : sl.scripts.Count));

        if (sl.scriptGroups != null)
            foreach (var g in sl.scriptGroups) DumpGroup(g, filter, 0);

        if (sl.scripts != null)
            foreach (var s in sl.scripts) DumpScript(s, filter, 0, sl);

        if (a.Any(x => x == "--fields") && sl.scriptGroups.Count > 0 && sl.scriptGroups[0].scripts.Count > 0)
        {
            Console.WriteLine("### 反射字段（用于比对默认值）");
            DumpFields("ScriptGroup", sl.scriptGroups[0]);
            DumpFields("Script", sl.scriptGroups[0].scripts[0]);
            DumpFields("ScriptAction", sl.scriptGroups[0].scripts[0].ScriptActionOnTrue[0]);
            DumpFields("OrCondition", sl.scriptGroups[0].scripts[0].scriptOrConditions[0]);
            DumpFields("ScriptCondition", sl.scriptGroups[0].scripts[0].scriptOrConditions[0].conditions[0]);
        }
        return 0;
    }

    static void DumpGroup(ScriptGroup g, string filter, int depth)
    {
        string ind = new string(' ', depth * 2);
        Console.WriteLine(ind + "[GROUP] \"" + g.Name + "\" IsActive=" + g.IsActive + " IsSubroutine=" + g.IsSubroutine);
        if (g.scripts != null) foreach (var s in g.scripts) DumpScript(s, filter, depth + 1, g);
        if (g.scriptGroups != null) foreach (var sg in g.scriptGroups) DumpGroup(sg, filter, depth + 1);
    }

    static void DumpScript(Script s, string filter, int depth, object owner)
    {
        string ind = new string(' ', depth * 2);
        var actions = s.ScriptActionOnTrue;
        int nActs = actions == null ? -1 : actions.Count;
        string firstLen = "-";
        string firstHead = "";
        if (actions != null && actions.Count > 0 && actions[0].arguments != null && actions[0].arguments.Count > 0)
        {
            var sv = actions[0].arguments[0].stringValue;
            if (sv == null) firstLen = "null";
            else { firstLen = sv.Length.ToString(); firstHead = sv.Length > 60 ? sv.Substring(0, 60) : sv; }
        }
        Console.WriteLine(ind + "[SCRIPT] \"" + s.Name + "\" active=" + s.isActive + " actions=" + nActs
                          + " arg0.len=" + firstLen);
        if (firstHead != "")
        {
            Console.WriteLine(ind + "        head=" + Escape(firstHead));
        }
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(c == '\r' ? "\\r" : c == '\n' ? "\\n" : (c < 32 ? "\\x" + ((int)c).ToString("x2") : c.ToString()));
        return sb.ToString();
    }

    static void DumpFields(string label, object o)
    {
        if (o == null) { Console.WriteLine("  " + label + " = null"); return; }
        Console.WriteLine("  ---- " + label + " (" + o.GetType().FullName + ")");
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object v;
            try { v = f.GetValue(o); } catch (Exception e) { v = "<" + e.Message + ">"; }
            string s;
            if (v == null) s = "null";
            else if (v is string) s = "\"" + ((string)v).Length + " chars\"";
            else if (v is ICollection) s = "ICollection[" + ((ICollection)v).Count + "]";
            else s = v.ToString();
            Console.WriteLine("     " + f.FieldType.Name + " " + f.Name + " = " + s);
        }
    }

    // ------------------------------------------------------------------ fields

    // 把某个脚本组整棵子树（组 / 脚本 / 条件 / 动作用的所有字段）展平成 "路径 = 值" 行，
    // 路径**相对于组根**，这样子树可以整段对照。字符串值只给 长度 + md5 + 头 64 字符
    // （内容字节数变化是允许的，其余字段必须逐项一致）。
    static int DoFields(string[] a)
    {
        if (a.Length < 2) { Usage(); return 2; }
        string mapPath = a[0], groupName = a[1];
        string outPath = null;
        for (int i = 2; i < a.Length; i++) if (a[i] == "--out") outPath = a[++i];

        var map = LoadMap(mapPath);
        var sl = FirstScriptList(map);
        ScriptGroup g;
        if (groupName.StartsWith("#")) g = sl.scriptGroups[int.Parse(groupName.Substring(1))];
        else g = sl.scriptGroups.FirstOrDefault(x => x.Name == groupName);
        if (g == null)
        {
            Console.Error.WriteLine("找不到脚本组 \"" + groupName + "\"，该图有: " +
                string.Join(" | ", sl.scriptGroups.Select(x => x.Name)));
            return 3;
        }
        var lines = new List<string>();
        EmitGroup(g, "group", lines);
        lines.Sort(StringComparer.Ordinal);
        var text = string.Join("\n", lines) + "\n";
        if (outPath != null) File.WriteAllText(outPath, text, new UTF8Encoding(false));
        else Console.Write(text);
        Console.Error.WriteLine("[fields] group=\"" + groupName + "\" 行数=" + lines.Count + (outPath != null ? " -> " + outPath : ""));
        return 0;
    }

    static void Add(List<string> L, string path, object v)
    {
        L.Add(path + " = " + FmtVal(v));
    }

    static string FmtVal(object v)
    {
        if (v == null) return "null";
        if (v is string)
        {
            var s = (string)v;
            string head = s.Length > 64 ? s.Substring(0, 64) : s;
            return "str(len=" + s.Length + ", md5=" + Md5(s) + ", head=" + Escape(head) + ")";
        }
        return Convert.ToString(v);
    }

    static string Md5(string s)
    {
        using (var md5 = MD5.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var h = md5.ComputeHash(bytes);
            var sb = new StringBuilder();
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString().Substring(0, 12);
        }
    }

    static void EmitGroup(object g, string p, List<string> L)
    {
        Add(L, p + ".@type", "ScriptGroup");
        Add(L, p + ".Name", Fld(g, "Name"));
        Add(L, p + ".IsActive", Fld(g, "IsActive"));
        Add(L, p + ".IsSubroutine", Fld(g, "IsSubroutine"));
        Add(L, p + ".id", Fld(g, "id"));
        Add(L, p + ".version", Fld(g, "version"));
        Add(L, p + ".assetName", Fld(g, "name"));
        Add(L, p + ".dataSize", Fld(g, "dataSize"));
        var scripts = (IList)Fld(g, "scripts");
        Add(L, p + ".scripts.count", scripts == null ? -1 : scripts.Count);
        if (scripts != null) for (int i = 0; i < scripts.Count; i++) EmitScript(scripts[i], p + ".scripts[" + i + "]", L);
        var subs = (IList)Fld(g, "scriptGroups");
        Add(L, p + ".scriptGroups.count", subs == null ? -1 : subs.Count);
        if (subs != null) for (int i = 0; i < subs.Count; i++) EmitGroup(subs[i], p + ".scriptGroups[" + i + "]", L);
    }

    static void EmitScript(object s, string p, List<string> L)
    {
        Add(L, p + ".@type", "Script");
        foreach (var f in new[] { "Name", "Conmment", "ConditionComment", "ActionComment", "IsSubroutine", "isActive",
            "DeactivateUponSuccess", "ActiveInEasy", "ActiveInMedium", "ActiveInHard", "EvaluationInterval",
            "ActionsFireSequentially", "LoopActions", "LoopCount", "SequentialTargetType", "SequentialTargetName",
            "Unknown", "id", "version", "name", "dataSize" })
            Add(L, p + "." + f, Fld(s, f));
        var ors = (IList)Fld(s, "scriptOrConditions");
        Add(L, p + ".scriptOrConditions.count", ors == null ? -1 : ors.Count);
        if (ors != null) for (int i = 0; i < ors.Count; i++) EmitOr(ors[i], p + ".scriptOrConditions[" + i + "]", L);
        foreach (var fname in new[] { "ScriptActionOnTrue", "ScriptActionOnFalse" })
        {
            var acts = (IList)Fld(s, fname);
            Add(L, p + "." + fname + ".count", acts == null ? -1 : acts.Count);
            if (acts != null) for (int i = 0; i < acts.Count; i++) EmitContent(acts[i], p + "." + fname + "[" + i + "]", L);
        }
    }

    static void EmitOr(object oc, string p, List<string> L)
    {
        Add(L, p + ".@type", "OrCondition");
        Add(L, p + ".id", Fld(oc, "id"));
        Add(L, p + ".version", Fld(oc, "version"));
        Add(L, p + ".assetName", Fld(oc, "name"));
        Add(L, p + ".dataSize", Fld(oc, "dataSize"));
        var conds = (IList)Fld(oc, "conditions");
        Add(L, p + ".conditions.count", conds == null ? -1 : conds.Count);
        if (conds != null) for (int i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            Add(L, p + ".conditions[" + i + "].IsInverted", Fld(c, "IsInverted"));
            EmitContent(Fld(c, "scriptContent"), p + ".conditions[" + i + "].scriptContent", L);
        }
    }

    static void EmitContent(object c, string p, List<string> L)
    {
        if (c == null) { Add(L, p, null); return; }
        Add(L, p + ".@type", c.GetType().Name);
        foreach (var f in new[] { "contentType", "assetPropertyType", "contentName", "nameIndex", "enable", "id", "version", "name", "dataSize" })
            Add(L, p + "." + f, Fld(c, f));
        var args = (IList)Fld(c, "arguments");
        Add(L, p + ".arguments.count", args == null ? -1 : args.Count);
        if (args != null) for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            Add(L, p + ".arguments[" + i + "].argumentType", Fld(arg, "argumentType"));
            Add(L, p + ".arguments[" + i + "].intValue", Fld(arg, "intValue"));
            Add(L, p + ".arguments[" + i + "].floatValue", Fld(arg, "floatValue"));
            Add(L, p + ".arguments[" + i + "].position", Fld(arg, "position"));
            Add(L, p + ".arguments[" + i + "].stringValue", Fld(arg, "stringValue"));
        }
    }

    // 反射读取字段（含基类 protected，如 MajorAsset.id/version/name/dataSize）。
    static object Fld(object o, string n)
    {
        if (o == null) return null;
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) { try { return f.GetValue(o); } catch { return null; } }
        }
        return null;
    }

    static void SetFld(object o, string n, object v)
    {
        for (Type t = o.GetType(); t != null; t = t.BaseType)
        {
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) { f.SetValue(o, v); return; }
        }
        throw new Exception("找不到字段 " + n + " on " + o.GetType().FullName);
    }

    // ---------------------------------------------------------------- replace

    static int DoReplace(string[] a)
    {
        if (a.Length < 3) { Usage(); return 2; }
        string srcMap = a[0], outMap = a[1], luaPath = a[2];
        string groupName = null, scriptName = null, dumpFields = null;
        int redundancy = 100;
        bool gbk = false, raw = false;
        string wbDir = Environment.GetEnvironmentVariable("RA3_WB_DIR") ?? "D:/Games/CoronaLauncher/CoronaResources/NewWorldBuilder";

        for (int i = 3; i < a.Length; i++)
        {
            if (a[i] == "--redundancy") redundancy = int.Parse(a[++i]);
            else if (a[i] == "--gbk") gbk = true;
            else if (a[i] == "--raw") raw = true;
            else if (a[i] == "--wb") wbDir = a[++i];
            else if (a[i] == "--dump-fields") dumpFields = a[++i];
            else if (groupName == null) groupName = a[i];
            else if (scriptName == null) scriptName = a[i];
            else throw new Exception("多余的参数: " + a[i]);
        }
        if (groupName == null) groupName = "Example_origin_05_top_button";
        if (scriptName == null) scriptName = "init.lua";

        GuardReadOnly(srcMap, outMap);
        Console.WriteLine("[1/5] 读取 lua: " + luaPath);
        var luaText = ReadLua(luaPath, gbk);
        var content = BuildContent(luaText, redundancy);
        Console.WriteLine("      lua 字符数=" + luaText.Length + "  (含头+占位后= " + content.Length + ")");

        Console.WriteLine("[2/5] 打开源图: " + Path.GetFullPath(srcMap) + "  (" + new FileInfo(srcMap).Length + " bytes)");
        var map = LoadMap(Path.GetFullPath(srcMap));
        var sl = FirstScriptList(map);
        EnsureScriptSpec(wbDir);
        Console.WriteLine("      源图分组数=" + sl.scriptGroups.Count);

        Console.WriteLine("[3/5] 原地定位 组 \"" + groupName + "\" / 脚本 \"" + scriptName + "\"");
        var g = sl.scriptGroups.FirstOrDefault(x => string.Equals(x.Name, groupName, StringComparison.Ordinal));
        if (g == null)
            throw new Exception("源图里没有脚本组 \"" + groupName + "\"。现有组: " +
                string.Join(" | ", sl.scriptGroups.Select(x => x.Name)) + "（若是想新增组，请用 `add` 模式）");
        var s = (g.scripts ?? new List<Script>()).FirstOrDefault(x => string.Equals(x.Name, scriptName, StringComparison.Ordinal));
        if (s == null)
            throw new Exception("组 \"" + groupName + "\" 里没有脚本 \"" + scriptName + "\"。现有脚本: " +
                string.Join(" | ", (g.scripts ?? new List<Script>()).Select(x => x.Name)));

        string how;
        int oldLen = ReplaceScriptContent(map, s, content, out how);
        Console.WriteLine("      内容替换: " + how + "  (" + oldLen + " -> " + content.Length + " chars)");

        Console.WriteLine("[4/5] 另存为: " + Path.GetFullPath(outMap));
        SaveOut(map, outMap, raw);

        Console.WriteLine("[5/5] 完成（组/脚本/条件对象未重建，字段与 id 全部沿用源图）。");
        if (dumpFields != null) EmitGroupFieldsToFile(g, dumpFields, "替换后的目标组（内存态，= 写盘内容）");
        Console.WriteLine("RESULT: OK (replace) -> " + Path.GetFullPath(outMap));
        return 0;
    }

    // ---------------------------------------------------------------- add

    static int DoAdd(string[] a)
    {
        if (a.Length < 4) { Usage(); return 2; }
        string srcMap = a[0], outMap = a[1], luaPath = a[2], srcGroupName = a[3];
        string newGroupName = null, newScriptName = null, srcScriptName = null, templateMapPath = null, dumpFields = null;
        int redundancy = 100;
        bool gbk = false, raw = false, keepOthers = false, regSelf = false;
        int listIndex = 0;
        string wbDir = Environment.GetEnvironmentVariable("RA3_WB_DIR") ?? "D:/Games/CoronaLauncher/CoronaResources/NewWorldBuilder";

        for (int i = 4; i < a.Length; i++)
        {
            if (a[i] == "--redundancy") redundancy = int.Parse(a[++i]);
            else if (a[i] == "--gbk") gbk = true;
            else if (a[i] == "--raw") raw = true;
            else if (a[i] == "--wb") wbDir = a[++i];
            else if (a[i] == "--src-script") srcScriptName = a[++i];
            else if (a[i] == "--template-map") templateMapPath = a[++i];
            else if (a[i] == "--dump-fields") dumpFields = a[++i];
            else if (a[i] == "--keep-other-scripts") keepOthers = true;
            else if (a[i] == "--register-self") regSelf = true;
            else if (a[i] == "--list-index") listIndex = int.Parse(a[++i]);
            else if (newGroupName == null) newGroupName = a[i];
            else if (newScriptName == null) newScriptName = a[i];
            else throw new Exception("多余的参数: " + a[i]);
        }
        if (newGroupName == null) newGroupName = "R3MapLib";
        if (newScriptName == null) newScriptName = "init.lua";

        GuardReadOnly(srcMap, outMap);
        Console.WriteLine("[1/6] 读取 lua: " + luaPath);
        var luaText = ReadLua(luaPath, gbk);
        var content = BuildContent(luaText, redundancy);
        var blank = BuildContent("", redundancy);
        Console.WriteLine("      lua 字符数=" + luaText.Length + "  (含头+占位后= " + content.Length + ")");

        Console.WriteLine("[2/6] 打开目标图: " + Path.GetFullPath(srcMap) + "  (" + new FileInfo(srcMap).Length + " bytes)");
        var map = LoadMap(Path.GetFullPath(srcMap));
        var ctx = map.getContext();
        var psl = map.getAsset<PlayerScriptsList>(Ra3MapConst.ASSET_PlayerScriptsList);
        var sl = FirstScriptList(map);
        EnsureScriptSpec(wbDir);
        Console.WriteLine("      目标图 ScriptLists=" + psl.scriptLists.Count + " scriptList[0] 分组数=" + sl.scriptGroups.Count);
        // 供流水线做"MapCoreLib 往返修复"用：记录 PlayerScriptsList 在未压缩流里的位置/长度
        Console.WriteLine("      PSL-OFFSETS dataStartPos=" + Fld(psl, "dataStartPos") + " dataSize=" + Fld(psl, "dataSize"));
        if (sl.scriptGroups.Any(x => string.Equals(x.Name, newGroupName, StringComparison.Ordinal)))
            throw new Exception("目标图里已存在组名: " + newGroupName + "（换个 newGroupName）");

        // 拷贝源：模板图（--template-map）里的组，或目标图自身的组
        ScriptGroup src;
        string srcLabel;
        if (templateMapPath != null)
        {
            Console.WriteLine("[3/6] 打开模板图: " + Path.GetFullPath(templateMapPath));
            var tmap = LoadMap(Path.GetFullPath(templateMapPath));
            var tsl = FirstScriptList(tmap);
            Console.WriteLine("      模板图分组数=" + tsl.scriptGroups.Count + "  组: " + string.Join(" | ", tsl.scriptGroups.Select(x => x.Name)));
            src = tsl.scriptGroups.FirstOrDefault(x => string.Equals(x.Name, srcGroupName, StringComparison.Ordinal));
            if (src == null) throw new Exception("模板图里没有脚本组 \"" + srcGroupName + "\"。现有组: " + string.Join(" | ", tsl.scriptGroups.Select(x => x.Name)));
            srcLabel = "模板图 \"" + srcGroupName + "\"";
        }
        else
        {
            Console.WriteLine("[3/6] 在目标图里找组 \"" + srcGroupName + "\"");
            src = sl.scriptGroups.FirstOrDefault(x => string.Equals(x.Name, srcGroupName, StringComparison.Ordinal));
            if (src == null) throw new Exception("目标图里没有脚本组 \"" + srcGroupName + "\"。现有组: " + string.Join(" | ", sl.scriptGroups.Select(x => x.Name)));
            srcLabel = "目标图 \"" + srcGroupName + "\"";
        }

        Console.WriteLine("[4/6] 深拷贝 " + srcLabel + " -> 新组 \"" + newGroupName + "\"");
        var clone = (ScriptGroup)DeepClone(src);
        var scripts = clone.scripts ?? (clone.scripts = new List<Script>());
        var target = (srcScriptName != null)
            ? scripts.FirstOrDefault(x => string.Equals(x.Name, srcScriptName, StringComparison.Ordinal))
            : (scripts.FirstOrDefault(x => string.Equals(x.Name, newScriptName, StringComparison.Ordinal)) ?? scripts.FirstOrDefault());
        if (target == null)
            throw new Exception("拷贝出来的组里没有可用的脚本（源组 scripts 数=" + scripts.Count + "）");

        Console.WriteLine("      重命名 组 \"" + src.Name + "\" -> \"" + newGroupName + "\"；目标脚本 \"" + target.Name + "\" -> \"" + newScriptName + "\"");
        clone.Name = newGroupName;
        target.Name = newScriptName;

        // 关键：**先用显式唯一索引**把克隆子树要用的名字登记进目标图 stringPool，再动内容。
        // 不能用 MapCoreLib 的 RegisterString(str) 单参版 —— 实测它给新名字分配与既有最大索引
        // 重复的 index（DEBUG_MESSAGE_BOX 与 WaypointsList 同为 76），既有资产 name 索引整体错位。
        // 也必须**先于** ScriptAction.of 调用（.of 内部会注册 contentName，走的就是那个坏重载）。
        int added = RegisterNamesIntoPool(ctx, clone, "DEBUG_MESSAGE_BOX");
        Console.WriteLine("      [pool] 预登记新名字 " + added + " 个（唯一索引）");

        // ★关键★ 脚本区里每个嵌套资产的**第一个字段不是普通 id，而是它资产名在 stringPool 里的下标**
        // （ScriptGroup=35 / Script=36 / OrCondition=37 / Condition=38 / ScriptAction=40 ...）。
        // 深拷贝从模板图带过来的这些下标是**模板图池子**的下标；目标图池子不同，必须按名字重映射，
        // 否则解析器拿 35 去目标池子查，查到的是别的名字 → 整组读不出来（MapTool 报 groups=0）。
        RemapAssetIds(clone, ctx);

        string how2;
        int oldLen2 = ReplaceScriptContent(map, target, content, out how2, true, true);
        Console.WriteLine("      目标脚本内容替换: " + how2 + "  (" + (oldLen2 < 0 ? "n/a" : oldLen2.ToString()) + " -> " + content.Length + " chars)");

        // 其余脚本：默认把内容换成空占位（字段/开关一字不动），可用 --keep-other-scripts 保留原内容
        foreach (var s2 in scripts)
        {
            if (ReferenceEquals(s2, target)) continue;
            if (keepOthers) { Console.WriteLine("      [keep] 其它脚本 \"" + s2.Name + "\" 保留原内容"); continue; }
            string how3;
            int o3 = ReplaceScriptContent(map, s2, blank, out how3, false, false);
            Console.WriteLine("      [blank] 其它脚本 \"" + s2.Name + "\" 内容清空（字段不变）: " + how3 + " (" + (o3 < 0 ? "n/a" : o3.ToString()) + " -> " + blank.Length + " chars)");
        }

        foreach (ScriptGroup sg in clone.scriptGroups ?? new List<ScriptGroup>())
            Console.WriteLine("      [warn] 组内还有子组 \"" + sg.Name + "\"（原样保留）");

        // 注册：只做官方插件同款的 group.registerSelf（见 RA3MapUtil_LuaImporter.AddScriptGroup）。
        // 注意：**不要**递归 registerSelf 到 ScriptContent/ScriptAction —— 实测那样会把 contentName
        // (DEBUG_MESSAGE_BOX) 以"资产"身份插进目标图的 stringPool 中段，导致既有资产的 name 索引错位
        // （WaypointsList 被顶掉）。--register-self 才开启递归（默认关）。
        var targetList = (listIndex >= 0 && listIndex < psl.scriptLists.Count) ? psl.scriptLists[listIndex] : sl;
        if (!ReferenceEquals(targetList, sl)) Console.Error.WriteLine("[note] 目标 ScriptList[" + listIndex + "]");
        targetList.scriptGroups.Add(clone);
        SyncNameIndexes(ctx, clone);
        if (regSelf) { clone.registerSelf(ctx); RegisterSelfRecursive(clone, ctx); }
        Console.WriteLine("      写入后分组数=" + sl.scriptGroups.Count);

        Console.WriteLine("[5/6] 另存为: " + Path.GetFullPath(outMap));
        SaveOut(map, outMap, raw);

        Console.WriteLine("[6/6] 完成（新组 = 模板组的逐字段深拷贝）。");
        if (dumpFields != null) EmitGroupFieldsToFile(clone, dumpFields, "新增组（内存态，= 写盘内容）");
        Console.WriteLine("RESULT: OK (add) -> " + Path.GetFullPath(outMap));
        return 0;
    }

    // 把克隆子树里所有资产的名字/内容名注册进目标图的 stringPool（模板图与目标图池子不同）。
    static void RegisterSelfRecursive(object o, MapDataContext ctx)
    {
        if (o == null) return;
        try
        {
            var m = o.GetType().GetMethod("registerSelf", new[] { typeof(MapDataContext) });
            if (m != null) m.Invoke(o, new object[] { ctx });
        }
        catch (Exception e) { Console.Error.WriteLine("[warn] registerSelf(" + o.GetType().Name + "): " + e.Message); }
        if (o is ScriptGroup)
        {
            var g = (ScriptGroup)o;
            if (g.scripts != null) foreach (var s in g.scripts) RegisterSelfRecursive(s, ctx);
            if (g.scriptGroups != null) foreach (var sg in g.scriptGroups) RegisterSelfRecursive(sg, ctx);
        }
        else if (o is Script)
        {
            var s = (Script)o;
            if (s.scriptOrConditions != null) foreach (var oc in s.scriptOrConditions) RegisterSelfRecursive(oc, ctx);
            if (s.ScriptActionOnTrue != null) foreach (var ac in s.ScriptActionOnTrue) RegisterSelfRecursive(ac, ctx);
            if (s.ScriptActionOnFalse != null) foreach (var ac in s.ScriptActionOnFalse) RegisterSelfRecursive(ac, ctx);
        }
        else if (o is OrCondition)
        {
            var oc = (OrCondition)o;
            if (oc.conditions != null) foreach (var c in oc.conditions) RegisterSelfRecursive(c, ctx);
        }
        else if (o is ScriptCondition)
        {
            var c = (ScriptCondition)o;
            if (c.scriptContent != null) RegisterSelfRecursive(c.scriptContent, ctx);
        }
    }

    // 收集子树里所有会进 stringPool 的名字：资产的 name（ScriptGroup/Script/ScriptAction/...）
    // 和脚本内容名（DEBUG_MESSAGE_BOX / CONDITION_TRUE）。并把每个 ScriptContent 的 nameIndex
    // 同步成目标池里的新索引。
    static int RegisterNamesIntoPool(MapDataContext ctx, ScriptGroup clone, params string[] extraNames)
    {
        var ms = ctx.MapStruct;
        var pf = typeof(Ra3MapStruct).GetField("stringPool", BindingFlags.NonPublic | BindingFlags.Instance);
        var pool = (IDictionary)pf.GetValue(ms);
        int max = 0;
        foreach (DictionaryEntry e in pool) { int v = (int)e.Value; if (v > max) max = v; }
        var objs = new List<object>();
        CollectScriptObjects(clone, objs, new HashSet<object>());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in objs)
        {
            var nm = Fld(o, "name") as string;          // MajorAsset.name
            if (!string.IsNullOrEmpty(nm)) names.Add(nm);
            var cn = Fld(o, "contentName") as string;   // ScriptContent.contentName
            if (!string.IsNullOrEmpty(cn)) names.Add(cn);
        }
        foreach (var e in extraNames) if (!string.IsNullOrEmpty(e)) names.Add(e);
        int added = 0;
        foreach (var nm in names.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (pool.Contains(nm)) continue;
            max++;
            ms.RegisterString(nm, max);
            added++;
            Console.WriteLine("      [pool+] \"" + nm + "\" -> index " + max);
        }
        // 同步 nameIndex
        foreach (var o in objs)
        {
            var cn = Fld(o, "contentName") as string;
            if (string.IsNullOrEmpty(cn)) continue;
            if (pool.Contains(cn)) SetFld(o, "nameIndex", (int)pool[cn]);
        }
        return added;
    }

    static void CollectScriptObjects(object o, List<object> acc, HashSet<object> seen)
    {
        if (o == null || o is string || o.GetType().IsValueType) return;
        if (!seen.Add(o)) return;
        acc.Add(o);
        foreach (var fname in new[] { "scripts", "scriptGroups", "scriptOrConditions", "conditions", "ScriptActionOnTrue", "ScriptActionOnFalse", "arguments" })
        {
            var v = Fld(o, fname) as IEnumerable;
            if (v == null || v is string) continue;
            foreach (var c in v) CollectScriptObjects(c, acc, seen);
        }
    }

    static int MaxAssetId(MapDataContext ctx)
    {
        int max = 0;
        foreach (MajorAsset a in ctx.MapStruct.getAssets())
        {
            object v = Fld(a, "id");
            if (v is int && (int)v > max) max = (int)v;
        }
        return max;
    }

    // 把克隆子树里每个"资产型"对象的 id 重映射成**目标图 stringPool 里该资产名(如 ScriptGroup)的下标**。
    // 这正是脚本区文件格式里嵌套资产的第一个字段的语义。
    static void RemapAssetIds(ScriptGroup clone, MapDataContext ctx)
    {
        var pool = (IDictionary)typeof(Ra3MapStruct).GetField("stringPool", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ctx.MapStruct);
        var objs = new List<object>();
        CollectScriptObjects(clone, objs, new HashSet<object>());
        foreach (var o in objs)
        {
            var nm = Fld(o, "name") as string;   // MajorAsset.name: ScriptGroup/Script/ScriptAction/OrCondition/Condition
            if (string.IsNullOrEmpty(nm) || !pool.Contains(nm)) continue;
            try { SetFld(o, "id", (int)pool[nm]); } catch { }
        }
    }

    static void SyncNameIndexes(MapDataContext ctx, ScriptGroup clone)
    {
        var pool = (IDictionary)typeof(Ra3MapStruct).GetField("stringPool", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ctx.MapStruct);
        var objs = new List<object>();
        CollectScriptObjects(clone, objs, new HashSet<object>());
        foreach (var o in objs)
        {
            var cn = Fld(o, "contentName") as string;
            if (string.IsNullOrEmpty(cn)) continue;
            if (pool.Contains(cn)) SetFld(o, "nameIndex", (int)pool[cn]);
        }
    }

    static void EmitGroupFieldsToFile(ScriptGroup g, string outPath, string label)
    {
        var lines = new List<string>();
        EmitGroup(g, "group", lines);
        lines.Sort(StringComparer.Ordinal);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        Console.WriteLine("      [dump-fields] " + label + " -> " + outPath + " (" + lines.Count + " 行)");
    }

    // 把 script 里承载 lua 内容的那个 action 的 stringValue 换成 content。
    // 优先复用已存在的"文本型" action（arguments[0] 是字符串），否则在 ScriptActionOnTrue 里追加一个
    // DEBUG_MESSAGE_BOX 文本 action。**绝不重建组 / 脚本 / 条件对象。**
    static int ReplaceScriptContent(Ra3Map map, Script s, string content, out string how, bool createIfMissing = false, bool clearIfCreate = false)
    {
        var ctx = map.getContext();
        var onTrue = s.ScriptActionOnTrue ?? (s.ScriptActionOnTrue = new List<ScriptAction>());
        // 1) 优先复用**已存在的文本型 action**（arguments[0] 是字符串）——这样 action 的
        //    name/version/id/nameIndex 全部原样保留，只换内容。
        ScriptAction textAct = null;
        for (int i = 0; i < onTrue.Count; i++)
        {
            var a = onTrue[i];
            if (a != null && a.arguments != null && a.arguments.Count > 0 && a.arguments[0] != null && a.arguments[0].stringValue != null)
            { textAct = a; break; }
        }
        if (textAct != null)
        {
            int old = textAct.arguments[0].stringValue.Length;
            textAct.arguments[0].stringValue = content;
            how = "复用已有文本 action[" + onTrue.IndexOf(textAct) + "] contentName=" + textAct.contentName;
            return old;
        }
        if (!createIfMissing) { how = "跳过（该脚本没有文本型 action，无法替换内容）"; return -1; }
        // 2) 新建一个 DEBUG_MESSAGE_BOX 文本 action（与官方插件 MakeScript 一致），并补全
        //    name/version（ScriptAction.of 留空），使其与解析出来的 action 字段一致。
        var na = ScriptAction.of(ctx, "DEBUG_MESSAGE_BOX", new List<object> { (object)content });
        SetFld(na, "name", "ScriptAction");
        SetFld(na, "version", (short)3);
        if (clearIfCreate)
        {
            onTrue.Clear();
            onTrue.Add(na);
            how = "替换整条 action 列表为 1 个 DEBUG_MESSAGE_BOX";
        }
        else
        {
            onTrue.Add(na);
            how = "追加 DEBUG_MESSAGE_BOX action";
        }
        return -1;
    }

    // 递归深拷贝（按字段，含基类 protected 字段）。结构：ScriptGroup/Script/ScriptAction(False)/
    // ScriptContent/ScriptCondition/OrCondition/ScriptArgument/List<T>。
    static object DeepClone(object o)
    {
        if (o == null) return null;
        Type t = o.GetType();
        if (t.IsValueType || o is string) return o;
        if (o is IList)
        {
            var list = (IList)Activator.CreateInstance(t);
            foreach (var item in (IList)o) list.Add(DeepClone(item));
            return list;
        }
        object n;
        try { n = Activator.CreateInstance(t, true); } catch { n = Activator.CreateInstance(t); }
        for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                try { f.SetValue(n, DeepClone(v)); } catch { }
            }
        }
        return n;
    }

    // ---------------------------------------------------------------- shared

    static void GuardReadOnly(string srcMap, string outMap)
    {
        string srcFull = Path.GetFullPath(srcMap);
        string outFull = Path.GetFullPath(outMap);
        if (string.Equals(srcFull, outFull, StringComparison.OrdinalIgnoreCase))
            throw new Exception("拒绝原地修改：输出不能等于输入 (" + srcFull + ")");
        if (!File.Exists(srcFull)) throw new FileNotFoundException(srcFull);
    }

    // MapCoreLib 的 map.save(path) 写出的是"带 CompressorFlag 的 refpack 流"（压缩图，和磁盘上官方图一致）。
    // --raw 时另写一份未压缩（CkMp 开头）副本，方便直接按字节 grep 脚本明文。
    static void SaveOut(Ra3Map map, string outMap, bool raw)
    {
        var outFull = Path.GetFullPath(outMap);
        var outDir = Path.GetDirectoryName(outFull);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        if (File.Exists(outFull)) File.Delete(outFull);
        map.save(outFull);
        Console.WriteLine("      输出大小=" + new FileInfo(outFull).Length + " bytes");
        if (raw)
        {
            string rawOut = outFull + ".raw";
            WriteRaw(map, rawOut);
            Console.WriteLine("      未压缩副本=" + rawOut + " (" + new FileInfo(rawOut).Length + " bytes)");
        }
    }

    static void WriteRaw(Ra3Map map, string outPath)
    {
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((uint)Ra3MapConst.UnCompressorFlag);
            var ctx = map.getContext();
            ctx.MapStruct.save(bw, ctx);
            bw.Flush();
        }
    }

    static int DoRoundtrip(string[] a)
    {
        if (a.Length < 2) { Usage(); return 2; }
        string src = a[0], outp = a[1];
        string mode = a.Length > 2 ? a[2] : "save";
        var map = LoadMap(src);
        Console.WriteLine("src=" + src + " (" + new FileInfo(src).Length + ")");
        if (mode == "modify") { map.modifyMap(); Console.WriteLine("called modifyMap()"); }
        if (mode == "decompress") { map.DeCompress(); Console.WriteLine("called DeCompress()"); }
        if (mode == "raw") WriteRaw(map, outp);
        else if (mode == "dosave") { if (File.Exists(outp)) File.Delete(outp); map.doSaveMap(outp); }
        else { if (File.Exists(outp)) File.Delete(outp); map.save(outp); }
        Console.WriteLine("out=" + outp + " (" + new FileInfo(outp).Length + ")");
        return 0;
    }

    static string ReadLua(string path, bool gbk)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        if (gbk) text = Encoding.GetEncoding("gbk").GetString(bytes);
        else
        {
            int off = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            text = new UTF8Encoding(false).GetString(bytes, off, bytes.Length - off);
        }
        return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    static string BuildContent(string luaText, int redundancy)
    {
        var sb = new StringBuilder();
        sb.Append(LUA_PREFIX);
        sb.Append(luaText);
        sb.Append("\r\n");
        for (int i = 0; i < redundancy; i++) sb.Append(PAD_LINE);
        return sb.ToString();
    }
}

// MapCoreLib 出错时会无限刷日志（实测 4.6GB）。这个 writer 只保留前 N 行，其余丢弃。
class CappedWriter : StreamWriter
{
    private readonly TextWriter inner;
    private readonly int maxLines;
    private int lines;
    public CappedWriter(TextWriter inner, int maxLines) : base(Stream.Null) { this.inner = inner; this.maxLines = maxLines; }
    public override void Write(char value)
    {
        if (lines < maxLines) { inner.Write(value); if (value == '\n') { lines++; inner.Flush(); } }
    }
    public override void Write(string s)
    {
        if (lines >= maxLines || s == null) return;
        foreach (char c in s) Write(c);
    }
    public override void WriteLine(string s) { Write(s); Write('\n'); }
    public override void WriteLine(char c) { Write(c); Write('\n'); }
    public override void WriteLine() { Write('\n'); }
    public override void Flush() { inner.Flush(); }
}
