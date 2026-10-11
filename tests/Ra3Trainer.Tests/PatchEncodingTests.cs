using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PatchEncodingTests
{
    [Fact]
    public void EncodeNearJumpPadsWithNops()
    {
        var patch = X86PatchEncoder.EncodeNearJumpWithNops(source: 0x1000, target: 0x2000, totalLength: 8);

        Assert.Equal(new byte[] { 0xE9, 0xFB, 0x0F, 0x00, 0x00, 0x90, 0x90, 0x90 }, patch);
    }

    [Fact]
    public void EncodeNearJumpRejectsTooShortPatch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            X86PatchEncoder.EncodeNearJumpWithNops(source: 0x1000, target: 0x2000, totalLength: 4));
    }

    [Fact]
    public void EncodeNearJumpAcceptsTheMinimumLengthWithoutPadding()
    {
        // totalLength == 5 是守卫的阈值边界：正好 5 字节，一个 NOP 都不补。
        var patch = X86PatchEncoder.EncodeNearJumpWithNops(source: 0x1000, target: 0x2000, totalLength: 5);

        Assert.Equal(5, patch.Length);
        Assert.Equal(new byte[] { 0xE9, 0xFB, 0x0F, 0x00, 0x00 }, patch);
    }

    [Fact]
    public void EncodeNearJumpHandlesBackwardTargetsWithASignedDisplacement()
    {
        // 反向跳（target < source+5）是符号处理最容易错的地方：rel32 必须是小端负值。
        var patch = X86PatchEncoder.EncodeNearJumpWithNops(source: 0x2000, target: 0x1000, totalLength: 5);

        Assert.Equal(0xE9, patch[0]);
        Assert.Equal(-0x1005, BitConverter.ToInt32(patch, 1)); // target - (source + 5)
        Assert.Equal(new byte[] { 0xE9, 0xFB, 0xEF, 0xFF, 0xFF }, patch);
    }

    [Fact]
    public void OriginalByteParserRejectsUnsupportedAndMalformedInput()
    {
        Assert.Throws<ArgumentNullException>(() => OriginalByteParser.Parse(null!));
        Assert.Throws<InvalidOperationException>(() => OriginalByteParser.Parse([null!]));
        Assert.Throws<NotSupportedException>(() => OriginalByteParser.Parse(["frobnicate eax"]));
        Assert.Throws<NotSupportedException>(() => OriginalByteParser.Parse(["mov eax,[r8+04]"]));
        Assert.Throws<InvalidOperationException>(() => OriginalByteParser.Parse(["db 1FF"]));
    }

    [Fact]
    public void OriginalByteParserCoversTheRemainingManifestMnemonics()
    {
        // 逐条对齐清单里真实出现的其余助记符（取自 trainer_report.json 的 original_assembly）。
        Assert.Equal(new byte[] { 0x8D, 0x48, 0x20, 0x39, 0x09, 0x5B },
            OriginalByteParser.Parse(["lea ecx,[eax+20]", "cmp [ecx],ecx", "pop ebx"]));
        Assert.Equal(new byte[] { 0xF3, 0x0F, 0x2C, 0x46, 0x1C },
            OriginalByteParser.Parse(["cvttss2si eax,[esi+1c]"]));
        Assert.Equal(new byte[] { 0xD9, 0x46, 0x1C, 0x89, 0x46, 0x5C },
            OriginalByteParser.Parse(["fld dword ptr [esi+1c]", "mov [esi+5c],eax"]));
        Assert.Equal(new byte[] { 0x8B, 0x40, 0x08, 0x2B, 0x41, 0x14 },
            OriginalByteParser.Parse(["mov eax,[eax+08]", "sub eax,[ecx+14]"]));
        Assert.Equal(new byte[] { 0x8B, 0x0C, 0xBA, 0x85, 0xC9 },
            OriginalByteParser.Parse(["mov ecx,[edx+edi*4]", "test ecx,ecx"]));
        // 单操作数的 db 行按字面量编码。
        Assert.Equal(new byte[] { 0x75, 0x07 }, OriginalByteParser.Parse(["db 75 07"]));
    }

    [Fact]
    public void OriginalByteParserEncodesRestoreAssembly()
    {
        var bytes = OriginalByteParser.Parse([
            "mov edx,[eax+28]",
            "mov eax,[edx+20]"
        ]);

        Assert.Equal(new byte[] { 0x8B, 0x50, 0x28, 0x8B, 0x42, 0x20 }, bytes);
    }

    [Fact]
    public void OriginalByteParserEncodesSseRestoreAssembly()
    {
        var bytes = OriginalByteParser.Parse([
            "movss [ebp+00000260],xmm0"
        ]);

        Assert.Equal(new byte[] { 0xF3, 0x0F, 0x11, 0x85, 0x60, 0x02, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void OriginalByteParserPreservesRa3RegisterComparisonEncoding()
    {
        var bytes = OriginalByteParser.Parse([
            "add edx,[eax+04]",
            "cmp edx,edi"
        ]);

        Assert.Equal(new byte[] { 0x03, 0x50, 0x04, 0x3B, 0xD7 }, bytes);
    }

    [Fact]
    public void OriginalByteParserPreservesRa3RegisterMoveEncoding()
    {
        var bytes = OriginalByteParser.Parse([
            "mov edx,[eax+3c]",
            "mov ecx,esi"
        ]);

        Assert.Equal(new byte[] { 0x8B, 0x50, 0x3C, 0x8B, 0xCE }, bytes);
    }
}
