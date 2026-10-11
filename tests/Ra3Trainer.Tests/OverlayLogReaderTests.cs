using System.Text;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class OverlayLogReaderTests
{
    [Fact]
    public void LargeLog_CatchesUpWithinBudgetAndKeepsLatestFailure()
    {
        const string marker = "osd-session 0000000000000025\n";
        var content = marker + string.Concat(Enumerable.Repeat("historical-diagnostic " + new string('x', 900) + "\n", 1800))
            + "osd-panel-step name=ReadMapping hr=80070057 reason=abi-mismatch\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var reader = new OverlayLogReader();
        var first = reader.Read(stream, 37);
        Assert.Equal(OverlayLogReader.ReadBudget, stream.Position);
        Assert.DoesNotContain("abi-mismatch", first);
        string result = first;
        while (stream.Position < stream.Length) result = reader.Read(stream, 37);
        Assert.True(result.Length < 270000);
        Assert.StartsWith(marker, result);
        // 直接钉住最后那条记录本身：否则只看下游解析成功，整行被当成"未完成记录"丢掉时也可能照样过。
        Assert.Contains("osd-panel-step name=ReadMapping hr=80070057 reason=abi-mismatch", result);
        Assert.Contains("ABI", OverlayFeedback.Parse(result, 37));
    }

    [Fact]
    public void PartialUtf8Record_IsNeverReportedUntilComplete()
    {
        using var stream = new MemoryStream();
        var reader = new OverlayLogReader();
        var bytes = Encoding.UTF8.GetBytes("osd-session 0000000000000025\nosd-panel-step name=ReadMapping hr=00000001 reason=mapping-missing message=中文\n");
        // Split in the middle of the final Chinese code point.
        stream.Write(bytes, 0, bytes.Length - 3);
        Assert.DoesNotContain("osd-panel-step", reader.Read(stream, 37));
        stream.Position = stream.Length;
        stream.Write(bytes, bytes.Length - 3, 3);
        var text = reader.Read(stream, 37);
        Assert.Contains("message=中文\n", text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public void TruncationAndNewSession_ResetAttribution()
    {
        using var stream = new MemoryStream();
        void Replace(string value)
        {
            stream.SetLength(0); stream.Position = 0;
            stream.Write(Encoding.UTF8.GetBytes(value));
        }
        var reader = new OverlayLogReader();
        Replace("osd-session 0000000000000025\nosd-panel-drawn\n" + new string('x', 100) + "\n");
        Assert.Contains("osd-panel-drawn", reader.Read(stream, 37));
        Replace("osd-panel-drawn\n");
        Assert.Empty(reader.Read(stream, 37));
        Replace("osd-session 0000000000000026\nosd-panel-drawn\n");
        Assert.Contains("osd-panel-drawn", reader.Read(stream, 38));
        Assert.Empty(reader.Read(stream, 37));
    }

    [Fact]
    public void RewriteToTheSameLengthIsDetectedByTheTailBytesAndResetsAttribution()
    {
        using var stream = new MemoryStream();
        var reader = new OverlayLogReader();
        stream.Write(Encoding.UTF8.GetBytes("osd-session 0000000000000025\nAAA\n"));
        Assert.Contains("AAA", reader.Read(stream, 37));

        // 同一会话标记、同样长度、内容不同（截断后重写）：只看长度判不出来，必须靠末尾字节比对复位。
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(Encoding.UTF8.GetBytes("osd-session 0000000000000025\nBBB\n"));
        var text = reader.Read(stream, 37);

        Assert.DoesNotContain("AAA", text);
        Assert.Contains("BBB", text);
    }

    [Fact]
    public void NextSessionAndOversizedLines_DoNotLeakRecords()
    {
        var input = "osd-session 0000000000000025\n" + new string('x', 5000) + "\nosd-panel-drawn\n"
            + "osd-session 0000000000000026\nosd-panel-step name=Stopped hr=80004005\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        Assert.Empty(new OverlayLogReader().Read(stream, 37));
    }
}
