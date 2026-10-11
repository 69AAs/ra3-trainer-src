using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class OverlayDrawDiagnosticsTests
{
    private const string Session = "osd-session 0000000000000001\n";

    [Theory]
    [InlineData("osd-draw-step name=DrawPrimitiveUP hr=00000000")]
    [InlineData("osd-draw-desc name=target Format=22 Pool=0 MultiSampleType=4")]
    [InlineData("osd-draw-present BackBufferFormat=22 MultiSampleType=4")]
    [InlineData("osd-draw-msaa type=4 hr=00000000 qualityLevels=1")]
    [InlineData("osd-draw-legacy-restrictions multisample=1 UpdateSurface=not-called")]
    [InlineData("osd-draw-failure-detail name=LockTexture hr=80004005")]
    [InlineData("osd-draw-failed hr=80004005 disabled")]
    public void DrawRecords_AreSessionScopedVerbatimDeduplicatedAndRetryOnSinkFailure(string record)
    {
        var relay = new OverlayEntryRelay();
        var rows = new List<string>();
        // 会话作用域：本次会话是 0002，记录却挂在旧会话 0001 段里 ⇒ 一条都不能转发。
        // （原来传的日志里根本没有 0002 标记，FindLastIndex 直接返回 -1，断言因此是空转。）
        relay.Forward(Session + record + "\nosd-session 0000000000000002\n", 2, (_, text) => rows.Add(text));
        relay.Forward(Session + record, 1, (_, text) => rows.Add(text));
        Assert.Empty(rows);
        relay.Forward(Session + record + "\n", 1, (_, _) => throw new IOException("fixture"));
        relay.Forward(Session + record + "\n", 1, (name, text) => { Assert.Equal(record.Split(' ')[0], name); rows.Add(text); });
        relay.Forward(Session + record + "\n", 1, (_, text) => rows.Add(text));
        Assert.Equal(record, Assert.Single(rows));
    }

    [Fact]
    public void DrawThrottleHeadIsPerSessionNotPerRelayInstance()
    {
        var relay = new OverlayEntryRelay();
        var rows = new List<string>();
        for (var i = 0; i < 6; i++)
            relay.Forward(Session + $"osd-draw-step name=Draw{i} hr=00000000\n", 1, (_, text) => rows.Add(text));
        Assert.Equal(5, rows.Count); // 头 DrawVerboseHead=5 条照发，第 6 条起节流

        // 换会话（creationTime 变）时节流计数必须一起清零：
        // 否则复用同一个 relay 时，新会话的第一帧绘制诊断会被旧计数静默丢掉。
        var next = new List<string>();
        relay.Forward("osd-session 0000000000000002\nosd-draw-step name=Next hr=00000000\n", 2, (_, text) => next.Add(text));
        Assert.Equal("osd-draw-step name=Next hr=00000000", Assert.Single(next));
    }

    [Fact]
    public void Failure_ShowsNamedStepWithoutInventingMsaaCause()
    {
        var log = Session + "osd-installed\nosd-draw-failure-detail name=DrawPrimitiveUP hr=8876086c\nosd-draw-failed hr=8876086c disabled\n";
        var feedback = OverlayFeedback.Parse(log, 1);
        Assert.Contains("失败步骤：name=DrawPrimitiveUP hr=8876086c", feedback);
        Assert.Contains("已停止绘制", feedback);
        Assert.DoesNotContain("多重采样", feedback);
        Assert.Null(OverlayFeedback.Parse(log, 2));
    }

    [Fact]
    public void SuccessfulDrawStep_DoesNotClaimPresentationOrPixels()
    {
        var log = Session + "osd-installed\nosd-draw-step name=DrawPrimitiveUP hr=00000000\nosd-draw-step name=ApplyStateBlock hr=00000000\n";
        Assert.Contains("尚未确认可见", OverlayFeedback.Parse(log, 1));
    }
}
