namespace Ra3Trainer.Core.Patching;

public sealed class PatchInstallException : InvalidOperationException
{
    public PatchInstallException(
        PatchHookPlan hook,
        int hookIndex,
        int hookCount,
        nint absoluteAddress,
        Exception innerException)
        : base(CreateMessage(hook, hookIndex, hookCount, absoluteAddress, innerException), innerException)
    {
        Hook = hook;
        HookIndex = hookIndex;
        HookCount = hookCount;
        AbsoluteAddress = absoluteAddress;
    }

    public PatchHookPlan Hook { get; }

    public int HookIndex { get; }

    public int HookCount { get; }

    public nint AbsoluteAddress { get; }

    private static string CreateMessage(
        PatchHookPlan? hook,
        int hookIndex,
        int hookCount,
        nint absoluteAddress,
        Exception? innerException)
    {
        // 这里是异常对象自己拼消息的地方（跑在基类构造参数里）：hook/inner 万一为 null，
        // 原来会在这里抛 NRE，把真正的安装失败盖掉。
        var section = string.IsNullOrWhiteSpace(hook?.SectionTitle)
            ? string.Empty
            : $" section '{hook.SectionTitle}',";
        return $"Failed to install hook {hookIndex}/{hookCount}{section} " +
            $"at {(hook?.Address ?? "(unknown)")} (absolute 0x{absoluteAddress:X}). {innerException?.Message ?? "(no inner exception)"}";
    }
}
