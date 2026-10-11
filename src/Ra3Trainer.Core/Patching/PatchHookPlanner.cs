using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Patching;

public static class PatchHookPlanner
{
    public static IReadOnlyList<PatchHookPlan> CreatePlans(PatchManifest manifest)
    {
        return manifest.Hooks
            .Where(hook => hook.TrampolineTarget is not null)
            .Select(hook =>
            {
                var originalBytes = OriginalByteParser.Parse(hook.OriginalAssembly);
                if (originalBytes.Length < 5)
                {
                    // 这里原来写 Math.Max(5, ...)：凑成 5 之后 PatchLength 就永远 != OriginalBytes.Length，
                    // 而 PatchEngine.Install 正是校验这两个数相等 ⇒ 错误被推到安装时抛一句看不出原因的话。
                    // 5 字节是 jmp rel32 的最小落地空间，本来就不该"补齐"，直接点名是哪个 hook。
                    throw new InvalidOperationException(
                        $"Patch hook '{hook.Address}' 的 original_assembly 只解出 {originalBytes.Length} 字节，放不下 5 字节的 jmp（至少要 5 字节的完整指令边界）。");
                }

                return new PatchHookPlan(
                    hook.Address,
                    hook.TrampolineTarget!,
                    originalBytes.Length,
                    originalBytes)
                {
                    SectionTitle = hook.SectionTitle
                };
            })
            .ToArray();
    }
}
