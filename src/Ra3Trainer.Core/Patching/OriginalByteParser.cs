using System.Globalization;
using System.Text.RegularExpressions;

namespace Ra3Trainer.Core.Patching;

public static partial class OriginalByteParser
{
    public static byte[] Parse(IReadOnlyList<string> originalAssembly)
    {
        ArgumentNullException.ThrowIfNull(originalAssembly);
        // 元素为 null 时原来在 .Trim() 上抛裸 NRE（清单是从 JSON 反序列化来的）。
        for (var index = 0; index < originalAssembly.Count; index++)
        {
            if (originalAssembly[index] is null)
            {
                throw new InvalidOperationException($"original_assembly[{index}] 为 null。");
            }
        }

        if (originalAssembly.Count == 1)
        {
            var line = originalAssembly[0].Trim();
            var match = DbLineRegex().Match(line);
            if (match.Success)
            {
                var tokens = match.Groups["bytes"].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    // "db " 能过正则（字符类含空格）但切出来是空：原来静默返回空数组，
                    // 等于把这一处的补丁长度当成 0，下面按长度做的校验全被绕过。
                    throw new InvalidOperationException($"original_assembly 的 db 行没有字节（原文: '{line}'）。");
                }

                return tokens.Select(value => ParseByte(value, line)).ToArray();
            }
        }

        return RestoreAssemblyEncoder.Encode(originalAssembly);
    }

    private static byte ParseByte(string value, string line)
    {
        // 正则允许任意长度的十六进制串（"db 1FF" 也匹配），byte.Parse 会抛 OverflowException：
        // 那是清单写错了，要报出是哪一行哪个 token，别让一句 Overflow 把加载流程带崩。
        if (value.Length > 2 || !byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"original_assembly 里的字节 '{value}' 不在 0..FF 范围（原文: {line}）。");
        }

        return parsed;
    }

    [GeneratedRegex(@"^db\s+(?<bytes>[0-9A-Fa-f ]+)$")]
    private static partial Regex DbLineRegex();
}
