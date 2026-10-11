namespace Ra3Trainer.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4
}

public sealed record HotkeyGesture(int VirtualKey, HotkeyModifiers Modifiers, string DisplayText)
{
    private static readonly IReadOnlyDictionary<string, int> NamedVirtualKeys =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Page Up"] = 0x21,
            ["PageUp"] = 0x21,
            ["Page Down"] = 0x22,
            ["PageDown"] = 0x22,
            ["Delete"] = 0x2E,
            ["Del"] = 0x2E,
            ["-"] = 0xBD,
            ["="] = 0xBB,
            ["["] = 0xDB,
            ["]"] = 0xDD,
            ["\\"] = 0xDC,
            [";"] = 0xBA,
            ["/"] = 0xBF,
            [","] = 0xBC,
            ["."] = 0xBE,
            ["'"] = 0xDE
        };

    private static readonly IReadOnlyDictionary<int, string> VirtualKeyDisplayTexts =
        new Dictionary<int, string>
        {
            [0x21] = "Page Up",
            [0x22] = "Page Down",
            [0x2E] = "Delete",
            [0xBA] = ";",
            [0xBB] = "=",
            [0xBC] = ",",
            [0xBD] = "-",
            [0xBE] = ".",
            [0xBF] = "/",
            [0xDB] = "[",
            [0xDC] = "\\",
            [0xDD] = "]",
            [0xDE] = "'"
        };

    public static HotkeyGesture Parse(string text)
    {
        return TryParse(text, out var gesture)
            ? gesture
            : throw new FormatException($"Unsupported hotkey '{text}'.");
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var modifier in parts[..^1])
        {
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else
            {
                return false;
            }
        }

        if (!TryParseVirtualKey(parts[^1], out var virtualKey))
        {
            return false;
        }

        gesture = new HotkeyGesture(virtualKey, modifiers, FormatDisplayText(virtualKey, modifiers));
        return true;
    }

    private static bool TryParseVirtualKey(string keyText, out int virtualKey)
    {
        virtualKey = 0;
        // NumberStyles.None：只要纯十进制数字，拒绝 “#+70”/“# 70”/“#- 70” 这类含符号或空白的无效 token。
        // 范围排除 0x00-0x06：0 无效、0x01-0x06 是鼠标键，键盘钩子（WH_KEYBOARD_LL）永远收不到，
        // 放进来就是一个不会报错、永远不触发的死键。
        if (keyText.StartsWith('#') &&
            int.TryParse(keyText[1..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var rawVirtualKey) &&
            rawVirtualKey is > 0x06 and <= 0xFF)
        {
            virtualKey = rawVirtualKey;
            return true;
        }

        if (keyText.Length == 1)
        {
            var key = char.ToUpperInvariant(keyText[0]);
            if (key is >= 'A' and <= 'Z')
            {
                virtualKey = key;
                return true;
            }

            if (key is >= '0' and <= '9')
            {
                virtualKey = key;
                return true;
            }
        }

        // F 键分支同样用 NumberStyles.None：否则 “F 1”/“F+1” 会因默认的宽松解析被当成 F1 放行。
        if (keyText.Length is 2 or 3 &&
            keyText[0] is 'F' or 'f' &&
            int.TryParse(keyText[1..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = 0x6F + functionKey;
            return true;
        }

        return NamedVirtualKeys.TryGetValue(keyText, out virtualKey);
    }

    private static string FormatDisplayText(int virtualKey, HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(FormatKey(virtualKey));
        return string.Join("+", parts);
    }

    private static string FormatKey(int virtualKey)
    {
        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        if (virtualKey is >= 'A' and <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= '0' and <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        return VirtualKeyDisplayTexts.TryGetValue(virtualKey, out var text)
            ? text
            : $"#{virtualKey}";
    }
}
