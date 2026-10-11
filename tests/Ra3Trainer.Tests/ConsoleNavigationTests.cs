using Ra3Trainer.App.ViewModels;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class ConsoleNavigationTests
{
    [Theory]
    [InlineData("启动")]
    [InlineData("增援")]
    [InlineData("功能")]
    [InlineData("诊断")]
    public void Navigation_SelectsExactlyOneStableSection(string section)
    {
        var navigation = new ConsoleNavigation();
        Assert.Equal("启动", navigation.Selected);
        Assert.Equal(new[] { "启动", "增援", "功能", "诊断" }, ConsoleNavigation.Sections);
        Assert.True(navigation.SelectCommand.CanExecute(section));
        navigation.SelectCommand.Execute(section);
        Assert.Single(ConsoleNavigation.Sections, item => item == navigation.Selected);
        Assert.Equal(section, navigation.Selected);
    }

    [Fact]
    public void Navigation_DefaultsToTheStartPageAndHasNoMaintenancePage()
    {
        var navigation = new ConsoleNavigation();
        Assert.Equal("启动", navigation.Selected);
        Assert.Equal(ConsoleNavigation.DefaultSection, navigation.Selected);
        // 「维护」页已拆掉：名字与内容对不上，用户明确批评过。旧名字必须被拒绝。
        Assert.False(navigation.SelectCommand.CanExecute("维护"));
        navigation.SelectCommand.Execute("维护");
        Assert.Equal("启动", navigation.Selected);
        Assert.DoesNotContain("维护", ConsoleNavigation.Sections);
        Assert.Equal(4, ConsoleNavigation.Sections.Count);
    }

    [Theory]
    [InlineData("维护")]
    [InlineData("预设")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("不存在")]
    public void InvalidNavigation_PreservesCurrentPage(string? section)
    {
        var navigation = new ConsoleNavigation { Selected = "诊断" };
        Assert.False(navigation.SelectCommand.CanExecute(section));
        navigation.SelectCommand.Execute(section);
        Assert.Equal("诊断", navigation.Selected);
    }

    [Fact]
    public void RepeatedNavigation_DoesNotResetPageOrNotify()
    {
        var navigation = new ConsoleNavigation { Selected = "增援" };
        var notifications = 0;
        navigation.PropertyChanged += (_, _) => notifications++;
        navigation.SelectCommand.Execute("增援");
        Assert.Equal(0, notifications);
        navigation.SelectCommand.Execute("诊断");
        navigation.SelectCommand.Execute("增援");
        Assert.Equal(2, notifications);
        Assert.Equal("增援", navigation.Selected);
    }

    [Fact]
    public void Windows_KeepIndependentNavigationState()
    {
        var main = new ConsoleNavigation { Selected = "诊断" };
        var floating = new ConsoleNavigation { Selected = "功能" };
        main.SelectCommand.Execute("启动");
        Assert.Equal("启动", main.Selected);
        Assert.Equal("功能", floating.Selected);
    }

    [Fact]
    public void PanelSections_AreTheInGameTabsAndStayDisjointFromDesktopPages()
    {
        // 面板页签与主窗页签故意不同：桌面页「启动/诊断」不得混进游戏内面板。
        Assert.Equal(new[] { "功能", "增援", "选单位" }, ConsoleNavigation.PanelSections);
        Assert.Equal("功能", ConsoleNavigation.DefaultPanelSection);
        Assert.Contains(ConsoleNavigation.DefaultPanelSection, ConsoleNavigation.PanelSections);
        Assert.True(ConsoleNavigation.IsPanelSection("功能"));
        Assert.True(ConsoleNavigation.IsPanelSection("增援"));
        Assert.True(ConsoleNavigation.IsPanelSection("选单位"));
        Assert.False(ConsoleNavigation.IsPanelSection("启动"));
        Assert.False(ConsoleNavigation.IsPanelSection("诊断"));
        Assert.False(ConsoleNavigation.IsPanelSection("维护"));
        Assert.False(ConsoleNavigation.IsPanelSection(""));
        Assert.False(ConsoleNavigation.IsPanelSection(null));
        // 两个列表只有「功能/增援」重合；任何一边被单方面改动都会在这里失败。
        Assert.Equal(new[] { "功能", "增援" }, ConsoleNavigation.PanelSections.Where(ConsoleNavigation.IsSection).ToArray());
        Assert.True(ConsoleNavigation.Sections.Concat(ConsoleNavigation.PanelSections).ToHashSet()
            .SetEquals(new[] { "启动", "增援", "功能", "诊断", "选单位" }));
        Assert.DoesNotContain("选单位", ConsoleNavigation.Sections);
    }

    [Fact]
    public void IsSection_AcceptsOnlyTheFourPages()
    {
        Assert.True(ConsoleNavigation.IsSection("启动"));
        Assert.True(ConsoleNavigation.IsSection("增援"));
        Assert.True(ConsoleNavigation.IsSection("功能"));
        Assert.True(ConsoleNavigation.IsSection("诊断"));
        Assert.False(ConsoleNavigation.IsSection("维护"));
        Assert.False(ConsoleNavigation.IsSection(null));
        Assert.False(ConsoleNavigation.IsSection(""));
    }
}
