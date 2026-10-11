using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class OverlayPanelFeedbackTests
{
    private const string Session = "osd-session 0000000000000025\n";

    [Theory]
    [InlineData("mapping-missing", "映射缺失")]
    [InlineData("mapping-size", "容量不符")]
    [InlineData("abi-mismatch", "ABI")]
    [InlineData("identity-mismatch", "身份")]
    [InlineData("heartbeat-expired", "心跳过期")]
    [InlineData("disabled", "关闭面板")]
    [InlineData("bounds-invalid", "越界")]
    [InlineData("format-unsupported", "格式不支持")]
    [InlineData("reserved-nonzero", "预留字段")]
    [InlineData("snapshot-busy", "本帧不画")]
    [InlineData("snapshot-torn", "本帧不画")]
    [InlineData("checksum-mismatch", "校验和")]
    [InlineData("generation-mismatch", "脏标记")]
    [InlineData("process-identity", "进程身份")]
    [InlineData("TargetFormat", "游戏后缓冲格式")]
    [InlineData("TargetBounds", "尺寸无法容纳")]
    [InlineData("TextureCaps", "纹理尺寸能力")]
    [InlineData("CheckDeviceMultiSampleType", "MSAA")]
    [InlineData("CreateTexture", "纹理创建失败")]
    [InlineData("LockTexture", "上传失败")]
    [InlineData("UnlockTexture", "上传失败")]
    [InlineData("TexturePitch", "上传失败")]
    [InlineData("ApplyStateBlock", "状态恢复失败")]
    public void MappingFailure_IsChineseAndPreservesReason(string reason, string chinese)
    {
        var text = OverlayFeedback.Parse(Session + $"osd-panel-step name=ReadMapping hr=00000001 reason={reason}\n", 37);
        Assert.Contains(chinese, text);
        Assert.Contains(reason, text);
        Assert.Contains("一键全撤", text);
    }

    [Fact]
    public void DrawFailure_PreservesNamedStepAfterGenericFailure()
    {
        var text = OverlayFeedback.Parse(Session + "osd-panel-step name=Stopped hr=80004005 reason=LockTexture\nosd-draw-failed hr=80004005 disabled\n", 37);
        Assert.Contains("游戏内面板", text);
        Assert.Contains("LockTexture", text);
        Assert.Contains("80004005", text);
    }

    [Fact]
    public void Recovery_IsVisibleButNeverClaimsGamePixelOrInputAcceptance()
    {
        var log = Session + "osd-panel-step name=ReadMapping hr=00000001 reason=mapping-missing\nosd-panel-drawn\n";
        Assert.Contains("真机像素待目视", OverlayFeedback.Parse(log, 37));
        Assert.DoesNotContain("映射缺失", OverlayFeedback.Parse(log, 37));
        Assert.Contains("显示成功不等于能用", OverlayFeedback.Parse(log + "osd-frame-presented\n", 37));
        Assert.Contains("本局未安装", OverlayFeedback.Parse(Session + "osd-install-refused foreign-entry\nosd-panel-drawn\nosd-frame-presented\n", 37));
    }

    [Fact]
    public void Relay_PreservesPanelStepsAndRecoversFromFailedSink()
    {
        string[] lines = ["osd-panel-step name=ReadMapping hr=00000000", "osd-panel-desc w=360 h=440 fmt=Pbgra32 pos=24,24",
            "osd-panel-uploaded bytes=1048576 payloadBytes=633600", "osd-panel-drawn"];
        var log = Session + string.Join('\n', lines) + "\n";
        var relay = new OverlayEntryRelay();
        var received = new List<string>();
        relay.Forward(log, 38, (_, line) => received.Add(line));
        Assert.Empty(received);
        relay.Forward(log, 37, (_, _) => throw new IOException("fixture sink failure"));
        relay.Forward(log, 37, (_, line) => received.Add(line));
        relay.Forward(log, 37, (_, line) => received.Add(line));
        Assert.Equal(lines, received);
        Assert.Null(OverlayFeedback.Parse(Session + "osd-panel-drawn", 37));
    }

    [Fact]
    public void Relay_RetriesThrottledDrawLinesAcrossFailedPollsInsteadOfDroppingThem()
    {
        // 同一批 draw 行连续 >DrawVerboseHead(5) 次投递失败后再成功：失败的尝试不许计入逐事件节流配额，
        // 否则 seen 会爬到 >5 且非 200 的倍数，这条从未送达的行就被永久节流、静默丢失。
        var lines = new[] { "osd-panel-drawn", "osd-draw-present" };
        var log = Session + string.Join('\n', lines) + "\n";
        var relay = new OverlayEntryRelay();
        var received = new List<string>();
        for (var poll = 0; poll < 6; poll++)
            relay.Forward(log, 37, (_, _) => throw new IOException("fixture sink failure"));
        Assert.Empty(received);
        relay.Forward(log, 37, (_, line) => received.Add(line));
        Assert.Equal(lines, received);       // 恢复后每条恰好投递一次
        relay.Forward(log, 37, (_, line) => received.Add(line));
        Assert.Equal(lines, received);       // 成功后进入去重集合，不再重复
    }

    [Fact]
    public void Relay_ForwardsTheNewSessionsPanelHeadEvenAfterAFullOldSession()
    {
        var relay = new OverlayEntryRelay();
        var first = new List<string>();
        // 旧会话把 osd-panel-step 的节流头（5 条）用光。
        for (var i = 0; i < 8; i++)
            relay.Forward(Session + $"osd-panel-step name=Old{i} hr=00000000 reason=disabled\n", 37, (_, line) => first.Add(line));
        Assert.Equal(5, first.Count);

        // 新会话 id：节流计数必须一起复位，新会话的头几条面板诊断不能被旧计数吃掉。
        var second = new List<string>();
        for (var i = 0; i < 2; i++)
            relay.Forward("osd-session 0000000000000026\nosd-panel-step name=New" + i + " hr=00000000 reason=disabled\n",
                38, (_, line) => second.Add(line));
        Assert.Equal(2, second.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("osd-session 0000000000000025")]              // 标记就是未收尾的最后一行
    [InlineData("osd-session 0000000000000025\n")]            // 只有标记
    [InlineData("osd-panel-step name=ReadMapping hr=00000001 reason=mapping-missing\n")]
    public void BoundaryLogsWithoutACurrentCompleteRecordAreNotEvidence(string log)
    {
        Assert.Null(OverlayFeedback.Parse(log, 37));
    }

    [Fact]
    public void RepeatedInvalidPanelStepIsIgnoredAndDoesNotBecomeEvidence()
    {
        // 同一行重复出现：仍然只按最后一次算，且必须给出同一条中文结论（不因重复而"升级"成证据）。
        var log = Session
            + "osd-panel-step name=ReadMapping hr=00000001 reason=mapping-missing\n"
            + "osd-panel-step name=ReadMapping hr=00000001 reason=mapping-missing\n";
        var text = OverlayFeedback.Parse(log, 37);

        Assert.NotNull(text);
        Assert.Contains("映射缺失", text);
        Assert.Contains("已停止绘制", text);
    }
}
