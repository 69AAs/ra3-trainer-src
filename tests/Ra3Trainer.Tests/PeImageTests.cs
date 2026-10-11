using Ra3Trainer.Core.Patching;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PeImageTests
{
    [Fact]
    public void MetadataReadsPeHeaderAndSectionTable()
    {
        var image = PeImage.FromBytes(CreatePeImage());

        Assert.Equal(0x14C, image.Metadata.Machine);
        Assert.Equal(0x12345678u, image.Metadata.TimeDateStamp);
        Assert.Equal(0x400000u, image.Metadata.ImageBase);
        Assert.Equal(0x900000, image.Metadata.SizeOfImage);

        var section = Assert.Single(image.Sections);
        Assert.Equal(".text", section.Name);
        Assert.Equal(0x1000, section.VirtualAddress);
        // VirtualSize 与 RawSize 刻意取不同值：两个字段写混了（offset+8 / +16 对调）必须在这里现形。
        Assert.Equal(SectionVirtualSize, section.VirtualSize);
        Assert.Equal(SectionRawPointer, section.RawPointer);
        Assert.Equal(SectionRawSize, section.RawSize);
        Assert.NotEqual(section.VirtualSize, section.RawSize);
    }

    [Fact]
    public void MapRvaHandlesTheSectionBoundaries()
    {
        var image = PeImage.FromBytes(CreatePeImage());

        // 节首：RVA 0x1000 -> 文件偏移 0x200
        Assert.Equal(0x200, image.MapRva(0x1000, 1).RawOffset);
        // 最后一个有效字节：RVA 0x11FF（= VA + RawSize - 1）-> 0x3FF
        Assert.Equal(0x3FF, image.MapRva(0x1000 + SectionRawSize - 1, 1).RawOffset);
        // 越过节尾一个字节就不属于任何节
        var error = Assert.Throws<InvalidOperationException>(() => image.MapRva(0x1000 + SectionRawSize, 1));
        Assert.Contains("not mapped by any PE section", error.Message);
        // 计数跨过节尾同样不行
        Assert.Throws<InvalidOperationException>(() => image.MapRva(0x1000 + SectionRawSize - 1, 2));
        // 头区里的 RVA 没有节承载
        Assert.Throws<InvalidOperationException>(() => image.MapRva(0x100, 1));
        // 负数计数由参数守卫拦下
        Assert.Throws<ArgumentOutOfRangeException>(() => image.MapRva(0x1000, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.ReadRva(0x1000, -1));
    }

    [Fact]
    public void MapRvaRejectsASectionWhoseRawRangeRunsPastTheFile()
    {
        var imageBytes = CreatePeImage();
        WriteUInt32(imageBytes, SectionTableOffset + 20, 0x3F0); // RawPointer 挪到文件尾附近
        var image = PeImage.FromBytes(imageBytes);

        // 节内（raw 偏移 0x3F0 + 0x1F0）已经越过 0x400 的文件长度：必须报"映射到文件之外"。
        var error = Assert.Throws<InvalidOperationException>(() => image.MapRva(0x1000 + 0x1F0, 1));
        Assert.Contains("maps outside the PE file", error.Message);
    }

    [Fact]
    public void FromBytesRejectsMalformedHeaders()
    {
        var badSignature = CreatePeImage();
        badSignature[0x80] = (byte)'X';
        Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(badSignature));

        var badPointer = CreatePeImage();
        WriteUInt32(badPointer, 0x3C, 0xFFFFFF); // e_lfanew 指到文件之外
        var pointerError = Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(badPointer));
        Assert.Contains("Unexpected end of PE file", pointerError.Message);

        var unsupportedMagic = CreatePeImage();
        WriteUInt16(unsupportedMagic, 0x80 + 24, 0x20C);
        var magicError = Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(unsupportedMagic));
        Assert.Contains("Unsupported PE optional header magic", magicError.Message);

        var truncated = CreatePeImage()[..0x90];
        Assert.Throws<InvalidOperationException>(() => PeImage.FromBytes(truncated));
    }

    [Fact]
    public void ReadRvaMapsSectionVirtualAddressToRawFileOffset()
    {
        var imageBytes = CreatePeImage();
        imageBytes[0x230] = 0x8B;
        imageBytes[0x231] = 0xCE;
        var image = PeImage.FromBytes(imageBytes);

        var bytes = image.ReadRva(0x1030, 2);

        Assert.Equal(new byte[] { 0x8B, 0xCE }, bytes);
    }

    [Fact]
    public void MapRvaReportsRawOffsetAndContainingSection()
    {
        var image = PeImage.FromBytes(CreatePeImage());

        var mapping = image.MapRva(0x1030, 2);

        Assert.Equal(".text", mapping.Section.Name);
        Assert.Equal(0x230, mapping.RawOffset);
    }

    // 节尺寸/节表位置用命名常量：VirtualSize 与 RawSize 必须可区分，边界断言才有意义。
    private const int SectionVirtualSize = 0x180;
    private const int SectionRawSize = 0x200;
    private const int SectionRawPointer = 0x200;
    private const int SectionTableOffset = 0x80 + 24 + 0xE0;

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

        var section = SectionTableOffset;
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
