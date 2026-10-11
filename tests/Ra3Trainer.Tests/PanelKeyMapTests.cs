using Ra3Trainer.App.ViewModels;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 小键盘从"导航键"改成"按钮"：每个键都有唯一语义，面板上哪一行印着哪个键就按哪个键。
/// 用户的实测：旧映射下 Num1 按了 3 次、Num7 按了 1 次，全都毫无反应（那 4 个键根本没定义）。
/// </summary>
public sealed class PanelKeyMapTests
{
    [Theory]
    [InlineData(PanelKeyMap.Numpad0, PanelKeyCommand.Digit0)]
    [InlineData(PanelKeyMap.Numpad1, PanelKeyCommand.Digit1)]
    [InlineData(PanelKeyMap.Numpad2, PanelKeyCommand.Digit2)]
    [InlineData(PanelKeyMap.Numpad3, PanelKeyCommand.Digit3)]
    [InlineData(PanelKeyMap.Numpad4, PanelKeyCommand.Digit4)]
    [InlineData(PanelKeyMap.Numpad5, PanelKeyCommand.Digit5)]
    [InlineData(PanelKeyMap.Numpad6, PanelKeyCommand.Digit6)]
    [InlineData(PanelKeyMap.Numpad7, PanelKeyCommand.Digit7)]
    [InlineData(PanelKeyMap.Numpad8, PanelKeyCommand.Digit8)]
    [InlineData(PanelKeyMap.Numpad9, PanelKeyCommand.Digit9)]
    [InlineData(PanelKeyMap.Add, PanelKeyCommand.Plus)]
    [InlineData(PanelKeyMap.Subtract, PanelKeyCommand.Minus)]
    [InlineData(PanelKeyMap.Multiply, PanelKeyCommand.Times)]
    [InlineData(PanelKeyMap.Divide, PanelKeyCommand.Divide)]
    [InlineData(PanelKeyMap.Decimal, PanelKeyCommand.Dot)]
    public void EveryPanelKeyMapsToItsOwnCommand(int virtualKey, PanelKeyCommand expected)
    {
        Assert.Equal(expected, PanelKeyMap.Map(virtualKey));
    }

    [Fact]
    public void SeparatorIsDotAndPagingAliasesKeepTheirDigits()
    {
        // 某些键盘布局的小键盘"点"发的是 Separator，与 Decimal 同一个意思。
        Assert.Equal(PanelKeyCommand.Dot, PanelKeyMap.Map(PanelKeyMap.Separator));
        // 换页 alias（口径 B）：PgUp/PgDn 必须仍然直达 7/9，掉成 None 会让这两个键按下没反应。
        Assert.Equal(PanelKeyCommand.Digit7, PanelKeyMap.Map(PanelKeyMap.PageUp));
        Assert.Equal(PanelKeyCommand.Digit9, PanelKeyMap.Map(PanelKeyMap.PageDown));
    }

    [Fact]
    public void NoNumpadKeyIsDead()
    {
        // 小键盘 16 个键全部有含义：不会再出现"按了没反应"。
        for (var virtualKey = 0x60; virtualKey <= 0x6F; virtualKey++)
        {
            Assert.NotEqual(PanelKeyCommand.None, PanelKeyMap.Map(virtualKey));
        }
    }

    [Fact]
    public void EveryCommandHasAUniqueLabelSoRowsCannotCollide()
    {
        // 行表按标签派发：两个命令共用一个标签的话，一个键会同时命中两行。
        var commands = new[]
        {
            PanelKeyCommand.Digit0, PanelKeyCommand.Digit1, PanelKeyCommand.Digit2, PanelKeyCommand.Digit3,
            PanelKeyCommand.Digit4, PanelKeyCommand.Digit5, PanelKeyCommand.Digit6, PanelKeyCommand.Digit7,
            PanelKeyCommand.Digit8, PanelKeyCommand.Digit9, PanelKeyCommand.Plus, PanelKeyCommand.Minus,
            PanelKeyCommand.Times, PanelKeyCommand.Divide, PanelKeyCommand.Dot
        };
        var specs = commands.Select(PanelKeyMap.Spec).ToArray();
        Assert.Equal(commands.Length, specs.Distinct().Count());
        Assert.DoesNotContain(string.Empty, specs);
        Assert.Equal(string.Empty, PanelKeyMap.Spec(PanelKeyCommand.None));
    }

    [Fact]
    public void Mapping_IsTotalAndFailsClosedForEveryOtherCode()
    {
        for (var virtualKey = 0; virtualKey <= 255; virtualKey++)
        {
            if (virtualKey is >= 0x60 and <= 0x6F) continue;   // 小键盘那 16 个键，上面已逐条验过
            // PgUp/PgDn 是口径 B 里"换页"的别名（NumLock 关掉时小键盘 9/3 也可能发这两个码），
            // 它们不是"放行给游戏"的键，单独验，不算漏网的键。
            if (virtualKey is PanelKeyMap.PageUp or PanelKeyMap.PageDown) continue;
            Assert.Equal(PanelKeyCommand.None, PanelKeyMap.Map(virtualKey));
        }
    }

    [Theory]
    [InlineData(0x57)]  // W — WASD 必须原样放行
    [InlineData(0x41)]  // A
    [InlineData(0x25)]  // Left arrow
    [InlineData(0x26)]  // Up arrow
    [InlineData(0x70)]  // F1
    [InlineData(0x2D)]  // Ins（开关面板模式的键，不是面板上的键）
    [InlineData(0x20)]  // Space
    public void GameKeys_NeverBecomePanelCommands(int virtualKey)
    {
        Assert.Equal(PanelKeyCommand.None, PanelKeyMap.Map(virtualKey));
    }

    [Theory]
    [InlineData(PanelKeyMap.Insert, PanelKeyCommand.Digit0)]
    [InlineData(PanelKeyMap.End, PanelKeyCommand.Digit1)]
    [InlineData(PanelKeyMap.Down, PanelKeyCommand.Digit2)]
    [InlineData(PanelKeyMap.PageDown, PanelKeyCommand.Digit3)]
    [InlineData(PanelKeyMap.Left, PanelKeyCommand.Digit4)]
    [InlineData(PanelKeyMap.Clear, PanelKeyCommand.Digit5)]
    [InlineData(PanelKeyMap.Right, PanelKeyCommand.Digit6)]
    [InlineData(PanelKeyMap.Home, PanelKeyCommand.Digit7)]
    [InlineData(PanelKeyMap.Up, PanelKeyCommand.Digit8)]
    [InlineData(PanelKeyMap.PageUp, PanelKeyCommand.Digit9)]
    [InlineData(PanelKeyMap.Delete, PanelKeyCommand.Dot)]
    public void NumLockOff_TheNumpadsNavigationTwinsStillReachTheirDigit(int virtualKey, PanelKeyCommand expected)
    {
        Assert.Equal(expected, PanelKeyMap.MapNumpadNavigation(virtualKey, isExtended: false));
    }

    [Theory]
    [InlineData(PanelKeyMap.Insert)]   // 真 Insert：留着开关面板模式，面板绝不能吃掉它
    [InlineData(PanelKeyMap.Left)]
    [InlineData(PanelKeyMap.Up)]
    [InlineData(PanelKeyMap.Right)]
    [InlineData(PanelKeyMap.Down)]
    [InlineData(PanelKeyMap.Home)]
    [InlineData(PanelKeyMap.End)]
    [InlineData(PanelKeyMap.PageUp)]
    [InlineData(PanelKeyMap.PageDown)]
    [InlineData(PanelKeyMap.Delete)]
    public void NumLockOff_TheDedicatedClusterIsLeftAlone(int virtualKey)
    {
        Assert.Equal(PanelKeyCommand.None, PanelKeyMap.MapNumpadNavigation(virtualKey, isExtended: true));
    }

    [Theory]
    [InlineData(0x57)]  // W
    [InlineData(0x41)]  // A
    [InlineData(0x70)]  // F1
    [InlineData(0x20)]  // Space
    public void GameKeys_NeverBecomeNumpadNavigationCommands(int virtualKey)
    {
        Assert.Equal(PanelKeyCommand.None, PanelKeyMap.MapNumpadNavigation(virtualKey, isExtended: false));
    }
}
