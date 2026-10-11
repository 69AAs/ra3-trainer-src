using System.Text;
using Ra3Trainer.Core.Features;
using Ra3Trainer.Core.Memory;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 自动记下来的单位要显示真名字（用户 2026-10-11：用模板里的内部名去查中文名）。
/// 口径：内置表命中 ⇒ 中文名；否则内部名像样 ⇒ 用内部名；否则「未命名」。全程只读、不抛。
/// </summary>
public sealed class UnitNamingTests
{
    private static IReadOnlyList<UnitInternalName> Table(params (string Internal, string Display)[] rows) =>
        rows.Select(row => new UnitInternalName(row.Internal, row.Display, "测试")).ToArray();

    [Fact]
    public void KnownInternalNameBecomesTheChineseDisplayName()
    {
        var table = Table(("SovietNewTower", "苏联新塔"), ("SovietHeavyWalkerVehicle", "收割机甲"));

        Assert.Equal("苏联新塔", UnitNaming.DisplayNameFor("SovietNewTower", table));
        Assert.Equal("苏联新塔", UnitNaming.DisplayNameFor("sovietnewtower", table));   // 大小写不敏感
    }

    [Fact]
    public void UnknownInternalNameFallsBackToTheInternalName()
    {
        var table = Table(("SovietNewTower", "苏联新塔"));

        Assert.Equal("RemixWonderTank", UnitNaming.DisplayNameFor("RemixWonderTank", table));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x12345678")]
    [InlineData("hello")]
    public void JunkOrMissingBecomesThePlaceholder(string? internalName)
    {
        Assert.Equal(UnitNaming.UnknownName, UnitNaming.DisplayNameFor(internalName, Table()));
    }

    [Theory]
    [InlineData("SovietNewTower", true)]
    [InlineData("RemixWonderTank", true)]
    [InlineData("abc", false)]
    [InlineData("Ab", false)]
    [InlineData("0x1234", false)]
    [InlineData(null, false)]
    public void InternalNameShapeCheck(string? text, bool expected)
    {
        Assert.Equal(expected, UnitTemplateNameProbe.LooksLikeInternalName(text));
    }

    [Fact]
    public void ProbePrefersTheNameThatMatchesTheKnownTable()
    {
        // 模板 +0x10 放一个指到 "SovietNewTower" 的指针（取证：探针就是从模板附近的字符串指针拿到的名字）。
        var memory = TemplateMemory(0x20000000, 0x10, 0x30000100, "SovietNewTower");
        var known = new[] { "SovietNewTower" };

        Assert.Equal("SovietNewTower", UnitTemplateNameProbe.TryRead(memory, 0x20000000, known));
    }

    [Fact]
    public void ProbeFallsBackToALookalikeCandidateWhenTheTableMisses()
    {
        var memory = TemplateMemory(0x20000000, 0x40, 0x30000200, "RemixWonderTank");

        Assert.Equal("RemixWonderTank", UnitTemplateNameProbe.TryRead(memory, 0x20000000, Array.Empty<string>()));
    }

    [Fact]
    public void ProbeIgnoresJunkStrings()
    {
        var memory = TemplateMemory(0x20000000, 0x10, 0x30000300, "hello world");

        Assert.Null(UnitTemplateNameProbe.TryRead(memory, 0x20000000, Array.Empty<string>()));
    }

    [Fact]
    public void ProbeNeverThrowsOnUnreadableMemory()
    {
        Assert.Null(UnitTemplateNameProbe.TryRead(new ThrowingMemory(), 0x20000000, Array.Empty<string>()));
        Assert.Null(UnitTemplateNameProbe.TryRead(new ThrowingMemory(), 0, Array.Empty<string>()));
    }

    private static FakeMemory TemplateMemory(uint template, int pointerOffset, uint stringAddress, string text)
    {
        var templateBytes = new byte[0x1000];
        BitConverter.GetBytes(stringAddress).CopyTo(templateBytes, pointerOffset);
        var stringBytes = new byte[0x1000];
        var encoded = Encoding.ASCII.GetBytes(text + "\0");
        var at = (int)(stringAddress % 0x1000);
        encoded.CopyTo(stringBytes, at);
        return new FakeMemory(new Dictionary<long, byte[]>
        {
            [template] = templateBytes,
            [stringAddress - (uint)at] = stringBytes,
        });
    }

    private sealed class FakeMemory(Dictionary<long, byte[]> regions) : IProcessMemory
    {
        public byte[] ReadBytes(nint address, int count)
        {
            foreach (var (start, bytes) in regions)
            {
                var offset = (long)address - start;
                if (offset >= 0 && offset + count <= bytes.Length)
                {
                    var slice = new byte[count];
                    Array.Copy(bytes, offset, slice, 0, count);
                    return slice;
                }
            }

            throw new InvalidOperationException($"未映射：0x{(long)address:X}");
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }

    private sealed class ThrowingMemory : IProcessMemory
    {
        public byte[] ReadBytes(nint address, int count) => throw new InvalidOperationException("读不了");

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }
}
