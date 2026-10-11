using Ra3Trainer.App.ViewModels;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class PanelNavigatorTests
{
    private static PanelNavRow Row(string label, PanelRowKind kind = PanelRowKind.Action, string state = "") =>
        new() { Kind = kind, Action = label, Label = label, State = state };

    private static PanelNavigator WithRows(int count)
    {
        var navigator = new PanelNavigator();
        // 面板页 = {功能, 增援, 选单位}（不是主窗四页）：用"启动"会被判成未知页并落到面板默认页，
        // 断言 PageLabel 就会拿到一个和传入值不一样的前缀，读起来莫名其妙。
        navigator.SetRows(ConsoleNavigation.DefaultPanelSection, Enumerable.Range(0, count).Select(i => Row($"行{i}")));
        return navigator;
    }

    [Fact]
    public void SetRows_KeepsNoHighlightUntilAKeyArrives()
    {
        var navigator = WithRows(3);
        Assert.Equal(3, navigator.RowCount);
        Assert.Equal(-1, navigator.SelectedIndex);
        Assert.False(navigator.HasSelection);
        Assert.Null(navigator.Selected);
        Assert.DoesNotContain(navigator.Rows, row => row.IsSelected);
    }

    [Fact]
    public void Move_ClampsAtBothEndsInsteadOfWrapping()
    {
        var navigator = WithRows(3);
        Assert.True(navigator.Move(1));            // 无选中时向下落到第一行
        Assert.Equal(0, navigator.SelectedIndex);
        Assert.False(navigator.Move(-1));          // 顶行再向上：钳制，不动
        Assert.Equal(0, navigator.SelectedIndex);
        Assert.True(navigator.Move(1));
        Assert.True(navigator.Move(1));
        Assert.Equal(2, navigator.SelectedIndex);
        Assert.False(navigator.Move(1));           // 末行再向下：钳制，不动
        Assert.Equal(2, navigator.SelectedIndex);
        Assert.True(navigator.Move(-1));
        Assert.Equal(1, navigator.SelectedIndex);
    }

    [Fact]
    public void MoveWithoutSelection_SelectsAnEdgeRow()
    {
        var navigator = WithRows(4);
        Assert.True(navigator.Move(-1));
        Assert.Equal(3, navigator.SelectedIndex);  // 无选中时向上落到最后一行
        navigator.ClearSelection();
        Assert.Equal(-1, navigator.SelectedIndex);
        Assert.True(navigator.Move(1));
        Assert.Equal(0, navigator.SelectedIndex);
    }

    [Fact]
    public void Select_ClampsAndNegativeClears()
    {
        var navigator = WithRows(3);
        Assert.True(navigator.Select(99));
        Assert.Equal(2, navigator.SelectedIndex);
        Assert.False(navigator.Select(2));
        Assert.True(navigator.Select(-5));
        Assert.Equal(-1, navigator.SelectedIndex);
        Assert.False(navigator.ClearSelection());
    }

    [Fact]
    public void ExactlyOneRowIsHighlightedAtAnyTime()
    {
        var navigator = WithRows(4);
        navigator.Select(2);
        Assert.Single(navigator.Rows, row => row.IsSelected);
        Assert.True(navigator.Rows[2].IsSelected);
        navigator.Move(1);
        Assert.Single(navigator.Rows, row => row.IsSelected);
        Assert.True(navigator.Rows[3].IsSelected);
        navigator.ClearSelection();
        Assert.DoesNotContain(navigator.Rows, row => row.IsSelected);
        Assert.Equal(-1, navigator.SelectedIndex);
        Assert.Null(navigator.Selected);
    }

    [Fact]
    public void SetRows_ClampsAStaleIndexOntoTheNewRowSet()
    {
        var navigator = WithRows(5);
        navigator.Select(4);
        navigator.SetRows("增援", new[] { Row("a"), Row("b") });
        Assert.Equal(2, navigator.RowCount);
        Assert.Equal(1, navigator.SelectedIndex);      // 旧索引 4 被钳到新的末行
        Assert.True(navigator.Rows[1].IsSelected);
    }

    [Fact]
    public void SetRows_WithNoRowsDropsTheHighlight()
    {
        var navigator = WithRows(3);
        navigator.Select(1);
        navigator.SetRows("功能", Array.Empty<PanelNavRow>());
        Assert.Equal(0, navigator.RowCount);
        Assert.Equal(-1, navigator.SelectedIndex);
        Assert.False(navigator.HasSelection);
        Assert.False(navigator.Move(1));
        Assert.Equal(-1, navigator.SelectedIndex);
    }

    [Fact]
    public void SetRows_RejectsAnUnknownPageAndKeepsTheDefault()
    {
        var navigator = WithRows(1);
        // 先离开默认页：否则页面本来就是默认值，"兜底"和"直接忽略未知页"两种实现看起来一模一样。
        navigator.SetRows("增援", new[] { Row("a") });
        Assert.Equal("增援", navigator.Page);
        navigator.SetRows("维护", new[] { Row("a") });
        // 兜底必须落在**面板页**上（"启动"是主窗首页，面板里根本没有这一页）
        Assert.Equal(ConsoleNavigation.DefaultPanelSection, navigator.Page);
        Assert.Contains(navigator.Page, ConsoleNavigation.PanelSections);
    }

    // 面板页序 = ConsoleNavigation.PanelSections = {功能, 增援, 选单位}（用户 2026-10-06 定的口径：
    // 面板只在游戏里看得见，所以不放"启动/诊断"这种桌面页；用主窗四页来断言是旧口径）。
    [Theory]
    [InlineData("功能", 1, "增援")]
    [InlineData("增援", 1, "选单位")]
    [InlineData("选单位", 1, "功能")]   // 末页向后换页回到首页
    [InlineData("功能", -1, "选单位")]  // 首页向前换页到末页
    [InlineData("选单位", -1, "增援")]
    [InlineData("增援", -1, "功能")]
    [InlineData("不存在", 1, "增援")]
    public void NeighbourPage_WrapsInBothDirections(string current, int direction, string expected)
    {
        Assert.Equal(expected, PanelNavigator.NeighbourPage(current, direction));
    }

    [Fact]
    public void RowKinds_DescribeWhatNum5AndValueKeysMayDo()
    {
        Assert.True(Row("v", PanelRowKind.Value).IsAdjustable);
        Assert.True(Row("p", PanelRowKind.Position).IsAdjustable);
        Assert.False(Row("a", PanelRowKind.Action).IsAdjustable);
        Assert.False(Row("t", PanelRowKind.Toggle).IsAdjustable);
        Assert.False(Row("s", PanelRowKind.Status).IsAdjustable);
        Assert.Equal("执行", Row("a", PanelRowKind.Action).ButtonText);
        // 开关行的状态只显示一次（列里），按钮只负责"切换"。
        Assert.Equal("切换", Row("t", PanelRowKind.Toggle, "已启用").ButtonText);
        Assert.Equal("已启用", Row("t", PanelRowKind.Toggle, "已启用").State);
        // 只读行没有点击入口：状态板不做点击。
        Assert.Equal(string.Empty, Row("s", PanelRowKind.Status, "已启用").ButtonText);
        Assert.Equal(string.Empty, Row("v", PanelRowKind.Value).ButtonText);
        Assert.Equal(string.Empty, Row("p", PanelRowKind.Position).ButtonText);
    }

    [Fact]
    public void Page_IsOnlyNotifiedWhenThePageReallyChanges()
    {
        // 视图用 Page 通知判定"换页后回到页首"。每次重建行都通知的话，按一下 Num2
        // 画面就被拽回顶部 —— 正是用户报的"往下翻显示不跟随"。
        var navigator = WithRows(2);
        var pages = new List<string>();
        navigator.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PanelNavigator.Page)) pages.Add(navigator.Page); };
        navigator.SetRows("增援", new[] { Row("a") });
        Assert.Single(pages);
        navigator.SetRows("增援", new[] { Row("a"), Row("b") });   // 同页重建：不算换页
        navigator.SetRows("增援", new[] { Row("a") });
        Assert.Single(pages);
        navigator.Move(1);
        Assert.Single(pages);
        navigator.SetRows("功能", new[] { Row("a") });             // 真换页
        Assert.Equal(new[] { "增援", "功能" }, pages);
    }

    [Fact]
    public void PageLabel_ReportsTheCursorPositionWithinTheRowSet()
    {
        var navigator = WithRows(4);
        Assert.Equal("功能 · 0/4", navigator.PageLabel);
        navigator.Select(1);
        Assert.Equal("功能 · 2/4", navigator.PageLabel);
        Assert.Equal("行1", navigator.SelectionLabel);
        navigator.ClearSelection();
        Assert.Equal("未选中 · Num8/2 选行", navigator.SelectionLabel);
    }
}
