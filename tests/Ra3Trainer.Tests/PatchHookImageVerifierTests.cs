using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PatchHookImageVerifierTests
{
    [Fact]
    public void VerifyComparesManifestRestoreBytesWithImageBytes()
    {
        var imageBytes = CreatePeImage();
        imageBytes[0x230] = 0x8B;
        imageBytes[0x231] = 0x50;
        imageBytes[0x232] = 0x3C;
        imageBytes[0x233] = 0x8B;
        imageBytes[0x234] = 0xCE;
        var image = PeImage.FromBytes(imageBytes);

        var result = Assert.Single(PatchHookImageVerifier.Verify(Manifest(), image));

        Assert.Equal(0x1030, result.Rva);
        Assert.Equal(0x230, result.RawOffset);
        Assert.Equal(".text", result.SectionName);
        Assert.True(result.Matches);
        Assert.Equal(new byte[] { 0x8B, 0x50, 0x3C, 0x8B, 0xCE }, result.ExpectedBytes);
        Assert.Equal(new byte[] { 0x8B, 0x50, 0x3C, 0x8B, 0xCE }, result.ImageBytes);
    }

    [Fact]
    public void VerifyReportsAMismatchWhenTheImageBytesDifferFromTheManifest()
    {
        // 不写 0x230 处的字节：映像里是 0，清单要的是 8B 50 3C 8B CE ⇒ Matches 必须为 false。
        var image = PeImage.FromBytes(CreatePeImage());

        var result = Assert.Single(PatchHookImageVerifier.Verify(Manifest(), image));

        Assert.False(result.Matches);
        Assert.Equal(new byte[] { 0x8B, 0x50, 0x3C, 0x8B, 0xCE }, result.ExpectedBytes);
        Assert.Equal(new byte[5], result.ImageBytes);
    }

    [Fact]
    public void VerifyThrowsWhenTheHookRvaIsNotMappedByAnySection()
    {
        var manifest = new PatchManifest([ManifestHook() with { Address = "ra3_1.12.game+3000" }]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PatchHookImageVerifier.Verify(manifest, PeImage.FromBytes(CreatePeImage())));

        Assert.Contains("not mapped by any PE section", error.Message);
    }

    [Fact]
    public void VerifyWithNoHooksReturnsAnEmptyResult()
    {
        Assert.Empty(PatchHookImageVerifier.Verify(new PatchManifest([]), PeImage.FromBytes(CreatePeImage())));
    }

    private static PatchManifest Manifest() => new([ManifestHook()]);

    private static PatchHook ManifestHook() => new(
        Address: "ra3_1.12.game+1030",
        SectionTitle: "Player God Mode Code",
        PatchAssembly: ["jmp MustCode+41e"],
        TrampolineTarget: "MustCode+41e",
        ReturnLabel: "_BackPlayerGodMode",
        EnableFlags: ["iEnable+16"],
        OriginalAssembly: ["mov edx,[eax+3c]", "mov ecx,esi"]);

    // 节尺寸用命名常量，并给虚拟尺寸留出余量：否则"探测边界"的意图会被夹具本身的紧贴布局掩盖。
    private const int SectionVirtualSize = 0x800;
    private const int SectionRawSize = 0x200;
    private const int SectionRawPointer = 0x200;

    private static byte[] CreatePeImage()
    {
        var bytes = new byte[0x400];
        WriteUInt32(bytes, 0x3C, 0x80);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        WriteUInt16(bytes, 0x84, 0x14C);
        WriteUInt16(bytes, 0x86, 1);
        WriteUInt32(bytes, 0x88, 0x12345678);
        WriteUInt16(bytes, 0x94, 0xE0);
        var optionalHeader = 0x80 + 24;
        WriteUInt16(bytes, optionalHeader, 0x10B);
        WriteUInt32(bytes, optionalHeader + 28, 0x400000);
        WriteUInt32(bytes, optionalHeader + 56, 0x900000);

        var section = 0x80 + 24 + 0xE0;
        var name = ".text"u8.ToArray();
        Array.Copy(name, 0, bytes, section, name.Length);
        WriteUInt32(bytes, section + 8, SectionVirtualSize);
        WriteUInt32(bytes, section + 12, 0x1000);
        WriteUInt32(bytes, section + 16, SectionRawSize);
        WriteUInt32(bytes, section + 20, SectionRawPointer);
        return bytes;
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}
