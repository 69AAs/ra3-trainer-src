using System.Diagnostics;

namespace Ra3Trainer.Core.Runtime;

public interface ILauncher
{
    /// <summary>
    /// 启动启动器/游戏。**失败必须抛异常，不许返回 null**（调用方直接 using 这个返回值）。
    /// </summary>
    /// <remarks>
    /// 归属：返回的 <see cref="Process"/> 由调用方决定是否 Dispose。
    /// 注意实现用 <c>UseShellExecute = true</c> 时这个 Process **没有真实进程句柄**，
    /// 只能当"启动动作已发出"的信号，不要用 Kill/HasExited/Handle 去控制被启动的进程。
    /// </remarks>
    Process Start(string launcherPath, string arguments = "");
}
