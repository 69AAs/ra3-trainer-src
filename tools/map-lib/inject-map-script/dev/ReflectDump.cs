// ReflectDump.cs — 探查 MapCoreLib 内部类型（ScriptContent / ScriptAction / MapDataContext 等）
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using MapCoreLib.Core;
using MapCoreLib.Core.Asset;

class ReflectDump
{
    static void Main(string[] argv)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        var asm = typeof(Ra3Map).Assembly;
        if (argv.Length == 0) { Console.WriteLine("usage: ReflectDump <typeName> | args <map>"); return; }
        if (argv[0] == "args")
        {
            DumpArgs(argv[1]);
            return;
        }
        foreach (var t in asm.GetTypes().Where(t => t.Name.IndexOf(argv[0], StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(t => t.FullName))
        {
            Console.WriteLine("=== " + t.FullName + "  (base=" + (t.BaseType==null?"-":t.BaseType.FullName) + ")");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine("   F " + Mods(f) + " " + Pretty(f.FieldType) + " " + f.Name);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Console.WriteLine("   P " + Pretty(p.PropertyType) + " " + p.Name);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
                Console.WriteLine("   M " + Pretty(m.ReturnType) + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType) + " " + p.Name)) + ")");
        }
    }

    static string Mods(FieldInfo f)
    {
        var s = "";
        if (f.IsStatic) s += "static ";
        if (f.IsPublic) s += "public "; else if (f.IsPrivate) s += "private "; else s += "protected ";
        return s.Trim();
    }
    static string Pretty(Type t)
    {
        if (t == null) return "?";
        if (!t.IsGenericType) return t.Name;
        return t.Name.Substring(0, t.Name.IndexOf('`')) + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
    }

    static void DumpArgs(string mapPath)
    {
        var map = new Ra3Map(mapPath);
        map.parse();
        var psl = map.getAsset<PlayerScriptsList>(Ra3MapConst.ASSET_PlayerScriptsList);
        var sl = psl.scriptLists[0];
        var g = sl.scriptGroups[0];
        var s = g.scripts[0];
        var act = s.ScriptActionOnTrue[0];
        Console.WriteLine("group.Name=" + g.Name);
        Console.WriteLine("script.Name=" + s.Name);
        Console.WriteLine("action.type=" + act.GetType().FullName);
        Console.WriteLine("action.contentName=" + act.contentName + " nameIndex=" + act.nameIndex);
        Console.WriteLine("arguments.Count=" + act.arguments.Count);
        var a0 = act.arguments[0];
        Console.WriteLine("arg0.type=" + (a0 == null ? "null" : a0.GetType().FullName));
        if (a0 != null)
        {
            foreach (var f in a0.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object v = null; try { v = f.GetValue(a0); } catch (Exception e) { v = "<" + e.Message + ">"; }
                string sv = v is string ? ("\"" + ((string)v).Length + " chars: " + Trunc((string)v, 40) + "\"") : (v == null ? "null" : v.ToString());
                Console.WriteLine("   arg0." + f.FieldType.Name + " " + f.Name + " = " + sv);
                if (v != null && v.GetType().IsArray) Console.WriteLine("      isArray len=" + ((Array)v).Length);
            }
        }
        // 深入 ScriptContent
        var cond = s.scriptOrConditions[0].conditions[0];
        Console.WriteLine("cond.scriptContent.type=" + (cond.scriptContent == null ? "null" : cond.scriptContent.GetType().FullName));
        DumpObj("\ncondition.scriptContent", cond.scriptContent);
        // context string pool
        var ctx = map.getContext();
        Console.WriteLine("\ncontext.type=" + ctx.GetType().FullName);
        foreach (var f in ctx.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object v = null; try { v = f.GetValue(ctx); } catch (Exception e) { v = "<" + e.Message + ">"; }
            if (v is ICollection) Console.WriteLine("   ctx." + f.FieldType.Name + " " + f.Name + " = ICollection[" + ((ICollection)v).Count + "]");
            else Console.WriteLine("   ctx." + f.FieldType.Name + " " + f.Name + " = " + (v == null ? "null" : Trunc(v.ToString(), 60)));
        }
    }

    static void DumpObj(string label, object o)
    {
        if (o == null) { Console.WriteLine(label + " = null"); return; }
        Console.WriteLine(label + " (" + o.GetType().FullName + ")");
        foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            object v = null; try { v = f.GetValue(o); } catch (Exception e) { v = "<" + e.Message + ">"; }
            string sv = v is string ? ("\"" + ((string)v).Length + " chars: " + Trunc((string)v, 40) + "\"") : (v == null ? "null" : (v is ICollection ? "ICollection[" + ((ICollection)v).Count + "]" : v.ToString()));
            Console.WriteLine("   " + f.FieldType.Name + " " + f.Name + " = " + sv);
        }
    }

    static string Trunc(string s, int n) => s == null ? "null" : (s.Length <= n ? s.Replace("\r", "\\r").Replace("\n", "\\n") : s.Substring(0, n).Replace("\r", "\\r").Replace("\n", "\\n") + "...");
}
