namespace Ra3Trainer.Core.Runtime;

public interface IProcessSuspender
{
    /// <summary>
    /// 挂起目标进程；释放返回的 <see cref="IDisposable"/> 时恢复。
    /// **返回 null 表示"没有挂起"**（进程已退出 / 权限不足 / 实现选择跳过）：
    /// 调用方必须自己判断——拿不到句柄还继续改内存，就是在没挂起的进程上写代码。
    /// </summary>
    IDisposable? Suspend(int processId);
}
