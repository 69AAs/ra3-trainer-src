namespace Ra3Trainer.Core.Memory;

public sealed class AddressResolver
{
    private readonly nint _moduleBase;
    private readonly IReadOnlyDictionary<string, nint> _symbols;

    public AddressResolver(nint moduleBase, IReadOnlyDictionary<string, nint> symbols)
    {
        _moduleBase = moduleBase;
        _symbols = new Dictionary<string, nint>(symbols, StringComparer.OrdinalIgnoreCase);
    }

    public nint Resolve(string expression)
    {
        var parts = expression.Split('+', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            if (_symbols.TryGetValue(parts[0], out var symbolAddress))
            {
                return symbolAddress;
            }
            throw new InvalidOperationException($"Unknown address expression '{expression}'.");
        }

        int offset;
        try
        {
            offset = Convert.ToInt32(parts[1], 16);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // 全类只抛 InvalidOperationException（调用方如 ReadOnlyProbe.ResolveOptional 就按它兜底）。
            // Convert.ToInt32 抛的是 Format/OverflowException（如 "iEnable+FFFFFFFF"），逃出去等于崩一整屏。
            throw new InvalidOperationException($"Invalid address offset in '{expression}'.", ex);
        }

        if (parts[0].Equals("ra3_1.12.game", StringComparison.OrdinalIgnoreCase))
        {
            return _moduleBase + offset;
        }

        if (_symbols.TryGetValue(parts[0], out var baseAddress))
        {
            return baseAddress + offset;
        }

        throw new InvalidOperationException($"Unknown symbol '{parts[0]}'.");
    }
}
