using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class OverlayEntryRelayTests
{
    private const string Entry = "osd-entry slot=Reset bytes=E9000000009090909090909090909090 target=0x10000005 targetModule=unidentified reason=entry-transfer-unverified module=d3d9.dll";

    [Fact]
    public void Forward_PreservesBytesAndDeduplicatesCompleteCurrentSessionRecords()
    {
        var relay = new OverlayEntryRelay();
        var rows = new List<string>();
        var log = "osd-session 0000000000000001\n" + Entry + "\n";
        relay.Forward(log, 2, (_, value) => rows.Add(value));
        Assert.Empty(rows);
        relay.Forward(log[..^1], 1, (_, value) => rows.Add(value));
        Assert.Empty(rows);
        relay.Forward(log, 1, (name, value) => { Assert.Equal("osd-entry", name); rows.Add(value); });
        relay.Forward(log, 1, (_, value) => rows.Add(value));
        Assert.Equal(Entry, Assert.Single(rows));
    }

    [Fact]
    public void Forward_ResetsDrawThrottleAcrossSessions()
    {
        var relay = new OverlayEntryRelay();
        var first = new List<string>();
        // 内容各不相同，避免 _sent 去重把节流路径遮住。
        for (var i = 0; i < 8; i++)
            relay.Forward("osd-session 0000000000000001\nosd-draw-step name=Old" + i + " hr=00000000\n",
                1, (_, text) => first.Add(text));
        Assert.Equal(5, first.Count); // 头 5 条照发

        // 换会话：节流计数必须清零，新会话的头两条都要能出去。
        var second = new List<string>();
        for (var i = 0; i < 2; i++)
            relay.Forward("osd-session 0000000000000002\nosd-draw-step name=New" + i + " hr=00000000\n",
                2, (_, text) => second.Add(text));
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Forward_FailedSinkRetriesAndCannotChangeFeedback()
    {
        var relay = new OverlayEntryRelay();
        var log = "osd-session 0000000000000001\n" + Entry + "\nosd-install-refused foreign-entry\n";
        var feedback = OverlayFeedback.Parse(log, 1);
        relay.Forward(log, 1, (_, _) => throw new IOException("fixture"));
        var rows = new List<string>();
        var invocations = 0;
        relay.Forward(log, 1, (_, value) => { invocations++; rows.Add(value); });
        Assert.Equal(1, invocations);
        // 重发必须原样送达同一条记录：失败的那次不能把条目弄丢或被截断。
        Assert.Equal(Entry, Assert.Single(rows));
        Assert.Equal(feedback, OverlayFeedback.Parse(log, 1));
        Assert.Contains("本局未安装", feedback);
    }

    [Fact]
    public void Forward_StopsAtNextSessionAndResetsDeduplication()
    {
        var relay = new OverlayEntryRelay();
        var rows = new List<string>();
        var log = "osd-session 0000000000000001\nosd-session 0000000000000002\n" + Entry + "\n";
        relay.Forward(log, 1, (_, value) => rows.Add(value));
        Assert.Empty(rows);
        relay.Forward(log, 2, (_, value) => rows.Add(value));
        relay.Forward("osd-session 0000000000000003\n" + Entry + "\n", 3, (_, value) => rows.Add(value));
        // 两个会话各转发一次，且顺序必须与调用顺序一致；内容不能被换成标记行或别的记录。
        Assert.Equal(new[] { Entry, Entry }, rows);
    }
}
