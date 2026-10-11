using System.Diagnostics;

namespace Ra3Trainer.Core.Runtime;

public sealed class GameLauncher : ILauncher
{
    /// <summary>UseShellExecute=true ⇒ 返回的 Process 句柄不可用于控制被启动进程（见接口契约）。</summary>
    public Process Start(string launcherPath, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
        {
            throw new ArgumentException("Launcher path is required.", nameof(launcherPath));
        }

        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException("Launcher file was not found.", launcherPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            Arguments = arguments,
            // GetDirectoryName("game.exe") 返回空串（不是 null）：?? 兜不住，
            // 工作目录会变成空 → 游戏按错误的相对路径找资源。
            WorkingDirectory = Path.GetDirectoryName(launcherPath) is { Length: > 0 } directory
                ? directory
                : Environment.CurrentDirectory,
            UseShellExecute = true
        };

        // 失败不止"返回 null"一种：拒绝访问 / 不是有效可执行文件 / 平台不支持都会抛。
        // 包成一个能看懂的句子，别让界面只显示一句 generic 报错。
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start launcher process.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or PlatformNotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException($"启动 {Path.GetFileName(launcherPath)} 失败：{ex.Message}", ex);
        }
    }
}
