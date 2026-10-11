using System.Text;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class UnitTypeNameScannerTests
{
    [Fact]
    public void CanceledScanDoesNotEnumerateOrReadMemory()
    {
        var memory = new CancelingMemory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new UnitTypeNameScanner(memory, memory)
            .Scan(["TestUnit"], cancellationToken: cancellation.Token));
        Assert.Equal(0, memory.Enumerations);
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void CancellationBetweenChunksStopsFurtherReads()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new CancelingMemory { AfterRead = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => new UnitTypeNameScanner(memory, memory)
            .Scan(["TestUnit"], cancellationToken: cancellation.Token));
        Assert.Equal(1, memory.Reads);
        // 钉住“取消前不许提前枚举”：只有读取区间用的 1 次 + 主循环入口的 1 次，共 2 次。
        Assert.Equal(2, memory.Enumerations);
    }

    [Fact]
    public void CancellationDuringPointerScanStopsAfterPassOneCompleted()
    {
        // 取消点落在第二遍（指针扫描）里：第一遍必须整块跑完（并找到名字），第二遍入口枚举一次后
        // 在读第一块时就取消 —— 说明第二遍同样受取消约束，不会“先急切枚举/多读再检查”。
        using var cancellation = new CancellationTokenSource();
        var memory = new FlatMemory(Base, 0x4000) { AfterRead = read => { if (read == 2) cancellation.Cancel(); } };
        memory.Poke(Base + 0x100, Encoding.ASCII.GetBytes("TestUnit\0"));

        Assert.Throws<OperationCanceledException>(() => new UnitTypeNameScanner(memory, memory)
            .Scan(["TestUnit"], cancellationToken: cancellation.Token));

        // 枚举 3 次 = 读取区间(1) + 第一遍主循环入口(2) + 第二遍入口(3)；读 2 次 = 第一遍 1 块 + 第二遍 1 块。
        Assert.Equal(3, memory.Enumerations);
        Assert.Equal(2, memory.Reads);
    }

    private sealed class CancelingMemory : IProcessMemory, IMemoryRegionSource
    {
        public int Enumerations { get; private set; }
        public int Reads { get; private set; }
        public Action? AfterRead { get; init; }
        public IReadOnlyList<MemoryRegion> EnumerateReadableRegions()
        {
            Enumerations++;
            return [new MemoryRegion(0x5000, 2 << 20)];
        }
        public byte[] ReadBytes(nint address, int count)
        {
            Reads++;
            AfterRead?.Invoke();
            return new byte[count];
        }
        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }

    /// <summary>
    /// 把一段字节数组当游戏内存：区域内按绝对地址读写。
    /// 越界读的行为与生产端 UnitTypeNameScanner.ReadInto 对齐 —— **补零不抛**（生产端 catch 后清空并记 failedChunks），
    /// 同时把越界请求记进 <see cref="OverrunReads"/>：用例据此断言“没发生越界读”，
    /// 否则 chunk 边界/offset 回归会被补零静默掩盖。
    /// </summary>
    private sealed class FlatMemory : IProcessMemory, IMemoryRegionSource
    {
        private readonly byte[] _bytes;
        private readonly nint _base;

        public FlatMemory(nint baseAddress, int size)
        {
            _base = baseAddress;
            _bytes = new byte[size];
        }

        public int Reads { get; private set; }
        public int Enumerations { get; private set; }
        public List<(nint Address, int Count)> OverrunReads { get; } = [];
        public Action<int>? AfterRead { get; init; }

        public void Poke(nint address, byte[] data) => data.CopyTo(_bytes, (int)(address - _base));

        public void PokeDword(nint address, uint value) =>
            Poke(address, BitConverter.GetBytes(value));

        public byte[] ReadBytes(nint address, int count)
        {
            Reads++;
            AfterRead?.Invoke(Reads);
            var result = new byte[count];
            var offset = (int)(address - _base);
            if (offset < 0 || offset + count > _bytes.Length)
            {
                OverrunReads.Add((address, count));
                return result; // 全零，等同生产端 catch 后的 span.Clear()。
            }

            Array.Copy(_bytes, offset, result, 0, count);
            return result;
        }

        public void WriteBytes(nint address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();

        public IReadOnlyList<MemoryRegion> EnumerateReadableRegions()
        {
            Enumerations++;
            return [new MemoryRegion(_base, _bytes.Length)];
        }
    }

    private static readonly nint Base = 0x00400000;

    [Fact]
    public void FindsNameAndFlagsKnownCodeNearPointer()
    {
        var memory = new FlatMemory(Base, 0x20000);

        // 内存里放：内部名字符串
        var nameAddress = Base + 0x1000;
        memory.Poke(nameAddress, Encoding.ASCII.GetBytes("SovietAntiVehicleVehicleTech3\0"));

        // 类型表项：指向名字的指针 + 代号（真机校准过的版式：代号就在名字指针 -0x14）
        var entryAddress = Base + 0x8000;
        memory.PokeDword(entryAddress, (uint)(int)nameAddress);
        memory.PokeDword(entryAddress - 0x14, 0x9E45383C);

        var scanner = new UnitTypeNameScanner(memory, memory);
        var lines = scanner.Scan(
            ["SovietAntiVehicleVehicleTech3", "JAPANMECHAKING"],
            new Dictionary<uint, string> { [0x9E45383C] = "苏联 · 天启坦克" });

        var text = string.Join("\n", lines);
        Assert.Contains("SovietAntiVehicleVehicleTech3", text);
        Assert.Contains("0x9E45383C", text);
        Assert.Contains("代号@-0x14 = 0x9E45383C", text);
        Assert.Contains("★已知代号=苏联 · 天启坦克", text);
        Assert.Contains("命中单位名 1", text);
        // 汇总表里也要能直接抄走：内部名 <TAB> 代号 <TAB> 中文名
        Assert.Contains("SovietAntiVehicleVehicleTech3\t0x9E45383C\t苏联 · 天启坦克", text);
        Assert.Contains("=== 代号表（名字指针 -0x14）共 1 条 ===", text);
        // 内存里没有的名字不该出现
        Assert.DoesNotContain("JAPANMECHAKING", text);
        // 扫描过程中不许出现越界读（否则 chunk 边界/offset 回归会被补零静默掩盖）。
        Assert.Empty(memory.OverrunReads);
    }

    [Fact]
    public void IgnoresNamePrefixThatIsPartOfLongerName()
    {
        var memory = new FlatMemory(Base, 0x4000);
        memory.Poke(Base + 0x100, Encoding.ASCII.GetBytes("AlliedPrismTankUpgraded\0"));

        var scanner = new UnitTypeNameScanner(memory, memory);
        var lines = scanner.Scan(["AlliedPrismTank"]);

        Assert.Contains("命中单位名 0", string.Join("\n", lines));
        Assert.Empty(memory.OverrunReads);
    }

    [Fact]
    public void ReportsNothingFoundWhenMemoryIsEmpty()
    {
        var memory = new FlatMemory(Base, 0x4000);
        var scanner = new UnitTypeNameScanner(memory, memory);
        var lines = scanner.Scan(["JapanMechaKing"]);

        Assert.Contains(lines, line => line.Contains("没找到任何内部名字符串"));
        Assert.Empty(memory.OverrunReads);
    }

    [Fact]
    public void FindsNameStringEvenWithoutAnyPointer()
    {
        var memory = new FlatMemory(Base, 0x4000);
        memory.Poke(Base + 0x200, Encoding.ASCII.GetBytes("JapanMechaKing\0"));

        var scanner = new UnitTypeNameScanner(memory, memory);
        var lines = scanner.Scan(["JapanMechaKing"]);

        var text = string.Join("\n", lines);
        Assert.Contains("JapanMechaKing", text);
        Assert.Contains("指针 0 个", text);
        Assert.Empty(memory.OverrunReads);
    }
}
