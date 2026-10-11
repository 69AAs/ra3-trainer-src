using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using Ra3Trainer.App.ViewModels;

namespace Ra3Trainer.App;

public partial class App : Application
{
    private static class NativeMethods
    {
        public const int ShowNoActivate = 4;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr FindWindow(string? className, string windowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr handle, int command);

    }

    /// <summary>
    /// One view model shared by the main window and the floating panel, so both views always show
    /// the same connection state and the same feature toggles.
    /// </summary>
    private static MainViewModel? _sharedViewModel;
    private static readonly object SharedViewModelGate = new();
    private static bool _shuttingDown;

    public static MainViewModel SharedViewModel
    {
        get
        {
            // ??= 不是线程安全的（键盘钩子线程/清理线程都可能来取），而且退出之后重建会在
            // ShutdownMode=OnExplicitShutdown 下造出一个没人管的僵尸视图模型。
            lock (SharedViewModelGate)
            {
                if (_shuttingDown)
                {
                    throw new InvalidOperationException("应用正在退出，视图模型不可再创建。");
                }

                return _sharedViewModel ??= MainViewModel.LoadDefault();
            }
        }
    }

    private Mutex? _singleInstanceMutex;
    private static readonly System.Diagnostics.Stopwatch StartupClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Startup timing trail. The owner reported a dead-black window on the very first
    /// launch of a freshly deployed exe, normal after a restart. This log exists to
    /// prove where that first frame goes, instead of guessing.
    /// </summary>
    public static void StartupLog(string stage)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ra3Trainer");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] t+{StartupClock.ElapsedMilliseconds}ms {stage}\n");
        }
        catch
        {
            // Diagnostics must never interfere with startup.
        }
    }

    public App()
    {
        StartupLog("app-ctor");
        ConfigureRendering();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--overlay-cleanup")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // --overlay-cleanup 必须带一个有效 ownerPid：只写开关时原来会把 0 当 pid 传下去，
            // 清理工作者拿着 0 去做无意义的查找（真实调用点 ProductOverlay.cs:380 一定带 pid）。
            if (e.Args.Length != 2 || !int.TryParse(e.Args[1], out var ownerPid) || ownerPid <= 0)
            { Shutdown(2); return; }
            _ = CleanupAndExit(ownerPid);
            return;
        }
        // Two instances would install the same hooks in the same process and corrupt each other's
        // patches, so only the first one runs.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Ra3Trainer.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 重复启动也不能打断游戏焦点；只无激活显示已有主窗。
            TryShowExistingInstance();

            Shutdown(1);
            return;
        }

        base.OnStartup(e);
        try
        {
            StartupLog("window-ctor-begin");
            var mainWindow = new MainWindow();
            StartupLog("window-ctor-end");
            MainWindow = mainWindow;
            mainWindow.ContentRendered += (_, _) => StartupLog("first-render");
            mainWindow.Loaded += (_, _) =>
            {
                StartupLog("window-loaded");
                // Belt and braces for the owner's dead-black first frame: force one
                // layout/render pass once the dispatcher is idle. Pure redraw, no
                // input, no window style change, no focus change.
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        mainWindow.InvalidateVisual();
                        mainWindow.UpdateLayout();
                        StartupLog("repaint-nudge-done");
                    }
                    catch (Exception ex) { StartupLog("repaint-nudge-failed " + ex.GetType().Name); }
                }));
            };
            StartupLog("show-begin");
            mainWindow.Show();
            StartupLog("show-end");
        }
        catch (Exception ex)
        {
            // 起不来就干净退出：曾经的坑是启动异常弹个框，用户关掉后进程还在、单实例锁还被占着，
            // 表现成"一直说已经有一个在运行了"。宁可让他重开，也不要留幽灵。
            WriteCrashLog(ex);
            Shutdown(1);
        }
    }

    private async System.Threading.Tasks.Task CleanupAndExit(int ownerPid)
    {
        try { await Diagnostics.ProductOverlay.RunCleanupWorker(ownerPid); }
        catch (Exception ex) { WriteCrashLog(ex); }
        finally { Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lock (SharedViewModelGate)
        {
            // 先立"正在退出"的牌子：晚到的回调（键盘钩子/清理线程/最后一次心跳）不能在
            // 视图模型释放之后把它重建出来。
            _shuttingDown = true;
        }

        _sharedViewModel?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>无激活显示已有主窗；不改变浮窗的窗口样式。</summary>
    private static bool TryShowExistingInstance()
    {
        var handle = NativeMethods.FindWindow(null, "RA3 Trainer");
        if (handle == IntPtr.Zero)
        {
            return false;
        }
        return NativeMethods.ShowWindow(handle, NativeMethods.ShowNoActivate);
    }

    public static void ConfigureRendering()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        // 致命异常不该被吞掉：OOM / 访问违例之后进程状态已不可信，继续跑只会留下
        // "钩子还在、面板半死"的局面。交回默认崩溃路径（OnUnhandledException 会写日志）。
        if (e.Exception is OutOfMemoryException or AccessViolationException or System.Runtime.InteropServices.SEHException)
        {
            e.Handled = false;
            return;
        }

        if (_sharedViewModel is not null)
        {
            _sharedViewModel.StatusMessage = $"发生错误：{e.Exception.Message}";
        }
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Ra3Trainer");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {exception}\n\n");
        }
        catch
        {
            // Last-resort error logging must never crash the UI.
        }
    }
}
