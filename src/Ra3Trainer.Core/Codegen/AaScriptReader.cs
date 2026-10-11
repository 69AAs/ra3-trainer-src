using System.Globalization;
using System.Text.RegularExpressions;

namespace Ra3Trainer.Core.Codegen;

public static partial class AaScriptReader
{
    public static AaDocument Read(IReadOnlyList<string> lines)
    {
        var blocks = new List<AaBlock>();
        var initializers = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        AaBlock? currentBlock = null;
        string? pendingDataLabel = null;
        var inEnable = false;

        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Equals("[ENABLE]", StringComparison.OrdinalIgnoreCase))
            {
                inEnable = true;
                continue;
            }
            if (line.Equals("[disable]", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (!inEnable)
            {
                continue;
            }

            var label = LabelRegex().Match(line);
            if (label.Success)
            {
                var symbol = label.Groups["symbol"].Value;
                // 超长偏移（MustCode+FFFFFFFFFF:）会让 int.Parse 抛 OverflowException，
                // 原来没有任何上下文；这里点名是哪一行哪个符号的偏移写坏了。
                var offset = 0;
                if (label.Groups["offset"].Success &&
                    !int.TryParse(label.Groups["offset"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
                {
                    throw new InvalidOperationException(
                        $"标签 '{symbol}+{label.Groups["offset"].Value}' 的偏移超出 32 位范围（原文: {line}）。");
                }

                if (symbol.Equals("MustCode", StringComparison.OrdinalIgnoreCase) ||
                    symbol.Equals("MustCode2", StringComparison.OrdinalIgnoreCase))
                {
                    currentBlock = null;
                    pendingDataLabel = null;
                    currentBlock = new AaBlock(symbol, offset, []);
                    blocks.Add(currentBlock);
                }
                else if (symbol.Equals("iEnable", StringComparison.OrdinalIgnoreCase))
                {
                    currentBlock = null;
                    pendingDataLabel = null;
                    pendingDataLabel = offset == 0 ? symbol : $"{symbol}+{offset:X}";
                }
                else if (symbol.Equals("ra3_1.12.game", StringComparison.OrdinalIgnoreCase))
                {
                    currentBlock = null;
                    pendingDataLabel = null;
                }
                else if (currentBlock is not null)
                {
                    currentBlock.Lines.Add(line);
                }
                else
                {
                    pendingDataLabel = null;
                }
                continue;
            }

            if (pendingDataLabel is not null && line.StartsWith("db ", StringComparison.OrdinalIgnoreCase))
            {
                initializers[pendingDataLabel] = ParseDb(line);
                pendingDataLabel = null;
                continue;
            }

            if (pendingDataLabel is not null && line.Length > 0)
            {
                // 标签后面出现的不是 db 行 ⇒ 这个标签已经没承载物了，立刻作废。
                // 否则后面随便一个 db 行会被静默绑到这个标签上（生成错误的 iEnable 初值）。
                pendingDataLabel = null;
            }

            if (currentBlock is not null && !IsDirective(line))
            {
                currentBlock.Lines.Add(line);
            }
        }

        return new AaDocument(blocks, initializers);
    }

    private static bool IsDirective(string line)
    {
        return line.StartsWith("fullaccess", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("globalalloc", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("define", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("label", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ParseDb(string line)
    {
        // byte.Parse 对 "1FF" 这种超范围 token 抛 OverflowException，会把整份 .aa 读崩；
        // 这是"脚本写错了"，报出是哪一行哪个 token。
        var tokens = line[3..].Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Length > 2 ||
                !byte.TryParse(tokens[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                throw new InvalidOperationException($"db 行的字节 '{tokens[index]}' 不在 0..FF 范围（原文: {line}）。");
            }
        }

        return bytes;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    [GeneratedRegex(@"^(?<symbol>[A-Za-z_][\w\.]*)(\+(?<offset>[0-9A-Fa-f]+))?:\s*$")]
    private static partial Regex LabelRegex();
}

public sealed record AaDocument(
    IReadOnlyList<AaBlock> Blocks,
    IReadOnlyDictionary<string, byte[]> IEnableInitializers);

public sealed record AaBlock(string Symbol, int Offset, List<string> Lines);
