using Ra3Trainer.Core.Runtime;
using Xunit;
namespace Ra3Trainer.Tests;
public sealed class Round33OverlayTests
{
    [Theory]
    [InlineData("verified-system", "允许进入安装")]
    [InlineData("patched-nonrelocation-byte", "非重定位字节不一致")]
    [InlineData("entry-branch", "目标模块不等于修改者")]
    [InlineData("image-mismatch", "映像身份或重定位值不一致")]
    [InlineData("unidentified", "格式不支持")]
    public void ExactClassificationIsDisplayed(string reason, string conclusion)
    {
        var entry = $"osd-entry slot=Reset target=0x12345678 targetModule=fixture.dll reason={reason} transfer=push-ret module=d3d9.dll\n";
        var text = OverlayFeedback.Parse("osd-session 0000000000000001\n" + entry
            + (reason == "verified-system" ? "osd-installed\n" : "osd-install-refused foreign-entry\n"), 1)!;
        Assert.NotNull(text);
        Assert.Contains(reason, text); Assert.Contains(conclusion, text);
        Assert.DoesNotContain("可能为重定位", text);
        if (reason == "entry-branch") Assert.Contains("fixture.dll", text);
        if (reason != "verified-system") Assert.Contains("本局未安装", text);
        // 分支互斥：非 verified 的原因绝不能同时给出"已安装"，verified 也必须不出现拒绝措辞。
        if (reason == "verified-system") Assert.DoesNotContain("本局未安装", text);
        else Assert.DoesNotContain("本局已安装", text);
    }
    [Theory]
    [InlineData("entry-changed-during-prepare", "安装准备期间入口发生变化，已失败关闭")]
    [InlineData("thread-window", "无法证明安装线程窗口安全")]
    [InlineData("Ex-presentation", "尚不支持 D3D9Ex 呈现")]
    [InlineData("allocation", "覆盖层内存分配失败")]
    [InlineData("creation-thread-unverified", "无法确认 CreateDevice 线程来源，已停止安装")]
    [InlineData("retain-disabled", "安装失败，具体原因未识别")]
    [InlineData("brand-new-code", "安装校验失败，诊断码：brand-new-code")]
    public void EveryRefusalReasonCodeMapsToItsExactChineseConclusion(string code, string conclusion)
    {
        var text = OverlayFeedback.Parse("osd-session 0000000000000001\nosd-install-refused " + code + "\n", 1);

        Assert.NotNull(text);
        Assert.Contains("本局未安装", text);
        Assert.Contains(conclusion, text);
        Assert.DoesNotContain("本局已安装", text);
    }

    [Fact]
    public void SuccessfulFrameKeepsVerificationAndPixelBoundary()
    {
        var text = OverlayFeedback.Parse("osd-session 0000000000000001\n"
            + "osd-entry slot=Present reason=verified-system module=d3d9.dll\n"
            + "osd-entry slot=Reset reason=verified-system module=d3d9.dll\n"
            + "osd-installed\nosd-frame-presented\n", 1)!;
        Assert.Contains("Present", text); Assert.Contains("Reset", text);
        Assert.Contains("verified-system", text); Assert.Contains("实际文字以游戏画面为准", text);
    }
}
