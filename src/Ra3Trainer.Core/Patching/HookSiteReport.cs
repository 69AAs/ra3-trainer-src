namespace Ra3Trainer.Core.Patching;

/// <summary>What a hook site currently contains, judged against the manifest — never against
/// knowledge of a particular mod.</summary>
public enum HookSiteState
{
    /// <summary>Untouched: the bytes recorded in the manifest are still there (safe to install).</summary>
    Original,

    /// <summary>Already carries the jump this tool writes (another instance installed it, or it is still installed).</summary>
    PatchedByUs,

    /// <summary>Something else is there: another tool/mod patched this code, or the binary is not the
    /// build the manifest was derived from.</summary>
    Unexpected
}

public sealed record HookSiteSnapshot(
    string Address,
    string SectionTitle,
    nint AbsoluteAddress,
    HookSiteState State,
    byte[] ExpectedBytes,
    byte[] ActualBytes)
{
    // 这份快照就是"这里原本是什么"的证据：数组按引用存的话，持有 plan.OriginalBytes 或
    // 读缓冲的人改一个字节，报告里的证据就跟着变（PatchHookPlan 用的是同一套存取副本约定）。
    private readonly byte[] _expectedBytes = (byte[])(ExpectedBytes ?? []).Clone();
    private readonly byte[] _actualBytes = (byte[])(ActualBytes ?? []).Clone();

    public byte[] ExpectedBytes
    {
        get => (byte[])_expectedBytes.Clone();
        init => _expectedBytes = (byte[])(value ?? []).Clone();
    }

    public byte[] ActualBytes
    {
        get => (byte[])_actualBytes.Clone();
        init => _actualBytes = (byte[])(value ?? []).Clone();
    }
}

/// <summary>
/// Result of inspecting every hook site of the manifest. This is the pre-flight check: it tells the
/// user whether the 22 code sites still hold the bytes the patch manifest was built from, which is
/// the only thing that makes writing a jump there safe.
/// </summary>
public sealed record HookSiteReport(IReadOnlyList<HookSiteSnapshot> Sites)
{
    // 这些计数会被 WPF 绑定反复读，原来每次都全表扫一遍；构造时算一次。
    private readonly int _total = (Sites ?? []).Count;
    private readonly int _originalCount = (Sites ?? []).Count(site => site.State == HookSiteState.Original);
    private readonly int _patchedByUsCount = (Sites ?? []).Count(site => site.State == HookSiteState.PatchedByUs);
    private readonly int _unexpectedCount = (Sites ?? []).Count(site => site.State == HookSiteState.Unexpected);

    public int Total => _total;

    public int OriginalCount => _originalCount;

    public int PatchedByUsCount => _patchedByUsCount;

    public int UnexpectedCount => _unexpectedCount;

    /// <summary>Every single site is foreign: the target binary almost certainly is not the manifest's build.</summary>
    public bool AllUnexpected => Total > 0 && UnexpectedCount == Total;

    public bool IsCleanForInstall => Total > 0 && OriginalCount == Total;

    public string Summary =>
        $"hook 点：{OriginalCount}/{Total} 与清单一致，{PatchedByUsCount} 已是我们装的，{UnexpectedCount} 有陌生改动";

    public IReadOnlyList<string> UnexpectedAddresses =>
        (Sites ?? []).Where(site => site.State == HookSiteState.Unexpected)
            .Select(site => site.Address)
            .ToArray();
}
