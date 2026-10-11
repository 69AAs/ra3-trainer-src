using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.App;

/// <summary>
/// Small always-on-top panel so features can be clicked without alt-tabbing out of the game.
/// Two hard requirements drive the implementation:
/// 1. The window must never take activation away from the game (WS_EX_NOACTIVATE), or clicking it
///    would minimize/pause the game.
/// 2. The window must not appear in Alt+Tab / the taskbar (WS_EX_TOOLWINDOW + ShowInTaskbar=false).
/// Because it can never be activated, it contains no text inputs — values that need typing stay in
/// the main window on purpose.
/// </summary>
public partial class FloatingWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    private readonly MainViewModel _viewModel;

    public FloatingReinforcementViewModel ReinforcementEditor { get; }
    private HwndSource? _windowSource;

    /// <summary>
    /// 浮窗总是以"收起来"的样子打开（用户明确要求：默认收起来），滚动的展开态要主动点开。
    /// 位置仍然记忆；IsCollapsed 依旧写进设置，只是不再决定启动时的形态。
    /// </summary>



    /// <summary>
    /// 细条临时展开成功能面板；切换形态时复位，不落盘。
    /// </summary>
    private bool _formInitialized;

    private readonly bool _offscreen;
    public FloatingWindow(MainViewModel viewModel) : this(viewModel, false) { }

    internal FloatingWindow(MainViewModel viewModel, bool offscreen)
    {
        _offscreen = offscreen;
        _viewModel = viewModel;
        ReinforcementEditor = new FloatingReinforcementViewModel(viewModel);
        try
        {
        InitializeComponent();
        DataContext = viewModel;
        // 行板要跟着光标滚：Num8/2 移高亮、Num7/9 换页时画面必须跟上，
        // 否则高亮跑到视口外面，用户看到的就是"按了没反应"。
        viewModel.Navigator.PropertyChanged += OnPanelCursorChanged;
        // 桌面浮窗已取消：这个窗口只剩"面板渲染器"一个用途 ——
        // 永远不 Show()、不建 HWND、不装输入计时器、不存桌面位置（位置由面板的 位置格 管）。
        Left = Top = -30000; Opacity = 0;
        RootCard.Opacity = 1;
        RootCard.Style = (Style)FindResource("FloatCard");
        BarPanel.Visibility = Visibility.Visible;
        UpdatePanelLayout();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoadedHandler;
        }
        catch
        {
            // 构造中途失败：先把挂到共享视图模型/Navigator 上的处理器摘掉再清理抛出。
            // 否则被丢弃的窗口实例会永远留在共享 Navigator 的事件表里（OnClosed 不会再跑），
            // 每次导航变化都会回调这个死窗口。-= 对未订阅的处理器是安全的空操作。
            _viewModel.Navigator.PropertyChanged -= OnPanelCursorChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ReinforcementEditor.Dispose();
            throw;
        }
    }


    /// <summary>
    /// 面板模式下强制"展开"渲染。游戏内面板就是浮窗的截图，而形态（发丝状态线 / 边缘热区 /
    /// 迷你条）是**桌面**上的事：默认形态就是发丝，让它生效的话按 Ins 游戏里面板会变成
    /// 一条 88px 的空壳（正文全没）。桌面形态只在窗口模式下说话。
    /// </summary>
    private bool PanelForcesExpanded => _viewModel.PanelMode;


    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_offscreen) return;

        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(PreventMouseActivation);
        var exStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        var updated = exStyle | WsExToolWindow | WsExNoActivate;
        if (updated != exStyle)
        {
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(updated));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ReinforcementEditor.Dispose();
        _viewModel.Navigator.PropertyChanged -= OnPanelCursorChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _windowSource?.RemoveHook(PreventMouseActivation);
        _windowSource = null;

        // The floating panel is the only visible window when the main window is hidden; closing it
        // must never leave the app running headless.
        var mainWindow = Application.Current?.MainWindow;
        if (!_offscreen && mainWindow is MainWindow main && !mainWindow.IsVisible)
        {
            main.ShowWithoutActivation();
        }

        base.OnClosed(e);
    }

    private static IntPtr PreventMouseActivation(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Active-window tracking can bypass WS_EX_NOACTIVATE. Keep the click,
        // but explicitly reject activation for both click and hover paths.
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE
        }
        return IntPtr.Zero;
    }

    private void OnLoadedHandler(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedHandler;
        _formInitialized = true;
        ApplySelectedForm(resetCollapse: false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 面板模式开关必须重算可见性：曾经只盯桌面形态，打开面板模式后窗口变成 360×440
        // 但 BarPanel 仍是 Collapsed ⇒ 游戏内浮窗只剩一条状态行的空壳（页签/列表全不见）。
        if (e.PropertyName == nameof(MainViewModel.PanelMode) && _formInitialized)
            ApplySelectedForm();
    }

    private void ApplySelectedForm(bool resetCollapse = true)
    {
        // 面板是该窗口唯一形态：没有发丝/迷你条/边缘热区可切，重算一次布局即可。
        // ApplyFormSurface() 末尾已经调用过 UpdatePanelLayout()，这里不再重复第二次布局。
        // （resetCollapse 参数保留：离屏冒烟夹具按 ApplySelectedForm(bool) 反射调用。）
        ApplyFormSurface();
    }

    /// <summary>
    /// Swaps the window chrome for the hairline strip: no 8px margin and no drop shadow (otherwise
    /// a 16px strip would still read as a floating card), and the bar panels give way to one line of
    /// text. Also the single place the idle opacity is reset, so a fresh strip starts fully visible.
    /// </summary>
    private void ApplyFormSurface()
    {
        // 桌面形态（发丝状态线 / 迷你条 / 边缘热区）已取消：面板是该窗口唯一形态。
        RootCard.Style = (Style)FindResource("FloatCard");
        BarPanel.Visibility = Visibility.Visible;
        MinWidth = 0;
        MaxWidth = double.PositiveInfinity;
        UpdatePanelLayout();
    }









    /// <summary>
    /// 面板行板跟着光标滚。用户反馈的 bug：用数字键盘往下/翻页时画面不跟随，
    /// 高亮行跑到视口外面，看起来像按键没反应。换页时回到顶部，移行时把该行滚进可视区。
    /// </summary>
    private void OnPanelCursorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelNavigator.SelectedIndex)) FollowPanelCursor(resetToTop: false);
        // 只有真的换页才回到页首；行集合重建（每次按键都会重建）不能把画面拽回顶部。
        else if (e.PropertyName == nameof(PanelNavigator.Page)) FollowPanelCursor(resetToTop: true);
        else if (e.PropertyName == nameof(PanelNavigator.Rows)) FollowPanelCursor(resetToTop: false);
    }

    private bool _panelScrollBusy;

    /// <summary>
    /// 面板里的滚轮：按一格的量滚行板（面板 360×440，内容比视口高时用它翻）。
    /// 正 delta = 往下翻。纯装饰性动作，失败也不许影响面板本身。
    /// </summary>
    public void ScrollBoard(double delta)
    {
        if (PanelBoardScroll is null || delta == 0) return;
        try
        {
            PanelBoardScroll.UpdateLayout();
            var target = PanelBoardScroll.VerticalOffset + delta;
            PanelBoardScroll.ScrollToVerticalOffset(Math.Max(0, target));
        }
        catch
        {
            // 滚动失败不是面板的错，也不能把面板带下水。
        }
    }

    private void FollowPanelCursor(bool resetToTop)
    {
        // 纯装饰性动作：绝不能让它把面板/键盘分派带下水，也绝不重入布局。
        if (_panelScrollBusy) return;
        _panelScrollBusy = true;
        try
        {
            if (PanelBoardScroll is null || PanelRowsControl is null) return;
            PanelBoardScroll.UpdateLayout();
            if (resetToTop)
            {
                PanelBoardScroll.ScrollToTop();
                PanelBoardScroll.UpdateLayout();
            }
            var row = _viewModel.Navigator.Selected;
            if (row is null) return;
            if (PanelBoardScroll.ViewportHeight <= 0) return;
            var content = PanelBoardScroll.Content as FrameworkElement;
            var element = FindRowElement(PanelRowsControl, row);
            if (content is null || element is null || element.ActualHeight <= 0) return;
            var top = element.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            var bottom = top + element.ActualHeight;
            var offset = PanelBoardScroll.VerticalOffset;
            var viewport = PanelBoardScroll.ViewportHeight;
            const double margin = 2;
            // 只在行真的被视口切掉时才滚：行在视口上沿之上往上贴，在下沿之下往下贴。
            // （早期写法用 top - margin < offset，行本来就贴着上沿时也会被推动，把标题条挤出画面。）
            if (top < offset)
                PanelBoardScroll.ScrollToVerticalOffset(Math.Max(0, top - margin));
            else if (bottom > offset + viewport)
                PanelBoardScroll.ScrollToVerticalOffset(bottom + margin - viewport);
        }
        catch (Exception)
        {
            // 滚动只是显示细节：失败就保持原偏移，不影响任何操作。
        }
        finally
        {
            _panelScrollBusy = false;
        }
    }

    /// <summary>广度优先找出行对应的可视元素（ItemsControl 容器的 DataContext 就是行）。</summary>
    private static FrameworkElement? FindRowElement(DependencyObject root, object row)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is FrameworkElement element && ReferenceEquals(element.DataContext, row)) return element;
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }






    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
    }








    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();










    /// <summary>面板是该窗口唯一形态 ⇒ 尺寸恒为 360×440（曾经还有发丝 200×19 / 迷你条 360×88）。</summary>
    private void UpdatePanelLayout()
    {
        Width = 360;
        Height = 440;
        UpdateLayout();
    }

    /// <summary>Keeps the panel reachable after a resolution change moved the saved spot off-screen.</summary>
    private void ClampToVisibleDesktop()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var minLeft = SystemParameters.VirtualScreenLeft;
        var minTop = SystemParameters.VirtualScreenTop;
        var maxLeft = minLeft + SystemParameters.VirtualScreenWidth - width;
        var maxTop = minTop + SystemParameters.VirtualScreenHeight - height;

        Left = Math.Max(minLeft, Math.Min(Left, maxLeft));
        Top = Math.Max(minTop, Math.Min(Top, maxTop));
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
