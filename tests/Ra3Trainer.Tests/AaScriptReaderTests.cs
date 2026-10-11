using Ra3Trainer.Core.Codegen;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class AaScriptReaderTests
{
    [Fact]
    public void ReadBootstrapScriptExtractsDataAndRemoteBlocks()
    {
        var script = TestAssets.ReadBootstrapLines();

        var document = AaScriptReader.Read(script);

        Assert.Equal(new byte[] { 0xA0, 0xA5, 0x86, 0x65 }, document.IEnableInitializers["iEnable+24"]);
        Assert.Contains(document.Blocks, block => block.Symbol.Equals("MustCode", StringComparison.OrdinalIgnoreCase) && block.Offset == 0);
        Assert.Contains(document.Blocks, block => block.Symbol.Equals("MustCode", StringComparison.OrdinalIgnoreCase) && block.Offset == 0x29);
        Assert.Contains(document.Blocks, block => block.Symbol.Equals("MustCode2", StringComparison.OrdinalIgnoreCase) && block.Offset == 0x700);
        // 原来这里只断言"没有 ra3_1.12.game 的块"——Read 永远不会产出那个符号的块，等于什么都没验。
        // 改成钉住真正的可观察行为：只会有 MustCode/MustCode2 两种块，且两段都从偏移 0 开始。
        Assert.All(document.Blocks, block => Assert.True(
            block.Symbol.Equals("MustCode", StringComparison.OrdinalIgnoreCase) ||
            block.Symbol.Equals("MustCode2", StringComparison.OrdinalIgnoreCase),
            $"出现了不该有的块：{block.Symbol}"));
        Assert.Contains(document.Blocks, block => block.Symbol.Equals("MustCode", StringComparison.OrdinalIgnoreCase) && block.Offset == 0);
        Assert.Contains(document.Blocks, block => block.Symbol.Equals("MustCode2", StringComparison.OrdinalIgnoreCase) && block.Offset == 0);
    }

    [Fact]
    public void ReadKeepsLocalLabelsInsideCurrentRemoteBlock()
    {
        var script = new[]
        {
            "[ENABLE]",
            "MustCode+10:",
            "push eax",
            "_ExitPlayerOneKillItMode:",
            "pop eax",
            "ret",
            "[disable]"
        };

        var document = AaScriptReader.Read(script);

        var block = Assert.Single(document.Blocks);
        Assert.Equal(["push eax", "_ExitPlayerOneKillItMode:", "pop eax", "ret"], block.Lines);
    }

    [Fact]
    public void ReadStopsRemoteBlockWhenModuleHookAnchorStarts()
    {
        var script = new[]
        {
            "[ENABLE]",
            "MustCode:",
            "ret",
            "ra3_1.12.game+1000:",
            "jmp MustCode+10",
            "_Back:",
            "MustCode+10:",
            "nop",
            "[disable]"
        };

        var document = AaScriptReader.Read(script);

        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(["ret"], document.Blocks[0].Lines);
        Assert.Equal(["nop"], document.Blocks[1].Lines);
    }

    [Fact]
    public void ReadRejectsLabelOffsetThatDoesNotFitInt32()
    {
        // 超长偏移原来抛的是裸 OverflowException，看不出是哪一行；现在点名报错。
        var script = new[]
        {
            "[ENABLE]",
            "MustCode+FFFFFFFFFF:",
            "ret",
            "[disable]"
        };

        var exception = Assert.Throws<InvalidOperationException>(() => AaScriptReader.Read(script));

        Assert.Contains("MustCode+FFFFFFFFFF", exception.Message);
    }

    [Fact]
    public void ReadDropsInstructionsAfterModuleHookAnchorEvenAfterALocalLabel()
    {
        // 真实脚本里锚点前面常有一条本地标签（_BackPlayerXxx:）：锚点一旦出现，
        // 后面的指令既不能落到锚点、也不能粘回上一个块。
        var script = new[]
        {
            "[ENABLE]",
            "MustCode+10:",
            "push eax",
            "_Local:",
            "ra3_1.12.game+2000:",
            "jmp MustCode+10",
            "_Back:",
            "MustCode+20:",
            "nop",
            "[disable]"
        };

        var document = AaScriptReader.Read(script);

        Assert.Equal(2, document.Blocks.Count);
        Assert.Equal(["push eax", "_Local:"], document.Blocks[0].Lines);
        Assert.Equal(["nop"], document.Blocks[1].Lines);
    }

    [Fact]
    public void ReadKeepsTheLastBlockWhenInputHasNoDisableMarker()
    {
        var script = new[] { "[ENABLE]", "MustCode:", "ret" };

        var document = AaScriptReader.Read(script);

        Assert.Equal(["ret"], Assert.Single(document.Blocks).Lines);
    }

    [Fact]
    public void ReadStoresInitializerKeysWithUppercaseHexOffsets()
    {
        // 读取端把键统一成 $"{symbol}+{offset:X}"（大写十六进制），查找端必须能命中同一个键。
        var script = new[] { "[ENABLE]", "iEnable+2c:", "db 01 02 03 04", "[disable]" };

        var document = AaScriptReader.Read(script);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, document.IEnableInitializers["iEnable+2C"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, document.IEnableInitializers["ienable+2c"]);
    }

    [Fact]
    public void ReadReturnsNothingBeforeTheEnableMarkerOrWithEmptyInput()
    {
        Assert.Empty(AaScriptReader.Read([]).Blocks);
        Assert.Empty(AaScriptReader.Read([]).IEnableInitializers);
        Assert.Empty(AaScriptReader.Read(["[disable]", "MustCode:", "ret"]).Blocks);

        var emptyEnableSection = AaScriptReader.Read(["[ENABLE]", "[disable]"]);
        Assert.Empty(emptyEnableSection.Blocks);
        Assert.Empty(emptyEnableSection.IEnableInitializers);
    }
}
