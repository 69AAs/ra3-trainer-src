using System.IO.Compression;
using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class HookEntryAuditTests
{
    [Theory]
    [InlineData("EB039090908B40048B8EB0030000", 5, 9)]
    [InlineData("74039090908B40048B8EB0030000", 5, 9)]
    [InlineData("E8030000008B40048B8EB0030000", 5, 9)]
    [InlineData("0F840200000090908B40048B8EB0030000", 6, 9)]
    [InlineData("E2039090908B40048B8EB0030000", 5, 9)]
    public void InternalDirectEntryFails(string hex, int offset, int length)
    {
        var bytes = Convert.FromHexString(hex);
        // Targets are strictly inside the patch, including conditional calls and loop branches.
        Assert.Throws<InvalidOperationException>(() => HookEntryAudit.Verify(bytes, 0x1000, (uint)(0x1000 + offset - 1), length));
    }

    [Theory]
    [InlineData("EB038B40048B8EB0030000", 5, 6)]
    [InlineData("EB068B8EB003000090", 2, 6)]
    [InlineData("90908B8EB0030000", 2, 6)]
    [InlineData("8D0476C1E004909090", 0, 3)]
    public void CleanRegionAndBoundaryTargetsPass(string hex, int offset, int length) =>
        HookEntryAudit.Verify(Convert.FromHexString(hex), 0x1000, (uint)(0x1000 + offset), length);

    [Fact]
    public void EveryProductionHookMustPassDumpImageGate()
    {
        using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "round15-text.bin.gz"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var code = output.ToArray();
        var plans = PatchHookPlanner.CreatePlans(TestAssets.LoadManifest().PatchManifest);
        Assert.Equal(22, plans.Count);
        foreach (var plan in plans)
        {
            // 用生产端的解析（LastIndexOf('+') + int.Parse），不自己 Split：
            // 测试版解析一旦和生产版走岔，这里就在验错字节。
            var rva = PatchHookImageVerifier.ParseRva(plan.Address);
            // A new/changed hook outside this evidence or with unknown original bytes fails closed.
            Assert.InRange(rva, 0x1000, 0x1000 + code.Length - plan.PatchLength);
            Assert.Equal(plan.OriginalBytes, code.AsSpan(rva - 0x1000, plan.PatchLength).ToArray());
            HookEntryAudit.Verify(code, 0x401000, 0x400000 + (uint)rva, plan.PatchLength);
        }

        // 这两条"内部入口"负向用例的地址只对 round15 这份取证有意义。原来只断言 Throws：
        // 清单一改、地址失效，Throws 会因为别的原因成立，测试就变成"假通过"。
        // 这里先把解码器在这两个区间里实际找到的入口逐条钉住（原文与目标地址），再断言 Verify 会抛。
        var internalEntryCases = new[]
        {
            (Site: 0x6CFD0D, Length: 9, Expected: new[]
            {
                new InternalHookEntry(0xACFD0B, 0xACFD10, "jmp short 00ACFD10h")
            }),
            (Site: 0x6CFE6C, Length: 6, Expected: new[]
            {
                new InternalHookEntry(0xACFE5C, 0xACFE6F, "jmp short 00ACFE6Fh"),
                new InternalHookEntry(0xACFE6A, 0xACFE6F, "jmp short 00ACFE6Fh")
            })
        };
        foreach (var (siteRva, length, expected) in internalEntryCases)
        {
            Assert.InRange(0x1000 + siteRva, 0x1000, 0x1000 + code.Length - length);
            var site = 0x400000u + (uint)siteRva;
            Assert.Equal(expected, HookEntryAudit.Find(code, 0x401000, site, length));
            Assert.Throws<InvalidOperationException>(() => HookEntryAudit.Verify(code, 0x401000, site, length));
        }
    }

    [Fact]
    public void AuditedRegionMustLieInsideTheDecodedCode()
    {
        // 区间不在被审代码里时不能"什么都没找到就通过"：那是没解码过，不是没有入口。
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HookEntryAudit.Verify(Convert.FromHexString("9090"), 0x1000, 0x0800, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HookEntryAudit.Verify(Convert.FromHexString("9090"), 0x1000, 0x1001, 4));
    }
}
