using System.Windows;
using Ra3Trainer.App.ViewModels;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.App;

public partial class MainWindow : Window
{
    public ConsoleNavigation Navigation { get; } = new();
    public FloatingReinforcementViewModel ReinforcementEditor { get; }
    private readonly MainViewModel _viewModel;

    public MainWindow() : this(App.SharedViewModel) { }

    /// <summary>
    /// Explicit view model so the same window can be laid out offscreen by the UI
    /// fixture. Production always goes through <see cref="App.SharedViewModel"/>;
    /// construction alone never creates an HWND and never activates anything.
    /// </summary>
    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        ReinforcementEditor = new FloatingReinforcementViewModel(viewModel);
        InitializeComponent();
        // Shared with the floating panel so both views drive the same session/toggles.
        DataContext = viewModel;
        viewModel.LaunchWindowModeRequested += OnLaunchWindowModeRequested;
        // 主窗页签（Navigation.Selected）和游戏内面板的页是**同一份状态**：
        // 以前它们各走各的 —— 点主窗页签面板不动，面板里 Num7/9 换页主窗也不动。
        Navigation.PropertyChanged += OnNavigationChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.SelectPanelPage(Navigation.Selected);
        Closed += (_, _) =>
        {
            ReinforcementEditor.Dispose();
            _viewModel.LaunchWindowModeRequested -= OnLaunchWindowModeRequested;
            Navigation.PropertyChanged -= OnNavigationChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        };
    }

    private bool _syncingPages;

    private void OnNavigationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_syncingPages || e.PropertyName != nameof(ConsoleNavigation.Selected)) return;
        _syncingPages = true;
        try { _viewModel.SelectPanelPage(Navigation.Selected); }
        finally { _syncingPages = false; }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 面板模式被关掉（含"游戏退出"那条路，见 MainViewModel.OnTargetExitTimerTick）⇒ 把主窗叫回来。
        // 用户报过：从游戏退出来后面板没了、进程还在后台、主窗看不见，只能靠任务管理器找。
        if (e.PropertyName == nameof(MainViewModel.PanelMode) && !_viewModel.PanelMode)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) ShowWithoutActivation();
            return;
        }

        if (_syncingPages || e.PropertyName != nameof(MainViewModel.OverlayPanelSection)) return;
        _syncingPages = true;
        try { Navigation.Selected = _viewModel.OverlayPanelSection; }
        finally { _syncingPages = false; }
    }

    public void ShowWithoutActivation()
    {
        ShowActivated = false;
        if (!IsVisible) Show();
        ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, 4); // SW_SHOWNOACTIVATE
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    private void OnFloatModeClick(object sender, RoutedEventArgs e)
    {
        // 桌面浮窗已取消：这个入口现在只开关游戏内注入面板。
        App.SharedViewModel.TogglePanelMode();
    }

    private void OnLaunchWindowModeRequested(LaunchWindowMode mode)
    {
        if (mode == LaunchWindowMode.Main)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) ShowWithoutActivation();
            return;
        }
        // 桌面浮窗已取消：这个模式现在只是"启动并装载后自动开游戏内面板"。
        App.SharedViewModel.PanelMode = true;
        if (IsVisible) Hide();
    }

    // 自绘窗头：没有系统标题栏，最小化/最大化/关闭必须自己实现
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }
}
