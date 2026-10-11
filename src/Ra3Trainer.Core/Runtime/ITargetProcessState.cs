using System.Diagnostics;

namespace Ra3Trainer.Core.Runtime;

public interface ITargetProcessState
{
    bool IsRunning(int? processId);
}

public sealed class TargetProcessState : ITargetProcessState
{
    public static TargetProcessState Instance { get; } = new();

    private TargetProcessState()
    {
    }

    // PID 会被系统回收：GetProcessById 只保证"当前有某个进程叫这个号"，
    // 不能保证它还是我们当初挂上的那个游戏。记下第一次看到的启动时间，之后对不上就当它已退出
    // （否则游戏早关了、号被别人用了，界面还显示"已连接"、钩子也不撤）。
    private readonly Dictionary<int, long> _seenStarts = new();

    public bool IsRunning(int? processId)
    {
        if (processId is null)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            // 只读一次 HasExited：属性每次都重新查询，多次读可能得到不一致的结果。
            if (process.HasExited)
            {
                return false;
            }

            var start = process.StartTime.ToFileTimeUtc();
            lock (_seenStarts)
            {
                if (_seenStarts.TryGetValue(processId.Value, out var known))
                {
                    return known == start;
                }

                _seenStarts[processId.Value] = start;
                return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            // 探活必须只回答"在不在"：受保护/跨会话进程查 StartTime/句柄会抛 Win32Exception、
            // 平台不支持会抛 NotSupportedException，逃出去就是把 UI 的布尔判断变成崩溃。
            return false;
        }
    }
}
