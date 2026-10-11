using System.Buffers.Binary;
using System.Text;
using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// 只读诊断：在游戏内存里找"单位内部名"字符串，再找指向它的指针，读出指针附近的 dword，
/// 用已知代号（code.txt 里的）当场校准，用来把隐藏/mod 单位（如 JapanMechaKing）的代号挖出来。
///
/// 为什么要这么绕：代号是运行时分配的类型 ID（EA 的 NameKeyGenerator 是顺序发号，不是名字哈希），
/// 数据文件里查不到、也算不出来，只能从游戏内存里读。
/// </summary>
public sealed class UnitTypeNameScanner
{
    private const int ChunkSize = 1 << 20;

    private readonly IMemoryRegionSource _regions;
    private readonly IProcessMemory _memory;

    public UnitTypeNameScanner(IMemoryRegionSource regions, IProcessMemory memory)
    {
        _regions = regions;
        _memory = memory;
    }

    public IReadOnlyList<string> Scan(
        IEnumerable<string> internalNames,
        IReadOnlyDictionary<uint, string>? knownCodes = null,
        int maxHitsPerName = 12,
        int maxPointersPerHit = 6,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = internalNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var needles = names
            .Select(name => (Name: name, Bytes: Encoding.ASCII.GetBytes(name)))
            // 比一块还长的名字在分块扫描里根本不可能匹配（span 最多 ChunkSize 字节），先挡掉。
            .Where(pair => pair.Bytes.Length >= 4 && pair.Bytes.Length <= ChunkSize)
            .ToArray();

        // 跨块回溯长度 = 最长名字 - 1（原来写死 192：名字比它长又正好跨块就永远找不到）。
        // 至少 4 字节前缀，上限留出一块，保证循环净步长 = ChunkSize - overlap > 0。
        var longestNeedle = needles.Length == 0 ? 0 : needles.Max(pair => pair.Bytes.Length);
        var chunkOverlap = Math.Clamp(longestNeedle - 1, 4, ChunkSize - 1);

        // 4 字节前缀 → 候选下标（穷人版多模式匹配：一次遍历就能筛掉 99.99% 的位置）
        var prefixes = new Dictionary<uint, List<int>>();
        for (var index = 0; index < needles.Length; index++)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(needles[index].Bytes);
            if (!prefixes.TryGetValue(key, out var list))
            {
                list = [];
                prefixes[key] = list;
            }

            list.Add(index);
        }

        var readable = _regions.EnumerateReadableRegions();
        var regionRanges = readable
            .Select(region => (Start: (uint)(int)region.BaseAddress, End: (uint)(int)region.EndAddress))
            .ToArray();
        var executableRanges = readable
            .Where(region => region.IsExecutable)
            .Select(region => (Start: (uint)(int)region.BaseAddress, End: (uint)(int)region.EndAddress))
            .ToArray();

        var buffer = new byte[ChunkSize + 256];
        var hitCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var hits = new List<(string Name, nint Address)>();
        long bytesScanned = 0;
        var failedChunks = 0;

        foreach (var region in _regions.EnumerateReadableRegions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (long offset = 0; offset < region.Size; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSize, region.Size - offset);
                var span = ReadInto(buffer, region.BaseAddress + (nint)offset, count, out var readOk);
                if (!readOk)
                {
                    // ReadInto 失败时给的是全零缓冲：那**不是**真实内存内容，必须记下来，
                    // 否则报告里看不出"这块没读到"（会当成"这里确实没有名字"）。
                    failedChunks++;
                }
                bytesScanned += count;

                for (var position = 0; position <= span.Length - 4; position++)
                {
                    var prefix = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
                    if (!prefixes.TryGetValue(prefix, out var candidates))
                    {
                        continue;
                    }

                    foreach (var index in candidates)
                    {
                        var needle = needles[index].Bytes;
                        if (position + needle.Length > span.Length || !span.Slice(position).StartsWith(needle))
                        {
                            continue;
                        }

                        // 名字后面必须是结束符或非标识符字符，避免匹配到更长的名字片段
                        var after = position + needle.Length;
                        if (after < span.Length && IsIdentifierChar(span[after]))
                        {
                            continue;
                        }

                        var name = needles[index].Name;
                        var total = hitCounts.GetValueOrDefault(name) + 1;
                        hitCounts[name] = total;

                        if (total <= maxHitsPerName)
                        {
                            hits.Add((name, region.BaseAddress + (nint)offset + position));
                        }
                    }
                }

                if (count < ChunkSize)
                {
                    break;
                }

                // 让跨块的字符串也能被找到（按最长名字算重叠，不是写死 192）
                offset -= chunkOverlap;
            }
        }

        var header = $"扫描完成：内存 {bytesScanned / 1048576.0:F0} MB，命中单位名 {hitCounts.Count} / {names.Length} 个" +
            (knownCodes is { Count: > 0 } ? $"，用 {knownCodes.Count} 个已知代号校准 ★" : string.Empty);
        if (failedChunks > 0)
        {
            header += $"；{failedChunks} 块读取失败（按零处理，可能漏命中）";
        }

        var lines = new List<string> { header };

        if (hits.Count == 0)
        {
            lines.Add("没找到任何内部名字符串：游戏可能还没进到读地图/单位表的阶段。");
            return lines;
        }

        // 第二遍：找指向这些字符串的指针（类型表里一般存着名字指针）
        var addressSet = hits.Select(hit => (uint)(int)hit.Address).ToHashSet();
        var pointers = new List<(nint Where, uint Target)>();
        var pointerBudget = hits.Count * maxPointersPerHit;

        foreach (var region in _regions.EnumerateReadableRegions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pointers.Count >= pointerBudget)
            {
                break;
            }

            for (long offset = 0; offset < region.Size; offset += ChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkSize, region.Size - offset);
                var span = ReadInto(buffer, region.BaseAddress + (nint)offset, count, out var readOk);
                if (!readOk)
                {
                    failedChunks++;
                }

                for (var position = 0; position <= span.Length - 4; position += 4)
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
                    if (addressSet.Contains(value) && !IsInside((uint)(int)(region.BaseAddress + (nint)offset + position), executableRanges))
                    {
                        // 只收数据区里的指针：代码区里也能搜到"字符串地址"，但旁边不是类型表（会读出机器码）
                        pointers.Add((region.BaseAddress + (nint)offset + position, value));
                    }
                }

                if (count < ChunkSize)
                {
                    break;
                }
            }
        }

        var pointersByName = new Dictionary<nint, List<nint>>();
        foreach (var pointer in pointers)
        {
            if (!pointersByName.TryGetValue((nint)(int)pointer.Target, out var list))
            {
                list = [];
                pointersByName[(nint)(int)pointer.Target] = list;
            }

            if (list.Count < maxPointersPerHit)
            {
                list.Add(pointer.Where);
            }
        }

        // 关键产出：类型表里的版式是 [ … 代号在名字指针 -0x14 … ][ 名字指针 ]。
        // 用 84 个已知代号校准过（-0x14 全部对得上），所以这里直接给出"内部名 → 代号"。
        const int CodeOffsetFromNamePointer = -0x14;
        var derived = new List<(string Name, uint Code)>();
        var details = new List<string>();

        foreach (var (name, address) in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pointerList = pointersByName.TryGetValue(address, out var list) ? list : [];
            var codes = new List<uint>();

            foreach (var where in pointerList)
            {
                var code = TryReadDword(where + CodeOffsetFromNamePointer);
                if (code is not (uint value and >= 0x1000 and < 0xFFF00000))
                {
                    continue;
                }

                var knownName = knownCodes is not null && knownCodes.TryGetValue(value, out var resolved) ? resolved : null;
                var known = knownName is not null;
                if (!known && IsUnusableCandidate(value, regionRanges))
                {
                    // 填充图案（0xCCCCCCCC）或"其实就是个指针"—— 不是代号，丢掉
                    details.Add($"     指针@0x{(uint)(int)where:X8}  -0x14 = 0x{value:X8}  ✗丢弃（{(IsFillPattern(value) ? "填充图案" : "指向内存的指针")}）");
                    continue;
                }

                codes.Add(value);
                var mark = known
                    ? $"  ★已知代号={knownName}"
                    : "  ← 清单里没有（未验证候选）";
                details.Add($"     指针@0x{(uint)(int)where:X8}  代号@-0x14 = 0x{value:X8}{mark}");
            }

            var star = codes.Any(code => knownCodes is not null && knownCodes.ContainsKey(code)) ? "★" : " ";
            details.Insert(0, $"{star} {name}  字符串@0x{(uint)(int)address:X8}" +
                (hitCounts[name] > 1 ? $"（内存里 {hitCounts[name]} 处）" : string.Empty) +
                (pointerList.Count > 0 ? $"  指针 {pointerList.Count} 个" : "  指针 0 个"));

            foreach (var code in codes.Distinct())
            {
                derived.Add((name, code));
            }
        }

        lines.Add(string.Empty);
        lines.Add($"=== 代号表（名字指针 -0x14）共 {derived.Count} 条 ===");
        foreach (var (name, code) in derived
            .GroupBy(item => item.Name)
            .Select(group => (Name: group.Key, Code: group.First().Code))
            .OrderByDescending(item => knownCodes is not null && !knownCodes.ContainsKey(item.Code))
            .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            var label = knownCodes is not null && knownCodes.TryGetValue(code, out var knownName)
                ? knownName
                : "（清单里没有·未验证）";
            lines.Add($"{name}\t0x{code:X8}\t{label}");
        }

        lines.Add(string.Empty);
        lines.Add("=== 明细 ===");
        lines.AddRange(details);

        return lines;
    }

    private static bool IsFillPattern(uint value) =>
        value is 0x00000000 or 0xFFFFFFFF or 0xCCCCCCCC or 0xCDCDCDCD or 0xDDDDDDDD or 0xFEEEFEEE
            or 0x55555555 or 0xAAAAAAAA or 0x90909090 or 0xABABABAB;

    /// <summary>值落在某个可读区域里 = 它是个指针，不是类型代号（曾被这类值骗过）。</summary>
    private static bool IsInside(uint value, (uint Start, uint End)[] regions)
    {
        foreach (var (start, end) in regions)
        {
            if (value >= start && value < end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnusableCandidate(uint value, (uint Start, uint End)[] regions) =>
        IsFillPattern(value) || IsInside(value, regions);

    private uint? TryReadDword(nint address)
    {
        try
        {
            return BitConverter.ToUInt32(_memory.ReadBytes(address, 4));
        }
        catch
        {
            return null;
        }
    }

    private Span<byte> ReadInto(byte[] buffer, nint address, int count, out bool ok)
    {
        var span = buffer.AsSpan(0, count);
        try
        {
            var read = _memory.ReadBytes(address, count).AsSpan();
            read.CopyTo(span);
            ok = true;
        }
        catch
        {
            // 读不到的块按零填充继续扫（区域边界上单块失败不该让整次扫描作废），
            // 但调用方要把 ok=false 记进报告，别当成"这块内存真的是零"。
            span.Clear();
            ok = false;
        }

        return span;
    }

    private static bool IsIdentifierChar(byte value) =>
        value is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_';
}
