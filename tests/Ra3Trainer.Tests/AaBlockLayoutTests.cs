using Ra3Trainer.Core.Codegen;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class AaBlockLayoutTests
{
    [Fact]
    public void LayoutPadsBlocksToFixedOffsets()
    {
        var blocks = new[]
        {
            new AaEncodedBlock("MustCode", 0, [0xAA, 0xBB]),
            new AaEncodedBlock("MustCode", 4, [0xCC])
        };

        var result = AaBlockLayout.Build("MustCode", 0x10, blocks);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0xCC }, result[..5]);
        // 只补"块与块之间"的空洞，**不**把尾部补齐到 maxSize：返回长度 = 最后一块的末尾（这里是 5）。
        // 把这条钉住：远程段本来就是整块分配的，尾部没写到的字节保持分配时的内容。
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void LayoutAcceptsBlockStartingExactlyAtPreviousEnd()
    {
        // block.Offset < output.Count 才是重叠；正好接着上一块末尾（== Count）必须接受。
        var blocks = new[]
        {
            new AaEncodedBlock("MustCode", 0, [0xAA, 0xBB, 0xCC]),
            new AaEncodedBlock("MustCode", 3, [0xDD])
        };

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, AaBlockLayout.Build("MustCode", 0x10, blocks));
    }

    [Fact]
    public void LayoutRejectsBlockThatRunsPastAllocatedSize()
    {
        var blocks = new[] { new AaEncodedBlock("MustCode", 0x0E, [0xAA, 0xBB, 0xCC, 0xDD]) };

        var error = Assert.Throws<InvalidOperationException>(() => AaBlockLayout.Build("MustCode", 0x10, blocks));

        Assert.Contains("0x10", error.Message);
    }

    [Fact]
    public void LayoutOnlyTakesBlocksOfTheRequestedSymbolIgnoringCase()
    {
        var blocks = new[]
        {
            new AaEncodedBlock("MustCode2", 0, [0x11]),   // 别的符号：不参与
            new AaEncodedBlock("MUSTCODE", 0, [0xAA]),    // 只有大小写不同：必须参与
            new AaEncodedBlock("OtherCode", 4, [0x22])
        };

        Assert.Equal(new byte[] { 0xAA }, AaBlockLayout.Build("MustCode", 0x10, blocks));
    }

    [Fact]
    public void LayoutSortsBlocksByOffset()
    {
        // 乱序输入（+4 在前、+0 在后）也要按偏移排好：不排序的话 +0 会撞上已写字节直接抛。
        var blocks = new[]
        {
            new AaEncodedBlock("MustCode", 4, [0xCC]),
            new AaEncodedBlock("MustCode", 0, [0xAA, 0xBB])
        };

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0x00, 0x00, 0xCC }, AaBlockLayout.Build("MustCode", 0x10, blocks));
    }

    [Fact]
    public void LayoutRejectsBlocksThatOverlapFixedOffsets()
    {
        var blocks = new[]
        {
            new AaEncodedBlock("MustCode", 0, [0xAA, 0xBB, 0xCC]),
            new AaEncodedBlock("MustCode", 2, [0xDD])
        };

        Assert.Throws<InvalidOperationException>(() => AaBlockLayout.Build("MustCode", 0x10, blocks));
    }

    [Fact]
    public void LayoutRejectsNullBlockSequenceAndNullEntries()
    {
        Assert.Throws<ArgumentNullException>(() => AaBlockLayout.Build("MustCode", 0x10, null!));

        var nullEntry = new AaEncodedBlock[] { null! };
        Assert.Throws<InvalidOperationException>(() => AaBlockLayout.Build("MustCode", 0x10, nullEntry));

        var nullBytes = new[] { new AaEncodedBlock("MustCode", 0, null!) };
        Assert.Throws<InvalidOperationException>(() => AaBlockLayout.Build("MustCode", 0x10, nullBytes));
    }
}
