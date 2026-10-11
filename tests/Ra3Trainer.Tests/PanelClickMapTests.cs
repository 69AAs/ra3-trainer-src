using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public class PanelClickMapTests
{
    [Fact]
    public void MapsAGamePixelOntoThePanelOneToOne()
        => Assert.True(PanelClickMap.TryMap(24 + 100, 24 + 200, 24, 24, 360, 440, out var x, out var y) && x == 100 && y == 200);

    [Fact]
    public void RejectsPressesOutsideThePanel()
    {
        Assert.False(PanelClickMap.TryMap(0, 0, 24, 24, 360, 440, out _, out _));          // above/left of it
        Assert.False(PanelClickMap.TryMap(24 + 360, 24 + 10, 24, 24, 360, 440, out _, out _)); // just past the right edge
        Assert.False(PanelClickMap.TryMap(24 + 10, 24 + 440, 24, 24, 360, 440, out _, out _)); // just past the bottom edge
    }

    [Fact]
    public void AcceptsTheExactCornerPixels()
    {
        Assert.True(PanelClickMap.TryMap(24, 24, 24, 24, 360, 440, out var x0, out var y0) && x0 == 0 && y0 == 0);
        Assert.True(PanelClickMap.TryMap(24 + 359, 24 + 439, 24, 24, 360, 440, out var x1, out var y1) && x1 == 359 && y1 == 439);
    }

    [Fact]
    public void RejectsHugeOrDegenerateTargets()
    {
        Assert.False(PanelClickMap.TryMap(100, 100, 24, 24, 0, 440, out _, out _));
        Assert.False(PanelClickMap.TryMap(long.MinValue, 100, 24, 24, 360, 440, out _, out _));
    }

    [Theory]
    [InlineData(long.MinValue, 100)] // X 下界之外：long.MinValue - panelX 会回绕成正数
    [InlineData(long.MaxValue, 100)] // X 上界之外（32 位线格式装不下）
    [InlineData(100, long.MinValue)] // Y 下界之外
    [InlineData(100, long.MaxValue)] // Y 上界之外
    public void RejectsCoordinatesOutsideTheWireRangeOnEitherAxis(long gameX, long gameY)
    {
        // 越界必须在做减法之前被拒，否则回绕后的值会被当成合法落点。
        Assert.False(PanelClickMap.TryMap(gameX, gameY, 24, 24, 360, 440, out var x, out var y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Theory]
    [InlineData(0, 440)]
    [InlineData(-1, 440)]
    [InlineData(360, 0)]
    [InlineData(360, -1)]
    public void RejectsDegeneratePanelDimensionsWithInRangeCoordinates(int width, int height)
    {
        Assert.False(PanelClickMap.TryMap(24 + 10, 24 + 10, 24, 24, width, height, out var x, out var y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void RejectedPressesResetTheOutputsToZero()
    {
        // 每条拒绝路径都要把输出清零：否则调用方可能拿着上一次成功的结果当本次落点。
        Assert.False(PanelClickMap.TryMap(0, 0, 24, 24, 360, 440, out var above, out var left));
        Assert.Equal(0, above);
        Assert.Equal(0, left);
    }
}
