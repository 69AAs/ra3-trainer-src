using System.Buffers.Binary;

namespace Ra3Trainer.Core.Patching;

public sealed class PeImage
{
    private readonly byte[] _bytes;

    private PeImage(byte[] bytes)
    {
        _bytes = bytes;
        (Metadata, Sections) = ReadHeaders(bytes);
    }

    public PeImageMetadata Metadata { get; }

    public IReadOnlyList<PeSectionInfo> Sections { get; }

    public static PeImage Load(string path)
    {
        return FromBytes(File.ReadAllBytes(path));
    }

    public static PeImage FromBytes(byte[] bytes)
    {
        return new PeImage(bytes.ToArray());
    }

    public byte[] ReadRva(int rva, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var mapping = MapRva(rva, count);

        var result = new byte[count];
        Array.Copy(_bytes, mapping.RawOffset, result, 0, count);
        return result;
    }

    public PeRvaMapping MapRva(int rva, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var section = Sections.FirstOrDefault(item => item.Contains(rva, count));
        if (section is null)
        {
            throw new InvalidOperationException($"RVA 0x{rva:X} is not mapped by any PE section.");
        }

        var rawOffset = checked(section.RawPointer + (rva - section.VirtualAddress));
        // 同上：rawOffset + count 用 long 比，别让溢出把越界读放进来。
        if (rawOffset < 0 || count < 0 || (long)rawOffset + count > _bytes.Length)
        {
            throw new InvalidOperationException($"RVA 0x{rva:X} maps outside the PE file.");
        }

        return new PeRvaMapping(rva, rawOffset, section);
    }

    private static (PeImageMetadata Metadata, IReadOnlyList<PeSectionInfo> Sections) ReadHeaders(byte[] bytes)
    {
        var peHeaderOffset = ReadInt32(bytes, 0x3C);
        if (ReadUInt32(bytes, peHeaderOffset) != 0x00004550)
        {
            throw new InvalidOperationException("Invalid PE signature.");
        }

        var machine = ReadUInt16(bytes, peHeaderOffset + 4);
        var sectionCount = ReadUInt16(bytes, peHeaderOffset + 6);
        var timeDateStamp = ReadUInt32(bytes, peHeaderOffset + 8);
        var optionalHeaderSize = ReadUInt16(bytes, peHeaderOffset + 20);
        var optionalHeaderOffset = peHeaderOffset + 24;
        var imageBase = ReadImageBase(bytes, optionalHeaderOffset);
        var sizeOfImage = ReadInt32(bytes, optionalHeaderOffset + 56);
        var metadata = new PeImageMetadata(machine, timeDateStamp, imageBase, sizeOfImage);
        // 节表偏移 = 可选头偏移 + 可选头长度，两个数都来自文件：用 long 算完再判界，
        // 否则 sectionTableOffset + index*40 也能溢出到合法区间里去。
        var sectionTableOffset = (long)optionalHeaderOffset + optionalHeaderSize;
        if (sectionTableOffset < 0 || sectionTableOffset + (long)sectionCount * 40 > bytes.Length)
        {
            throw new InvalidOperationException("Unexpected end of PE file.");
        }

        var sectionTableStart = (int)sectionTableOffset;
        var sections = new List<PeSectionInfo>(sectionCount);
        for (var index = 0; index < sectionCount; index++)
        {
            var offset = sectionTableStart + index * 40;
            var name = ReadSectionName(bytes, offset);
            var virtualSize = ReadInt32(bytes, offset + 8);
            var virtualAddress = ReadInt32(bytes, offset + 12);
            var rawSize = ReadInt32(bytes, offset + 16);
            var rawPointer = ReadInt32(bytes, offset + 20);
            sections.Add(new PeSectionInfo(
                name,
                virtualAddress,
                virtualSize,
                rawPointer,
                rawSize));
        }

        return (metadata, sections);
    }

    private static ulong ReadImageBase(byte[] bytes, int optionalHeaderOffset)
    {
        var magic = ReadUInt16(bytes, optionalHeaderOffset);
        return magic switch
        {
            0x10B => ReadUInt32(bytes, optionalHeaderOffset + 28),
            0x20B => ReadUInt64(bytes, optionalHeaderOffset + 24),
            _ => throw new InvalidOperationException($"Unsupported PE optional header magic 0x{magic:X}.")
        };
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        // 走 EnsureRange，别直接 AsSpan：可选头被截断时要给约定的 InvalidOperationException，
        // 而不是 ArgumentOutOfRangeException（调用方只按前者兜底）。
        EnsureRange(bytes, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)));
    }

    private static string ReadSectionName(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, 8);
        var length = 0;
        while (length < 8 && bytes[offset + length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.ASCII.GetString(bytes, offset, length);
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
    }

    private static void EnsureRange(byte[] bytes, int offset, int count)
    {
        // 必须用 long 比较：offset/count 都来自文件内容（0x3C 处的 PE 头偏移、可选头尺寸等），
        // int 相加溢出成负数就会绕过这道检查，然后把越界偏移交给 AsSpan/BinaryPrimitives。
        // 结局不是我们约定的 InvalidOperationException，而是 ArgumentOutOfRangeException 从深处冒出来。
        if (offset < 0 || count < 0 || (long)offset + count > bytes.Length)
        {
            throw new InvalidOperationException("Unexpected end of PE file.");
        }
    }

}

public sealed record PeImageMetadata(
    ushort Machine,
    uint TimeDateStamp,
    ulong ImageBase,
    int SizeOfImage);

public sealed record PeRvaMapping(int Rva, int RawOffset, PeSectionInfo Section);

public sealed record PeSectionInfo(
    string Name,
    int VirtualAddress,
    int VirtualSize,
    int RawPointer,
    int RawSize)
{
    public bool Contains(int rva, int count)
    {
        if (rva < 0 || count < 0)
        {
            return false;
        }

        // 三个加法全用 long：构造过的 PE 把 VirtualAddress/VirtualSize 摆到 int 上限附近时，
        // int 相加会回绕成小值，让"不在节里"的 RVA 通过包含判定 → MapRva 返回假映射。
        var size = (long)Math.Max(VirtualSize, RawSize);
        return rva >= VirtualAddress
            && (long)rva + count <= (long)VirtualAddress + size
            && (long)rva - VirtualAddress + count <= RawSize;
    }
}
