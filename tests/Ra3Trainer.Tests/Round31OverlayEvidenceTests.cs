using Ra3Trainer.Core.Runtime;
using Xunit;
namespace Ra3Trainer.Tests;
public class Round31OverlayEvidenceTests
{
    [Theory]
    [InlineData("nvspcap.dll")]
    [InlineData("EnhancerCorona.dll")]
    // 不用 "unidentified"：那是 Field("targetModule") 的回退值，断言会被回退值满足（假通过）。
    [InlineData("mystery-capture.dll")]
    public void TransferIsDestinationEvidenceNotWriter(string target)
    {
        var text=OverlayFeedback.Parse("osd-session 000000000000007B\nosd-entry slot=Reset address=1 reason=entry-transfer-unverified transfer=jmp-rel32 target=12345678 targetModule="+target+" module=d3d9.dll\nosd-install-refused foreign-entry\n",123)!;
        Assert.Contains(target,text); Assert.Contains("目标模块不等于修改者",text); Assert.Contains("本局未安装",text);
    }
    [Fact] public void NonTransferDoesNotCertifySystemMismatch()
    {
        var text=OverlayFeedback.Parse("osd-session 000000000000007B\nosd-entry slot=Reset address=1 reason=relocation-or-image-mismatch module=d3d9.dll\nosd-install-refused foreign-entry\n",123)!;
        Assert.Contains("尚未证实系统差异",text); Assert.Contains("仍拒装",text);
        // 非 transfer 原因绝不能走 transfer 分支：既不能宣称"目标模块不等于修改者"，也不能带出 transfer= 目标信息。
        Assert.DoesNotContain("目标模块不等于修改者",text);
        Assert.DoesNotContain("entry-transfer-unverified",text);
    }
}
