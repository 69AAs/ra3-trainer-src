namespace Ra3Trainer.Core.Runtime;

public enum LaunchWindowMode { Main, Floating }

/// <summary>Both launch buttons use this completion boundary, including exception/timeout paths.</summary>
/// <remarks>必须在 UI 线程串行调用（会直接回调 UI 改窗口模式）；不要并发重入同一次启动。</remarks>
public static class LaunchWindowCoordinator
{
    public static async Task RunAsync(Func<Task<bool>> launchAndAttachAndLoad,
        Func<bool> autoFloatEnabled, Action<LaunchWindowMode> requestWindowMode)
    {
        ArgumentNullException.ThrowIfNull(launchAndAttachAndLoad);
        ArgumentNullException.ThrowIfNull(autoFloatEnabled);
        ArgumentNullException.ThrowIfNull(requestWindowMode);

        requestWindowMode(LaunchWindowMode.Main);
        var success = false;
        try
        {
            success = await launchAndAttachAndLoad();
        }
        finally
        {
            // 改窗口模式是收尾动作：它自己抛（窗口已 Dispose、订阅者里抛）不许把
            // 启动/附加的原始异常顶掉——调用方只按原始异常报状态，顶掉就看不到真原因。
            try
            {
                requestWindowMode(success && autoFloatEnabled() ? LaunchWindowMode.Floating : LaunchWindowMode.Main);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 没有第二条上报通道：压住，优先保证原始失败/结果传出去。
            }
        }
    }
}
