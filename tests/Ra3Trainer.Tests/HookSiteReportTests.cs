using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Patching;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// The hook-site check is what makes writing a jump into the game safe: it compares the live bytes
/// against the patch manifest, with no knowledge of which mod is installed.
/// </summary>
public sealed class HookSiteReportTests
{
    [Fact]
    public void InspectReportsAllSitesCleanWhenTheBinaryStillMatchesTheManifest()
    {
        var (session, memory, plans) = CreateAttachedSession();
        SeedOriginalBytes(session, memory, plans);

        var report = session.InspectHookSites();

        Assert.Equal(plans.Count, report.Total);
        Assert.Equal(plans.Count, report.OriginalCount);
        Assert.Equal(0, report.UnexpectedCount);
        Assert.True(report.IsCleanForInstall);
        Assert.False(report.AllUnexpected);
        Assert.Contains("与清单一致", report.Summary);
    }

    [Fact]
    public void InspectFlagsForeignBytesAsUnexpected()
    {
        var (session, memory, plans) = CreateAttachedSession();
        SeedOriginalBytes(session, memory, plans);
        Assert.True(plans.Count > 3, "清单至少要有 4 个 hook，plans[3] 才有意义");
        var tampered = plans[3];
        memory.WriteBytes(
            session.Resolver!.Resolve(tampered.Address),
            Enumerable.Repeat((byte)0xCC, tampered.OriginalBytes.Length).ToArray());

        var report = session.InspectHookSites();

        Assert.Equal(1, report.UnexpectedCount);
        Assert.Contains(tampered.Address, report.UnexpectedAddresses);
        Assert.False(report.IsCleanForInstall);
    }

    [Fact]
    public void InspectRecognisesTheToolsOwnJumpAsOursNotAsForeign()
    {
        var (session, memory, plans) = CreateAttachedSession();
        foreach (var plan in plans)
        {
            // 前置条件：InspectHookSites 读的是 OriginalBytes.Length 字节、比的却是 PatchLength 长的跳转，
            // 只有 OriginalBytes 不短于跳转长度时"我们自己装的"才判得出来。清单若混进更短的 hook，这里先报。
            Assert.True(plan.OriginalBytes.Length >= plan.PatchLength,
                $"{plan.Address}: OriginalBytes({plan.OriginalBytes.Length}) 短于 PatchLength({plan.PatchLength})");
            var address = session.Resolver!.Resolve(plan.Address);
            var patch = X86PatchEncoder.EncodeNearJumpWithNops(
                address,
                session.Resolver.Resolve(plan.Target),
                plan.PatchLength);
            memory.WriteBytes(address, patch);
        }

        var report = session.InspectHookSites();

        Assert.Equal(plans.Count, report.PatchedByUsCount);
        Assert.Equal(0, report.UnexpectedCount);
        Assert.False(report.AllUnexpected);
    }

    [Fact]
    public void InspectReportsEverySiteAsForeignWhenTheBinaryIsSomethingElse()
    {
        var (session, memory, plans) = CreateAttachedSession();
        foreach (var plan in plans)
        {
            memory.WriteBytes(
                session.Resolver!.Resolve(plan.Address),
                Enumerable.Repeat((byte)0x90, plan.OriginalBytes.Length).ToArray());
        }

        var report = session.InspectHookSites();

        Assert.True(report.AllUnexpected);
        Assert.False(report.IsCleanForInstall);
        Assert.Equal(plans.Count, report.UnexpectedAddresses.Count);
    }

    [Fact]
    public void InspectReportsMixedPatchedForeignAndOriginalSitesWithDistinctCounts()
    {
        var (session, memory, plans) = CreateAttachedSession();
        SeedOriginalBytes(session, memory, plans);
        var ours = plans[0];
        var foreign = plans[1];
        var oursAddress = session.Resolver!.Resolve(ours.Address);
        memory.WriteBytes(oursAddress, X86PatchEncoder.EncodeNearJumpWithNops(
            oursAddress, session.Resolver.Resolve(ours.Target), ours.PatchLength));
        memory.WriteBytes(session.Resolver.Resolve(foreign.Address),
            Enumerable.Repeat((byte)0xCC, foreign.OriginalBytes.Length).ToArray());

        var report = session.InspectHookSites();

        // 部分改动的真实状态：三个计数必须彼此可区分，且两个"干净"判据都为 false。
        Assert.Equal(plans.Count, report.Total);
        Assert.Equal(1, report.PatchedByUsCount);
        Assert.Equal(1, report.UnexpectedCount);
        Assert.Equal(plans.Count - 2, report.OriginalCount);
        Assert.False(report.IsCleanForInstall);
        Assert.False(report.AllUnexpected);
        Assert.Equal(new[] { foreign.Address }, report.UnexpectedAddresses);
        Assert.Contains($"{plans.Count - 2}/{plans.Count} 与清单一致，1 已是我们装的，1 有陌生改动", report.Summary);
    }

    [Fact]
    public void InspectBeforeAttachThrows()
    {
        var session = new TrainerSession(
            TestAssets.LoadManifest(),
            new FakeProcessMemory(),
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true));

        Assert.Throws<InvalidOperationException>(() => session.InspectHookSites());
    }

    private static (TrainerSession Session, FakeProcessMemory Memory, IReadOnlyList<PatchHookPlan> Plans) CreateAttachedSession()
    {
        var manifest = TestAssets.LoadManifest();
        var memory = new FakeProcessMemory();
        var session = new TrainerSession(
            manifest,
            memory,
            new TrainerTarget("ra3_1.12.game", 0x400000, true, true),
            new SequentialRemoteAllocator(0x700000));

        Assert.True(session.Attach().Success);
        // 先断言 Resolver 存在：否则后续 session.Resolver! 失败会以 NRE 出现，分不清是夹具问题还是真回归。
        Assert.NotNull(session.Resolver);
        return (session, memory, PatchHookPlanner.CreatePlans(manifest.PatchManifest));
    }

    private static void SeedOriginalBytes(
        TrainerSession session,
        FakeProcessMemory memory,
        IReadOnlyList<PatchHookPlan> plans)
    {
        foreach (var plan in plans)
        {
            memory.WriteBytes(session.Resolver!.Resolve(plan.Address), plan.OriginalBytes);
        }
    }
}
