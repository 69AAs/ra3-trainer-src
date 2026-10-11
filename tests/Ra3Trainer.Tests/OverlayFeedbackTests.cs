using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public class OverlayFeedbackTests
{
    private const string Session = "osd-session 000000000000007B\n";
    [Theory]
    [InlineData("nvspcap.dll")]
    [InlineData("EnhancerCorona.dll")]
    [InlineData("Lyi.dll")]
    public void Refusal_NamesActualSlotModule(string module)
    {
        var log = Session + $"osd-entry slot=Present address=123 reason=foreign-module module={module}\n"
            + "osd-entry slot=Reset address=456 reason=verified-system module=d3d9.dll\nosd-install-refused foreign-entry\nosd-install-refused retain-disabled\n";
        var text = OverlayFeedback.Parse(log, 123)!;
        Assert.Contains("本局未安装", text); Assert.Contains(module, text); Assert.Contains("Reset", text);
        Assert.DoesNotContain("已部署", text); Assert.DoesNotContain("已连接", text);
    }
    [Fact] public void StalePid_DoesNotSupplyCurrentState() => Assert.Null(OverlayFeedback.Parse(Session + "osd-installed\n", 124));
    [Fact] public void Unknown_DoesNotInventNvidia()
    {
        var text = OverlayFeedback.Parse(Session + "osd-install-refused foreign-entry\n", 123)!;
        Assert.Contains("未识别", text); Assert.DoesNotContain("nvspcap", text);
        // Guidance 常量从不进 Parse 的返回值，所以上面那句只说明"返回文本里没有"，没有证明力。
        // 真正要钉的是：没有条目时只能说"占槽模块未识别"，不得点名任何模块。
        Assert.Contains("占槽模块未识别", text); Assert.Contains("不是归因", text);
    }
    [Fact] public void SystemMismatch_DoesNotNameThirdPartyAsCulprit()
    {
        var text = OverlayFeedback.Parse(Session + "osd-entry slot=Present address=123 reason=system-entry-unverified module=d3d9.dll\nosd-install-refused foreign-entry\n", 123)!;
        Assert.Contains("修改者未识别", text); Assert.DoesNotContain("NVIDIA", text);
        // 只许复述日志里真实出现的模块名，不得把第三方覆盖层当替罪羊。
        Assert.Contains("Present：d3d9.dll", text); Assert.DoesNotContain("模块未识别", text);
    }
    [Fact] public void Refusal_CannotBeOverwrittenBySuccess()
        => Assert.Contains("本局未安装", OverlayFeedback.Parse(Session + "osd-install-refused foreign-entry\nosd-installed\nosd-frame-presented\n", 123));
    [Fact] public void NewSession_DropsOldRefusal()
        => Assert.DoesNotContain("本局未安装", OverlayFeedback.Parse("osd-session 000000000000007A\nosd-install-refused foreign-entry\n" + Session + "osd-installed\n", 123));
    [Fact] public void Installed_IsNotVisible()
        => Assert.Contains("尚未确认可见", OverlayFeedback.Parse(Session + "osd-installed\n", 123));
    [Fact] public void DrawFailure_OverridesPreviousFrame()
        => Assert.Contains("已停止绘制", OverlayFeedback.Parse(Session + "osd-frame-presented\nosd-draw-failed hr=1 disabled\n", 123));
    [Fact] public void Guidance_IsActionableAndHonest()
    {
        Assert.Contains("Highlights", OverlayFeedback.Guidance); Assert.Contains("全局", OverlayFeedback.Guidance);
        Assert.Contains("重启游戏", OverlayFeedback.Guidance); Assert.Contains("候选", OverlayFeedback.Guidance);
        Assert.Contains("不要删改", OverlayFeedback.Guidance);
    }
    [Fact] public void PartialLogRecord_IsNotEvidence()
        => Assert.Null(OverlayFeedback.Parse(Session + "osd-installed", 123));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void SessionMarkerBoundariesRoundTripThroughX16Formatting(long creationTime)
    {
        var marker = $"osd-session {creationTime:X16}\n";

        // 同一个值必须被认成当前会话（X16 往返），换个值就一条都不认。
        Assert.Contains("本局已安装", OverlayFeedback.Parse(marker + "osd-installed\n", creationTime));
        Assert.Null(OverlayFeedback.Parse(marker + "osd-installed\n", creationTime + 1));
    }

    [Fact] public void MalformedOrExtendedSessionMarkerIsNotEvidence()
    {
        Assert.Null(OverlayFeedback.Parse("osd-session ZZZZZZZZZZZZZZZZ\nosd-installed\n", 0));
        Assert.Null(OverlayFeedback.Parse("osd-session\nosd-installed\n", 123));
        Assert.Null(OverlayFeedback.Parse("osd-session 000000000000007B extra\nosd-installed\n", 123));
    }

    [Fact] public void RefusalAfterEarlierSuccessStillWins()
        => Assert.Contains("本局未安装",
            OverlayFeedback.Parse(Session + "osd-installed\nosd-frame-presented\nosd-install-refused foreign-entry\n", 123));

    [Fact] public void DrawFailureAfterInstallStopsDrawing()
    {
        var text = OverlayFeedback.Parse(Session + "osd-installed\nosd-draw-failed hr=1 disabled\n", 123);
        Assert.Contains("绘制／呈现失败", text); Assert.Contains("已停止绘制", text);
    }

    [Fact] public void PanelDrawnAfterAStepFailureClearsTheFailureText()
    {
        var text = OverlayFeedback.Parse(Session
            + "osd-panel-step name=LockTexture hr=80004005 reason=LockTexture\n"
            + "osd-panel-drawn\n", 123);
        Assert.Contains("收到绘制记录", text);
        Assert.DoesNotContain("面板纹理像素上传失败", text);
        Assert.DoesNotContain("已停止绘制", text);
    }
}
