using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ra3Trainer.Core.Features;

namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// 采集「当前玩的是哪个 mod」需要的**硬信号**：跑着的游戏进程的已加载模块 + 命令行。
///
/// <para>为什么可行：修改器发布的是 <b>x86</b>（游戏也是 x86），同一个位数下
/// <see cref="Process.Modules"/> 能列出目标进程**完整的 32 位模块表**（实测：日冕的
/// <c>D:\Games\CoronaLauncher\tools\EnhancerCorona.dll</c>、<c>...\CoronaLauncher\bin\Lyi.dll</c>
/// 都在里面）。用 64 位进程去列 WOW64 目标只会看到 ntdll/wow64* 几条 —— 位数不对就拿不到真模块表。</para>
///
/// <para>任何一步失败都退化成空信号（绝不抛）：拿不到硬信号就落回启动器配置/default，保持宽容。</para>
/// </summary>
public static class TargetModSignalProbe
{
    /// <summary>取一次信号快照；进程没跑/拿不到就只带回启动器路径。</summary>
    public static ModSignalSnapshot Capture(string? launcherPath, int? processId)
    {
        if (processId is not int pid)
        {
            return new ModSignalSnapshot(launcherPath, null, Array.Empty<string>());
        }

        return new ModSignalSnapshot(launcherPath, Win32ProcessCommandLine.TryRead(pid), TryReadModulePaths(pid));
    }

    /// <summary>目标进程已加载模块的完整路径；读不到就是空表。</summary>
    public static IReadOnlyList<string> TryReadModulePaths(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var paths = new List<string>();
            foreach (ProcessModule module in process.Modules)
            {
                try
                {
                    var file = module.FileName;
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        paths.Add(file);
                    }
                }
                catch
                {
                    // 个别模块拒绝查询文件名；跳过它，别让整张表作废。
                }
            }

            return paths;
        }
        catch
        {
            // 进程已退出 / 位数不匹配 / 无权限 —— 当作拿不到。
            return Array.Empty<string>();
        }
    }
}
