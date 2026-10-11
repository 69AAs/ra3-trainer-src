using System.Diagnostics;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Runtime;

// 真机取证夹具（只读）：用**生产位数（x86）**验证「当前玩的是哪个 mod」的硬信号真的取得回来。
// 用法： dotnet run --project tools/ModIdentityProbe            （自动找 ra3_1.12.game）
//        dotnet run --project tools/ModIdentityProbe -- <pid>
// 它不做任何写操作、不启动游戏。

var pid = args.Length > 0 && int.TryParse(args[0], out var explicitPid) ? explicitPid : FindGameProcess();
if (pid == 0)
{
    Console.WriteLine("没找到 ra3_1.12.game（游戏没在跑）。");
    return 2;
}

Console.WriteLine($"process bitness : {(Environment.Is64BitProcess ? "x64" : "x86")}");
Console.WriteLine($"target pid      : {pid}");

var commandLine = Win32ProcessCommandLine.TryRead(pid);
Console.WriteLine("command line    : " + (commandLine ?? "<读不到>"));

var snapshot = TargetModSignalProbe.Capture(null, pid);
var modules = snapshot.TargetModulePaths ?? Array.Empty<string>();
Console.WriteLine($"module count    : {modules.Count}");
foreach (var module in modules)
{
    if (module.Contains("corona", StringComparison.OrdinalIgnoreCase)
        || module.Contains("lyi", StringComparison.OrdinalIgnoreCase)
        || module.Contains("luabridge", StringComparison.OrdinalIgnoreCase)
        || module.Contains("enhancer", StringComparison.OrdinalIgnoreCase)
        || module.Contains("wic", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("   mod module  : " + module);
    }
}

var identity = ModIdentifier.Identify(snapshot);
Console.WriteLine($"identity        : {identity.DisplayText}");
Console.WriteLine($"evidence        : {identity.EvidenceText}");
Console.WriteLine($"mod key         : {identity.ModKey} (known={identity.IsKnown}, vanilla={identity.IsVanilla})");
return 0;

static int FindGameProcess()
{
    foreach (var process in Process.GetProcesses())
    {
        try
        {
            var name = process.MainModule?.ModuleName ?? string.Empty;
            if (name.Equals("ra3_1.12.game", StringComparison.OrdinalIgnoreCase))
            {
                return process.Id;
            }
        }
        catch
        {
            // 系统进程拒绝访问，跳过。
        }
        finally
        {
            process.Dispose();
        }
    }

    return 0;
}
