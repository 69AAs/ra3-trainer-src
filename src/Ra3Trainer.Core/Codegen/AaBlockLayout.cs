namespace Ra3Trainer.Core.Codegen;

public static class AaBlockLayout
{
    public static byte[] Build(string symbol, int maxSize, IEnumerable<AaEncodedBlock> blocks)
    {
        // blocks 是调用方（BootstrapCodeBuilder）喂进来的编码结果：null 序列或 null 条目在这里
        // 只会变成 NullReferenceException，看不出是编码链哪一环坏了，所以点名报错。
        ArgumentNullException.ThrowIfNull(blocks);
        var matched = new List<AaEncodedBlock>();
        foreach (var candidate in blocks)
        {
            if (candidate is null)
            {
                throw new InvalidOperationException($"{symbol} 的编码块集合里有 null 条目。");
            }

            if (candidate.Bytes is null)
            {
                throw new InvalidOperationException($"编码块 {candidate.Symbol}+{candidate.Offset:X} 的字节为 null。");
            }

            if (candidate.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(candidate);
            }
        }

        var output = new List<byte>(maxSize);
        foreach (var block in matched.OrderBy(block => block.Offset))
        {
            if (block.Offset < output.Count)
            {
                throw new InvalidOperationException(
                    $"{block.Symbol}+{block.Offset:X} overlaps previous encoded bytes ending at +{output.Count:X}.");
            }

            while (output.Count < block.Offset)
            {
                output.Add(0);
            }

            output.AddRange(block.Bytes);
            if (output.Count > maxSize)
            {
                throw new InvalidOperationException($"{symbol} exceeds allocated size 0x{maxSize:X}.");
            }
        }

        return output.ToArray();
    }
}

public sealed record AaEncodedBlock(string Symbol, int Offset, byte[] Bytes);
