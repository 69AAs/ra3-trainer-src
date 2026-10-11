using System.Linq;
using Ra3Trainer.Core.Hotkeys;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+F1", 0x70, HotkeyModifiers.Control, "Ctrl+F1")]
    [InlineData("P", 0x50, HotkeyModifiers.None, "P")]
    [InlineData("-", 0xBD, HotkeyModifiers.None, "-")]
    [InlineData("=", 0xBB, HotkeyModifiers.None, "=")]
    [InlineData("Page Up", 0x21, HotkeyModifiers.None, "Page Up")]
    [InlineData("Page Down", 0x22, HotkeyModifiers.None, "Page Down")]
    [InlineData("[", 0xDB, HotkeyModifiers.None, "[")]
    [InlineData("]", 0xDD, HotkeyModifiers.None, "]")]
    [InlineData("\\", 0xDC, HotkeyModifiers.None, "\\")]
    [InlineData(";", 0xBA, HotkeyModifiers.None, ";")]
    [InlineData("/", 0xBF, HotkeyModifiers.None, "/")]
    [InlineData("Delete", 0x2E, HotkeyModifiers.None, "Delete")]
    [InlineData(",", 0xBC, HotkeyModifiers.None, ",")]
    [InlineData(".", 0xBE, HotkeyModifiers.None, ".")]
    [InlineData("L", 0x4C, HotkeyModifiers.None, "L")]
    [InlineData("X", 0x58, HotkeyModifiers.None, "X")]
    [InlineData("J", 0x4A, HotkeyModifiers.None, "J")]
    [InlineData("I", 0x49, HotkeyModifiers.None, "I")]
    public void TryParseSupportsSourceTrainerScreenshotKeys(
        string text,
        int expectedVirtualKey,
        HotkeyModifiers expectedModifiers,
        string expectedDisplayText)
    {
        var parsed = HotkeyGesture.TryParse(text, out var gesture);

        Assert.True(parsed);
        Assert.NotNull(gesture);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        Assert.Equal(expectedDisplayText, gesture.DisplayText);
    }

    [Theory]
    [InlineData("#7", 7)]     // 下限边界：0x07 是第一个可接受值（0x01-0x06 是鼠标键，键盘钩子收不到）
    [InlineData("#10", 10)]
    [InlineData("#255", 255)] // 上限边界：0xFF
    public void TryParseAcceptsRawVirtualKeyBoundariesAndRoundTripsDisplayText(string text, int expectedVirtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        Assert.Equal("#" + expectedVirtualKey, gesture.DisplayText);
        // 没有显示名映射的 VK 回显成 #N，必须能再解析回同一个 VK。
        Assert.True(HotkeyGesture.TryParse(gesture.DisplayText, out var roundTrip));
        Assert.Equal(gesture, roundTrip);
    }

    [Theory]
    [InlineData("#0")]    // 0 无效
    [InlineData("#6")]    // 0x01-0x06 是鼠标键：键盘钩子永远收不到，放进来是永不触发的死键
    [InlineData("#256")]  // 上限之上
    [InlineData("#-1")]   // NumberStyles.None 拒绝符号
    [InlineData("# 10")]  // 拒绝空白
    [InlineData("#abc")]  // 拒绝十六进制字母
    [InlineData("#")]
    public void TryParseRejectsRawVirtualKeysOutsideTheKeyboardRange(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("Ctrl+")]        // 只有修饰键
    [InlineData("Ctrl+Foo")]     // 未知键名
    [InlineData("Win+F1")]       // 不支持的修饰键
    public void TryParseRejectsUnsupportedTextWithoutProducingAGesture(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    [Theory]
    [InlineData("Alt+F1", 0x70, HotkeyModifiers.Alt, "Alt+F1")]
    [InlineData("Shift+[", 0xDB, HotkeyModifiers.Shift, "Shift+[")]
    [InlineData("Shift+[ ", 0xDB, HotkeyModifiers.Shift, "Shift+[")]
    [InlineData("Ctrl+Shift+F1", 0x70, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+F1")]
    [InlineData("Shift+Ctrl+F1", 0x70, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+F1")]
    [InlineData("Ctrl+Alt+Shift+F24", 0x6F + 24, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
        "Ctrl+Alt+Shift+F24")]
    public void TryParseOrsModifiersAndFormatsThemInCanonicalOrder(
        string text, int expectedVirtualKey, HotkeyModifiers expectedModifiers, string expectedDisplayText)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        Assert.Equal(expectedDisplayText, gesture.DisplayText);
    }

    [Theory]
    [InlineData("ctrl+f1", 0x70, "Ctrl+F1")]
    [InlineData("CTRL + F1", 0x70, "Ctrl+F1")]
    [InlineData("control+f1", 0x70, "Ctrl+F1")]
    [InlineData("  f1  ", 0x70, "F1")]
    [InlineData("p", 0x50, "P")]
    public void TryParseNormalisesCaseAndWhitespaceAndRoundTripsDisplayText(string text, int expectedVirtualKey, string expectedDisplayText)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(expectedDisplayText, gesture.DisplayText);
        Assert.True(HotkeyGesture.TryParse(gesture.DisplayText, out var roundTrip));
        Assert.Equal(gesture, roundTrip);
    }

    [Theory]
    [InlineData("#189", 0xBD, "-")]
    [InlineData("#187", 0xBB, "=")]
    [InlineData("#219", 0xDB, "[")]
    [InlineData("#221", 0xDD, "]")]
    [InlineData("#220", 0xDC, "\\")]
    [InlineData("#186", 0xBA, ";")]
    [InlineData("#188", 0xBC, ",")]
    [InlineData("#190", 0xBE, ".")]
    [InlineData("#191", 0xBF, "/")]
    [InlineData("#222", 0xDE, "'")]
    public void TryParseSupportsRawVirtualKeyFallbacks(
        string text,
        int expectedVirtualKey,
        string expectedDisplayText)
    {
        var parsed = HotkeyGesture.TryParse(text, out var gesture);

        Assert.True(parsed);
        Assert.NotNull(gesture);
        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        Assert.Equal(expectedDisplayText, gesture.DisplayText);
    }

    /// <summary>
    /// 心灵控制的键盘快捷键（`MainViewModel.OwnershipHotkey`）必须与清单里 32 条功能键**不撞**。
    /// 这条是把"新键位选得对不对"钉住的判据之一 —— 键位既然要写死成常量，就让测试替它守门，
    /// 别等用户按一下同时触发两条功能才发现（他在真机上就是这么发现"新键撞了游戏自己的键"的）。
    /// 另一半（游戏 `Profiles\h\hotkeys.ini` 有没有占）在机器上查，依据写进
    /// `references/hotkey-occupancy-check-both-sides.md`：游戏占 ALT+D（坚守位置），**CTRL+D 没占**。
    /// </summary>
    [Fact]
    public void OwnershipHotkeyDoesNotCollideWithAnyManifestFeatureHotkey()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+D", out var ownership));
        Assert.Equal(0x44, ownership.VirtualKey);                    // 'D'
        Assert.Equal(HotkeyModifiers.Control, ownership.Modifiers);

        // ⚠ 清单里**不是每条 hotkey 都能解析**（实测 'PP' 就是解析不了的死写法：两字符、既不是 F 键也不是符号键，
        //   TryParse 直接 false）—— 这里只比"解析得出来的那些"，别让一条本来就坏的写法把这条断言拖红。
        var taken = TestAssets.LoadManifest().Features
            .Where(feature => !string.IsNullOrWhiteSpace(feature.Hotkey))
            .Select(feature => HotkeyGesture.TryParse(feature.Hotkey, out var parsed) ? parsed : null)
            .Where(gesture => gesture is not null)
            .Select(gesture => gesture!)
            .ToArray();
        Assert.NotEmpty(taken);

        Assert.DoesNotContain(taken, gesture =>
            gesture.VirtualKey == ownership.VirtualKey && gesture.Modifiers == ownership.Modifiers);
    }
}
